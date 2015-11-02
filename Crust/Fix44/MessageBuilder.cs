using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    // This class takes care of creating outgoing FIX messages.
    class MessageBuilder
    {
        readonly ClientConfig _cfg;
        readonly ClOrdIDGenerator _clOrdIDGenerator;

        public MessageBuilder(ClientConfig cfg)
        {
            _cfg = cfg;
            _clOrdIDGenerator = new ClOrdIDGenerator(cfg.ClOrdIDPrefix);
        }

        public ClientConfig Config
        {
            get { return _cfg; }
        }

        public Mantle.Fix44.Logon Logon()
        {
            var res = new Mantle.Fix44.Logon() { StandardHeader = StandardHeader() };
            res.EncryptMethod.Value = 0;
            res.HeartBtInt.Value = _cfg.HeartBtInt;
            if (_cfg.Username != null) res.Username.Value = _cfg.Username;
            res.Password.Value = _cfg.Password;
            res.ResetSeqNumFlag.Value = true;
            return res;
        }

        public Mantle.Fix44.MarketDataRequest[] MarketDataRequest(string symbol)
        {
            // We need to request a snapshot of bids ('0'), a snapshot of asks ('1'), and live trades ('2').
            // OKcoin doesn't allow all three to be sent in a single message. Thus we send one message
            // with MDEntryTypes = {'0', '1'} and then another one with MDEntryTypes = {'2'}.
            char[][] mdEntryTypes = { new[] { '0', '1' }, new[] { '2' } };
            Mantle.Fix44.MarketDataRequest[] res = new Mantle.Fix44.MarketDataRequest[mdEntryTypes.Length];
            for (int i = 0; i != res.Length; ++i)
            {
                res[i] = new Mantle.Fix44.MarketDataRequest() { StandardHeader = StandardHeader() };
                var instrument = new Mantle.Fix44.Instrument();
                instrument.Symbol.Value = symbol;
                res[i].RelatedSym.Add(instrument);
                res[i].MDReqID.Value = Guid.NewGuid().ToString();
                res[i].SubscriptionRequestType.Value = '1';
                res[i].MarketDepth.Value = 0;
                res[i].MDUpdateType.Value = 1;
                foreach (char c in mdEntryTypes[i])
                {
                    res[i].MDEntryTypes.Add(new Mantle.Fix44.MDEntryType() { Value = c });
                }
            }
            return res;
        }

        public Mantle.Fix44.TestRequest TestRequest(string testReqID)
        {
            var res = new Mantle.Fix44.TestRequest() { StandardHeader = StandardHeader() };
            res.TestReqID.Value = testReqID;
            return res;
        }

        public Mantle.Fix44.Heartbeat Heartbeat(string testReqID)
        {
            var res = new Mantle.Fix44.Heartbeat() { StandardHeader = StandardHeader() };
            res.TestReqID.Value = testReqID;
            if (_cfg.Username != null) res.Username.Value = _cfg.Username;
            res.Password.Value = _cfg.Password;
            return res;
        }

        public Mantle.Fix44.NewOrderSingle NewOrderSingle(NewOrderRequest request)
        {
            var res = new Mantle.Fix44.NewOrderSingle() { StandardHeader = StandardHeader() };
            res.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
            res.Account.Value = _cfg.Account;
            if (_cfg.PartyID != null)
            {
                var party = new Mantle.Fix44.Party();
                party.PartyID.Value = _cfg.PartyID;
                party.PartyIDSource.Value = _cfg.PartyIDSource;
                party.PartyRole.Value = _cfg.PartyRole;
                res.PartyGroup.Add(party);
            }
            if (_cfg.TradingSessionID != null)
                res.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = _cfg.TradingSessionID });
            res.Instrument.Symbol.Value = request.Symbol;
            res.Side.Value = request.Side == Side.Buy ? '1' : '2';
            res.TransactTime.Value = res.StandardHeader.SendingTime.Value;
            res.OrderQtyData.OrderQty.Value = request.Quantity;
            res.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
            if (request.Price.HasValue)
                res.Price.Value = request.Price.Value;
            return res;
        }

        public Mantle.Fix44.OrderCancelRequest OrderCancelRequest(NewOrderRequest request, string orderID)
        {
            var res = new Mantle.Fix44.OrderCancelRequest() { StandardHeader = StandardHeader() };
            res.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
            // This field is required. It's treated differently by different exchanges:
            // - MOEX ignores this field (it it'll reject the request if the field isn't set).
            // - OKcoin uses this field to identify the order. Note that they want OrderID (!) there.
            res.OrigClOrdID.Value = orderID;
            // MOEX identifies the order based on this field. OKcoin ignores this field.
            res.OrderID.Value = orderID;
            res.Instrument.Symbol.Value = request.Symbol;
            res.Side.Value = request.Side == Side.Buy ? '1' : '2';
            res.TransactTime.Value = res.StandardHeader.SendingTime.Value;
            return res;
        }

        public Mantle.Fix44.OrderCancelReplaceRequest OrderCancelReplaceRequest(
            NewOrderRequest request, string orderID, decimal quantity, decimal price, OnReplaceReject onReject)
        {
            var res = new Mantle.Fix44.OrderCancelReplaceRequest() { StandardHeader = StandardHeader() };
            res.ClOrdID.Value = _clOrdIDGenerator.GenerateID();
            res.OrigClOrdID.Value = res.ClOrdID.Value;
            res.OrderID.Value = orderID;
            res.Account.Value = _cfg.Account;
            if (_cfg.PartyID != null)
            {
                var party = new Mantle.Fix44.Party();
                party.PartyID.Value = _cfg.PartyID;
                party.PartyIDSource.Value = _cfg.PartyIDSource;
                party.PartyRole.Value = _cfg.PartyRole;
                res.PartyGroup.Add(party);
            }
            res.Instrument.Symbol.Value = request.Symbol;
            res.Price.Value = price;
            res.OrderQty.Value = quantity;
            if (_cfg.TradingSessionID != null)
                res.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = _cfg.TradingSessionID });
            res.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
            res.Side.Value = request.Side == Side.Buy ? '1' : '2';
            res.TransactTime.Value = res.StandardHeader.SendingTime.Value;
            if (onReject == OnReplaceReject.Cancel)
                res.CancelOrigOnReject.Value = true;
            return res;
        }

        Mantle.Fix44.StandardHeader StandardHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = _cfg.SenderCompID;
            res.TargetCompID.Value = _cfg.TargetCompID;
            res.SendingTime.Value = DateTime.UtcNow;
            return res;
        }
    }
}
