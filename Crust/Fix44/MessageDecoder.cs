﻿using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    // Data received from the exchange that is relevant to fills.
    // All fields are optional (may be null). They are extracted independently from incoming messages.
    class FillData
    {
        public string Symbol;
        public Side? Side;
        public decimal? Quantity;
        public decimal? Price;

        // If all necessary fields are set, returns a valid fill.
        // Otherwise returns null.
        public Fill MakeFill()
        {
            if (Symbol == null || !Side.HasValue || !Quantity.HasValue || !Price.HasValue) return null;
            return new Fill() { Symbol = Symbol, Side = Side.Value, Quantity = Quantity.Value, Price = Price.Value };
        }

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (Symbol != null)
            {
                res.AppendFormat("Symbol = {0}", Symbol);
                empty = false;
            }
            if (Side != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Side = {0}", Side);
                empty = false;
            }
            if (Quantity != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Quantity = {0}", Quantity);
                empty = false;
            }
            if (Price != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Price = {0}", Price);
                empty = false;
            }
            res.Append(")");
            return res.ToString();
        }
    }

    // Data received from the exchange that is relevant to us.
    // All fields are optional (may be null). They are extracted independently from incoming messages.
    class IncomingMessage
    {
        // If set, we need to reply with a heartbeat.
        public string TestReqID;

        // If set, we are supposed to have an order with this ID.
        // Order.OrderID should contain the new ID that the exchange
        // has assigned to the order.
        public string OrigOrderID;

        // These fields are new'ed to make setting them easier.
        public OrderOpID Op = new OrderOpID();
        public OrderUpdate Order = new OrderUpdate();
        public FillData Fill = new FillData();
        public MarketData MarketData = new MarketData();

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool comma = false;
            comma = Append(res, comma, TestReqID, "TestReqID");
            comma = Append(res, comma, OrigOrderID, "OrigOrderID");
            comma = Append(res, comma, Op, "Op");
            comma = Append(res, comma, Order, "Order");
            comma = Append(res, comma, Fill, "Fill");
            comma = Append(res, comma, MarketData, "MarketData");
            res.Append(")");
            return res.ToString();
        }

        static bool Append(StringBuilder buf, bool comma, object obj, string name)
        {
            if (obj == null) return comma;
            string val = obj.ToString();
            if (val == "()" && obj.GetType() != typeof(String)) return comma;
            if (comma) buf.Append(", ");
            buf.AppendFormat("{0} = {1}", name, val);
            return true;
        }
    }

    // This class transforms an incoming FIX message into its distilled form.
    class MessageDecoder : Mantle.Fix44.IServerMessageVisitor<IncomingMessage>
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly long _sessionID;

        // SessionID is DurableMessage.SessionID.
        public MessageDecoder(long sessionID)
        {
            _sessionID = sessionID;
        }

        public IncomingMessage Visit(Mantle.Fix44.Logon msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.Heartbeat msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.SequenceReset msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.ResendRequest msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.OrderMassCancelReport msg) { return null; }

        public IncomingMessage Visit(Mantle.Fix44.TestRequest msg)
        {
            var res = new IncomingMessage();
            if (msg.TestReqID.HasValue)
                res.TestReqID = msg.TestReqID.Value;
            else
                _log.Warn("TestRequest is missing TestReqID");
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.Reject msg)
        {
            var res = new IncomingMessage();
            if (msg.RefSeqNum.HasValue)
                res.Op.SeqNum = new DurableSeqNum { SessionID = _sessionID, SeqNum = msg.RefSeqNum.Value };
            else
                _log.Warn("Reject is missing RefSeqNum");
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.OrderCancelReject msg)
        {
            var res = new IncomingMessage();
            if (msg.ClOrdID.HasValue)
                res.Op.ClOrdID = msg.ClOrdID.Value;
            else
                _log.Warn("OrderCancelReject is missing ClOrdID");
            // 0: Too late to cancel.
            // 1: Unknown order.
            if (msg.CxlRejReason.HasValue && (msg.CxlRejReason.Value == 0 || msg.CxlRejReason.Value == 1))
                res.Order.Status = OrderStatus.Finished;
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.ExecutionReport msg)
        {
            var res = new IncomingMessage();
            if (msg.ClOrdID.HasValue)
                res.Op.ClOrdID = msg.ClOrdID.Value;
            else
                _log.Warn("ExecutionReport is missing ClOrdID");
            if (msg.OrigOrderID.HasValue)
                res.OrigOrderID = msg.OrigOrderID.Value;
            if (msg.OrderID.HasValue)
                res.Order.OrderID = msg.OrderID.Value;
            switch (msg.OrdStatus.Value)
            {
                // New.
                case '0': res.Order.Status = OrderStatus.Accepted; break;
                // Partially filled.
                case '1': res.Order.Status = OrderStatus.PartiallyFilled; break;
                // Filled.
                case '2': res.Order.Status = OrderStatus.Finished; break;
                // Cancelled.
                case '4': res.Order.Status = OrderStatus.Finished; break;
                // Pending cancel.
                case '6': res.Order.Status = OrderStatus.TearingDown; break;
                // Rejected.
                case '8': res.Order.Status = OrderStatus.Finished; break;
                // Suspended.
                case '9': res.Order.Status = OrderStatus.Finished; break;
                // Pending replace.
                case 'E': res.Order.Status = OrderStatus.Accepted; break;
                default:
                    _log.Warn("Unknown OrdStatus '{0}' in message {1}", msg.OrdStatus.Value, msg);
                    break;
            }
            if (msg.Price.HasValue && msg.Price.Value > 0)
                res.Order.Price = msg.Price.Value;
            // If OrdStatus is TearingDown, LeavesQty is set to 0 even though the order is still active.
            // We'd rather leave the last value.
            if (msg.LeavesQty.HasValue && msg.OrdStatus.Value != '6')
                res.Order.LeftQuantity = msg.LeavesQty.Value;
            if (msg.Symbol.HasValue)
                res.Fill.Symbol = msg.Symbol.Value;
            if (msg.Side.HasValue)
            {
                if (msg.Side.Value == '1') res.Fill.Side = Side.Buy;
                else if (msg.Side.Value == '2') res.Fill.Side = Side.Sell;
                else _log.Warn("Unknown Side '{0}' in message {1}", msg.Side.Value, msg);
            }
            if (msg.LastQty.HasValue && msg.LastQty.Value > 0)
                res.Fill.Quantity = msg.LastQty.Value;
            if (msg.LastPx.HasValue && msg.LastPx.Value > 0)
                res.Fill.Price = msg.LastPx.Value;
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.MarketDataResponse msg)
        {
            var res = new IncomingMessage();
            if (!msg.OrigTime.HasValue)
            {
                _log.Warn("MarketDataResponse is missing OrigTime field: {0}", msg);
                return null;
            }
            res.MarketData.ServerTime = msg.OrigTime.Value;
            if (!msg.Instrument.Symbol.HasValue)
            {
                _log.Warn("MarketDataResponse is missing Symbol field: {0}", msg);
                return null;
            }
            res.MarketData.Symbol = msg.Instrument.Symbol.Value;
            foreach (Mantle.Fix44.MDEntry entry in msg.MDEntries)
            {
                MarketOrder order;
                string error;
                switch (MakeOrder(entry, out order, out error))
                {
                    case MDEntryType.Invalid:
                        _log.Warn("{0}: {1}", error, msg);
                        break;
                    case MDEntryType.Order:
                        if (res.MarketData.Snapshot == null)
                            res.MarketData.Snapshot = new List<MarketOrder>();
                        res.MarketData.Snapshot.Add(order);
                        break;
                    case MDEntryType.Trade:
                        if (res.MarketData.Trades == null)
                            res.MarketData.Trades = new List<MarketOrder>();
                        res.MarketData.Trades.Add(order);
                        break;
                }
            }
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.MarketDataIncrementalRefresh msg)
        {
            var res = new IncomingMessage();
            if (!msg.Instrument.Symbol.HasValue)
            {
                _log.Warn("MarketDataResponse is missing Symbol field: {0}", msg);
                return null;
            }
            res.MarketData.Symbol = msg.Instrument.Symbol.Value;
            foreach (Mantle.Fix44.MDEntry entry in msg.MDEntries)
            {
                MarketOrder order;
                string error;
                switch (MakeOrder(entry, out order, out error))
                {
                    case MDEntryType.Invalid:
                        _log.Warn("{0}: {1}", error, msg);
                        break;
                    case MDEntryType.Trade:
                        _log.Warn("MarketDataIncrementalRefresh cannot contain trades: {0}", msg);
                        break;
                    case MDEntryType.Order:
                        var diff = new MarketOrderDiff();
                        diff.Order = order;
                        if (!entry.MDUpdateAction.HasValue)
                        {
                            _log.Warn("Missing MDUpdateAction field: {0}", msg);
                            break;
                        }
                        else if (entry.MDUpdateAction.Value == '0')
                        {
                            diff.Type = DiffType.New;
                        }
                        else if (entry.MDUpdateAction.Value == '1')
                        {
                            diff.Type = DiffType.Change;
                        }
                        else if (entry.MDUpdateAction.Value == '2')
                        {
                            diff.Type = DiffType.Delete;
                        }
                        else
                        {
                            _log.Warn("Invalid value of MDUpdateAction {0}: {1}", entry.MDUpdateAction.Value, msg);
                            break;
                        }
                        if (res.MarketData.Diff == null)
                            res.MarketData.Diff = new List<MarketOrderDiff>();
                        res.MarketData.Diff.Add(diff);
                        break;
                    
                }
            }
            return res;
        }

        enum MDEntryType
        {
            Invalid,
            Order,
            Trade,
        }

        static MDEntryType MakeOrder(Mantle.Fix44.MDEntry entry, out MarketOrder order, out string error)
        {
            order = new MarketOrder();
            error = null;
            if (!entry.MDEntryPx.HasValue)
            {
                error = "Missing MDEntryPx field";
                return MDEntryType.Invalid;
            }
            order.Price = entry.MDEntryPx.Value;
            if (!entry.MDEntrySize.HasValue)
            {
                error = "Missing MDEntrySize field";
                return MDEntryType.Invalid;
            }
            order.Quantity = entry.MDEntrySize.Value;
            if (!entry.MDEntryType.HasValue)
            {
                error = "Missing MDEntryType field";
                return MDEntryType.Invalid;
            }
            switch (entry.MDEntryType.Value)
            {
                case '0':
                    order.Side = Side.Buy;
                    return MDEntryType.Order;
                case '1':
                    order.Side = Side.Sell;
                    return MDEntryType.Order;
                case '2':
                    if (!entry.Side.HasValue)
                    {
                        error = "Missing Side field";
                        return MDEntryType.Invalid;
                    }
                    if (entry.Side.Value == '1')
                    {
                        order.Side = Side.Buy;
                    }
                    else if (entry.Side.Value == '2')
                    {
                        order.Side = Side.Sell;
                    }
                    else
                    {
                        error = String.Format("Invalid value of Side: {0}", entry.Side.Value);
                        return MDEntryType.Invalid;
                    }
                    return MDEntryType.Trade;
                default:
                    error = String.Format("Invalid value of MDEntryType: {0}", entry.MDEntryType.Value);
                    return MDEntryType.Invalid;
            }
        }
    }
}
