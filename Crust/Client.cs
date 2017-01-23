using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        /// The instrument being traded.
        /// </summary>
        public string Symbol;

        /// <summary>
        /// Is this an order to buy or to sell?
        /// </summary>
        public Side Side;

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
        /// How many lots have been filled so far? If there were two fills for the order
        /// for 2 and 3 lots, FillQuantity will be 5.
        /// </summary>
        public decimal FillQuantity;

        /// <summary>
        /// Only limit orders have price.
        /// </summary>
        public decimal? Price;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            if (UserID != null) res.AppendFormat("UserID = ({0}), ", UserID);
            res.AppendFormat("Symbol = {0}", Symbol);
            res.AppendFormat(", Side = {0}", Side);
            res.AppendFormat(", Status = {0}", Status);
            res.AppendFormat(", LeftQuantity = {0}", LeftQuantity);
            res.AppendFormat(", FillQuantity = {0}", FillQuantity);
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
        /// Attempts to cancel an order. The result is true if a request was sent to
        /// the exchange (but not necessarily processed yet), false otherwise.
        /// </summary>
        Task<bool> Cancel();

        /// <summary>
        /// Attempts to replace an order. The result is true if a request was sent to
        /// the exchange (but not necessarily processed yet), false otherwise.
        ///
        /// Only limit orders without fills can be replaced.
        /// </summary>
        Task<bool> Replace(decimal quantity, decimal price);

        /// <summary>
        /// Attempts to replace an order. If it's not possible due to the
        /// order being partially filled, then attempts to cancel it.
        /// In pseudocode:
        /// 
        ///   if (CanReplace()) Replace(quantity, price);
        ///   else Cancel();
        /// </summary>
        Task<bool> ReplaceOrCancel(decimal quantity, decimal price);

        /// <summary>
        /// Attempts to request order status from the exchange. The result is true
        /// if a request was sent to the exchange (but not necessarily processed
        /// yet), false otherwise.
        /// </summary>
        Task<bool> RequestStatus();
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
    /// On order in the limit order book. It may belong to a third party.
    /// It may be even an aggregate of several orders on the same price level.
    /// </summary>
    public class MarketOrder : ICloneable
    {
        /// <summary>
        /// Is it an order to buy or to sell?
        /// </summary>
        public Side Side;

        /// <summary>
        /// Price of the order, per lot. The total price of the order is Price * Quantity.
        /// </summary>
        public decimal Price;

        /// <summary>
        /// How big is the order?
        /// </summary>
        public decimal Quantity;

        public override string ToString()
        {
            return String.Format("(Side = {0}, Price = {1}, Quantity = {2})",
                                 Side, Price, Quantity);
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// A successful trade transaction between two parties.
    /// </summary>
    public class MarketTrade : ICloneable
    {
        /// <summary>
        /// Price at which the transaction is executed, per lot.
        /// The total price of the transaction is Price * Quantity.
        /// </summary>
        public decimal Price;

        /// <summary>
        /// How big is the transaction, in lots.
        /// </summary>
        public decimal Quantity;

        /// <summary>
        /// TODO: figure out what this field means.
        /// </summary>
        public Side? Side;

        /// <summary>
        /// Time of the transaction.
        /// </summary>
        public DateTime? Timestamp;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.AppendFormat("(Price = {0}, Quantity = {1}", Price, Quantity);
            if (Side.HasValue) res.AppendFormat(", Side = {0}", Side.Value);
            if (Timestamp.HasValue) res.AppendFormat(", Timestamp = {0}", Timestamp.Value);
            res.Append(")");
            return res.ToString();
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    public enum DiffType
    {
        New,
        Change,
        Delete,
    }

    public class MarketOrderDiff : ICloneable
    {
        public DiffType Type;
        public MarketOrder Order;

        public override string ToString()
        {
            return String.Format("(Type = {0}, Order = {1})", Type, Order);
        }

        public object Clone()
        {
            MarketOrderDiff res = (MarketOrderDiff)MemberwiseClone();
            if (res.Order != null) res.Order = (MarketOrder)res.Order.Clone();
            return res;
        }
    }

    /// <summary>
    /// Market data for one instrument.
    /// </summary>
    public class MarketData : ICloneable
    {
        /// <summary>
        /// Server time when this market data was generated by the server.
        /// </summary>
        public DateTime ServerTime;

        /// <summary>
        /// Market data for what?
        /// </summary>
        public string Symbol;

        /// <summary>
        /// Limit order book.
        /// 
        /// Both Diff and Snapshot can't be set at the same time.
        /// </summary>
        public List<MarketOrder> Snapshot;

        /// <summary>
        /// Both Diff and Snapshot can't be set at the same time.
        /// </summary>
        public List<MarketOrderDiff> Diff;

        /// <summary>
        /// Trades by third parties together with our own trades.
        /// </summary>
        public List<MarketTrade> Trades;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.AppendFormat("(ServerTime = {0}, Symbol = {1}", ServerTime, Symbol);
            Append(res, "Snapshot", Snapshot);
            Append(res, "Diff", Diff);
            Append(res, "Trades", Trades);
            res.Append(")");
            return res.ToString();
        }

        public object Clone()
        {
            return new MarketData()
            {
                ServerTime = ServerTime,
                Symbol = Symbol,
                Snapshot = Clone(Snapshot),
                Diff = Clone(Diff),
                Trades = Clone(Trades),
            };
        }

        static void Append(StringBuilder buf, string name, IEnumerable<object> collection)
        {
            if (collection == null || !collection.Any()) return;
            buf.AppendFormat(", {0} = ({1})", name, String.Join(", ", collection.Select(o => o.ToString())));
        }

        static List<T> Clone<T>(IEnumerable<T> collection) where T : ICloneable
        {
            if (collection == null) return null;
            return collection.Select(o => o == null ? default(T) : (T)o.Clone()).ToList();
        }
    }

    public class Asset : ICloneable
    {
        public decimal Available = 0m;
        // This field is always zero when dealing with BTCC.
        public decimal InUse = 0m;

        public object Clone()
        {
            return MemberwiseClone();
        }

        public override string ToString()
        {
            return String.Format("(Available = {0}, InUse = {1})", Available, InUse);
        }
    }

    public class AccountInfo : ICloneable
    {
        public Dictionary<string, Asset> Assets;

        public object Clone()
        {
            var res = new AccountInfo();
            if (Assets == null) return res;
            foreach (var kv in Assets)
            {
                res.Assets.Add(kv.Key, (Asset)kv.Value.Clone());
            }
            return res;
        }

        public override string ToString()
        {
            var res = new StringBuilder("(");
            if (Assets != null)
            {
                bool empty = true;
                foreach (var kv in Assets)
                {
                    if (!empty) res.Append(", ");
                    res.AppendFormat("{0} = {1}", kv.Key, kv.Value);
                    empty = false;
                }
            }
            res.Append(")");
            return res.ToString();
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
        /// Server time, when this message was sent by the server. (if applicable)
        /// </summary>
        public DateTime? SendingTime;
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

        public MarketData MarketData;

        public AccountInfo AccountInfo;

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
            if (MarketData != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("MarketData = {0}", MarketData);
                empty = false;
            }
            if (AccountInfo != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("AccountInfo = {0}", AccountInfo);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }

        public object Clone()
        {
            return new OrderEvent()
            {
                SendingTime = this.SendingTime,
                State = (OrderState)State.Clone(),
                Fill = (Fill)Fill.Clone(),
                MarketData = (MarketData)MarketData.Clone(),
                AccountInfo = (AccountInfo)AccountInfo.Clone(),
            };
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

        /// <summary>
        /// If set, the order will be automatically cancelled if it's still active
        /// after the specified time.
        /// </summary>
        public TimeSpan? TimeToLive;

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
        /// The notifications are delivered from a single thread, the same one that is used
        /// for all network IO. It's OK to call methods of IClient or IOrderCtrl in the callback
        /// but it's not OK to block there. An attempt to call IOrderCtrl.Cancel().Result
        /// (that is, to block until the cancel request is sent) will cause a deadlock.
        /// </summary>
        event Action<OrderEvent> OnOrderEvent;

        /// <summary>
        /// Creates a new order.
        ///
        /// The task holds null if we were unable to send the request to place the order. In this case
        /// OnOrderEvent is fired once for the order with Status = Finished (this happens before the
        /// task finishes).
        ///
        /// Otherwise the task completes immediately after the request is sent and usually before
        /// the response from the exchange is received. The initial state of the order is defined
        /// as follows:
        ///   UserID = request.UserID
        ///   Status = OrderStatus.Created
        ///   LeftQuantity = request.Quantity
        ///   Price = request.Price
        ///
        /// OnOrderEvent will fire whenever the state of the order changes. It shall not be called
        /// synchronously from any methods of IClient or IOrderCtrl. All change notifications coming
        /// from the same IClient object are serialized, even the ones that belong to different orders.
        ///
        /// The order is sent to the exchange asynchronously. The events for it may be generated
        /// before the task finishes and even before CreateOrder returns.
        /// </summary>
        Task<IOrderCtrl> CreateOrder(NewOrderRequest request);

        /// <summary>
        /// Transition to the "connected" state, in which the client MAY have a live connection to
        /// the exchange and thus may raise order events.
        /// 
        /// This method has no preconditions. It's OK to call it any time from any thread.
        /// </summary>
        Task Connect();

        /// <summary>
        /// Transition to the "disconnected" state, in which the client SHALL NOT have a live
        /// connection to the exchange and SHALL NOT raise order events.
        /// 
        /// This method has no preconditions. It's OK to call it any time from any thread.
        /// 
        /// NOTE: Dispose() is equivalent to Disconnect().Wait(). It's OK to use Disconnect() as an
        /// asynchronous Dispose().
        /// 
        /// WARNING: Any unfinished orders you might have had before calling Disconnect() won't
        /// finish, ever!
        /// </summary>
        Task Disconnect();

        /// <summary>
        /// If called while disconnected, equivalent to Connect(). Otherwise closes and reopens
        /// the connection to the exchange. Unlike Disconnect(), doesn't fuck up unfinished orders.
        /// </summary>
        Task Reconnect();

        /// <summary>
        /// Requests a snapshot of market data from the exchange. The snapshot will be delivered
        /// asynchronously to the event callback.
        /// 
        /// The task completes with `true` as soon as the request is sent to the exchange (without
        /// waiting for the reply). If the request can't be send to the exchange (for example, if
        /// there is no connection), the task completes with `false`.
        /// </summary>
        Task<bool> RequestMarketData(string symbol);

        /// <summary>
        /// Requests account info from the exchange. The info will be delivered asynchronously to
        /// the event callback.
        /// 
        /// The task completes with `true` as soon as the request is sent to the exchange (without
        /// waiting for the reply). If the request can't be send to the exchange (for example, if
        /// there is no connection), the task completes with `false`.
        /// </summary>
        Task<bool> RequestAccountInfo();

        /// <summary>
        /// Requests status updates for all open orders from the exchange. The info will be delivered
        /// asynchronously to the event callback.
        /// 
        /// The task completes with `true` as soon as the request is sent to the exchange (without
        /// waiting for the reply). If the request can't be send to the exchange (for example, if
        /// there is no connection), the task completes with `false`.
        /// </summary>
        Task<bool> RequestMassOrderStatus(string symbol);
    }
}
