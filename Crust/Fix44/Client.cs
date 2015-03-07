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
        public int HeartBtInt;
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
        /// Recommended value: 60.
        /// </summary>
        public double RequestTimeoutSeconds;
    }

    class OrderOp
    {
        public Order Order;
        // Key as it was passed to the corresponding method of IOrderCtrl.
        public Object Key;
        public DurableSeqNum RefSeqNum;
        public string ClOrdID;
        // Time when the request was sent to the exchange.
        public DateTime SendTime;
    }

    class OrderReport
    {
        public OrderStatus OrderStatus;
        // May be null. If not null, and not equal to the previous ID of the order,
        // it is the new ID.
        public string OrderID;
        // Set only for limit orders.
        public decimal? Price;
        // Not set if OrdStatus is Pending Cancel.
        public decimal? LeavesQuantity;
        // Not set if OrdStatus is Pending Cancel.
        public decimal? CumFillQuantity;
        // These two fields are set on Fill reports.
        public decimal? FillQuantity;
        public decimal? FillPrice;
    }

    class Order
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        OrderState _state;
        // If order status is Created, the id is null.
        // If it's Finished, it may be null.
        // Otherwise not null. Once it becomes non-null, it can't become null again.
        // OrderID is assigned when the order is accepted. After that it changes
        // on each successful order replacement.
        // TODO: ensure that the guarantee described above (esp the not-null part) holds.
        string _orderID;
        // Did we send a request affecting this order to which we expect a reply?
        bool _pending = false;
        readonly Action<OrderStateChangeEvent> _onChange;
        // The last time when we either received status for this order from the exchange
        // or sent any requests for it.
        //
        // Initialized to MaxValue so that we don't request status of this order
        // from the exchange until we send the first request for it.
        DateTime _lastActivityTime = DateTime.MaxValue;

        public Order(OrderState state, Action<OrderStateChangeEvent> onChange)
        {
            _state = state;
            _onChange = onChange;
        }

        public bool Pending { get { return _pending; } }

        public string OrderID { get { return _orderID; } }

        public void OnSent(bool pending)
        {
            _lastActivityTime = DateTime.UtcNow;
            if (pending)
            {
                Debug.Assert(!_pending);
                _pending = true;
            }
        }

        // Request is set if it's a reply to out requests (the one that was pending).
        // OrderReport may be null. This happens, for example, when we get Reject<3> to our request.
        public void OnReceived(RequestStatus? requestStatus, OrderReport report)
        {
            _state = NewState(_state, report);
            if (requestStatus.HasValue)
            {
                Debug.Assert(_pending);
                _pending = false;
                // When Submit() request fails or times out, transition to OrderStatus.Finished.
                // Ideally, when Submit() times out, it would be nice to ask the exchange for the status
                // MOEX doesn't support status query by ClOrdID, so we can't really do that.
                // Let's assume that the order is dead.
                if (requestStatus != RequestStatus.OK && _state.Status == OrderStatus.Created)
                    _state.Status = OrderStatus.Finished;
            }
            if (report != null)
            {
                _lastActivityTime = DateTime.UtcNow;
                if (report.OrderID != null && report.OrderID != _orderID)
                {
                    _log.Info("OrderID has changed: {0} => {1}", _orderID, report.OrderID);
                    _orderID = report.OrderID;
                }
            }
        }

        public OrderState State { get { return _state; } }

        // Can we remove this order from memory? It's safe to do if we can't send any
        // useful requests to the exchange for this order (the order is in its terminal state)
        // and we we don't expect any messages from the exchange for it (all pending operations
        // have been completed).
        public bool Done()
        {
            return _state.Status == OrderStatus.Finished && !_pending;
        }

        public Action<OrderStateChangeEvent> OnChange { get { return _onChange; } }

        public DateTime LastActivityTime
        {
            get
            {
                return _state.Status == OrderStatus.Finished ? DateTime.MaxValue : _lastActivityTime;
            }
        }

        static OrderState NewState(OrderState old, OrderReport report)
        {
            if (report == null) return old;
            return new OrderState()
            {
                Status = report.OrderStatus,
                LeftQuantity = report.LeavesQuantity.HasValue ? report.LeavesQuantity.Value : old.LeftQuantity,
                FilledQuantity = report.CumFillQuantity.HasValue ? report.CumFillQuantity.Value : old.FilledQuantity,
                Price = report.Price.HasValue ? report.Price.Value : old.Price,
            };
        }
    }

    class OrderHeap
    {
        Dictionary<DurableSeqNum, OrderOp> _opsBySeqNum = new Dictionary<DurableSeqNum, OrderOp>();
        Dictionary<string, OrderOp> _opsByClOrdID = new Dictionary<string, OrderOp>();
        BiDictionary<Order, string> _ordersByOrderID = new BiDictionary<Order, string>();
        SortedPropertyDictionary<Order, DateTime> _ordersByLastActivityTime = new SortedPropertyDictionary<Order, DateTime>();
        SortedPropertyDictionary<OrderOp, DateTime> _opsBySendTime = new SortedPropertyDictionary<OrderOp, DateTime>();

        public void Add(Order order, OrderOp op)
        {
            _opsBySeqNum.Add(op.RefSeqNum, op);
            _opsByClOrdID.Add(op.ClOrdID, op);
            _ordersByLastActivityTime.Update(order, order.LastActivityTime);
            _opsBySendTime.Add(op, op.SendTime);
        }

        public OrderOp FindOp(DurableSeqNum refSeqNum, string clOrdID)
        {
            // A request is finished when we receive Reject with the matching RefSeqNum or
            // Execution Report with the matching ClOrdID.
            // Additionally, Cancel is done when we receive Order Cancel Reject with the
            // matching ClOrdID.
            OrderOp res;
            if (refSeqNum != null && _opsBySeqNum.TryGetValue(refSeqNum, out res)) return res;
            if (clOrdID != null && _opsByClOrdID.TryGetValue(clOrdID, out res)) return res;
            return null;
        }

        public Order FindOrder(string orderID)
        {
            // An Execution Report matches an order if either:
            // 1. It matches a request for this order. In other words, if FindOp() returns an op,
            //    it links to the order. In this case FindOrder() doesn't need to be called.
            // 2. OrderID matches.
            Order res;
            if (orderID != null && _ordersByOrderID.BySecond.TryGetValue(orderID, out res)) return res;
            return null;
        }

        public void Finish(OrderOp op, Order order)
        {
            if (op != null)
            {
                _opsByClOrdID.Remove(op.ClOrdID);
                _opsBySeqNum.Remove(op.RefSeqNum);
                _opsBySendTime.Remove(op);
            }
            if (order != null)
            {
                if (order.Done())
                {
                    _ordersByOrderID.ByFirst.Remove(order);
                    _ordersByLastActivityTime.Remove(order);
                }
                else
                {
                    _ordersByLastActivityTime.Update(order, order.LastActivityTime);
                    if (order.OrderID != null)
                        _ordersByOrderID.ByFirst[order] = order.OrderID;
                }
            }
        }

        // Returns the order for which the last activity was receives the most
        // amount of time ago, or null if there are no orders.
        public Order MostQuietOrder()
        {
            if (_ordersByLastActivityTime.Count == 0) return null;
            Order order;
            DateTime time;
            _ordersByLastActivityTime.SmallestProperty(out order, out time);
            return order;
        }

        // Returns the oldest op, or null if there are no ops.
        public OrderOp OldestOp()
        {
            if (_opsBySendTime.Count == 0) return null;
            OrderOp op;
            DateTime time;
            _opsBySendTime.SmallestProperty(out op, out time);
            return op;
        }
    }

    class ClOrdIDGenerator
    {
        readonly string _prefix;
        readonly Object _monitor = new Object();
        uint _last = 0;

        public ClOrdIDGenerator(string prefix)
        {
            _prefix = prefix == null ? "" : prefix;
            _prefix += SessionID();
        }

        public string GenerateID()
        {
            uint n;
            lock (_monitor) { n = ++_last; }
            // 32 bit integer can be encoded as 6 base64 characters, plus 2 characters for padding.
            // We don't need the padding.
            return _prefix + System.Convert.ToBase64String(BitConverter.GetBytes(n)).Substring(0, 6);
        }

        static string SessionID()
        {
            var t = DateTime.UtcNow;
            // The session ID is time of the startup measured in the number
            // of second since midnight. If two instances of the app
            // are launched within a second or less of each other, they'll
            // have the same ID.
            uint sec = (uint)(t.Hour * 3600 + t.Minute * 60 + t.Second);
            // Note that sec has no more than 18 bits set. It's actually even less
            // than that, but we only care that it's not more than 18, so that it can
            // be encoded as 3 base64 characters.
            byte[] bytes = BitConverter.GetBytes(sec);
            for (int i = 0; i != bytes.Length; ++i)
            {
                bytes[i] = ReverseBits(bytes[i]);
            }
            return System.Convert.ToBase64String(bytes).Substring(0, 3);
        }

        static byte ReverseBits(byte b)
        {
            byte res = 0;
            for (int i = 0; i != 8; ++i)
            {
                res <<= 1;
                res |= (byte)(b & 1);
                b >>= 1;
            }
            return res;
        }
    }

    public class Client : IClient
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly ClientConfig _cfg;
        readonly DurableConnection _connection;
        readonly ClOrdIDGenerator _clOrdIDGenerator;
        readonly OrderHeap _orders = new OrderHeap();
        readonly Object _monitor = new Object();
        readonly Task _reciveLoop;
        readonly Task _syncLoop;
        volatile bool _stopping = false;
        readonly Object _onChangeMonitor = new Object();

        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _connection = new DurableConnection(new InitializingConnector(connector, Logon));
            _clOrdIDGenerator = new ClOrdIDGenerator(cfg.ClOrdIDPrefix);
            _reciveLoop = new Task(ReceiveLoop);
            _reciveLoop.Start();
            _syncLoop = new Task(SyncLoop);
            _syncLoop.Start();
            // TODO: send test request every 30 seconds.
        }

        public IOrderCtrl CreateOrder(NewOrderRequest request, Action<OrderStateChangeEvent> onChange)
        {
            var state = new OrderState
            {
                Status = OrderStatus.Created,
                LeftQuantity = request.Quantity,
                Price = request.Price,
                FilledQuantity = 0,
            };
            return new OrderCtrl(this, new Order(state, onChange), request);
        }

        public void CancelAllOrders()
        {
            var msg = new Mantle.Fix44.OrderMassCancelRequest() { StandardHeader = MakeHeader() };
            msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
            msg.Account.Value = _cfg.Account;
            msg.TransactTime.Value = msg.StandardHeader.SendingTime.Value;
            _connection.Send(msg);
        }

        public void Dispose()
        {
            _stopping = true;
            _connection.Dispose();
            try { _syncLoop.Wait(); }
            catch (Exception) { }
            try { _reciveLoop.Wait(); }
            catch (Exception) { }
        }

        async void ReceiveLoop()
        {
            while (true)
            {
                DurableMessage msg = await _connection.Receive();
                if (msg == null)
                    return;
                try
                {
                    ((Mantle.Fix44.IServerMessage)msg.Message).Visit(new MessageVisitor(this, msg.SessionID));
                }
                catch (Exception e)
                {
                    if (_log.IsErrorEnabled)
                        _log.Error(String.Format("Failed to handle a message received from the exchange: {0}.", msg.Message), e);
                }
            }
        }

        async void SyncLoop()
        {
            for (; !_stopping; await Task.Delay(1000))  // 1 second sleep at the end
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    if (_cfg.RequestTimeoutSeconds > 0)
                        while (TryExpireOp(now)) { }
                }
                catch (Exception e)
                {
                    _log.Error("Unexpected exception", e);
                }
            }
        }

        bool TryExpireOp(DateTime now)
        {
            OrderStateChangeEvent e = null;
            Action<OrderStateChangeEvent> onChange = null;
            lock (_monitor)
            {
                OrderOp op = _orders.OldestOp();
                if (op == null || (now - op.SendTime).TotalSeconds < _cfg.RequestTimeoutSeconds)
                    return false;
                _log.Warn("FIX request with ClOrdID '{0}' and Key '{1}' for order '{2}' timed out",
                          op.ClOrdID, op.Key, op.Order.OrderID);
                op.Order.OnReceived(RequestStatus.Unknown, null);
                onChange = op.Order.OnChange;
                e = new OrderStateChangeEvent()
                {
                    NewState = op.Order.State,
                    FinishedRequestKey = op.Key,
                    FinishedRequestStatus = RequestStatus.Unknown,
                };
                _orders.Finish(op, op.Order);
            }
            try
            {
                if (onChange != null)
                {
                    _log.Info("Publishing OrderStateChangeEvent: {0}", e);
                    // TODO: there is a race in here. OrderStateChangeEvent may be sent out of order.
                    lock (_onChangeMonitor) { onChange.Invoke(e); }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Event handler failed", ex);
            }
            return true;
        }

        class MessageVisitor : Mantle.Fix44.IServerMessageVisitor<Object>
        {
            private static readonly Logger _log = LogManager.GetCurrentClassLogger();

            readonly long _sessionID;
            readonly Client _client;

            public MessageVisitor(Client client, long sessionID)
            {
                _sessionID = sessionID;
                _client = client;
            }

            public Object Visit(Mantle.Fix44.Logon msg) { return null; }
            public Object Visit(Mantle.Fix44.Heartbeat msg) { return null; }
            public Object Visit(Mantle.Fix44.SequenceReset msg) { return null; }
            public Object Visit(Mantle.Fix44.ResendRequest msg) { return null; }
            public Object Visit(Mantle.Fix44.OrderMassCancelReport msg) { return null; }

            public Object Visit(Mantle.Fix44.TestRequest msg)
            {
                var heartbeat = new Mantle.Fix44.Heartbeat() { StandardHeader = _client.MakeHeader() };
                heartbeat.TestReqID.Value = msg.TestReqID.Value;
                _client._connection.Send(heartbeat);
                return null;
            }

            public Object Visit(Mantle.Fix44.Reject msg)
            {
                if (msg.RefSeqNum.HasValue)
                {
                    var seqNum = new DurableSeqNum { SessionID = _sessionID, SeqNum = msg.RefSeqNum.Value };
                    HandleMessage(seqNum, null, null, RequestStatus.Error, null);
                }
                return null;
            }

            public Object Visit(Mantle.Fix44.OrderCancelReject msg)
            {
                if (!msg.ClOrdID.HasValue) return null;

                OrderReport report = null;
                if (msg.CxlRejReason.HasValue && msg.CxlRejReason.Value == 1)
                    report = new OrderReport { OrderStatus = OrderStatus.Finished };
                HandleMessage(null, msg.ClOrdID.Value, null, RequestStatus.Error, report);
                return null;
            }

            public Object Visit(Mantle.Fix44.ExecutionReport msg)
            {
                if (!msg.OrdStatus.HasValue)
                {
                    _log.Error("Message unexpectedly missing OrdStatus: {0}", msg);
                    return null;
                }
                RequestStatus reqStatus = RequestStatus.Unknown;
                if (msg.ExecType.HasValue)
                {
                    if (msg.ExecType.Value == '8')
                        reqStatus = RequestStatus.Error;
                    else if (msg.ExecType.Value != 'I')
                        reqStatus = RequestStatus.OK;
                }
                OrderReport report = new OrderReport();
                if (msg.OrderID.HasValue)
                    report.OrderID = msg.OrderID.Value;
                switch (msg.OrdStatus.Value)
                {
                    case '0': report.OrderStatus = OrderStatus.Accepted; break;
                    case '1': report.OrderStatus = OrderStatus.PartiallyFilled; break;
                    case '2': report.OrderStatus = OrderStatus.Finished; break;
                    case '4': report.OrderStatus = OrderStatus.Finished; break;
                    case '6': report.OrderStatus = OrderStatus.TearingDown; break;
                    case '8': report.OrderStatus = OrderStatus.Finished; break;
                    case '9': report.OrderStatus = OrderStatus.Finished; break;
                    case 'E': report.OrderStatus = OrderStatus.Accepted; break;
                    default:
                        _log.Error("Unexpected OrdStatus: {0}", msg);
                        return null;
                }
                if (msg.Price.HasValue && msg.Price.Value > 0)
                    report.Price = msg.Price.Value;
                if (msg.LeavesQty.HasValue && msg.OrdStatus.Value != '6')
                    report.LeavesQuantity = msg.LeavesQty.Value;
                if (msg.CumQty.HasValue && msg.CumQty.Value > 0)
                    report.CumFillQuantity = msg.CumQty.Value;
                if (msg.LastQty.HasValue && msg.LastQty.Value > 0)
                {
                    report.FillQuantity = msg.LastQty.Value;
                    if (msg.LastPx.HasValue && msg.LastPx.Value > 0)
                        report.FillPrice = msg.LastPx.Value;
                }
                HandleMessage(null, msg.ClOrdID.Value, msg.OrigOrderID.Value, reqStatus, report);
                return null;
            }

            void HandleMessage(DurableSeqNum refSeqNum, string clOrdID, string origOrderID,
                               RequestStatus status, OrderReport report)
            {
                OrderStateChangeEvent e = null;
                Action<OrderStateChangeEvent> onChange = null;
                lock (_client._monitor)
                {
                    // Try to match an OrderOp. If successful, we also have the order.
                    // Otherwise if OrigOrderID is set, use it to find the order.
                    // Else use OrderID.
                    OrderOp op = _client._orders.FindOp(refSeqNum, clOrdID);
                    Order order = op != null ? op.Order : _client._orders.FindOrder(origOrderID ?? report.OrderID);

                    if (op != null)
                    {
                        switch (status)
                        {
                            case RequestStatus.Error:
                                _log.Warn("FIX request with ClOrdID '{0}' and key '{1}' for order '{2}' failed",
                                          op.ClOrdID, op.Key, order.OrderID);
                                break;
                            case RequestStatus.Unknown:
                                _log.Error("Can't make sense of response to request with ClOrdID '{0}' and Key '{1}' for order '{2}'. " +
                                           "Treating it the same as timeout.", op.ClOrdID, op.Key, order.OrderID);
                                break;
                            case RequestStatus.OK:
                                _log.Info("FIX request with ClOrdID '{0}' and key '{1}' for order '{2}' succeeded",
                                          op.ClOrdID, op.Key, order.OrderID);
                                break;
                        }
                    }

                    if (report != null && report.FillQuantity.HasValue && report.FillQuantity.Value > 0 && order == null)
                    {
                        // We've received a fill notification for an unknown order. We don't expect
                        // such things to happen. This can cause the internal position to go out of
                        // sync with the real position.
                        _log.Error("Unmatched fill report. Internal position may be out of sync.");
                    }
                    if (order != null)
                    {
                        OrderState oldState = order.State;
                        order.OnReceived(op == null ? null : (RequestStatus?)status, report);
                        e = new OrderStateChangeEvent()
                        {
                            NewState = order.State,
                            Fill = MakeFill(oldState, order.State, report),
                        };
                        if (op != null)
                        {
                            e.FinishedRequestKey = op.Key;
                            e.FinishedRequestStatus = status;
                        }
                        onChange = order.OnChange;
                    }
                    _client._orders.Finish(op, order);
                }
                if (onChange != null)
                {
                    _log.Info("Publishing OrderStateChangeEvent: {0}", e);
                    lock (_client._onChangeMonitor) { onChange.Invoke(e); }
                }
            }

            static Fill MakeFill(OrderState oldState, OrderState newState, OrderReport report)
            {
                decimal qty = newState.FilledQuantity - oldState.FilledQuantity;
                if (qty <= 0) return null;
                if (report.FillPrice.HasValue && report.FillQuantity.HasValue && qty == report.FillQuantity.Value)
                    return new Fill() { Price = report.FillPrice.Value, Quantity = qty };
                else
                    return new Fill() { Quantity = qty };
            }
        }

        void Logon(IConnection session, CancellationToken cancellationToken)
        {
            var logon = new Mantle.Fix44.Logon() { StandardHeader = MakeHeader() };
            logon.EncryptMethod.Value = 0;
            logon.HeartBtInt.Value = _cfg.HeartBtInt;
            logon.Password.Value = _cfg.Password;
            logon.ResetSeqNumFlag.Value = true;
            session.Send(logon);
            if (!(session.Receive(cancellationToken).Result is Mantle.Fix44.Logon))
                throw new UnexpectedMessageReceived("Expected Logon");
        }

        Mantle.Fix44.StandardHeader MakeHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = _cfg.SenderCompID;
            res.TargetCompID.Value = _cfg.TargetCompID;
            res.SendingTime.Value = DateTime.UtcNow;
            return res;
        }

        bool Submit(Order order, Object requestKey, NewOrderRequest request)
        {
            // The lock is solely to avoid receiving a reply before we put the order
            // in the order heap. It would be nice to put it in the heap before sending
            // and thus avoid holding the lock while doing the I/O, but it's not an easy
            // thing to do because we don't have the SeqNum until the message is sent.
            lock (_monitor)
            {
                if (order.State.Status != OrderStatus.Created || order.Pending) return false;
                var msg = new Mantle.Fix44.NewOrderSingle() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
                msg.Account.Value = _cfg.Account;
                if (_cfg.PartyID != null)
                {
                    var party = new Mantle.Fix44.Party();
                    party.PartyID.Value = _cfg.PartyID;
                    party.PartyIDSource.Value = _cfg.PartyIDSource;
                    party.PartyRole.Value = _cfg.PartyRole;
                    msg.PartyGroup.Add(party);
                }
                msg.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = _cfg.TradingSessionID });
                msg.Instrument.Symbol.Value = request.Symbol;
                msg.Side.Value = request.Side == Side.Buy ? '1' : '2';
                msg.TransactTime.Value = msg.StandardHeader.SendingTime.Value;
                msg.OrderQtyData.OrderQty.Value = request.Quantity;
                msg.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
                if (request.Price.HasValue)
                    msg.Price.Value = request.Price.Value;

                DurableSeqNum seqNum = _connection.Send(msg);
                if (seqNum == null) return false;

                OrderOp op = new OrderOp
                {
                    ClOrdID = msg.ClOrdID.Value,
                    Key = requestKey,
                    RefSeqNum = seqNum,
                    Order = order,
                    SendTime = DateTime.UtcNow,
                };
                _orders.Add(order, op);
                order.OnSent(pending:true);
                return true;
            }
        }

        bool Cancel(Order order, Object requestKey, NewOrderRequest request)
        {
            lock (_monitor)
            {
                if (order.Pending) return false;
                if (order.OrderID == null) return false;  // This means the order is in state Created.
                if (order.State.Status == OrderStatus.Finished) return false;
                if (order.State.Status == OrderStatus.TearingDown) {
                    if (_cfg.RequestTimeoutSeconds <= 0)
                        return false;
                    if ((DateTime.UtcNow - order.LastActivityTime).TotalSeconds < _cfg.RequestTimeoutSeconds)
                        return false;
                }
                var msg = new Mantle.Fix44.OrderCancelRequest() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
                msg.OrigClOrdID.Value = msg.ClOrdID.Value;  // It's required but ignored.
                msg.OrderID.Value = order.OrderID;
                msg.Side.Value = request.Side == Side.Buy ? '1' : '2';
                msg.TransactTime.Value = msg.StandardHeader.SendingTime.Value;

                DurableSeqNum seqNum = _connection.Send(msg);
                if (seqNum == null) return false;

                OrderOp op = new OrderOp
                {
                    ClOrdID = msg.ClOrdID.Value,
                    Key = requestKey,
                    RefSeqNum = seqNum,
                    Order = order,
                    SendTime = DateTime.UtcNow,
                };
                _orders.Add(order, op);
                order.OnSent(pending:true);
                return true;
            }
        }

        bool Replace(Order order, Object requestKey, NewOrderRequest request, decimal quantity, decimal price)
        {
            lock (_monitor)
            {
                if (order.Pending) return false;
                if (order.State.Status != OrderStatus.Accepted) return false;
                var msg = new Mantle.Fix44.OrderCancelReplaceRequest() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
                msg.OrigClOrdID.Value = msg.ClOrdID.Value;
                msg.OrderID.Value = order.OrderID;
                msg.Account.Value = _cfg.Account;
                if (_cfg.PartyID != null)
                {
                    var party = new Mantle.Fix44.Party();
                    party.PartyID.Value = _cfg.PartyID;
                    party.PartyIDSource.Value = _cfg.PartyIDSource;
                    party.PartyRole.Value = _cfg.PartyRole;
                    msg.PartyGroup.Add(party);
                }
                msg.Instrument.Symbol.Value = request.Symbol;
                msg.Price.Value = price;
                msg.OrderQty.Value = quantity;
                msg.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = _cfg.TradingSessionID });
                msg.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
                msg.Side.Value = request.Side == Side.Buy ? '1' : '2';
                msg.TransactTime.Value = msg.StandardHeader.SendingTime.Value;

                DurableSeqNum seqNum = _connection.Send(msg);
                if (seqNum == null) return false;

                OrderOp op = new OrderOp
                {
                    ClOrdID = msg.ClOrdID.Value,
                    Key = requestKey,
                    RefSeqNum = seqNum,
                    Order = order,
                    SendTime = DateTime.UtcNow,
                };
                _orders.Add(order, op);
                order.OnSent(pending:true);
                return true;
            }
        }

        class OrderCtrl : IOrderCtrl
        {
            readonly Client _client;
            readonly Order _order;
            readonly NewOrderRequest _request;

            public OrderCtrl(Client client, Order order, NewOrderRequest request)
            {
                _client = client;
                _order = order;
                _request = request;
            }

            public bool Submit(Object requestKey)
            {
                return _client.Submit(_order, requestKey, _request);
            }

            public bool Cancel(Object requestKey)
            {
                return _client.Cancel(_order, requestKey, _request);
            }

            public bool Replace(Object requestKey, decimal quantity, decimal price)
            {
                return _client.Replace(_order, requestKey, _request, quantity, price);
            }
        }
    }
}
