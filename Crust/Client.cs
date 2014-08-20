using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    class UnexpectedMessageReceived : Exception
    {
        public UnexpectedMessageReceived(string msg) : base(msg) { }
    }

    // Every order starts in state Created.
    //
    // OrderStatus can only increase and can't go back: PartiallyFilled can transition
    // to TearingDown or Finished and can't transition to Created or Accepted.
    //
    // All possible transitions:
    //   Created -> Accepted | Finished
    //   Accepted -> PartiallyFilled | TearingDown | Finished
    //   PartiallyFilled -> TearingDown | Finished
    //   TearingDown -> Finished
    enum OrderStatus
    {
        // The order hasn't yet been accepted by the exchange.
        Created,
        // The order has been accepted by the exchange but not filled yet.
        // Replaced orders are also in Accepted status if they haven't been
        // filled yet.
        //
        // Orders in this state ca be cancelled or replaced.
        Accepted,
        // The order has been partially filled. Partially filled orders
        // can't be replaced but they can be cancelled.
        PartiallyFilled,
        // The order is about to be removed from the exchange. Such orders
        // can't be replaced nor cancelled.
        TearingDown,
        // The order has been removed from the exchange (fully filled, cancelled,
        // or rejected).
        Finished,
    }

    class OrderState
    {
        // When Status is Created, LeftQuantity is the initial order quantity.
        //
        // When Status is Accepted, LeftQuantity is positive and is equal to the order
        // quantity of the last replace request or the initial order creation request
        // if there were no replacements.
        //
        // When Status is PartiallyFilled, LeftQuantity is positive.
        //
        // When Status is Finished, LeftQuantity is zero.
        public OrderStatus Status;

        // How many lots are still waiting to be filled.
        public decimal LeftQuantity;

        // How many lots have been filled. The value is cumulative: if there were
        // two partial fills for 5 and 7 lots, FilledQuantity is 12.
        public decimal FilledQuantity;

        // Only limit orders have price.
        public decimal? Price;
    }

    // Allows controlling an order on the exchange.
    //
    // Thread-safe.
    interface IOrderCtrl
    {
        // All methods return false and do nothing if the operation can't be performed
        // with the order in the current state.
        //
        // Since order state is updated asynchronously, there is no way to know in advance
        // which operations will succeed. You have to try them.

        // Sends a new order to the exchange.
        bool Submit(Object requestKey);

        // Attempts to cancel an order.
        bool Cancel(Object requestKey);

        // Attempts to replace an order.
        //
        // Only limit orders without fills can be cancelled.
        bool Replace(Object requestKey, decimal quantity, decimal price);
    }

    // Describes a successful trade, a.k.a. a fill. A single order may
    // have several fills, also called partial fills.
    class Fill
    {
        // How much did we but/sell at this trade fill?
        // The value is not cummulative. If an order triggered two
        // trades for 5 and 7 lots, we'll have two separate fills with Quantity = 5
        // and Quantity = 7.
        public decimal Quantity = 1;
        // Price at which we bought/sold per lot. We paid/got Quantity * Price.
        // The field is present only when the fill price is known.
        public decimal? Price = 2;
    }

    enum RequestStatus
    {
        OK,
        Error,
        Timeout,
    }

    // Describes a change to an order.
    class OrderStateChangeEvent
    {
        // Equal to the key passed to Client.SubmitOrder.
        public Object OrderKey;
        public IOrderCtrl OrderCtrl;

        // If one of the previously issued requests (Submit, Cancel or Replace)
        // has finished, FinishedRequestKey is its key and FinishedRequestStatus contains
        // the status. Otherwise these fields are null.
        public Object FinishedRequestKey;
        public RequestStatus FinishedRequestStatus;

        // What was the state of the order before the change?
        public OrderState OldState;
        // What is the state of the order after the change?
        public OrderState NewState;

        // If not null, specifies how much we bought/sold and how much
        // it costed. Fills are not cumulative. If an order triggered two
        // trades for 1 lot each, we'll have two separate events each with
        // a fill with Quantity = 1.
        public Fill Fill;
    }

    enum Side
    {
        // We are buying.
        Buy = 1,
        // We are selling.
        Sell = -1,
    }

    enum OrderType
    {
        Market,
        Limit,
    }

    class NewOrderRequest
    {
        public string Symbol;
        public Side Side;
        public decimal Quantity;
        public OrderType OrderType;
        // Must be set of limit orders.
        public decimal? Price;
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
        public decimal? Price;
        public decimal? Quantity;
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
        Object _key;
        IOrderCtrl _ctrl;
        List<OrderOp> _inflightOps = new List<OrderOp>();
        string _origClOrdID;

        public Order(NewOrderRequest request, Object key, IOrderCtrl ctrl, string origClOrdID)
        {
            _state = new OrderState()
            {
                Status = OrderStatus.Created,
                LeftQuantity = request.Quantity,
                Price = request.Price,
                FilledQuantity = 0,
            };
            _key = key;
            _ctrl = ctrl;
            _origClOrdID = origClOrdID;
        }

        public OrderStatus CurrentStatus { get { return _state.Status; } }

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
            OrderState newState = NewState(_state, report);
            OrderStateChangeEvent e = new OrderStateChangeEvent()
            {
                OrderKey = _key,
                OrderCtrl = _ctrl,
                OldState = _state,
                NewState = newState,
                Fill = MakeFill(_state, newState, report),
            };
            if (op != null)
            {
                _inflightOps.Remove(op);
                e.FinishedRequestKey = op.Key;
                e.FinishedRequestStatus = requestStatus;
            }
            _state = newState;
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
            // TODO: check what OrdStatus we get when we query status of inexisting order.
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

        public void AddOrder(Order order)
        {
            _ordersByOrigOrdID[order.OrigClOrdID] = order;
        }

        public void OnSent(Order order, OrderOp op)
        {
            order.OnSent(op);
            _opsBySeqNum[op.RefSeqNum] = op;
            _opsByOrdID[op.ClOrdID] = op;
        }

        public OrderStateChangeEvent OnReceived(long? refSeqNum, string clOrdID, string origClOrdID,
                                                RequestStatus requestStatus, OrderReport orderReport)
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

    class Session : IDisposable
    {
        ClientConfig _cfg;
        Connector _connector;
        Connection _connection;
        Mantle.Receiver _receiver;
        bool _stopped = false;
        Object _monitor = new Object();
        long _lastOutSeqNum = 0;
        Action<Mantle.Fix44.IServerMessage> _onMessage;
        Thread _thread;

        public Session(ClientConfig cfg, Connector connector,
                       Action<Mantle.Fix44.IServerMessage> onMessage)
        {
            _cfg = cfg;
            _connector = connector;
            _onMessage = onMessage;
            _thread = new Thread(Loop);
            _thread.Start();
        }

        public void Send(Mantle.Fix44.IClientMessage msg)
        {
            // TODO: Who sets the SeqNum for the outgoing message?
        }

        public void Dispose()
        {
            lock (_monitor)
            {
                if (!_stopped)
                    _stopped = true;
                if (_connection != null)
                {
                    _connection.Dispose();
                    _connection = null;
                }
            }
            _thread.Join();
        }

        void Loop()
        {
            Mantle.Receiver receiver = null;
            while (true)
            {
                receiver = GetReceiver(receiver);
                if (receiver == null) return;
                try
                {
                    while (true)
                    {
                        var msg = (Mantle.Fix44.IServerMessage)receiver.Receive();
                        if (msg is Mantle.Fix44.TestRequest)
                        {
                            var heartbeat = new Mantle.Fix44.Heartbeat() { StandardHeader = MakeHeader() };
                            heartbeat.TestReqID.Value = ((Mantle.Fix44.TestRequest)msg).TestReqID.Value;
                            // TODO: Need ostream to publish and it should belong to the same connection
                            // as the input. Sigh. Probably need to introduce InOut which is just a pair
                            // of Receiver and output stream.
                            // Mantle.Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, heartbeat);
                        }
                        _onMessage.Invoke(msg);
                    }
                }
                catch (Exception)
                {
                    // TODO: log.
                }
            }
        }

        Mantle.Receiver GetReceiver(Mantle.Receiver old)
        {
            lock (_monitor)
            {
                if (_stopped) return null;
                if (_receiver != old) return _receiver;
                Connect();
                return _receiver;
            }
        }

        Stream GetOutStream(Stream old)
        {
            lock (_monitor)
            {
                if (_stopped) return null;
                if (_connection.Out != old) return _connection.Out;
                Connect();
                return _connection.Out;
            }
        }

        void Connect()
        {
            while (true)
            {
                try
                {
                    _lastOutSeqNum = 0;
                    if (_connection != null) _connection.Dispose();
                    _connection = _connector.CreateConnection();
                    var protocols = new Dictionary<string, Mantle.IMessageFactory>() {
                        { Mantle.Fix44.Protocol.Value, new Mantle.Fix44.MessageFactory() }
                    };
                    _receiver = new Mantle.Receiver(_connection.In, 1 << 20 /* 1MB max message size */, protocols);
                    Logon();
                    // TODO: cancel all orders.
                }
                catch (Exception)
                {
                    // TODO: log.
                }
            }
        }

        void Logon()
        {
            var logon = new Mantle.Fix44.Logon() { StandardHeader = MakeHeader() };
            logon.EncryptMethod.Value = 0;
            logon.HeartBtInt.Value = _cfg.HeartBtInt;
            logon.Password.Value = _cfg.Password;
            logon.ResetSeqNumFlag.Value = true;
            Mantle.Publisher.Publish(_connection.Out, Mantle.Fix44.Protocol.Value, logon);
            if (!(_receiver.Receive() is Mantle.Fix44.Logon))
                throw new UnexpectedMessageReceived("Expected Logon");
        }

        Mantle.Fix44.StandardHeader MakeHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = _cfg.SenderCompID;
            res.TargetCompID.Value = _cfg.TargetCompID;
            res.MsgSeqNum.Value = ++_lastOutSeqNum;
            res.SendingTime.Value = DateTime.Now;
            return res;
        }
    }

    // Client maintains a connection with the exchange, reopening it when necessary.
    // It cancels all orders upon opening a new connection.
    class Client
    {
        ClientConfig _cfg;
        Connector _connector;
        OrderHeap _orders = new OrderHeap();
        BlockingCollection<OrderStateChangeEvent> _changeEvents = new BlockingCollection<OrderStateChangeEvent>();

        public Client(ClientConfig cfg, Connector connector)
        {
            _cfg = cfg;
            _connector = connector;
        }

        public IOrderCtrl SubmitOrder(NewOrderRequest request, Object orderKey)
        {
            return null;
        }

        BlockingCollection<OrderStateChangeEvent> OrderStateChanges { get { return _changeEvents; } }
    }
}
