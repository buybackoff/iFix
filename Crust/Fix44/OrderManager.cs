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
    class OrderUpdate
    {
        public string OrderID;
        public OrderStatus? Status;
        public decimal? Price;
        public decimal? LeftQuantity;

        public override string ToString()
        {
            return String.Format("OrderID = {0}, Status = {1}, Price = {2}, LeftQuantity = {3}",
                                 OrderID, Status, Price, LeftQuantity);
        }
    }

    class OrderOpID
    {
        public DurableSeqNum SeqNum;
        public string ClOrdID;

        public override string ToString()
        {
            return String.Format("SeqNum = {0}, ClOrdID = {1}", SeqNum, ClOrdID);
        }
    }

    interface IOrder
    {
        string OrderID { get; }
        OrderStatus Status { get; }
        OrderState Update(OrderUpdate update);
        bool IsPending { get; }
        void SetPending(OrderOpID id);
        void FinishPending();
    }

    interface IOrderMap
    {
        void AddOrder(string orderID, IOrder order);
        void RemoveOrder(string orderID);

        void AddOp(OrderOpID id, IOrder order);
        void RemoveOp(OrderOpID id);
    }

    class Order : IOrder
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // Null if status is Created, not null otherwise.
        string _orderID = null;
        // If Status == Finished, _pending is null.
        OrderOpID _pending = null;
        // Not null.
        OrderState _state;
        // Not null.
        IOrderMap _orders;

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
            bool changed = false;
            if (update.OrderID != null && update.OrderID != _orderID)
            {
                _log.Info("Updating OrderID from {0} to {1} for order {2}", _orderID, update.OrderID, _state);
                // This will throw if we already have an order with the same ID.
                _orders.AddOrder(update.OrderID, this);
                if (_orderID != null) _orders.RemoveOrder(_orderID);
                _orderID = update.OrderID;
            }
            if (update.Status.HasValue && update.Status != Status)
            {
                _state.Status = update.Status.Value;
                changed = true;
                if (Status == OrderStatus.Finished) Finish();
            }
            if (Status != OrderStatus.Finished && _orderID == null)
            {
                // This can happen if we sent a New Order Request to the exchange and the
                // reply doesn't contain OrderID.
                _log.Warn("Removing order with unknown ID: ", _state);
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

        void Finish()
        {
            _log.Info("Order with OrderID {0} has finished: {1}", _orderID, _state);
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
                Assert.True(!_orders._byOrderID.ContainsKey(orderID), "Duplicate OrderID: {0}", orderID);
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
                _log.Info("Issued an OrderOP for order with OrderID {0}: {1}", order.OrderID, id);
                _orders._bySeqNum.Add(id.SeqNum, order);
                _orders._byClOrdID.Add(id.ClOrdID, order);
            }

            public void RemoveOp(OrderOpID id)
            {
                Assert.NotNull(id);
                Assert.NotNull(id.SeqNum);
                Assert.NotNull(id.ClOrdID);
                _log.Info("OrderOp has finished: {0}", id);
                Assert.True(_orders._byClOrdID.ContainsKey(id.ClOrdID), "Unknown ClOrdID: {0}", id.ClOrdID);
                Assert.True(_orders._bySeqNum.Remove(id.SeqNum), "Unknown SeqNum: {0}", id.SeqNum);
                Assert.True(_orders._byClOrdID.Remove(id.ClOrdID));
            }
        }
    }
}
