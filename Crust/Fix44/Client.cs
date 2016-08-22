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

    public enum Extensions
    {
        None,
        // okcoin.com and okcoin.cn.
        OkCoin,
        // huobi.com.
        Huobi,
    }

    public class ClientConfig
    {
        /// <summary>
        /// Logon field. Recommended value: 30.
        /// </summary>
        public int HeartBtInt = 30;
        /// <summary>
        /// Logon field. Can be null.
        /// </summary>
        public string Username;
        /// <summary>
        /// Logon field. Can't be null.
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
        /// Common field for posting, changing and cancelling orders. May be null.
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

        /// <summary>
        /// Request market data for the specified symbols.
        /// </summary>
        public List<string> MarketDataSymbols = new List<string>();

        /// <summary>
        /// OKcoin sometimes sends us messages with bogus lists of live trades.
        /// Such messages usually have a fixed number of trades (e.g., 60) and ideally we want
        /// to ignore all trades from them. Unfortunately, the bogus messages have all
        /// the same tags as regular messages; the only thing that distinguishes them
        /// is the number of trades.
        /// 
        /// If FakeTradeCount is positive, iFix will ignore incoming messages
        /// that have exactly that many trades in them.
        /// </summary>
        public int FakeTradeCount = 0;

        /// <summary>
        /// Does the exchange allow orders to be moved? If set to false, IOrderCtrl.Replace()
        /// will always return false, while IOrcerCtrl.ReplaceOrCancel() will always cancel.
        /// </summary>
        public bool ReplaceEnabled = true;

        /// <summary>
        /// Some exhanges implement extensions to the FIX protocol. If you want to use them,
        /// specify which extensions the exchange you are connecting to uses.
        /// </summary>
        public Extensions Extensions = Extensions.None;

        /// <summary>
        /// Set this option to true if the exchange you are working with has a tendency to
        /// lose fill notifications (35=8 150=F). For example, okcoin.com is known to
        /// have this particular bug.
        /// 
        /// If set to true, iFix will generate fills based on field CumQty (14) instead of
        /// LastQty (32). In other words, it will infer fills from order state changes.
        /// </summary>
        public bool SimulateFills = false;
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

    class ConnectionWatchdog
    {
        static readonly Logger _log = LogManager.GetCurrentClassLogger();

        static readonly string TestReqID = "iFix";
        static readonly int NumSentBeforeReconnect = 2;

        readonly DurableConnection _connection;
        readonly Scheduler _scheduler;
        readonly MessageBuilder _messageBuilder;
        readonly TimeSpan _hearBeatPeriod;

        int _numSent = 0;
        DateTime _updated = DateTime.UtcNow;

        public ConnectionWatchdog(DurableConnection connection, Scheduler scheduler, MessageBuilder messageBuilder)
        {
            _connection = connection;
            _scheduler = scheduler;
            _messageBuilder = messageBuilder;
            _hearBeatPeriod = TimeSpan.FromSeconds(messageBuilder.Config.HeartBtInt);
            Reschedule();
        }

        void Check()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now - _updated < _hearBeatPeriod) return;
                _updated = now;
                // If we have successfully sent several test requests in a row and
                // haven't received any heartbeats back, it's time to reconnect.
                if (_numSent == NumSentBeforeReconnect)
                {
                    _log.Info("Didn't recieve a heartbeat for a long time. " +
                              "Assuming that the remote side is dead. Reconnecting.");
                    _numSent = 0;
                    try { _connection.Reconnect(); }
                    catch { }
                    return;
                }
                try
                {
                    _log.Info("Sending a TestRequest");
                    if (_connection.Send(_messageBuilder.TestRequest(TestReqID)) != null)
                    {
                        ++_numSent;
                        return;
                    }
                }
                catch { }
                // We were unable to send a test request. Reset the counter. There is no connection
                // to reset anyway.
                _numSent = 0;
                return;
            }
            finally
            {
                Reschedule();
            }
        }

        public void OnHeartbeat(string reply)
        {
            if (reply != TestReqID)
            {
                _log.Warn("Heartbeat has invalid TestReqID field: {0}", reply);
                return;
            }
            _numSent = 0;
            _updated = DateTime.UtcNow;
        }

        void Reschedule()
        {
            _scheduler.Schedule(Check, _updated + _hearBeatPeriod);
        }
    }

    class ConnectedClient
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly OrderManager _orders = new OrderManager();
        readonly Scheduler _scheduler = new Scheduler();
        readonly ClientConfig _cfg;
        readonly MessageBuilder _messageBuilder;
        readonly DurableConnection _connection;
        readonly MessagePump _messagePump;
        readonly ConnectionWatchdog _watchdog;
        // Set to true when Dispose() is called.
        volatile bool _disposed = false;

        public ConnectedClient(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _messageBuilder = new MessageBuilder(cfg);
            _connection = new DurableConnection(new InitializingConnector(connector, (session, cancel) =>
            {
                session.Send(_messageBuilder.Logon());
                if (!(session.Receive(cancel).Result is Mantle.Fix44.Logon))
                    throw new UnexpectedMessageReceived("Expected Logon");
                foreach (string symbol in _cfg.MarketDataSymbols)
                {
                    session.Send(_messageBuilder.MarketDataRequest(symbol, MessageBuilder.MarketDataType.Order));
                    session.Send(_messageBuilder.MarketDataRequest(symbol, MessageBuilder.MarketDataType.Trade));
                }
            }));
            _messagePump = new MessagePump(
                _connection,
                (msg, sessionID) => _scheduler.Schedule(
                    () => OnMessage(msg.Visit(new MessageDecoder(sessionID, _cfg.Extensions)))));
            _watchdog = new ConnectionWatchdog(_connection, _scheduler, _messageBuilder);
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

        public Task<bool> RequestMarketData(string symbol)
        {
            var res = new Task<bool>(() =>
            {
                if (_connection.Send(_messageBuilder.MarketDataRequest(symbol, MessageBuilder.MarketDataType.Order)) == null)
                    return false;
                return true;
            });
            _scheduler.Schedule(() => res.RunSynchronously());
            return res;
        }

        public Task<bool> RequestAccountInfo()
        {
            var res = new Task<bool>(() => _connection.Send(_messageBuilder.AccountInfoRequest()) != null);
            _scheduler.Schedule(() => res.RunSynchronously());
            return res;
        }

        public void Dispose()
        {
            _log.Info("Disposing of iFix.Crust.ConnectedClient");
            _disposed = true;
            try { _scheduler.Dispose(); } catch { }
            try { _messagePump.StartDispose(); } catch { }
            try { _connection.Dispose(); } catch { }
            try { _messagePump.Dispose(); } catch { }
            _log.Info("iFix.Crust.ConnectedClient successfully disposed of");
        }

        void TryTearDown(IOrder order)
        {
            if (order.Status == OrderStatus.TearingDown)
            {
                _log.Warn("Order has been in status TearingDown for too long. Finishing it: {0}", order);
                Fill dummy;
                RaiseOrderEvent(state: order.Update(new OrderUpdate() { Status = OrderStatus.Finished }, out dummy));
            }
        }

        OrderState UpdateOrder(string origOrderID, OrderOpID op, OrderUpdate update, out Fill fill)
        {
            // Match by OrderID happens on fills.
            // Match by OrigOrderID happens on moves.
            // Match by OrderOpID happens on order creation and when we are trying to cancel/move an
            // order with unknown ID (Order Cancel Reject <9>).
            IOrder order = _orders.FindByOrderID(update.OrderID) ?? _orders.FindByOrderID(origOrderID) ?? _orders.FindByOpID(op);
            if (order == null)
            {
                fill = null;
                return null;
            }
            OrderStatus oldStatus = order.Status;
            OrderState res = order.Update(update, out fill);
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
            _log.Debug("Decoded incoming message: {0}", msg);
            if (msg.TestReqID != null)
                _connection.Send(_messageBuilder.Heartbeat(msg.TestReqID));
            if (msg.TestRespID != null)
                _watchdog.OnHeartbeat(msg.TestRespID);
            Fill fill;
            OrderState state = UpdateOrder(msg.OrigOrderID, msg.Op.Value, msg.Order.Value, out fill);
            if (!_cfg.SimulateFills) fill = msg.Fill.Value.MakeFill();
            RaiseOrderEvent(
                sendingTime: msg.SendingTime,
                state: state,
                fill: fill,
                marketData: msg.MarketData.ValueOrNull,
                accountInfo: msg.AccountInfo.ValueOrNull);
            // Finish a pending op, if there is one. Note that can belong to a different
            // order than the one we just updated. The protocol doesn't allow this but our
            // code will work just fine if it happens.
            IOrder order = _orders.FindByOpID(msg.Op.Value);
            if (order != null) order.FinishPending();
        }

        void TryTimeout(OrderOpID id, OrderStatus? statusOnTimeout)
        {
            IOrder order = _orders.FindByOpID(id);
            if (order == null) return;
            _log.Warn("OrderOp {0} timed out for order {1}", id, order);
            Fill dummy;
            RaiseOrderEvent(state: order.Update(new OrderUpdate() { Status = statusOnTimeout }, out dummy));
            // The previus call to Update may have finished the Op, so we need to check
            // whether it's still pending.
            if (order.IsPending) order.FinishPending();
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

        void RaiseOrderEvent(DateTime? sendingTime = default(DateTime?), OrderState state = null, Fill fill = null, MarketData marketData = null,
                             AccountInfo accountInfo = null)
        {
            if (_cfg.FakeTradeCount > 0 && marketData != null && marketData.Trades != null &&
                marketData.Trades.Count == _cfg.FakeTradeCount)
            {
                _log.Info("Incoming message has {0} trade(s). Probably bogus data. Ignoring it.", _cfg.FakeTradeCount);
                marketData.Trades = null;
            }
            if (state != null || fill != null || marketData != null || accountInfo != null)
            {
                var e = new OrderEvent() { SendingTime = sendingTime, State = state, Fill = fill, MarketData = marketData, AccountInfo = accountInfo };
                _log.Debug("Publishing OrderEvent: {0}", e);
                Action<OrderEvent> action = Volatile.Read(ref OnOrderEvent);
                if (action != null) action(e);
            }
        }

        bool Submit(IOrder order, NewOrderRequest request)
        {
            Assert.True(!order.IsPending);
            Assert.True(order.Status == OrderStatus.Created, "Status = {0}", order.Status);
            Mantle.Fix44.NewOrderSingle msg = _messageBuilder.NewOrderSingle(request);
            if (StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), OrderStatus.Finished)) return true;
            // We were unable to send a new order request to the exchange.
            // Notify the caller that the order is finished.
            Fill dummy;
            RaiseOrderEvent(state: order.Update(new OrderUpdate() { Status = OrderStatus.Finished }, out dummy));
            return false;
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
                return StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), null);
            }
            catch (Exception e)
            {
                if (!_disposed)
                    _log.Error(e, "Unexpected error while cancelling an order");
                return false;
            }
        }

        bool Replace(IOrder order, NewOrderRequest request, decimal quantity, decimal price, OnReplaceReject onReject)
        {
            try
            {
                if (!_cfg.ReplaceEnabled)
                {
                    switch (onReject)
                    {
                        case OnReplaceReject.Keep:
                            _log.Info("Order Replace() requested but the exchange doesn't support moves. Doing nothing.");
                            return false;
                        case OnReplaceReject.Cancel:
                            _log.Info("Order ReplaceOrCancel() requested but the exchange doesn't support moves. Just cancelling.");
                            return Cancel(order, request);
                    }
                    Debug.Assert(false, "Unreachable");
                }

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
                return StoreOp(order, msg.ClOrdID.Value, _connection.Send(msg), null);
            }
            catch (Exception e)
            {
                if (!_disposed)
                    _log.Error(e, "Unexpected error while replacing an order");
                return false;
            }
        }

        class OrderCtrl : IOrderCtrl
        {
            readonly ConnectedClient _client;
            readonly IOrder _order;
            readonly NewOrderRequest _request;

            public OrderCtrl(ConnectedClient client, IOrder order, NewOrderRequest request)
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

    class Box<T>
    {
        public T Value;

        public Box(T value = default(T))
        {
            Value = value;
        }
    }

    public class Client : IClient
    {
        static readonly Logger _log = LogManager.GetCurrentClassLogger();
        readonly ClientConfig _cfg;
        readonly IConnector _connector;

        // States:
        //   Disconnected: _client is null, _trasition is null.
        //   Connecting or Disconnecting: _client is unspecified, _transition is not null.
        //   Connected: _client is not null, transition is null.
        ConnectedClient _client = null;
        Task _transition = null;

        // Protects _client and _transition.
        readonly object _monitor = new object();

        /// <summary>
        /// Creates a new client. It starts in the "connected" state (in quotes because it may not
        /// actually be connected yet; the actual connection to the exchange is established asynchronously).
        /// </summary>
        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _connector = connector;
        }

        public void Dispose()
        {
            Disconnect().Wait();
        }

        public event Action<OrderEvent> OnOrderEvent;

        public Task<IOrderCtrl> CreateOrder(NewOrderRequest request)
        {
            lock (_monitor)
            {
                if (_client == null || _transition != null) return Task.FromResult<IOrderCtrl>(null);
                return _client.CreateOrder(request);
            }
        }

        public Task Connect()
        {
            _log.Info("Connect requested by the user");
            return Transition(() =>
            {
                if (_client != null) return;
                _client = new ConnectedClient(_cfg, _connector);
                _client.OnOrderEvent += (OrderEvent e) =>
                {
                    Action<OrderEvent> action = Volatile.Read(ref OnOrderEvent);
                    if (action != null) action(e);
                };
            });
        }

        public Task Disconnect()
        {
            _log.Info("Disconnect requested by the user");
            return Transition(() =>
            {
                if (_client == null) return;
                _client.Dispose();
                _client = null;
            });
        }

        public Task<bool> RequestMarketData(string symbol)
        {
            lock (_monitor)
            {
                if (_client == null || _transition != null) return Task.FromResult(false);
                return _client.RequestMarketData(symbol);
            }
        }

        public Task<bool> RequestAccountInfo()
        {
            lock (_monitor)
            {
                if (_client == null || _transition != null) return Task.FromResult(false);
                return _client.RequestAccountInfo();
            }
        }

        Task Transition(Action transition)
        {
            // We need the task handle on heap.
            var box = new Box<Task>();
            box.Value = new Task(() =>
            {
                // It's OK to run it without holding a lock. There is at most transition running
                // at a time, so writes don't race with each other. They also don't race with
                // reads because CreateOrder() fails fast if _transition is not null.
                transition();
                lock (_monitor)
                {
                    // If there are no transitions scheduled, set transition to null.
                    if (_transition == box.Value)
                        _transition = null;
                }
            });
            lock (_monitor)
            {
                if (_transition == null)
                {
                    box.Value.Start();
                }
                else
                {
                    _transition.ContinueWith((t) => box.Value.Start());
                }
                _transition = box.Value;
            }
            return box.Value;
        }
    }
}
