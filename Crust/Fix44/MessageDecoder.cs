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

    class Lazy<T> where T : class, new()
    {
        T _value;

        public T Value
        {
            get
            {
                if (_value == null) _value = new T();
                return _value;
            }
        }

        public T ValueOrNull
        {
            get { return _value; }
        }

        public bool Has
        {
            get { return _value != null; }
        }

        public override string ToString()
        {
            return _value == null ? "null" : _value.ToString();
        }
    }

    // Data received from the exchange that is relevant to us.
    // All fields are optional (may be null). They are extracted independently from incoming messages.
    class IncomingMessage
    {
        // Server timestamp (if available).
        public DateTime? SendingTime;

        // If set, we need to reply with a heartbeat.
        public string TestReqID;

        // If set, it's a reply to our test request.
        public string TestRespID;

        // If set, we are supposed to have an order with this ID.
        // Order.OrderID should contain the new ID that the exchange
        // has assigned to the order.
        public string OrigOrderID;

        // These fields are new'ed to make setting them easier.
        public Lazy<OrderOpID> Op = new Lazy<OrderOpID>();
        public Lazy<OrderUpdate> Order = new Lazy<OrderUpdate>();
        public Lazy<FillData> Fill = new Lazy<FillData>();
        public Lazy<MarketData> MarketData = new Lazy<MarketData>();
        public Lazy<AccountInfo> AccountInfo = new Lazy<AccountInfo>();

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
            if (TestRespID != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("TestRespID = {0}", TestRespID);
                empty = false;
            }
            if (OrigOrderID != null)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("OrigOrderID = {0}", OrigOrderID);
                empty = false;
            }
            if (Op.Has)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Op = {0}", Op);
                empty = false;
            }
            if (Order.Has)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Order = {0}", Order);
                empty = false;
            }
            if (Fill.Has)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("Fill = {0}", Fill);
                empty = false;
            }
            if (MarketData.Has)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("MarketData = {0}", MarketData);
                empty = false;
            }
            if (AccountInfo.Has)
            {
                if (!empty) res.Append(", ");
                res.AppendFormat("AccountInfo = {0}", AccountInfo);
                empty = false;
            }
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
        readonly Extensions _extensions;

        // SessionID is DurableMessage.SessionID.
        public MessageDecoder(long sessionID, Extensions extensions)
        {
            _sessionID = sessionID;
            _extensions = extensions;
        }

        public IncomingMessage Visit(Mantle.Fix44.Logon msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.SequenceReset msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.ResendRequest msg) { return null; }
        public IncomingMessage Visit(Mantle.Fix44.OrderMassCancelReport msg) { return null; }

        public IncomingMessage Visit(Mantle.Fix44.Logout msg)
        {
            _log.Warn("Received Logout from the exchange. Expect the connection to break!");
            return null;
        }

        public IncomingMessage Visit(Mantle.Fix44.Heartbeat msg) {
            if (!msg.TestReqID.HasValue) return null;
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            res.TestRespID = msg.TestReqID.Value;
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.TestRequest msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (msg.TestReqID.HasValue)
                res.TestReqID = msg.TestReqID.Value;
            else
                _log.Warn("TestRequest is missing TestReqID");
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.Reject msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (msg.RefSeqNum.HasValue)
                res.Op.Value.SeqNum = new DurableSeqNum { SessionID = _sessionID, SeqNum = msg.RefSeqNum.Value };
            else
                _log.Warn("Reject is missing RefSeqNum");
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.OrderCancelReject msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (msg.ClOrdID.HasValue)
                res.Op.Value.ClOrdID = msg.ClOrdID.Value;
            else
                _log.Warn("OrderCancelReject is missing ClOrdID");
            // 0: Too late to cancel.
            // 1: Unknown order.
            // OKcoin doesn't document this field but they actually support it.
            // It's important for iFix.
            if (msg.CxlRejReason.HasValue && (msg.CxlRejReason.Value == 0 || msg.CxlRejReason.Value == 1))
                res.Order.Value.Status = OrderStatus.Finished;
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.ExecutionReport msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (msg.ClOrdID.HasValue)
                res.Op.Value.ClOrdID = msg.ClOrdID.Value;
            else
                _log.Warn("ExecutionReport is missing ClOrdID");
            if (msg.OrigOrderID.HasValue)
                res.OrigOrderID = msg.OrigOrderID.Value;
            if (msg.OrderID.HasValue)
                res.Order.Value.OrderID = msg.OrderID.Value;
            switch (msg.OrdStatus.Value)
            {
                // New.
                case '0': res.Order.Value.Status = OrderStatus.Accepted; break;
                // Partially filled.
                case '1': res.Order.Value.Status = OrderStatus.PartiallyFilled; break;
                // Filled.
                case '2': res.Order.Value.Status = OrderStatus.Finished; break;
                // Cancelled.
                case '4': res.Order.Value.Status = OrderStatus.Finished; break;
                // Pending cancel.
                case '6': res.Order.Value.Status = OrderStatus.TearingDown; break;
                // Rejected.
                case '8': res.Order.Value.Status = OrderStatus.Finished; break;
                // Suspended.
                case '9': res.Order.Value.Status = OrderStatus.Finished; break;
                // Pending replace.
                case 'E': res.Order.Value.Status = OrderStatus.Accepted; break;
                default:
                    _log.Warn("Unknown OrdStatus '{0}' in message {1}", msg.OrdStatus.Value, msg);
                    break;
            }
            if (msg.Price.HasValue && msg.Price.Value > 0)
                res.Order.Value.Price = msg.Price.Value;
            // If OrdStatus is TearingDown, LeavesQty is set to 0 even though the order is still active.
            // We'd rather leave the last value.
            if (msg.LeavesQty.HasValue && msg.OrdStatus.Value != '6')
                res.Order.Value.LeftQuantity = msg.LeavesQty.Value;
            if (msg.CumQty.HasValue)
                res.Order.Value.FillQuantity = msg.CumQty.Value;
            if (msg.AvgPx.HasValue)
                res.Order.Value.AverageFillPrice = msg.AvgPx.Value;
            if (msg.Symbol.HasValue)
                res.Fill.Value.Symbol = msg.Symbol.Value;
            if (msg.Side.HasValue)
            {
                if (msg.Side.Value == '1') res.Fill.Value.Side = Side.Buy;
                else if (msg.Side.Value == '2') res.Fill.Value.Side = Side.Sell;
                else _log.Warn("Unknown Side '{0}' in message {1}", msg.Side.Value, msg);
            }
            if (msg.LastQty.HasValue && msg.LastQty.Value > 0)
                res.Fill.Value.Quantity = msg.LastQty.Value;
            if (msg.LastPx.HasValue && msg.LastPx.Value > 0)
                res.Fill.Value.Price = msg.LastPx.Value;
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.MarketDataResponse msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (msg.OrigTime.HasValue)
            {
                res.MarketData.Value.ServerTime = msg.OrigTime.Value;
            }
            else if (msg.StandardHeader.SendingTime.HasValue)
            {
                res.MarketData.Value.ServerTime = msg.StandardHeader.SendingTime.Value;
            }
            else
            {
                _log.Warn("MarketDataResponse is missing OrigTime and SendingTime fields: {0}", msg);
                return null;
            }
            if (!msg.Instrument.Symbol.HasValue)
            {
                _log.Warn("MarketDataResponse is missing Symbol field: {0}", msg);
                return null;
            }
            res.MarketData.Value.Symbol = msg.Instrument.Symbol.Value;
            foreach (Mantle.Fix44.MDEntry entry in msg.MDEntries)
            {
                string error;
                MarketOrder order = MakeOrder(entry, out error);
                if (order != null)
                {
                    if (res.MarketData.Value.Snapshot == null)
                        res.MarketData.Value.Snapshot = new List<MarketOrder>();
                    res.MarketData.Value.Snapshot.Add(order);
                    continue;
                }
                if (error != null)
                {
                    _log.Warn("{0}: {1}", error, msg);
                    continue;
                }
                // ServerTime was already set above.
                MarketTrade trade = MakeTrade(res.MarketData.Value.ServerTime, entry, out error);
                if (trade != null)
                {
                    if (res.MarketData.Value.Trades == null)
                        res.MarketData.Value.Trades = new List<MarketTrade>();
                    res.MarketData.Value.Trades.Add(trade);
                    continue;
                }
                _log.Warn("Ignoring MDEntry of unkonwn format: {0}", msg);
            }
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.MarketDataIncrementalRefresh msg)
        {
            var res = new IncomingMessage();
            res.SendingTime = msg.StandardHeader.SendingTime.Value;
            if (!msg.StandardHeader.SendingTime.HasValue)
            {
                _log.Warn("MarketDataResponse is missing SendingTime field: {0}", msg);
                return null;
            }
            res.MarketData.Value.ServerTime = msg.StandardHeader.SendingTime.Value;
            if (!msg.Instrument.Symbol.HasValue)
            {
                _log.Warn("MarketDataResponse is missing Symbol field: {0}", msg);
                return null;
            }
            res.MarketData.Value.Symbol = msg.Instrument.Symbol.Value;
            foreach (Mantle.Fix44.MDEntry entry in msg.MDEntries)
            {
                string error;
                MarketOrder order = MakeOrder(entry, out error);
                if (order != null)
                {
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
                    if (res.MarketData.Value.Diff == null)
                        res.MarketData.Value.Diff = new List<MarketOrderDiff>();
                    res.MarketData.Value.Diff.Add(diff);
                    continue;
                }
                if (error != null)
                {
                    _log.Warn("{0}: {1}", error, msg);
                    continue;
                }
                _log.Warn("Ignoring MDEntry of unkonwn format: {0}", msg);
            }
            return res;
        }

        public IncomingMessage Visit(Mantle.Fix44.AccountInfoResponse msg)
        {
            switch (_extensions)
            {
                case Extensions.OkCoin:
                    {
                        if (!msg.Currency.HasValue)
                        {
                            _log.Warn("AccountInfoResponse is missing Currency field: {0}", msg);
                            return null;
                        }
                        string[] currency = msg.Currency.Value.Split('/');
                        if (currency.Length != 3 || currency.Any(s => s.Length == 0))
                        {
                            _log.Warn("AccountInfoResponse has invalid Currency field: {0}", msg);
                            return null;
                        }
                        if (!msg.FreeCurrency1.HasValue || !msg.FreeCurrency2.HasValue || !msg.FreeCurrency3.HasValue)
                        {
                            _log.Warn("AccountInfoResponse is missing FreeCurrencyX field: {0}", msg);
                            return null;
                        }
                        if (!msg.FrozenCurrency1.HasValue || !msg.FrozenCurrency2.HasValue || !msg.FrozenCurrency3.HasValue)
                        {
                            _log.Warn("AccountInfoResponse is missing FrozenCurrencyX field: {0}", msg);
                            return null;
                        }
                        var assets = new Dictionary<string, Asset>();
                        // Note that the mapping between currency[i] and {Free,Used}Currency[j] isn't straightforward.
                        assets[currency[0]] = new Asset() { Available = msg.FreeCurrency3.Value, InUse = msg.FrozenCurrency3.Value };
                        assets[currency[1]] = new Asset() { Available = msg.FreeCurrency1.Value, InUse = msg.FrozenCurrency1.Value };
                        assets[currency[2]] = new Asset() { Available = msg.FreeCurrency2.Value, InUse = msg.FrozenCurrency2.Value };
                        if (assets.Count != 3)
                        {
                            // This can happen if there are dups in `currency`.
                            _log.Warn("AccountInfoResponse has invalid Currency field: {0}", msg);
                            return null;
                        }
                        var res = new IncomingMessage();
                        res.SendingTime = msg.StandardHeader.SendingTime.Value;
                        res.AccountInfo.Value.Assets = assets;
                        return res;
                    }
                case Extensions.Huobi:
                    {
                        var res = new IncomingMessage();
                        res.SendingTime = msg.StandardHeader.SendingTime.Value;
                        res.AccountInfo.Value.Assets = new Dictionary<string, Asset>();
                        if (msg.HuobiAvailableCny.HasValue && msg.HuobiFrozenCny.HasValue)
                        {
                            res.AccountInfo.Value.Assets.Add(
                                "cny", new Asset() { Available = msg.HuobiAvailableCny.Value, InUse = msg.HuobiFrozenCny.Value });
                        }
                        if (msg.HuobiAvailableBtc.HasValue && msg.HuobiFrozenBtc.HasValue)
                        {
                            res.AccountInfo.Value.Assets.Add(
                                "btc", new Asset() { Available = msg.HuobiAvailableBtc.Value, InUse = msg.HuobiFrozenBtc.Value });
                        }
                        if (msg.HuobiAvailableLtc.HasValue && msg.HuobiFrozenLtc.HasValue)
                        {
                            res.AccountInfo.Value.Assets.Add(
                                "ltc", new Asset() { Available = msg.HuobiAvailableLtc.Value, InUse = msg.HuobiFrozenLtc.Value });
                        }
                        return res;
                    }
            }
            _log.Warn("Don't know how to parse AccountInfoResponse. Did you specify the right value for the Client.Extensions field?");
            return null;
        }

        // Three possible outcomes:
        // - Result is not null. That's success. In this case error is guaranteed to be null.
        // - Result is null, error is null. It means the entry isn't a trade. Not an error.
        // - Result is null, error is not null. That's an error.
        static MarketTrade MakeTrade(DateTime msgTime, Mantle.Fix44.MDEntry entry, out string error)
        {
            var res = new MarketTrade();
            error = null;

            if (!entry.MDEntryType.HasValue)
            {
                error = "Missing MDEntryType field";
                return null;
            }

            if (entry.MDEntryType.Value != '2') return null;  // No error.

            if (!entry.MDEntryPx.HasValue)
            {
                error = "Missing MDEntryPx field";
                return null;
            }
            res.Price = entry.MDEntryPx.Value;
            if (!entry.MDEntrySize.HasValue)
            {
                error = "Missing MDEntrySize field";
                return null;
            }
            res.Quantity = entry.MDEntrySize.Value;

            if (entry.Side.HasValue)
            {
                if (entry.Side.Value == '1') res.Side = Side.Buy;
                else if (entry.Side.Value == '2') res.Side = Side.Sell;
                else _log.Warn("Unknown value of Side field: {0}", entry.Side.Value);
            }

            if (entry.MDEntryTime.HasValue)
            {
                // Huobi sends MDEntryTime with every trade report but this field contains only
                // time without the date. We combine it with OrigTime or SendingTime (whichever is present)
                // to get a full timestamp.
                res.Timestamp = Combine(msgTime, entry.MDEntryTime.Value);
            }

            return res;
        }

        static DateTime Combine(DateTime baseTimestamp, TimeSpan timeOnly)
        {
            return new int[] { -1, 0, 1 }
                .Select(n => baseTimestamp.Date + TimeSpan.FromDays(n) + timeOnly)
                .OrderBy(d => Math.Abs(d.Ticks - baseTimestamp.Ticks))
                .First();
        }

        // Three possible outcomes:
        // - Result is not null. That's success. In this case error is guaranteed to be null.
        // - Result is null, error is null. It means the entry isn't an order. Not an error.
        // - Result is null, error is not null. That's an error.
        static MarketOrder MakeOrder(Mantle.Fix44.MDEntry entry, out string error)
        {
            var res = new MarketOrder();
            error = null;

            if (!entry.MDEntryType.HasValue)
            {
                error = "Missing MDEntryType field";
                return null;
            }

            if (entry.MDEntryType.Value == '0') res.Side = Side.Buy;
            else if (entry.MDEntryType.Value == '1') res.Side = Side.Sell;
            else return null;  // No error.

            if (!entry.MDEntryPx.HasValue)
            {
                error = "Missing MDEntryPx field";
                return null;
            }
            res.Price = entry.MDEntryPx.Value;
            if (!entry.MDEntrySize.HasValue)
            {
                error = "Missing MDEntrySize field";
                return null;
            }
            res.Quantity = entry.MDEntrySize.Value;

            return res;
        }
    }
}
