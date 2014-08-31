using System;
using System.Collections.Concurrent;

namespace iFix.Crust
{
    // Every order starts in state Created.
    //
    // OrderStatus can only increase and can't go back: PartiallyFilled can transition
    // to TearingDown or Finished but can't transition to Created or Accepted.
    //
    // All possible transitions:
    //   Created -> Accepted | Finished
    //   Accepted -> PartiallyFilled | TearingDown | Finished
    //   PartiallyFilled -> TearingDown | Finished
    //   TearingDown -> Finished
    //
    // That's the theory. In practice, the client will always trust the exchange w.r.t. the
    // order state. If the exchange tells us that the order has transitioned from Finished
    // to Accepted, that's what we must believe; the client will generate a corresponding
    // state change event without so much as blinking.
    public enum OrderStatus
    {
        // The order hasn't yet been accepted by the exchange.
        Created,
        // The order has been accepted by the exchange but not filled yet.
        // Replaced orders are also in Accepted status if they haven't been
        // filled yet.
        //
        // Orders in this state can be cancelled or replaced.
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

    // State of the order on the exchange. This is the ground truth and should always be trusted.
    public class OrderState
    {
        // When Status is Created, LeftQuantity is the initial order quantity and FilledQuantity
        // is zero.
        //
        // When Status is Accepted, LeftQuantity is positive and FilledQuantity is zero.
        //
        // When Status is PartiallyFilled, LeftQuantity is positive.
        //
        // When Status is Finished, LeftQuantity is zero.
        //
        // These are the expectations, but some of them can be violated if the exchange
        // sends us explicit data to that effect. For example, if the exchange explicitly says
        // that FileldQuantity = 10 and Status = Accepted, then that's what we'll have.
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
    public interface IOrderCtrl
    {
        // All methods return false and do nothing if the operation can't be performed
        // with the order in the current state.
        //
        // Since order state is updated asynchronously, there is no way to know in advance
        // which operations will succeed. You have to try them.
        //
        // All methods may perform synchronous IO and therefore may block.
        //
        // The requestKey parameter can be used for identifying finished operations.
        // When the operation finishes, an OrderStateChangeEvent with FinishedRequestKey equal
        // to requestKey is generated. IOrderCtrl doesn't use this value in any way
        // and doesn't require its uniqueness. If the caller doesn't need request keys,
        // it's OK to pass null.

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
    public class Fill
    {
        // How much did we buy/sell at this trade fill?
        // The value is not cummulative. If an order triggered two
        // trades for 5 and 7 lots, we'll have two separate fills with Quantity = 5
        // and Quantity = 7.
        public decimal Quantity = 1;

        // Price at which we bought/sold per lot. We paid/got Quantity * Price.
        // The field is present only when the fill price is known.
        public decimal? Price = 2;
    }

    // What happened to our request (Submit, Cancel or Replace) to the exchange?
    public enum RequestStatus
    {
        // Request successful.
        OK,
        // Request rejected by the exchange.
        Error,
        // We didn't get a reply from the exchange and we don't expect
        // one. The status of the request is unknown.
        Unknown,
    }

    // Describes a change to an order.
    public class OrderStateChangeEvent
    {
        // If one of the previously issued requests (Submit, Cancel or Replace)
        // has finished, FinishedRequestKey is its key and FinishedRequestStatus contains
        // the status. Otherwise these fields are null.
        public Object FinishedRequestKey;
        public RequestStatus? FinishedRequestStatus;

        // What is the state of the order after the change? Never null.
        public OrderState NewState;

        // If not null, specifies how much we bought/sold and how much
        // it costed. Fills are not cumulative. If an order triggered two
        // trades for 1 lot each, we'll have two separate events each with
        // a fill with Quantity = 1.
        //
        // fill.Quantity is always equal to the difference between FilledQuantity
        // in NewState and the previous state of the order.
        public Fill Fill;
    }

    public enum Side
    {
        // We are buying.
        Buy = 1,
        // We are selling.
        Sell = -1,
    }

    public enum OrderType
    {
        Market,
        Limit,
    }

    public class NewOrderRequest
    {
        // All fields are required unless specified otherwise.

        // Ticker symbol. For example, "USD000UTSTOM".
        public string Symbol;
        // Do we want to buy or to sell?
        public Side Side;
        // How many lots do we want to trade?
        public decimal Quantity;
        // What kind of order should be placed?
        public OrderType OrderType;
        // Price per lot. Must be set of limit orders.
        public decimal? Price;
    }

    // Client for communication with the exchange. Allows placing orders, tracking and amending them.
    // Does not provide market data for other market players.
    //
    // IClient is thread-safe. Its methods don't perform IO and don't block.
    public interface IClient : IDisposable
    {
        // Creates a new order.
        //
        // The initial state of the order is defined as follows:
        //   Status = OrderStatus.Created
        //   LeftQuantity = request.Quantity
        //   Price = request.Price
        //   FilledQuantity = 0
        //
        // Action onChange is called whenever the state of the order changes or a request issued
        // through IOrderCtrl completes. It shall not be called synchronously from any methods of
        // IClient or IOrderCtrl. All change notifications coming from the same IClient object
        // are serialized, even the ones that belong to different orders.
        //
        // The order isn't sent to the exchange and no events are generated for
        // the order until IOrderCtrl.Submit() is called.
        //
        // It's not allowed to modify 'request' after passing it to CreateOrder().
        IOrderCtrl CreateOrder(NewOrderRequest request, Action<OrderStateChangeEvent> onChange);
    }
}
