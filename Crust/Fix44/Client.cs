using iFix.Common;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    class UnexpectedMessageReceived : Exception
    {
        public UnexpectedMessageReceived(string msg) : base(msg) { }
    }

    public class ClientConfig
    {
        /// <summary>
        /// Logon field. Recommended value: 30.
        /// </summary>
        public int HeartBtInt = 30;
        /// <summary>
        /// Logon field.
        /// </summary>
        public string Password;

        /// <summary>
        /// Common field for all requests.
        /// </summary>
        public string SenderCompID;
        /// <summary>
        /// Common field for all requests.
        /// </summary>
        public string TargetCompID;

        /// <summary>
        /// Common field for posting, changing and cancelling orders.
        /// </summary>
        public string Account;
        /// <summary>
        /// Common field for posting, changing and cancelling orders.
        /// </summary>
        public string TradingSessionID;
        /// <summary>
        /// If null, PartyIDSource and PartyRole are ignored.
        /// </summary>
        public string PartyID;
        /// <summary>
        /// Ignored if PartyID is is null.
        /// </summary>
        public char PartyIDSource;
        /// <summary>
        /// Ignored if PartyID is is null.
        /// </summary>
        public int PartyRole;

        /// <summary>
        /// If not null, all ClOrdID in all outgoing messages
        /// will have this prefix.
        /// </summary>
        public string ClOrdIDPrefix;

        /// <summary>
        /// If the exhange hasn't replied anything to our request for
        /// this long, assume that it's not going to reply ever.
        /// Zero means no timeout.
        ///
        /// Recommended value: 30 seconds.
        /// </summary>
        public TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    }

    // What should be done with the order if an attempt to replace it is rejected?
    // Most notably, an attempt to replace an order is rejected if it's partially filled.
    enum OnReplaceReject
    {
        // Cancel the order.
        Cancel,
        // Keep the order as is, essentially ignoring the replace request.
        Keep,
    }

    public class Client : IClient
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly OrderManager _orders = new OrderManager();
        readonly Scheduler _scheduler = new Scheduler();
        readonly ClientConfig _cfg;
        readonly MessageBuilder _messageBuilder;
        readonly DurableConnection _connection;
        readonly MessagePump _messagePump;
        // Set to true when Dispose() is called.
        volatile bool _disposed = false;

        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _messageBuilder = new MessageBuilder(cfg);
            _connection = new DurableConnection(new InitializingConnector(connector, (session, cancel) =>
            {
                session.Send(_messageBuilder.Logon());
                if (!(session.Receive(cancel).Result is Mantle.Fix44.Logon))
                    throw new UnexpectedMessageReceived("Expected Logon");
            }));
            _messagePump = new MessagePump(
                _connection,
                (msg, sessionID) => _scheduler.Schedule(() => OnMessage(msg.Visit(new MessageDecoder(sessionID)))));
        }

        public event Action<OrderEvent> OnOrderEvent;

        public Task<IOrderCtrl> CreateOrder(NewOrderRequest request)
        {
            request = (NewOrderRequest)request.Clone();
            IOrder order = _orders.CreateOrder(request);
            var res = new Task<IOrderCtrl>(() => Submit(order, request) ? new OrderCtrl(this, order, request) : null);
            _scheduler.Schedule(() => res.RunSynchronously());
            return res;
        }

        public void Dispose()
        {
            _log.Info("Disposing of iFix.Crust.Client");
            _disposed = true;
            try { _messagePump.Dispose(); } catch { }
            try { _scheduler.Dispose(); } catch { }
            try { _connection.Dispose(); } catch { }
            _log.Info("iFix.Crust.Client successfully disposed of");
        }

        void TryTearDown(IOrder order)
        {
            if (order.Status == OrderStatus.TearingDown)
            {
                _log.Warn("Order has been in status TearingDown for too long. Finishing it: {0}", order);
                RaiseOrderEvent(order.Update(new OrderUpdate() { Status = OrderStatus.Finished }), null);
            }
        }

        OrderState UpdateOrder(string origOrderID, OrderOpID op, OrderUpdate update)
        {
            // Match by OrderID happens on fills.
            // Match by OrigOrderID happens on moves.
            // Match by OrderOpID happens on order creation and when we are trying to cancel/move an
            // order with unknown ID (Order Cancel Reject <9>).
            IOrder order = _orders.FindByOrderID(update.OrderID) ?? _orders.FindByOrderID(origOrderID) ?? _orders.FindByOpID(op);
            if (order == null) return null;
            OrderStatus oldStatus = order.Status;
            OrderState res = order.Update(update);
            // If the order has transitioned to status TearingDown, schedule a check in RequestTimeout.
            // If it's still TearingDown by then, we'll mark it as Finished.
            if (order.Status == OrderStatus.TearingDown && order.Status != oldStatus && _cfg.RequestTimeout > TimeSpan.Zero)
            {
                _scheduler.Schedule(() => TryTearDown(order), DateTime.UtcNow + _cfg.RequestTimeout);
            }
            return res;
        }

        void OnMessage(IncomingMessage msg)
        {
            if (msg == null) return;
            _log.Info("Decoded incoming message: {0}", msg);
            if (msg.TestReqID != null)
                _connection.Send(_messageBuilder.Heartbeat(msg.TestReqID));
            RaiseOrderEvent(UpdateOrder(msg.OrigOrderID, msg.Op, msg.Order), msg.Fill.MakeFill());
            // Finish a pending op, if there is one. Note that can belong to a different
            // order than the one we just updated. The protocol doesn't allow this but our
            // code will work just fine if it happens.
            IOrder order = _orders.FindByOpID(msg.Op);
            if (order != null) order.FinishPending();
        }

        void TryTimeout(OrderOpID id, OrderStatus? statusOnTimeout)
        {
            IOrder order = _orders.FindByOpID(id);
            if (order == null) return;
            _log.Warn("OrderOp {0} timed out for order {1}", id, order);
            RaiseOrderEvent(order.Update(new OrderUpdate() { Status = statusOnTimeout }), null);
            // The previus call to Update may have finished the Op, so we need to check
            // whether it's still pending.
            if (order.IsPending) order.FinishPending();
        }

        OrderOpID StoreOp(IOrder order, string clOrdID, DurableSeqNum seqNum)
        {
            Assert.NotNull(order);
            Assert.NotNull(clOrdID);
            if (seqNum == null) return null;  // Didn't sent the request to the exchange.
            var id = new OrderOpID() { SeqNum = seqNum, ClOrdID = clOrdID };
            order.SetPending(id);
            return id;
        }

        void RaiseOrderEvent(OrderState state, Fill fill)
        {
            if (state != null || fill != null)
            {
                var e = new OrderEvent() { State = state, Fill = fill };
                _log.Info("Publishing OrderEvent: {0}", e);
                Action<OrderEvent> action = Volatile.Read(ref OnOrderEvent);
                if (action != null) action(e);
            }
        }

        bool Submit(IOrder order, NewOrderRequest request)
        {
            Assert.True(!order.IsPending);
            Assert.True(order.Status == OrderStatus.Created, "Status = {0}", order.Status);
            Mantle.Fix44.NewOrderSingle msg = _messageBuilder.NewOrderSingle(request);
            OrderOpID id = StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg));
            if (id == null)
            {
                RaiseOrderEvent(order.Update(new OrderUpdate() { Status = OrderStatus.Finished }), null);
                return false;
            }
            if (_cfg.RequestTimeout > TimeSpan.Zero)
                _scheduler.Schedule(() => TryTimeout(id, OrderStatus.Finished), DateTime.UtcNow + _cfg.RequestTimeout);
            return true;
        }

        bool Cancel(IOrder order, NewOrderRequest request)
        {
            try
            {
                if (order.IsPending)
                {
                    _log.Info("Can't cancel order with a pending request", order);
                    return false;
                }
                if (order.Status != OrderStatus.Accepted && order.Status != OrderStatus.PartiallyFilled)
                {
                    _log.Info("Order is not in a cancelable state: {0}", order);
                    return false;
                }
                Assert.NotNull(order.OrderID);
                Mantle.Fix44.OrderCancelRequest msg = _messageBuilder.OrderCancelRequest(request, order.OrderID);
                return StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg)) != null;
            }
            catch (Exception e)
            {
                if (!_disposed)
                    _log.Error("Unexpected error while cancelling an order", e);
                return false;
            }
        }

        bool Replace(IOrder order, NewOrderRequest request, decimal quantity, decimal price, OnReplaceReject onReject)
        {
            try
            {
                if (order.IsPending)
                {
                    _log.Info("Can't replace order with a pending request", order);
                    return false;
                }
                // If OnReplaceReject is Keep, the order status should be Accepted.
                // If it's Cancel, the order status may also be PartiallyFilled.
                if (order.Status != OrderStatus.Accepted &&
                    (onReject == OnReplaceReject.Keep || order.Status != OrderStatus.PartiallyFilled))
                {
                    _log.Info("Order is not in a replacable state with OnReject policy = {0}: {1}", onReject, order);
                    return false;
                }
                Assert.NotNull(order.OrderID);
                Mantle.Fix44.OrderCancelReplaceRequest msg =
                    _messageBuilder.OrderCancelReplaceRequest(request, order.OrderID, quantity, price, onReject);
                return StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg)) != null;
            }
            catch (Exception e)
            {
                if (!_disposed)
                    _log.Error("Unexpected error while replacing an order", e);
                return false;
            }
        }

        class OrderCtrl : IOrderCtrl
        {
            readonly Client _client;
            readonly IOrder _order;
            readonly NewOrderRequest _request;

            public OrderCtrl(Client client, IOrder order, NewOrderRequest request)
            {
                _client = client;
                _order = order;
                _request = request;
            }

            public Task<bool> Cancel()
            {
                var res = new Task<bool>(() => _client.Cancel(_order, _request));
                _client._scheduler.Schedule(() => res.RunSynchronously());
                return res;
            }

            public Task<bool> Replace(decimal quantity, decimal price)
            {
                var res = new Task<bool>(() => _client.Replace(_order, _request, quantity, price, OnReplaceReject.Keep));
                _client._scheduler.Schedule(() => res.RunSynchronously());
                return res;
            }

            public Task<bool> ReplaceOrCancel(decimal quantity, decimal price)
            {
                var res = new Task<bool>(() => _client.Replace(_order, _request, quantity, price, OnReplaceReject.Cancel));
                _client._scheduler.Schedule(() => res.RunSynchronously());
                return res;
            }
        }
    }
}
