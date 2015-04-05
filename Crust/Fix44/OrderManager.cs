using iFix.Common;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    // Modification for the order received from the exchange.
    // Fields that are null are assumed to be unchanged.
    class OrderUpdate
    {
        // If not null, this is the new OrderID assigned by the exchange.
        public string OrderID;
        // If set, this is the new status.
        public OrderStatus? Status;
        // If set, this is the new price.
        public decimal? Price;
        // If set, this is the new quantity.
        public decimal? LeftQuantity;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (OrderID != null)
            {
                res.AppendFormat("OrderID = {0}", OrderID);
                empty = false;
            }
            if (Status != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Status = {0}", Status);
                empty = false;
            }
            if (Price != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Price = {0}", Price);
                empty = false;
            }
            if (LeftQuantity != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("LeftQuantity = {0}", LeftQuantity);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }
    }

    // Identifier of an operation (a.k.a. request) that we sent to the exchange.
    class OrderOpID
    {
        // Not null.
        public DurableSeqNum SeqNum;
        // Not null.
        public string ClOrdID;

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (SeqNum != null)
            {
                res.AppendFormat("SeqNum = {0}", SeqNum);
                empty = false;
            }
            if (ClOrdID != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("ClOrdID = {0}", ClOrdID);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }
    }

    interface IOrder
    {
        // Null if status is Created. May be null if status is Finished. Not null otherwise.
        string OrderID { get; }

        OrderStatus Status { get; }

        // Returns null if the state didn't change.
        // Requires: update is not null and update.Status != Created.
        OrderState Update(OrderUpdate update);

        // Is there a pending operation for the order? In other words, are we expecting an
        // update from the exchange in the near future?
        //
        // If Status is Finished, IsPending is false.
        bool IsPending { get; }

        // Requires: id is not null, Status is not Finished.
        void SetPending(OrderOpID id);

        // Requires: IsPending is true.
        void FinishPending();
    }

    // This is essentially three dictionaries:
    //   1. OrderID -> Order.
    //   2. ClOrdID -> Order.
    //   3. DurableSeqNum -> Order.
    //
    // AddOrder() and RemoveOrder() operate on the first dictionary, while AddOp() and RemoveOp() operate
    // on the second and the third.
    interface IOrderMap
    {
        // Requires: oderID and order aren't null.
        //
        // If there is already an order with the same orderID (even if it's the
        // same order), throws ArgumentException.
        void AddOrder(string orderID, IOrder order);

        // Requires: orderID is not null and there is an order with the specified ID.
        void RemoveOrder(string orderID);

        // Requires: order, id and its fields aren't null and there is no order
        // with the same SeqNum or ClOrdID.
        void AddOp(OrderOpID id, IOrder order);

        // Requires: id and its fields are not null and there is an order with the specified
        // SeqNum and ClOrdID.
        void RemoveOp(OrderOpID id);
    }

    // Our order on the exchange. Mirrors the state held on the exchange.
    class Order : IOrder
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // Null if status is Created. May be null if status is Finished. Not null otherwise.
        string _orderID = null;
        // If Status == Finished, _pending is null.
        OrderOpID _pending = null;
        // Not null.
        readonly OrderState _state;
        // Not null.
        readonly IOrderMap _orders;

        public Order(IOrderMap orders, NewOrderRequest request)
        {
            Assert.NotNull(orders);
            Assert.NotNull(request);
            _state = new OrderState()
            {
                UserID = request.UserID,
                Status = OrderStatus.Created,
                LeftQuantity = request.Quantity,
                Price = request.Price,
            };
            _orders = orders;
        }

        public string OrderID { get { return _orderID; } }

        public OrderStatus Status { get { return _state.Status; } }

        public OrderState Update(OrderUpdate update)
        {
            Assert.True(Status != OrderStatus.Finished);
            Assert.True(update.Status != OrderStatus.Created, "Invalid update {0} for order {1}", update, this);
            bool changed = false;
            // Do we have a new OrderID assigned by the exchange?
            if (update.OrderID != null && update.OrderID != _orderID)
            {
                _log.Info("Updating OrderID from {0} to {1} for order {2}", _orderID ?? "null", update.OrderID, _state);
                // This will throw if we already have an order with the same ID. The update will be ignored.
                _orders.AddOrder(update.OrderID, this);
                if (_orderID != null) _orders.RemoveOrder(_orderID);
                _orderID = update.OrderID;
            }
            // Have the status changed?
            if (update.Status.HasValue && update.Status != Status)
            {
                _state.Status = update.Status.Value;
                changed = true;
                if (Status == OrderStatus.Finished) Finish();
            }
            // Orders that aren't Created or Finished must have OrderID.
            if (Status != OrderStatus.Finished && Status != OrderStatus.Created && _orderID == null)
            {
                // This can happen if we sent a New Order Request to the exchange and the
                // reply doesn't contain OrderID.
                _log.Warn("Removing order with unknown ID: ", this);
                changed = true;
                Finish();
            }
            // Orders in state Created can't have OrderID.
            if (Status == OrderStatus.Created && _orderID != null)
            {
                // This can happen if we sent a New Order Request to the exchange and the
                // reply doesn't contain order status.
                _log.Warn("Removing order in unknown status: ", this);
                changed = true;
                Finish();
            }
            if (update.Price.HasValue && update.Price != _state.Price)
            {
                _state.Price = update.Price;
                changed = true;
            }
            if (update.LeftQuantity.HasValue && update.LeftQuantity != _state.LeftQuantity)
            {
                _state.LeftQuantity = update.LeftQuantity.Value;
                changed = true;
            }
            return changed ? (OrderState)_state.Clone() : null;
        }

        public bool IsPending { get { return _pending != null; } }

        public void SetPending(OrderOpID id)
        {
            Assert.True(!IsPending);
            Assert.True(Status != OrderStatus.Finished);
            _orders.AddOp(id, this);
            _pending = id;
        }

        public void FinishPending()
        {
            Assert.True(IsPending);
            Assert.True(Status != OrderStatus.Finished);
            _orders.RemoveOp(_pending);
            _pending = null;
        }

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (_orderID != null)
            {
                res.AppendFormat("OrderID = {0}", _orderID);
                empty = false;
            }
            if (_pending != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Pending = {0}", _pending);
                empty = false;
            }
            if (_state != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("State = {0}", _state);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }

        void Finish()
        {
            _log.Info("Order has finished: {0}", this);
            if (_pending != null)
            {
                _orders.RemoveOp(_pending);
                _pending = null;
            }
            if (_orderID != null) _orders.RemoveOrder(_orderID);
            _state.Status = OrderStatus.Finished;
        }
    }

    class OrderManager
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        Dictionary<DurableSeqNum, IOrder> _bySeqNum = new Dictionary<DurableSeqNum, IOrder>();
        Dictionary<string, IOrder> _byClOrdID = new Dictionary<string, IOrder>();
        Dictionary<string, IOrder> _byOrderID = new Dictionary<string, IOrder>();

        public IOrder CreateOrder(NewOrderRequest request)
        {
            return new Order(new OrderMap(this), request);
        }

        public IOrder FindByOrderID(string orderID)
        {
            if (orderID == null) return null;
            IOrder res;
            _byOrderID.TryGetValue(orderID, out res);
            return res;
        }

        // If id.SeqNum is specified, tries to find the associated order.
        // The same for id.ClOrdID. If both are specified, verifies that the result is the same.
        public IOrder FindByOpID(OrderOpID id)
        {
            if (id == null) return null;
            IOrder bySeqNum = null;
            IOrder byClOrdID = null;
            if (id.SeqNum != null) _bySeqNum.TryGetValue(id.SeqNum, out bySeqNum);
            if (id.ClOrdID != null) _byClOrdID.TryGetValue(id.ClOrdID, out byClOrdID);
            if (bySeqNum != null && byClOrdID != null && bySeqNum != byClOrdID)
            {
                _log.Warn("Ambiguous OrderOpID: {0}", id);
                return null;
            }
            return bySeqNum != null ? bySeqNum : byClOrdID;
        }

        class OrderMap : IOrderMap
        {
            OrderManager _orders;

            public OrderMap(OrderManager orders)
            {
                Assert.NotNull(orders);
                _orders = orders;
            }

            public void AddOrder(string orderID, IOrder order)
            {
                Assert.NotNull(orderID);
                Assert.NotNull(order);
                // This check can trigger if exchange gives us duplicate IDs, that's why
                // it's ArgumentException and not an Assert.
                if (_orders._byOrderID.ContainsKey(orderID))
                    throw new ArgumentException(String.Format("Duplicate OrderID: {0}", orderID));
                _orders._byOrderID.Add(orderID, order);
            }

            public void RemoveOrder(string orderID)
            {
                Assert.NotNull(orderID);
                Assert.True(_orders._byOrderID.Remove(orderID), "Unknown OrderID: {0}", orderID);
            }

            public void AddOp(OrderOpID id, IOrder order)
            {
                Assert.NotNull(id);
                Assert.NotNull(id.SeqNum);
                Assert.NotNull(id.ClOrdID);
                Assert.NotNull(order);
                Assert.True(!_orders._bySeqNum.ContainsKey(id.SeqNum), "Duplicate SeqNum: {0}", id.SeqNum);
                Assert.True(!_orders._byClOrdID.ContainsKey(id.ClOrdID), "Duplicate ClOrdID: {0}", id.ClOrdID);
                _log.Info("Issued OrderOP {0} for order {1}", id, order);
                _orders._bySeqNum.Add(id.SeqNum, order);
                _orders._byClOrdID.Add(id.ClOrdID, order);
            }

            public void RemoveOp(OrderOpID id)
            {
                Assert.NotNull(id);
                Assert.NotNull(id.SeqNum);
                Assert.NotNull(id.ClOrdID);
                _log.Info("OrderOp {0} has finished", id);
                IOrder bySeqNum = null;
                IOrder byClOrdID = null;
                Assert.True(_orders._bySeqNum.TryGetValue(id.SeqNum, out bySeqNum), "Unknown SeqNum: {0}", id.SeqNum);
                Assert.True(_orders._byClOrdID.TryGetValue(id.ClOrdID, out byClOrdID), "Unknown ClOrdID: {0}", id.ClOrdID);
                Assert.True(bySeqNum == byClOrdID, "Ambiguous OrderOpID: {0}", id);
                Assert.True(_orders._bySeqNum.Remove(id.SeqNum));
                Assert.True(_orders._byClOrdID.Remove(id.ClOrdID));
            }
        }
    }
}
