﻿using iFix.Common;
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

    public class Client : IClient
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly OrderManager _orders = new OrderManager();
        readonly Scheduler _scheduler = new Scheduler();
        readonly ClientConfig _cfg;
        readonly MessageBuilder _messageBuilder;
        readonly DurableConnection _connection;
        readonly MessagePump _messagePump;

        public Client(ClientConfig cfg, IConnector connector)
        {
            // Add a no-op subscriber to make OnOrderEvent non-null.
            OnOrderEvent += e => { };
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

        public IOrderCtrl CreateOrder(NewOrderRequest request)
        {
            request = (NewOrderRequest)request.Clone();
            IOrder order = _orders.CreateOrder(request);
            _scheduler.Schedule(() => Submit(order, request));
            return new OrderCtrl(this, order, request);
        }

        public void Dispose()
        {
            try { _messagePump.Dispose(); } catch { }
            try { _scheduler.Dispose(); } catch { }
            try { _connection.Dispose(); } catch { }
        }

        void TryTearDown(IOrder order)
        {
            if (order.Status == OrderStatus.TearingDown)
            {
                _log.Warn("Order with OrderID {0} has been in status TearingDown for too long. Finishing it.", order.OrderID);
                RaiseOrderEvent(order.Update(new OrderUpdate() { Status = OrderStatus.Finished }), null);
            }
        }

        OrderState UpdateOrder(string origOrderID, OrderUpdate update)
        {
            IOrder order = _orders.FindByOrderID(update.OrderID) ?? _orders.FindByOrderID(origOrderID);
            if (order == null) return null;
            OrderStatus oldStatus = order.Status;
            OrderState res = order.Update(update);
            if (order.Status == OrderStatus.TearingDown && order.Status != oldStatus && _cfg.RequestTimeout > TimeSpan.Zero)
            {
                _scheduler.Schedule(() => TryTearDown(order), DateTime.UtcNow + _cfg.RequestTimeout);
            }
            return res;
        }

        void OnMessage(IncomingMessage msg)
        {
            _log.Info("Decoded incoming message: {0}", msg);
            if (msg == null) return;
            if (msg.TestReqID != null)
                _connection.Send(_messageBuilder.Heartbeat(msg.TestReqID));
            RaiseOrderEvent(UpdateOrder(msg.OrigOrderID, msg.Order), msg.Fill.MakeFill());
            // Finish a pending op, if there is one. Note that in can belong to a different
            // order than the one we just updated. The protocol doesn't allow this but our
            // code will work just fine if it happens.
            IOrder order = _orders.FindByOpID(msg.Op);
            if (order != null) order.FinishPending();
        }

        void TryTimeout(OrderOpID id, OrderStatus? statusOnTimeout)
        {
            IOrder order = _orders.FindByOpID(id);
            if (order == null) return;
            RaiseOrderEvent(order.Update(new OrderUpdate() { Status = statusOnTimeout }), null);
            order.FinishPending();
        }

        bool StoreOp(IOrder order, string clOrdID, DurableSeqNum seqNum, OrderStatus? statusOnTimeout)
        {
            Assert.NotNull(order);
            Assert.NotNull(clOrdID);
            if (seqNum == null) return false;  // Didn't sent the request to the exchange.
            var id = new OrderOpID() { SeqNum = seqNum, ClOrdID = clOrdID };
            order.SetPending(id);
            if (_cfg.RequestTimeout > TimeSpan.Zero)
                _scheduler.Schedule(() => TryTimeout(id, statusOnTimeout), DateTime.UtcNow + _cfg.RequestTimeout);
            return true;
        }

        void RaiseOrderEvent(OrderState state, Fill fill)
        {
            if (state != null || fill != null)
            {
                var e = new OrderEvent() { State = state, Fill = fill };
                _log.Info("Publishing OrderEvent: {0}", e);
                OnOrderEvent(e);
            }
        }

        void Submit(IOrder order, NewOrderRequest request)
        {
            Assert.True(!order.IsPending);
            Assert.True(order.Status == OrderStatus.Created, "Status = {0}", order.Status);
            Mantle.Fix44.NewOrderSingle msg = _messageBuilder.NewOrderSingle(request);
            if (!StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), OrderStatus.Finished))
                RaiseOrderEvent(order.Update(new OrderUpdate() { Status = OrderStatus.Finished }), null);
        }

        void Cancel(IOrder order, NewOrderRequest request)
        {
            if (order.Status != OrderStatus.Accepted && order.Status != OrderStatus.PartiallyFilled)
            {
                _log.Info("Can't cancel order with OrderID {0} in state {1}", order.OrderID, order.Status);
                return;
            }
            if (order.IsPending)
            {
                _log.Info("Can't cancel order with OrderID {0} because it has a pending request", order.OrderID);
                return;
            }
            Assert.NotNull(order.OrderID);
            Mantle.Fix44.OrderCancelRequest msg = _messageBuilder.OrderCancelRequest(request, order.OrderID);
            StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), null);
        }

        void Replace(IOrder order, NewOrderRequest request, decimal quantity, decimal price)
        {
            if (order.Status != OrderStatus.Accepted)
            {
                _log.Info("Can't replace order with OrderID {0} in state {1}", order.OrderID, order.Status);
                return;
            }
            if (order.IsPending)
            {
                _log.Info("Can't replace order with OrderID {0} because it has a pending request", order.OrderID);
                return;
            }
            Assert.NotNull(order.OrderID);
            Mantle.Fix44.OrderCancelReplaceRequest msg =
                _messageBuilder.OrderCancelReplaceRequest(request, order.OrderID, quantity, price);
            StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), null);
        }

        void ReplaceOrCancel(IOrder order, NewOrderRequest request, decimal quantity, decimal price)
        {
            if (order.Status == OrderStatus.Accepted)
                Replace(order, request, quantity, price);
            else
                Cancel(order, request);
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

            public void Cancel()
            {
                _client._scheduler.Schedule(() => _client.Cancel(_order, _request));
            }

            public void Replace(decimal quantity, decimal price)
            {
                _client._scheduler.Schedule(() => _client.Replace(_order, _request, quantity, price));
            }

            public void ReplaceOrCancel(decimal quantity, decimal price)
            {
                _client._scheduler.Schedule(() => _client.ReplaceOrCancel(_order, _request, quantity, price));
            }
        }
    }
}
