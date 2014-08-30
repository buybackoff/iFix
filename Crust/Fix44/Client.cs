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
        public long RefSeqNum;
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

        public Order(OrderState state, string origClOrdID)
        {
            _state = state;
            _origClOrdID = origClOrdID;
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
        Dictionary<long, OrderOp> _opsBySeqNum = new Dictionary<long, OrderOp>();
        Dictionary<string, OrderOp> _opsByOrdID = new Dictionary<string, OrderOp>();
        Dictionary<string, Order> _ordersByOrigOrdID = new Dictionary<string, Order>();
        Object _monitor = new Object();

        public void AddOrder(Order order)
        {
            lock (_monitor)
            {
                _ordersByOrigOrdID[order.OrigClOrdID] = order;
            }
        }

        public void OnSent(Order order, OrderOp op)
        {
            lock (_monitor)
            {
                order.OnSent(op);
                _opsBySeqNum[op.RefSeqNum] = op;
                _opsByOrdID[op.ClOrdID] = op;
            }
        }

        public OrderStateChangeEvent OnReceived(long? refSeqNum, string clOrdID, string origClOrdID,
                                                RequestStatus requestStatus, OrderReport orderReport)
        {
            lock (_monitor)
            {
                // Any request is finished when we receive Reject with the matching RefSeqNum or
                // Execution Report with the matching ClOrdID.
                // Additionally, Cancel is done when we receive Order Cancel Reject with the
                // matching ClOrdID.
                //
                // An Execution Report matches an order if:
                // 1. ClOrdID is equal to the ClOrdID of the message that created the order.
                // 2. OrigClOrdID is equal to the ClOrdID of the message that created the order.
                OrderOp op = null;
                Order order = null;
                if (refSeqNum.HasValue && _opsBySeqNum.TryGetValue(refSeqNum.Value, out op) ||
                    clOrdID != null && _opsByOrdID.TryGetValue(clOrdID, out op))
                {
                    order = op.Order;
                }
                else if (origClOrdID != null)
                {
                    _ordersByOrigOrdID.TryGetValue(origClOrdID, out order);
                }
                if (order == null) return null;
                OrderStateChangeEvent res = order.OnReceived(op, requestStatus, orderReport);
                if (op != null)
                {
                    _opsBySeqNum.Remove(op.RefSeqNum);
                    _opsByOrdID.Remove(op.ClOrdID);
                }
                if (order.Done())
                    _ordersByOrigOrdID.Remove(order.OrigClOrdID);
                return res;
            }
        }
    }

    class OrderCtrl : IOrderCtrl
    {
        DurableConnection _connection;
        Order _order;
        OrderHeap _orderHeap;

        public bool Submit(Object requestKey)
        {
            // _orderHeap.OnSent(_order, );
            return false;}

        // Attempts to cancel an order.
        public bool Cancel(Object requestKey)
        {
            return false;
        }

        // Attempts to replace an order.
        //
        // Only limit orders without fills can be cancelled.
        public bool Replace(Object requestKey, decimal quantity, decimal price)
        {
            return false;
        }
    }

    class Client : IDisposable, IClient
    {
        ClientConfig _cfg;
        DurableConnection _connection;
        OrderHeap _orders = new OrderHeap();
        Object _ordersMonitor = new Object();

        public Client(ClientConfig cfg, IConnector connector)
        {
            _cfg = cfg;
            _connection = new DurableConnection(new InitializingConnector(connector, Logon));
        }

        public IOrderCtrl CreateOrder(NewOrderRequest request, Action<OrderStateChangeEvent> onChange)
        {
            return null;
        }

        public void Dispose()
        {
            _connection.Dispose();
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
    }
}
