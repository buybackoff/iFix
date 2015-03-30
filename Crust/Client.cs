using System;
using System.Collections.Concurrent;
using System.Text;

namespace iFix.Crust
{
    /// <summary>
    /// Every order starts in state Created.
    ///
    /// Normally, OrderStatus can only increase and can't go back: PartiallyFilled
    /// can transition to TearingDown or Finished but can't transition to Created or
    /// Accepted.
    ///
    /// All possible transitions under normal circumstances:
    ///   Created -> Accepted | Finished
    ///   Accepted -> PartiallyFilled | TearingDown | Finished
    ///   PartiallyFilled -> TearingDown | Finished
    ///   TearingDown -> Finished
    ///
    /// That's the theory. In practice, the client will always trust the exchange w.r.t. the
    /// order state. If the exchange tells us that the order has transitioned from TearingDown
    /// to Accepted, that's what we must believe; the client will generate a corresponding
    /// state change event without so much as blinking.
    /// 
    /// The only guarantee that iFix provides is that every order eventually transitions to
    /// Finished and there are no messages for the order after that.
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
        /// The order has been removed from the exchange for one of the following
        /// reasons:
        ///   * It has never been accepted in the first place (we tried to place an order
        ///     and it got rejected).
        ///   * Fully filled.
        ///   * Cancelled.
        /// </summary>
        Finished,
    }

    /// <summary>
    /// State of the order on the exchange. This is the ground truth and should always be trusted.
    /// </summary>
    public class OrderState : ICloneable
    {
        /// <summary>
        /// Equal to the UserID field of the NewOrderRequest that created this order.
        /// iFix passes this value as is without interpreting it in any way.
        /// </summary>
        public Object UserID;

        /// <summary>
        /// Under normal circumstances:
        /// 
        ///   * When Status is Created, LeftQuantity is the initial order quantity.
        ///   * When Status is Accepted, LeftQuantity is positive.
        ///   * When Status is PartiallyFilled, LeftQuantity is positive.
        ///   * When Status is Finished, LeftQuantity is zero.
        ///
        /// These are the expectations, but some of them can be violated if the exchange
        /// sends us explicit data to that effect. For example, if the exchange explicitly says
        /// that LeftQuantity = 0 and Status = Accepted, then that's what we'll have.
        /// </summary>
        public OrderStatus Status;

        /// <summary>
        /// How many lots are still waiting to be filled.
        /// </summary>
        public decimal LeftQuantity;

        /// <summary>
        /// Only limit orders have price.
        /// </summary>
        public decimal? Price;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            if (UserID != null) res.AppendFormat("UserID = ({0}), ", UserID);
            res.AppendFormat("Status = {0}", Status);
            res.AppendFormat(", LeftQuantity = {0}", LeftQuantity);
            if (Price.HasValue) res.AppendFormat(", Price = {0}", Price.Value);
            res.Append(")");
            return res.ToString();
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// Allows controlling an order on the exchange.
    ///
    /// Thread-safe.
    ///
    /// All methods do nothing if the operation can't be performed with the order in the
    /// current state.
    ///
    /// Since order state is updated asynchronously, there is no way to know in advance
    /// which operations will succeed. You have to try them.
    ///
    /// All methods may perform asynchronous IO. They never block.
    /// </summary>
    public interface IOrderCtrl
    {
        /// <summary>
        /// Attempts to cancel an order.
        /// </summary>
        void Cancel();

        /// <summary>
        /// Attempts to replace an order.
        ///
        /// Only limit orders without fills can be replaced.
        /// </summary>
        void Replace(decimal quantity, decimal price);

        /// <summary>
        /// Attempts to replace an order. If it's not possible due to the
        /// order being partially filled, then attempts to cancel it.
        /// In pseudocode:
        /// 
        ///   if (CanReplace()) Replace(quantity, price);
        ///   else Cancel();
        /// </summary>
        void ReplaceOrCancel(decimal quantity, decimal price);
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
    /// Describes a successful trade, a.k.a. a fill. A single order may
    /// have several fills, also called partial fills.
    /// </summary>
    public class Fill : ICloneable
    {
        /// <summary>
        /// What did we buy/sell?
        /// </summary>
        public string Symbol;

        /// <summary>
        /// Did we buy or sell?
        /// </summary>
        public Side Side;

        /// <summary>
        /// How much did we buy/sell at this trade fill?
        /// The value is not cummulative. If an order triggered two trades for 5
        /// and 7 lots, we'll have two separate fills with Quantity = 5 and Quantity = 7.
        /// </summary>
        public decimal Quantity;

        /// <summary>
        /// Price at which we bought/sold per lot. We paid/got Quantity * Price.
        /// </summary>
        public decimal Price;

        public override string ToString()
        {
            return String.Format("(Symbol = {0}, Side = {1}, Quantity = {2}, Price = {3})",
                                 Symbol, Side, Quantity, Price);
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// Describes a change to an order.
    /// 
    /// At least one of the fields is not null.
    /// </summary>
    public class OrderEvent : ICloneable
    {
        /// <summary>
        /// What is the state of the order after the change? May be null if we don't know
        /// which order is affected by the event. For example, when the exchange notifies us
        /// about a fill we might not find the associated order.
        /// </summary>
        public OrderState State;

        /// <summary>
        /// If not null, specifies how much we bought/sold and how much
        /// it costed. Fills are not cumulative. If an order triggered two
        /// trades for 1 lot each, we'll have two separate events each with
        /// a fill with Quantity = 1.
        /// </summary>
        public Fill Fill;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (State != null)
            {
                res.AppendFormat("State = {0}", State);
                empty = false;
            }
            if (Fill != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Fill = {0}", Fill);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }

        public object Clone()
        {
            return new OrderEvent() { State = (OrderState)State.Clone(), Fill = (Fill)Fill.Clone() };
        }
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
    public class NewOrderRequest : ICloneable
    {
        /// <summary>
        /// User-specified opaque object associated with the order.
        /// iFix propagates it through OrderEvent.UserID without interpreting it
        /// in any way. It may be null.
        /// </summary>
        public object UserID;

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

        public object Clone()
        {
            return MemberwiseClone();
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
        /// Fires when anything happens to one of the submitted orders.
        /// </summary>
        event Action<OrderEvent> OnOrderEvent;

        /// <summary>
        /// Creates a new order.
        ///
        /// The initial state of the order is defined as follows:
        ///   UserID = request.UserID
        ///   Status = OrderStatus.Created
        ///   LeftQuantity = request.Quantity
        ///   Price = request.Price
        ///
        /// Action onChange is called whenever the state of the order changes or a request issued
        /// through IOrderCtrl completes. It shall not be called synchronously from any methods of
        /// IClient or IOrderCtrl. All change notifications coming from the same IClient object
        /// are serialized, even the ones that belong to different orders.
        ///
        /// The order is sent to the exchange asynchronously. The events for it may be generated for
        /// it even before CreateOrder returns. However, they will not be delivered from the same
        /// thread.
        /// </summary>
        IOrderCtrl CreateOrder(NewOrderRequest request);
    }
}
