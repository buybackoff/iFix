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

    class ClientConfig
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
    }

    class OrderOp
    {
        public Order Order;
        public Object Key;
        public DurableSeqNum RefSeqNum;
        public string ClOrdID;
        public OrderStatus TargetStatus;
    }

    enum ExecType
    {
        New,
        Cancelled,
        Replace,
        PendingCancel,
        Rejected,
        Trade,
        Other,
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
        readonly string _origClOrdID;
        readonly Action<OrderStateChangeEvent> _onChange;

        public Order(OrderState state, string origClOrdID, Action<OrderStateChangeEvent> onChange)
        {
            _state = state;
            _origClOrdID = origClOrdID;
            _onChange = onChange;
        }

        public OrderStatus TargetStatus
        {
            get { return _inflightOps.Count > 0 ? _inflightOps[_inflightOps.Count - 1].TargetStatus : _state.Status; }
        }

        /*public bool CanSend(OrderOpType op)
        {
            OrderStatus expectedStatus =
                _state.PendingStatus.HasValue ? _state.PendingStatus.Value : _state.Status;
            switch (op)
            {
                case OrderOpType.Submit:
                    return expectedStatus == OrderStatus.Created;
                case OrderOpType.Cancel:
                    return expectedStatus == OrderStatus.Accepted || expectedStatus == OrderStatus.PartiallyFilled;
                case OrderOpType.Replace:
                    return _orderType == OrderType.Limit && expectedStatus == OrderStatus.Accepted;
            }
            throw new ArgumentOutOfRangeException("op", op, "Unexpected value of OrderOpType");
        }*/

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
        public string OrigClOrdID { get { return _origClOrdID; } }
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
                return new Fill() { Price = report.Price.Value, Quantity = qty };
            else
                return new Fill() { Quantity = qty };
        }
    }

    class OrderHeap
    {
        Dictionary<DurableSeqNum, OrderOp> _opsBySeqNum = new Dictionary<DurableSeqNum, OrderOp>();
        Dictionary<string, OrderOp> _opsByOrdID = new Dictionary<string, OrderOp>();
        Dictionary<string, Order> _ordersByOrigOrdID = new Dictionary<string, Order>();

        public void Add(Order order, OrderOp op)
        {
            _ordersByOrigOrdID[order.OrigClOrdID] = order;
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
            // 1. ClOrdID is equal to the ClOrdID of the message that created the order.
            // 2. OrigClOrdID is equal to the ClOrdID of the message that created the order.
            op = null;
            order = null;

            if (refSeqNum != null && _opsBySeqNum.TryGetValue(refSeqNum, out op) ||
                clOrdID != null && _opsByOrdID.TryGetValue(clOrdID, out op))
            {
                order = op.Order;
            }
            else if (origClOrdID != null)
            {
                _ordersByOrigOrdID.TryGetValue(origClOrdID, out order);
            }
        }

        public void Finish(OrderOp op, Order order)
        {
            if (op != null)
            {
                _opsByOrdID.Remove(op.ClOrdID);
                _opsBySeqNum.Remove(op.RefSeqNum);
            }
            if (order != null && order.Done()) _ordersByOrigOrdID.Remove(order.OrigClOrdID);
        }
    }

    static class ClOrdIDGenerator
    {
        static readonly string _prefix = SessionID() + "-";
        static readonly Object _monitor = new Object();
        static long _last = 0;

        public static string GenerateID()
        {
            lock (_monitor)
            {
                return _prefix + (++_last).ToString();
            }
        }

        static string SessionID()
        {
            return new TimeSpan(DateTime.Now.Ticks - new DateTime(2014, 1, 1).Ticks).Seconds.ToString();
        }
    }

    class Client : IClient
    {
        readonly ClientConfig _cfg;
        readonly DurableConnection _connection;
        readonly OrderHeap _orders = new OrderHeap();
        readonly Object _monitor = new Object();
        readonly Task _reciveLoop;

        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _connection = new DurableConnection(new InitializingConnector(connector, Logon));
            new Task(ReceiveLoop).Start();
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
            return new OrderCtrl(this, new Order(state, ClOrdIDGenerator.GenerateID(), onChange), request);
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
                catch (Exception)
                {
                    // TODO: log.
                }
            }
        }

        class MessageVisitor : Mantle.Fix44.IServerMessageVisitor<Object>
        {
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
                // TODO: implement me.
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
                    if (order != null && op != null)
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
            res.SendingTime.Value = DateTime.Now;
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
                msg.ClOrdID.Value = ClOrdIDGenerator.GenerateID();
                msg.Account.Value = _cfg.Account;
                msg.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = _cfg.TradingSessionID });
                msg.Instrument.Symbol.Value = request.Symbol;
                msg.Side.Value = request.Side == Side.Buy ? '1' : '2';
                msg.TransactTime.Value = DateTime.Now;
                msg.OrderQtyData.OrderQty.Value = request.Quantity;
                msg.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
                if (request.Price.HasValue)
                    msg.Price.Value = request.Price.Value;

                DurableSeqNum seqNum = _connection.Send(msg);
                if (seqNum == null) return false;

                OrderOp op = new OrderOp
                {
                    ClOrdID = ClOrdIDGenerator.GenerateID(),
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
                return false;
            }

            public bool Replace(Object requestKey, decimal quantity, decimal price)
            {
                return false;
            }
        }
    }
}
