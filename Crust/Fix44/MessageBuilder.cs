using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    class UnsupportedOperationException : Exception
    {
        public UnsupportedOperationException(string msg) : base(msg) { }
    }

    // This class takes care of creating outgoing FIX messages.
    class MessageBuilder
    {
        readonly ClientConfig _cfg;
        readonly ClOrdIDGenerator _clOrdIDGenerator;

        public enum MarketDataType
        {
            Order,
            Trade,
        }

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

        public Mantle.Fix44.MarketDataRequest MarketDataRequest(string symbol, MarketDataType type)
        {
            var res = new Mantle.Fix44.MarketDataRequest();
            res = new Mantle.Fix44.MarketDataRequest() { StandardHeader = StandardHeader() };
            var instrument = new Mantle.Fix44.Instrument();
            instrument.Symbol.Value = symbol;
            res.RelatedSym.Add(instrument);
            // It's important for huobi that MDReqID has symbol as its prefix. Otherwise they'll
            // silently ignore our request. This is undocumented.
            res.MDReqID.Value = symbol + Guid.NewGuid().ToString();
            res.SubscriptionRequestType.Value = '1';
            res.MarketDepth.Value = 0;
            res.MDUpdateType.Value = 1;
            switch (type)
            {
                case MarketDataType.Order:
                    // Bids.
                    res.MDEntryTypes.Add(new Mantle.Fix44.MDEntryType() { Value = '0' });
                    // Asks.
                    res.MDEntryTypes.Add(new Mantle.Fix44.MDEntryType() { Value = '1' });
                    break;
                case MarketDataType.Trade:
                    // Live trades.
                    res.MDEntryTypes.Add(new Mantle.Fix44.MDEntryType() { Value = '2' });
                    break;
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
            res.OrdType.Value = request.OrderType == OrderType.Market ? '1' : '2';
            if (request.Price.HasValue)
                res.Price.Value = request.Price.Value;
            if (request.TimeToLive.HasValue)
            {
                res.TimeInForce.Value = '6';  // Good Till Date
                res.ExpireTime.Value = DateTime.UtcNow + request.TimeToLive.Value;
            }
            res.OrderQtyData.OrderQty.Value = request.Quantity;
            if (_cfg.Extensions == Extensions.Huobi)
            {
                res.MinQty.Value = request.Quantity;

                string coinType = null;
                if (request.Symbol == "btc") coinType = "1";
                else if (request.Symbol == "ltc") coinType = "2";
                else throw new ArgumentException(String.Format("Huobi doesn't support Symbol '{0}'", request.Symbol));

                string method = request.Side == Side.Buy ? "buy" : "sell";
                string price = null;

                if (request.OrderType == OrderType.Market)
                {
                    method += "_market";
                }
                else
                {
                    if (!request.Price.HasValue) throw new ArgumentException("Limit order is missing Price");
                    price = ToHuobiString(request.Price.Value);
                }

                res.HuobiSignature = HuobiSignature
                (
                    new KeyValuePair<string, string>[]
                    {
                        new KeyValuePair<string, string>("method", method),
                        new KeyValuePair<string, string>("amount", ToHuobiString(request.Quantity)),
                        new KeyValuePair<string, string>("coin_type", coinType),
                        new KeyValuePair<string, string>("price", price),
                    }
                );
            }
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

        public Mantle.Fix44.IClientMessage AccountInfoRequest()
        {
            switch (_cfg.Extensions)
            {
                case Extensions.OkCoin:
                    {
                        var res = new Mantle.Fix44.OkCoinAccountInfoRequest() { StandardHeader = StandardHeader() };
                        res.Account.Value = _cfg.Account;
                        res.OkCoinAccReqID.Value = Guid.NewGuid().ToString();
                        return res;
                    }
                case Extensions.Huobi:
                    {
                        var res = new Mantle.Fix44.HuobiAccountInfoRequest()
                        {
                            StandardHeader = StandardHeader(),
                            HuobiSignature = HuobiSignature
                            (
                                new KeyValuePair<string, string>[]
                                {
                                    new KeyValuePair<string, string>("method", "get_account_info")
                                }
                            )
                        };
                        res.Account.Value = _cfg.Account;
                        res.HuobiAccReqID.Value = Guid.NewGuid().ToString();
                        return res;
                    }
            }
            throw new UnsupportedOperationException(
                "AccountInfoRequest requires FIX extensions. If your exchange supports this operation, " +
                "make sure you are passing correct value of iFix.Crust.Fix44.Config.Extensions");
        }

        Mantle.Fix44.StandardHeader StandardHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = _cfg.SenderCompID;
            res.TargetCompID.Value = _cfg.TargetCompID;
            res.SendingTime.Value = DateTime.UtcNow;
            return res;
        }

        // Elements with null values are ignored.
        Mantle.Fix44.HuobiSignature HuobiSignature(IEnumerable<KeyValuePair<string, string>> data)
        {
            var res = new Mantle.Fix44.HuobiSignature();
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            res.HuobiCreated.Value = now;
            res.HuobiAccessKey.Value = _cfg.Account;
            // accessKey, amount, symbol, created, method,  price, secretKey
            var kv = data.Concat(new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>("access_key", _cfg.Account),
                new KeyValuePair<string, string>("created", now.ToString()),
                new KeyValuePair<string, string>("secret_key", _cfg.Password),
            });
            string s = String.Join("&", kv.Where(p => p.Value != null)
                                          .OrderBy(p => p.Key)
                                          .Select(p => String.Format("{0}={1}", p.Key, p.Value)));
            res.HuobiSign.Value = Md5Hex(s);
            return res;
        }

        // If Huobi starts rejecting our trade requests with 58=67, it's likely that changing this function
        // will fix it.
        static string ToHuobiString(decimal num)
        {
            // This is all undocumented. The algorithm for signing REST requests is also undocumented and
            // is slightly different. Here are a few examples.
            //
            // Number | REST | FIX
            // -------+------+------
            // 1      | 1    | 1.0
            // 1.2    | 1.2  | 1.2
            // 1.23   | 1.23 | 1.23
            string res = num.ToString();
            int period = res.IndexOf('.');
            if (period == -1) return res + ".0";
            int len = res.Length;
            while (len > period + 2 && res[len - 1] == '0') --len;
            return res.Substring(0, len);
        }

        static string Md5Hex(string s)
        {
            byte[] hash = MD5.Create().ComputeHash(Encoding.ASCII.GetBytes(s));
            StringBuilder res = new StringBuilder(2 * hash.Length);
            foreach (byte x in hash)
            {
                res.AppendFormat("{0:x2}", x);
            }
            return res.ToString();
        }
    }
}
