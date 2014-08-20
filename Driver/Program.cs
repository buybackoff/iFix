﻿using System;
using iFix.Mantle;
using iFix.Crust;
using System.Collections.Generic;

namespace iFix.Driver
{
    class Program
    {
        static long _msgSeqNum = 1;

        static Mantle.Fix44.StandardHeader MakeHeader()
        {
            var res = new Mantle.Fix44.StandardHeader();
            res.SenderCompID.Value = "MD9019500001";
            res.TargetCompID.Value = "MFIXTradeIDCurr";
            res.MsgSeqNum.Value = _msgSeqNum++;
            res.SendingTime.Value = DateTime.Now;
            return res;
        }

        public static void Main(string[] args)
        {
            var tcpConnector = new TcpConnector("194.84.44.1", 9212);
            var connection = tcpConnector.CreateConnection();
            var protocols = new Dictionary<string, IMessageFactory>() {
                { Mantle.Fix44.Protocol.Value, new Mantle.Fix44.MessageFactory() }
            };
            var receiver = new Receiver(connection.In, 1 << 20, protocols);
            var logon = new Mantle.Fix44.Logon() { StandardHeader = MakeHeader() };
            logon.EncryptMethod.Value = 0;
            logon.HeartBtInt.Value = 30;
            logon.Password.Value = "7118";
            logon.ResetSeqNumFlag.Value = true;
            Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, logon);
            while (true)
            {
                IMessage msg = receiver.Receive();
                Console.WriteLine("Received {0}", msg.GetType().Name);
                if (msg is Mantle.Fix44.TestRequest)
                {
                    Console.WriteLine("Sending Heartbeat.");
                    var heartbeat = new Mantle.Fix44.Heartbeat() { StandardHeader = MakeHeader() };
                    heartbeat.TestReqID.Value = ((Mantle.Fix44.TestRequest)msg).TestReqID.Value;
                    Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, heartbeat);
                }
                if (msg is Mantle.Fix44.Logon)
                {
                    /*
                    Console.WriteLine("Sending limit order.");
                    var order = new Mantle.Fix44.NewOrderSingle() { StandardHeader = MakeHeader() };
                    order.ClOrdID.Value = "MyOrder3";
                    order.Account.Value = "MB9019501190";
                    order.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = "CETS" });
                    order.Instrument.Symbol.Value = "USD000UTSTOM";
                    order.Side.Value = '1';  // 1 = Buy, 2 = Sell
                    order.TransactTime.Value = DateTime.Now;
                    order.OrderQtyData.OrderQty.Value = 2;
                    order.OrdType.Value = '2';  // 1 = Market, 2 = Limit
                    order.Price.Value = 34.1m;
                    Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, order);*/
                    Console.WriteLine("Requesting order status.");
                    var req = new Mantle.Fix44.OrderStatusRequest() { StandardHeader = MakeHeader() };
                    req.ClOrdID.Value = "MyOrder3";
                    Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, req);
                }
                if (msg is Mantle.Fix44.ExecutionReport && ((Mantle.Fix44.ExecutionReport)msg).ExecType.Value == '0')
                {
                    /*Console.WriteLine("Replacing limit order.");
                    var order = new Mantle.Fix44.OrderCancelReplaceRequest() { StandardHeader = MakeHeader() };
                    order.ClOrdID.Value = "MyOrder4";
                    order.OrigClOrdID.Value = "MyOrder3";
                    order.Account.Value = "MB9019501190";
                    order.TradingSessionIDGroup.Add(new Mantle.Fix44.TradingSessionID { Value = "CETS" });
                    order.Instrument.Symbol.Value = "USD000UTSTOM";
                    order.Side.Value = '1';  // 1 = Buy, 2 = Sell
                    order.TransactTime.Value = DateTime.Now;
                    order.OrderQty.Value = 1;
                    order.OrdType.Value = '2';  // 1 = Market, 2 = Limit
                    order.Price.Value = 34.15m;
                    Publisher.Publish(connection.Out, Mantle.Fix44.Protocol.Value, order);*/
                }
            }
        }
    }

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