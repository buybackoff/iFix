using NLog;
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

        public override string ToString()
        {
            var res = new StringBuilder();
            res.Append("(");
            bool empty = true;
            if (TestReqID != null)
            {
                res.AppendFormat("TestReqID = {0}", TestReqID);
                empty = false;
            }
            if (OrigOrderID != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("OrigOrderID = {0}", OrigOrderID);
                empty = false;
            }
            if (Op != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Op = {0}", Op);
                empty = false;
            }
            if (Order != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Order = {0}", Order);
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
    }
}
