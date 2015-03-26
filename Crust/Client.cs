using System;
using System.Collections.Concurrent;
using System.Text;

namespace iFix.Crust
{
    /// <summary>
    /// Every order starts in state Created.
    ///
    /// OrderStatus can only increase and can't go back: PartiallyFilled can transition
    /// to TearingDown or Finished but can't transition to Created or Accepted.
    ///
    /// All possible transitions:
    ///   Created -> Accepted | Finished
    ///   Accepted -> PartiallyFilled | TearingDown | Finished
    ///   PartiallyFilled -> TearingDown | Finished
    ///   TearingDown -> Finished
    ///
    /// That's the theory. In practice, the client will always trust the exchange w.r.t. the
    /// order state. If the exchange tells us that the order has transitioned from Finished
    /// to Accepted, that's what we must believe; the client will generate a corresponding
    /// state change event without so much as blinking.
    /// </summary>
    public enum OrderStatus
    {
        /// <summary>
        /// The order hasn't yet been accepted by the exchange.
        /// </summary>
        Created,
        /// <summary>
        /// The order has been accepted by the exchange but not filled yet.
        /// Replaced orders are also in Accepted status if they haven't been
        /// filled yet.
        ///
        /// Orders in this state can be cancelled or replaced.
        /// </summary>
        Accepted,
        /// <summary>
        /// The order has been partially filled. Partially filled orders
        /// can't be replaced but they can be cancelled.
        /// </summary>
        PartiallyFilled,
        /// <summary>
        /// The order is about to be removed from the exchange. Such orders
        /// can't be replaced nor cancelled.
        /// </summary>
        TearingDown,
        /// <summary>
        /// The order has been removed from the exchange (fully filled, cancelled,
        /// or rejected).
        /// </summary>
        Finished,
    }

    /// <summary>
    /// State of the order on the exchange. This is the ground truth and should always be trusted.
    /// </summary>
    public class OrderState
    {
        /// <summary>
        /// When Status is Created, LeftQuantity is the initial order quantity and FilledQuantity
        /// is zero.
        ///
        /// When Status is Accepted, LeftQuantity is positive and FilledQuantity is zero.
        ///
        /// When Status is PartiallyFilled, LeftQuantity is positive.
        ///
        /// When Status is Finished, LeftQuantity is zero.
        ///
        /// These are the expectations, but some of them can be violated if the exchange
        /// sends us explicit data to that effect. For example, if the exchange explicitly says
        /// that FileldQuantity = 10 and Status = Accepted, then that's what we'll have.
        /// </summary>
        public OrderStatus Status;

        /// <summary>
        /// How many lots are still waiting to be filled.
        /// </summary>
        public decimal LeftQuantity;

        /// <summary>
        /// How many lots have been filled. The value is cumulative: if there were
        /// two partial fills for 5 and 7 lots, FilledQuantity is 12.
        /// </summary>
        public decimal FilledQuantity;

