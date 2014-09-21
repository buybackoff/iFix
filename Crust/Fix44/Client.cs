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
        // Logon fields.
        public int HeartBtInt;
        public string Password;

        // Common fields for all requests.
        public string SenderCompID;
        public string TargetCompID;

        // Fields for posting, changing and cancelling orders.
        public string Account;
        public string TradingSessionID;

        // If not null, all ClOrdID in all outgoing messages
        // will have this prefix.
        public string ClOrdIDPrefix;
    }

    class OrderOp
    {
        public Order Order;
        public Object Key;
        public DurableSeqNum RefSeqNum;
        public string ClOrdID;
        public OrderStatus TargetStatus;
    }

    class OrderReport
    {
        public OrderStatus OrderStatus;
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
        OrderState _state;
        readonly List<OrderOp> _inflightOps = new List<OrderOp>();
        readonly string _firstClOrdID;
        string _lastClOrdID;
        readonly Action<OrderStateChangeEvent> _onChange;

        public Order(OrderState state, string clOrdID, Action<OrderStateChangeEvent> onChange)
        {
            _state = state;
            _firstClOrdID = clOrdID;
            _lastClOrdID = clOrdID;
            _onChange = onChange;
        }

        public OrderStatus TargetStatus
        {
            get { return _inflightOps.Count > 0 ? _inflightOps[_inflightOps.Count - 1].TargetStatus : _state.Status; }
        }

        public void OnSent(OrderOp op)
        {
            _inflightOps.Add(op);
        }

        // The op is not null if it's a reply to one of our requests.
        // Request status makes sense only when op is not null.
        // OrderReport may be null. This happens, for example, when we get Reject<3> to our request.
        public OrderStateChangeEvent OnReceived(OrderOp op, RequestStatus requestStatus, OrderReport report)
        {
            OrderState oldState = _state;
            _state = NewState(oldState, report);
            OrderStateChangeEvent e = new OrderStateChangeEvent()
            {
                NewState = _state,
                Fill = MakeFill(oldState, _state, report),
            };
            if (op != null)
            {
                _inflightOps.Remove(op);
                e.FinishedRequestKey = op.Key;
                e.FinishedRequestStatus = requestStatus;
                // When Submit() request fails, transition to OrderStatus.Finished.
                if (requestStatus == RequestStatus.Error && _inflightOps.Count == 0 && _state.Status == OrderStatus.Created)
                    _state.Status = OrderStatus.Finished;
            }
            return e;
        }

        // Can we remove this order from memory? It's safe to do if we can't send any
        // useful requests to the exchange for this order (the order is in its terminal state)
        // and we we don't expect any messages from the exchange for it (all pending operations
        // have been completed).
        public bool Done()
        {
            return _state.Status == OrderStatus.Finished && _inflightOps.Count == 0;
        }

        // ClOrdID of the New Order Request.
        public string FirstClOrdID { get { return _firstClOrdID; } }
        // The currently assigned ClOrdID. Initially it's the same as FirstClOrdID but then
        // it diverges (e.g., when the order is replaced).
        public string LastClOrdID { get { return _lastClOrdID; } set { _lastClOrdID = value; } }
        public Action<OrderStateChangeEvent> OnChange { get { return _onChange; } }

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

    class OrderHeap
    {
        Dictionary<DurableSeqNum, OrderOp> _opsBySeqNum = new Dictionary<DurableSeqNum, OrderOp>();
        Dictionary<string, OrderOp> _opsByOrdID = new Dictionary<string, OrderOp>();
        Dictionary<string, Order> _ordersByOrdID = new Dictionary<string, Order>();

        public void Add(Order order, OrderOp op)
        {
            _ordersByOrdID[order.FirstClOrdID] = order;
            _opsBySeqNum[op.RefSeqNum] = op;
            _opsByOrdID[op.ClOrdID] = op;
        }

        public void Match(DurableSeqNum refSeqNum, string clOrdID, string origClOrdID, out OrderOp op, out Order order)
        {
            // Any request is finished when we receive Reject with the matching RefSeqNum or
            // Execution Report with the matching ClOrdID.
            // Additionally, Cancel is done when we receive Order Cancel Reject with the
            // matching ClOrdID.
            //
            // An Execution Report matches an order if:
            // 1. ClOrdID is equal to the first or the last ClOrdID of the order.
            // 2. OrigClOrdID is equal to the first or the last ClOrdID of the order.
            op = null;
            order = null;

            if (refSeqNum != null && _opsBySeqNum.TryGetValue(refSeqNum, out op) ||
                clOrdID != null && _opsByOrdID.TryGetValue(clOrdID, out op))
            {
                order = op.Order;
            }
            else if (clOrdID != null && _ordersByOrdID.TryGetValue(clOrdID, out order) ||
                origClOrdID != null && _ordersByOrdID.TryGetValue(origClOrdID, out order))
            {
            }

            // If the exchange is assigning a new ClOrdID to the order, remember it.
            if (clOrdID != null && origClOrdID != null && clOrdID != origClOrdID && order != null)
            {
                if (order.LastClOrdID != order.FirstClOrdID)
                    _ordersByOrdID.Remove(order.LastClOrdID);
                order.LastClOrdID = clOrdID;
                _ordersByOrdID[order.LastClOrdID] = order;
            }
        }

        public void Finish(OrderOp op, Order order)
        {
            if (op != null)
            {
                _opsByOrdID.Remove(op.ClOrdID);
                _opsBySeqNum.Remove(op.RefSeqNum);
            }
            if (order != null && order.Done())
            {
                _ordersByOrdID.Remove(order.FirstClOrdID);
                _ordersByOrdID.Remove(order.LastClOrdID);
            }
        }
    }

    class ClOrdIDGenerator
    {
        readonly string _prefix;
        readonly Object _monitor = new Object();
        long _last = 0;

        public ClOrdIDGenerator(string prefix)
        {
            _prefix = prefix == null ? "" : prefix + "-";
            _prefix += SessionID();
            _prefix += "-";
        }

        public string GenerateID()
        {
            lock (_monitor)
            {
                return _prefix + (++_last).ToString();
            }
        }

        static string SessionID()
        {
            return ((long)new TimeSpan(DateTime.Now.Ticks - new DateTime(2014, 1, 1).Ticks).TotalSeconds).ToString();
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

        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _connection = new DurableConnection(new InitializingConnector(connector, Logon));
            _clOrdIDGenerator = new ClOrdIDGenerator(cfg.ClOrdIDPrefix);
            _reciveLoop = new Task(ReceiveLoop);
            _reciveLoop.Start();
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
            return new OrderCtrl(this, new Order(state, _clOrdIDGenerator.GenerateID(), onChange), request);
        }

        public void Dispose()
        {
            _connection.Dispose();
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
                if (msg.ClOrdID.HasValue)
                    HandleMessage(null, msg.ClOrdID.Value, null, RequestStatus.Error, null);
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
                    reqStatus = msg.ExecType.Value == '8' ? RequestStatus.Error : RequestStatus.OK;
                OrderReport report = new OrderReport();
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
                HandleMessage(null, msg.ClOrdID.Value, msg.OrigClOrdID.Value, reqStatus, report);
                return null;
            }

            void HandleMessage(DurableSeqNum refSeqNum, string clOrdID, string origClOrdID,
                               RequestStatus status, OrderReport report)
            {
                OrderStateChangeEvent e = null;
                Action<OrderStateChangeEvent> onChange = null;
                lock (_client._monitor)
                {
                    OrderOp op;
                    Order order;
                    _client._orders.Match(refSeqNum, clOrdID, origClOrdID, out op, out order);
                    if (order != null)
                    {
                        e = order.OnReceived(op, status, report);
                        onChange = order.OnChange;
                    }
                    _client._orders.Finish(op, order);
                }
                if (e != null) onChange.Invoke(e);
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
                if (order.TargetStatus != OrderStatus.Created) return false;
                var msg = new Mantle.Fix44.NewOrderSingle() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = order.FirstClOrdID;
                msg.Account.Value = _cfg.Account;
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
                    TargetStatus = OrderStatus.Accepted,
                };
                _orders.Add(order, op);
                order.OnSent(op);
                return true;
            }
        }

        bool Cancel(Order order, Object requestKey, NewOrderRequest request)
        {
            lock (_monitor)
            {
                if (order.TargetStatus <= OrderStatus.Created || order.TargetStatus >= OrderStatus.TearingDown) return false;
                var msg = new Mantle.Fix44.OrderCancelRequest() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
                msg.OrigClOrdID.Value = order.FirstClOrdID;
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
                    TargetStatus = OrderStatus.Finished,
                };
                _orders.Add(order, op);
                order.OnSent(op);
                return true;
            }
        }

        bool Replace(Order order, Object requestKey, NewOrderRequest request, decimal quantity, decimal price)
        {
            lock (_monitor)
            {
                if (order.TargetStatus != OrderStatus.Accepted) return false;
                var msg = new Mantle.Fix44.OrderCancelReplaceRequest() { StandardHeader = MakeHeader() };
                msg.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
                msg.OrigClOrdID.Value = order.FirstClOrdID;
                msg.Account.Value = _cfg.Account;
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
                    TargetStatus = OrderStatus.Accepted,
                };
                _orders.Add(order, op);
                order.OnSent(op);
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
