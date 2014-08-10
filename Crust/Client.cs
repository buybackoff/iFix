using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust
{
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

        // Only limit orders have price.
        public decimal? Price;

        // If this field is set, we expect the status of this order to change.
        // For example, when a new order is submitted to the exchange,
        // Status is Created and PendingStatus is Accepted. When a cancel order
        // request is sent, PendingStatus is TearingDown. When Status is TearingDown,
        // PendingStatus is Finished.
        //
        // When PendingStatus is set, we are expecting a reply from the exchange.
        // The reply doesn't guarantee that the status will change exactly as we
        // expect (for example, our request might get rejected). In either case
        // PendingStatus can have any value only for a short period of time until
        // we receive a reply from the exchange.
        //
        // If PendingStatus is set, it's always greater or equal than Status.
        public OrderStatus? PendingStatus;
    }

    // Allows controlling an order that has been placed on the exchange.
    //
    // Thread-safe.
    interface IOrderCtrl
    {
        // All methods return false and do nothing if the operation can't be performed
        // with the order in the current state.
        //
        // Since order state is updated asynchronously, there is no way to know in advance
        // which operations will succeed. You have to try them.

        // Attempts to cancel an order.
        //
        // Returns false and does nothing if Status or PendingStatus is
        // in {TearingDown, Finished}.
        //
        // Otherwise an event with OrderEvent = CancelSent is guaranteed
        // to be in the queue by the time the method returns. At a later point
        // an event with either OrderEvent = CancelAccepted or OrderEvent = CancelRejected
        // is guaranteed to come.
        bool Cancel();

        // Attempts to replace an order.
        //
        // Returns false if it's not a limit order or Status or PendingStatus is
        // in {PartiallyFilled, TearingDown, Finished}.
        //
        // Otherwise an event with OrderEvent = ReplaceSent is guaranteed
        // to be in the queue by the time the method returns. At a later point
        // an event with either OrderEvent = ReplaceAccepted or OrderEvent = ReplaceRejected
        // is guaranteed to come.
        bool Replace(decimal quantity, decimal price);
    }

    enum OrderEvent
    {
        // We sent a New Order Request.
        SubmitSent,
        // Our New Order Request has been rejected.
        SubmitRejected,
        // Our New Order Request has been accepted. The order is now
        // active on the exchange.
        SubmitAccepted,

        // We sent a Cancel Order Request.
        CancelSent,
        // Our Cancel Order Request has been rejected.
        CancelRejected,
        // Our Cancel Order Request has been accepted.
        // It doesn't mean the order has been cancelled yet; it will be
        // once OrderEvent == Cancelled arrives.
        CancelAccepted,

        // We sent a Change Order Request.
        ReplaceSent,
        // Our Change Order Request has been rejected.
        ReplaceRejected,
        // Our Change Order Request has been accepted. The quantity or
        // the price was changed by our request.
        ReplaceAccepted,

        // Filled or partially filled.
        Filled,
        // The order has been removed from the exchange before
        // it was fully filled. This may happen as a result of
        // our cancel request or due to reasons outside of our
        // control (the exchange and the broker may cancel our
        // requests for a number of reasons).
        Cancelled,
    }

    // Describes a successful trade, a.k.a. a fill. A single order may
    // have several fills, also called partial fills.
    class Fill
    {
        // How much did we but/sell at this trade fill?
        // The value is not cummulative. If an order triggered two
        // trades for 1 lot each, we'll have two fills with Quantity = 1.
        public decimal Quantity = 1;
        // Price at which we bought/sold.
        public decimal Price = 2;
    }

    // Describes a change to an order.
    class OrderStateChangeEvent
    {
        // Equal to the key passed to Client.SubmitOrder.
        public Object OrderKey;
        public IOrderCtrl OrderCtrl;
        // What kind of change happened?
        public OrderEvent Event;
        // What was the state of the order before the change?
        public OrderState OldState;
        // What is the state of the order after the change?
        public OrderState NewState;
        // Present only when Event is Filled.
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

    class Session
    {
        ClientConfig _cfg;

        public Session(ClientConfig cfg)
        {
            _cfg = cfg;
        }
    }

    enum OrderOpType
    {
        Submit,
        Cancel,
        Replace,
    }

    class OrderOp
    {
        public OrderOpType Type;
        public long RefSeqNum;
        public string ClOrdID;
        public Order Order;
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

    class Order
    {
        OrderState _state;
        Object _key;
        IOrderCtrl _ctrl;
        string _origClOrdID;
        List<OrderOp> _inflightOps;
        OrderType _orderType;

        public Order(NewOrderRequest request, Object key, IOrderCtrl ctrl)
        {
            _state = new OrderState()
            {
                Status = OrderStatus.Created,
                LeftQuantity = request.Quantity,
                Price = request.Price,
            };
            _key = key;
            _ctrl = ctrl;
            _orderType = request.OrderType;
        }

        public bool CanSend(OrderOpType op)
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
        }

        // Requires: CanSend(op.Type).
        public OrderStateChangeEvent OnSent(OrderOp op)
        {
            Debug.Assert(CanSend(op.Type));
            if (op.Type == OrderOpType.Submit && _origClOrdID == null)
            {
                _origClOrdID = op.ClOrdID;
            }
            OrderStateChangeEvent e = new OrderStateChangeEvent()
            {
                OrderKey = _key,
                OrderCtrl = _ctrl,
                OldState = _state,
            };
            OrderStatus pending;
            switch (op.Type)
            {
                case OrderOpType.Submit:
                    e.Event = OrderEvent.SubmitSent;
                    pending = OrderStatus.Accepted;
                    break;
                case OrderOpType.Cancel:
                    e.Event = OrderEvent.CancelSent;
                    pending = OrderStatus.Finished;
                    break;
                case OrderOpType.Replace:
                    e.Event = OrderEvent.ReplaceSent;
                    pending = OrderStatus.Accepted;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("op.Type", op.Type, "Unexpected value of OrderOpType");
            }
            _inflightOps.Add(op);
            _state = CloneState(_state);
            if (!_state.PendingStatus.HasValue || _state.PendingStatus.Value < pending)
                _state.PendingStatus = pending;
            e.NewState = _state;
            return e;
        }

        // Fill is not null if ExecType is Trade.
        // The op is not null if it's a reply to one of our requests.
        // For some values of ExecType, such as New or PendingCancel, op is expected to be not null.
        // For Trade it should be null. For Cancelled it may be null or not.
        IEnumerable<OrderStateChangeEvent> OnReceived(ExecType execType, Fill fill, OrderOp op)
        {
            return null;
        }

        // Can we remove this order from memory? It's safe to do if we can't send any
        // useful requests to the exchange for this order (the order is in its terminal state)
        // and we we don't expect any messages from the exchange for it (all pending operations
        // have been completed).
        public bool Done()
        {
            return _state.Status == OrderStatus.Finished && !_state.PendingStatus.HasValue;
        }

        // ClOrdID of the New Order Request.
        public string OrigClOrdID { get { return _origClOrdID; } }

        static OrderState CloneState(OrderState state)
        {
            return new OrderState()
            {
                Status = state.Status,
                LeftQuantity = state.LeftQuantity,
                Price = state.Price,
                PendingStatus = state.PendingStatus,
            };
        }
    }

    // Client maintains a connection with the exchange, reopening it when necessary.
    // It cancels all orders upon opening a new connection.
    class Client
    {
        ClientConfig _cfg;
        Connector _connector;
        BlockingCollection<OrderStateChangeEvent> _changeEvents = new BlockingCollection<OrderStateChangeEvent>();

        public Client(ClientConfig cfg, Connector connector)
        {
            _cfg = cfg;
            _connector = connector;
        }

        public IOrderCtrl SubmitOrder(NewOrderRequest request, Object key)
        {
            return null;
        }

        // Any request is finished when we receive Reject with the matching RefSeqNum or
        // Execution Report with the matching ClOrdID.
        // Additionally, Cancel is done when we receive Order Cancel Reject with the
        // matching ClOrdID.
        //
        // An Execution Report matches an order if:
        // 1. ClOrdID is equal to the ClOrdID of the message that created the order.
        // 2. OrigClOrdID is equal to the ClOrdID of the message that created the order.

        BlockingCollection<OrderStateChangeEvent> OrderStateChanges { get { return _changeEvents; } }
    }
}