        /// <summary>
        /// Only limit orders have price.
        /// </summary>
        public decimal? Price;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.AppendFormat("Status = {0}", Status);
            res.AppendFormat(", LeftQuantity = {0}", LeftQuantity);
            res.AppendFormat(", FilledQuantity = {0}", FilledQuantity);
            if (Price.HasValue)
                res.AppendFormat(", Price = {0}", Price.Value);
            return res.ToString();
        }
    }

    /// <summary>
    /// Allows controlling an order on the exchange.
    ///
    /// Thread-safe.
    ///
    /// All methods return false and do nothing if the operation can't be performed
    /// with the order in the current state.
    ///
    /// Since order state is updated asynchronously, there is no way to know in advance
    /// which operations will succeed. You have to try them.
    ///
    /// All methods may perform synchronous IO and therefore may block.
    ///
    /// The requestKey parameter can be used for identifying finished operations.
    /// When the operation finishes, an OrderStateChangeEvent with FinishedRequestKey equal
    /// to requestKey is generated. IOrderCtrl doesn't use this value in any way
    /// and doesn't require its uniqueness. If the caller doesn't need request keys,
    /// it's OK to pass null.
    /// </summary>
    public interface IOrderCtrl
    {
        /// <summary>
        /// Sends a new order to the exchange.
        /// </summary>
        bool Submit(Object requestKey);

        /// <summary>
        /// Attempts to cancel an order.
        /// </summary>
        bool Cancel(Object requestKey);

        /// <summary>
        /// Attempts to replace an order.
        ///
        /// Only limit orders without fills can be cancelled.
        /// </summary>
        bool Replace(Object requestKey, decimal quantity, decimal price);
    }

    /// <summary>
    /// Describes a successful trade, a.k.a. a fill. A single order may
    /// have several fills, also called partial fills.
    /// </summary>
    public class Fill
    {
        /// <summary>
        /// How much did we buy/sell at this trade fill?
        /// The value is not cummulative. If an order triggered two
        /// trades for 5 and 7 lots, we'll have two separate fills with Quantity = 5
        /// and Quantity = 7.
        /// </summary>
        public decimal Quantity;

        /// <summary>
        /// Price at which we bought/sold per lot. We paid/got Quantity * Price.
        /// The field is present only when the fill price is known.
        /// </summary>
        public decimal? Price;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.AppendFormat("Quantity = {0}", Quantity);
            if (Price.HasValue)
                res.AppendFormat(", Price = {0}", Price.Value);
            return res.ToString();
        }
    }

    /// <summary>
    /// What happened to our request (Submit, Cancel or Replace) to the exchange?
    /// </summary>
    public enum RequestStatus
    {
        /// <summary>
        /// Request successful.
        /// </summary>
        OK,
        /// <summary>
        /// Request rejected by the exchange.
        /// </summary>
        Error,
        /// <summary>
        /// We didn't get a reply from the exchange and we don't expect
        /// one. The status of the request is unknown.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// Describes a change to an order.
    /// </summary>
    public class OrderStateChangeEvent
    {
        /// <summary>
        /// If one of the previously issued requests (Submit, Cancel or Replace)
        /// has finished, FinishedRequestKey is its key and FinishedRequestStatus contains
        /// the status. Otherwise these fields are null.
        /// </summary>
        public Object FinishedRequestKey;
        /// <summary>
        /// If one of the previously issued requests (Submit, Cancel or Replace)
        /// has finished, FinishedRequestKey is its key and FinishedRequestStatus contains
        /// the status. Otherwise these fields are null.
        /// </summary>
        public RequestStatus? FinishedRequestStatus;

        /// <summary>
        /// What is the state of the order after the change? Never null.
        /// </summary>
        public OrderState NewState;

        /// <summary>
        /// If not null, specifies how much we bought/sold and how much
        /// it costed. Fills are not cumulative. If an order triggered two
        /// trades for 1 lot each, we'll have two separate events each with
        /// a fill with Quantity = 1.
        ///
        /// fill.Quantity is always equal to the difference between FilledQuantity
        /// in NewState and the previous state of the order.
        /// </summary>
        public Fill Fill;

        public override string ToString()
        {
            var buf = new StringBuilder();
            if (FinishedRequestKey != null)
                buf.AppendFormat(", FinishedRequestKey = {0}", FinishedRequestKey);
            if (FinishedRequestStatus.HasValue)
                buf.AppendFormat(", FinishedRequestStatus = {0}", FinishedRequestStatus.Value);
            if (NewState != null)
                buf.AppendFormat(", NewState = ({0})", NewState);
            if (Fill != null)
                buf.AppendFormat(", Fill = ({0})", Fill);
            String res = buf.ToString();
            return res.Length > 0 ? res.Substring(2) : res;
        }
    }

    /// <summary>
    /// Are we buying or selling?
    /// </summary>
    public enum Side
    {
        /// <summary>
        /// We are buying.
        /// </summary>
        Buy = 1,
        /// <summary>
        /// We are selling.
        /// </summary>
        Sell = -1,
    }

    /// <summary>
    /// Type of the order.
    /// </summary>
    public enum OrderType
    {
        /// <summary>
        /// Market order.
        /// </summary>
        Market,
        /// <summary>
        /// Limit order.
        /// </summary>
        Limit,
    }

    /// <summary>
    /// Parameters of a new order.
    /// All fields are required unless specified otherwise.
    /// </summary>
    public class NewOrderRequest
    {
        /// <summary>
        /// Ticker symbol. For example, "USD000UTSTOM".
        /// </summary>
        public string Symbol;
        /// <summary>
        /// Do we want to buy or to sell?
        /// </summary>
        public Side Side;
        /// <summary>
        /// How many lots do we want to trade?
        /// </summary>
        public decimal Quantity;
        /// <summary>
        /// What kind of order should be placed?
        /// </summary>
        public OrderType OrderType;
        /// <summary>
        /// Price per lot. Must be set for limit orders.
        /// Shouldn't be set of market orders.
        /// </summary>
        public decimal? Price;
    }

    /// <summary>
    /// Describes a fill of an unspecified order.
    /// </summary>
    public class FillEvent
    {
        /// <summary>
        /// Ticker symbol. For example, "USD000UTSTOM".
        /// </summary>
        public string Symbol;
        /// <summary>
        /// Did we buy or sell?
        /// </summary>
        public Side Side;
        /// <summary>
        /// How much we bought/sold and at what price.
        /// </summary>
        public Fill Fill;

        public override string ToString()
        {
            return String.Format("Symbol = {0}, Side = {1}, Fill = ({2})", Symbol, Side, Fill);
        }
    }

    /// <summary>
    /// Client for communication with the exchange. Allows placing orders, tracking and amending them.
    /// Does not provide market data for other market players.
    ///
    /// IClient is thread-safe. Its methods don't perform IO and don't block.
    /// </summary>
    public interface IClient : IDisposable
    {
        /// <summary>
        /// Creates a new order.
        ///
        /// The initial state of the order is defined as follows:
        ///   Status = OrderStatus.Created
        ///   LeftQuantity = request.Quantity
        ///   Price = request.Price
        ///   FilledQuantity = 0
        ///
        /// Action onChange is called whenever the state of the order changes or a request issued
        /// through IOrderCtrl completes. It shall not be called synchronously from any methods of
        /// IClient or IOrderCtrl. All change notifications coming from the same IClient object
        /// are serialized, even the ones that belong to different orders.
        ///
        /// The order isn't sent to the exchange and no events are generated for
        /// the order until IOrderCtrl.Submit() is called.
        ///
        /// It's not allowed to modify 'request' after passing it to CreateOrder().
        /// </summary>
        IOrderCtrl CreateOrder(NewOrderRequest request, Action<OrderStateChangeEvent> onChange);

        /// <summary>
        /// Fires when there is a stranded fill that doesn't correspond to any live order.
        /// Subscription should happen before the first order is created.
        /// </summary>
        event Action<FillEvent> OnFill;

        /// <summary>
        /// Cancels all orders.
        /// </summary>
        void CancelAllOrders();
    }
}
