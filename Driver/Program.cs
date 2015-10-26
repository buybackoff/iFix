﻿using System;
using iFix.Mantle;
using iFix.Crust;
using System.Collections.Generic;
using System.Threading;
using NLog;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.IO;

namespace iFix.Driver
{
    // OKcoin test.
    class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public static void Main(string[] args)
        {
            // TODO for OKcoin market feed:
            //   1. WTF is the deal with a bunch of trades always coming back in the first message?
            //   2. Are orders aggregated? Look into them.
            //   3. Manually verify that decoding works correctly.
            //   4. Add comments to the market feed API.
            try
            {
                // Copy-paste from https://www.okcoin.com/user/api.do.
                // Anyone who knows your keys can send orders on your behalf. Don't leak them!
                string apiKey = "FIXME";
                string secretKey = "FIXME";
                var client = new Crust.Fix44.Client(
                    new Crust.Fix44.ClientConfig()
                    {
                        Username = apiKey,
                        Password = secretKey,
                        SenderCompID = Guid.NewGuid().ToString(),
                        TargetCompID = "OKSERVER",
                        Account = String.Format("{0},{1}", apiKey, secretKey),
                        MarketDataSymbols = new List<string> { "BTC/USD" }
                    },
                    new TcpConnector("api.okcoin.com", 9880, ConnectionType.Secure));
                client.OnOrderEvent += e => _log.Info("Generated event: {0}", e);
                Thread.Sleep(5000);
                client.Dispose();
            }
            catch (Exception e)
            {
                _log.Fatal(e, "Unexpected exception. Terminating.");
            }
        }
    }

    // MOEX test.
    /*
    class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public static void Main(string[] args)
        {
            try
            {
                var client = new Crust.Fix44.Client(
                    new Crust.Fix44.ClientConfig()
                    {
                        Password = "7118",
                        SenderCompID = "MD9019500002",
                        TargetCompID = "MFIXTradeIDCurr",
                        Account = "MB9019501190",
                        TradingSessionID = "CETS",
                        // ClOrdIDPrefix = "BP14466/01#",
                        // PartyID = "BP14466",
                        // PartyIDSource = 'D',
                        // PartyRole = 3,
                    },
                    new TcpConnector("91.208.232.200", 9212));
                Thread.Sleep(3000);
                var req = new NewOrderRequest()
                {
                    Symbol = "USD000UTSTOM",
                    Side = Side.Buy,
                    Quantity = 1,
                    OrderType = OrderType.Limit,
                    Price = 57.9m,
                    UserID = "MyOrder",
                };
                IOrderCtrl order = client.CreateOrder(req).Result;
                if (order == null)
                {
                    _log.Info("CreateOrder: null");
                }
                else
                {
                    Thread.Sleep(1000);
                    bool replaced = order.ReplaceOrCancel(2, 57.85m).Result;
                    _log.Info("Replaced: {0}", replaced);
                    Thread.Sleep(1000);
                    bool cancelled = order.Cancel().Result;
                    _log.Info("Cancelled: {0}", cancelled);
                    Thread.Sleep(1000);
                }
                client.Dispose();
            }
            catch (Exception e)
            {
                _log.Fatal("Unexpected exception. Terminating.", e);
            }
        }
    }*/

    // Currencies.
    /*
    class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public static void Main(string[] args)
        {
            try
            {
                var client = new Crust.Fix44.Client(
                    new Crust.Fix44.ClientConfig()
                    {
                        HeartBtInt = 30,
                        Password = "7118",
                        SenderCompID = "MD9019500002",
                        TargetCompID = "MFIXTradeIDCurr",
                        Account = "MB9019501190",
                        TradingSessionID = "CETS",
                        ClOrdIDPrefix = "BP14466/01#",
                        PartyID = "BP14466",
                        PartyIDSource = 'D',
                        PartyRole = 3,
                        RequestTimeoutSeconds = 0,
                        OrderStatusSyncPeriod = 0,
                    },
                    new TcpConnector("194.84.44.1", 9212));
                var req = new NewOrderRequest()
                {
                    Symbol = "USD000UTSTOM",
                    Side = Side.Buy,
                    Quantity = 1,
                    OrderType = OrderType.Limit,
                    Price = 36.00m,
                };
                var order = client.CreateOrder(req, (OrderStateChangeEvent e) =>
                {
                    _log.Info("OrderStateChangeEvent: {0}", e);
                });
                if (!order.Submit("Submit"))
                    throw new Exception("Can't send the order");
                if (!order.Replace("Replace", 1, 36.01m))
                    throw new Exception("Can't send the order");
                while (true) Thread.Sleep(1000);
            }
            catch (Exception e)
            {
                _log.Fatal("Unexpected exception. Terminating.", e);
            }
        }
    }*/

    /*
     * Example of using iFix.Mantle
     */

    /*
    class Program
    {
        static long _msgSeqNum = 1;

        static Mantle.Fix44.StandardHeader MakeHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = "MD9019500001";
            res.TargetCompID.Value = "MFIXTradeIDCurr";
            res.MsgSeqNum.Value = _msgSeqNum++;
            res.SendingTime.Value = DateTime.UtcNow;
            return res;
        }

        public static void Main(string[] args)
        {
            try
            {
                var tcpConnector = new TcpConnector("194.84.44.1", 9212);
                Console.WriteLine("Connecting");
                var connection = tcpConnector.CreateConnection(CancellationToken.None).Result;
                var logon = new Mantle.Fix44.Logon() { StandardHeader = MakeHeader() };
                logon.EncryptMethod.Value = 0;
                logon.HeartBtInt.Value = 30;
                logon.Password.Value = "7118";
                logon.ResetSeqNumFlag.Value = true;
                Console.WriteLine("Sending logon");
                connection.Send(logon);
                while (true)
                {
                    Console.WriteLine("Reading message");
                    IMessage msg = connection.Receive(CancellationToken.None).Result;
                    Console.WriteLine("Received {0}", msg.GetType().Name);
                    if (msg is Mantle.Fix44.TestRequest)
                    {
                        Console.WriteLine("Sending Heartbeat.");
                        var heartbeat = new Mantle.Fix44.Heartbeat() { StandardHeader = MakeHeader() };
                        heartbeat.TestReqID.Value = ((Mantle.Fix44.TestRequest)msg).TestReqID.Value;
                        connection.Send(heartbeat);
                    }
                    if (msg is Mantle.Fix44.Logon)
                    {
                        // Console.WriteLine("Sending limit order.");
                        // var order = new Mantle.Fix44.NewOrderSingle() { StandardHeader = MakeHeader() };
                        // order.ClOrdID.Value = "MyOrder3";
                        // order.Account.Value = "MB9019501190";
                        // order.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = "CETS" });
                        // order.Instrument.Symbol.Value = "USD000UTSTOM";
                        // order.Side.Value = '1';  // 1 = Buy, 2 = Sell
                        // order.TransactTime.Value = DateTime.UtcNow;
                        // order.OrderQtyData.OrderQty.Value = 2;
                        // order.OrdType.Value = '2';  // 1 = Market, 2 = Limit
                        // order.Price.Value = 34.1m;
                        // connection.Send(order);
                        Console.WriteLine("Requesting order status.");
                        var req = new Mantle.Fix44.OrderStatusRequest() { StandardHeader = MakeHeader() };
                        req.ClOrdID.Value = "MyOrder3";
                        connection.Send(req);
                    }
                    if (msg is Mantle.Fix44.ExecutionReport && ((Mantle.Fix44.ExecutionReport)msg).ExecType.Value == '0')
                    {
                        // Console.WriteLine("Replacing limit order.");
                        // var order = new Mantle.Fix44.OrderCancelReplaceRequest() { StandardHeader = MakeHeader() };
                        // order.ClOrdID.Value = "MyOrder4";
                        // order.OrigClOrdID.Value = "MyOrder3";
                        // order.Account.Value = "MB9019501190";
                        // order.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = "CETS" });
                        // order.Instrument.Symbol.Value = "USD000UTSTOM";
                        // order.Side.Value = '1';  // 1 = Buy, 2 = Sell
                        // order.TransactTime.Value = DateTime.UtcNow;
                        // order.OrderQty.Value = 1;
                        // order.OrdType.Value = '2';  // 1 = Market, 2 = Limit
                        // order.Price.Value = 34.15m;
                        // connection.Send(order);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Caught an exception: {0}", e);
            }
        }
    }*/

    /*
     * Example of using iFix.Core
     */

    /*
    class LoginMsg : IEnumerable<iFix.Core.Field>
    {
        static Field MakeField(int tag, string value)
        {
            return new Field(Serialization.SerializeInt(tag), Serialization.SerializeString(value));
        }
        static Field MakeField(int tag, int value)
        {
            return new Field(Serialization.SerializeInt(tag), Serialization.SerializeInt(value));
        }
        public IEnumerator<Field> GetEnumerator()
        {
            yield return MakeField(35, "A");
            yield return MakeField(34, 1);
            yield return MakeField(49, "MD9019500001");
            yield return MakeField(52, "20140705-13:14:09.430");
            yield return MakeField(56, "MFIXTradeIDCurr");
            yield return MakeField(98, 0);
            yield return MakeField(108, 30);
            yield return MakeField(141, "Y");
            yield return MakeField(554, "7118");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
    class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("Opening connection");
            TcpClient client = new TcpClient("194.84.44.1", 9212);
            Console.WriteLine("Connection established");
            try
            {
                using (Stream strm = client.GetStream())
                {
                    Console.WriteLine("Sending request");
                    Serialization.WriteMessage(strm, Serialization.SerializeString("FIX.4.4"), new LoginMsg());
                    strm.Flush();

                    var msgReader = new MessageReader(1 << 20);
                    while (true)
                    {
                        var msg = new RawMessage(msgReader.ReadMessage(strm));
                        Console.WriteLine("Received message: {0}", msg);
                        Console.WriteLine("=================================");
                        foreach (Field field in msg)
                        {
                            Console.WriteLine(field);
                        }
                        Console.WriteLine("=================================");
                    }
                }
            }
            finally
            {
                Console.WriteLine("Closing connection");
                client.Close();
            }
        }
    }*/
}
