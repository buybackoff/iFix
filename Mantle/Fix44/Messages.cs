﻿using iFix.Core;
using System;
using System.Collections.Generic;
using System.Text;

// This file defines a subset of messages supported by FIX 4.4.

namespace iFix.Mantle.Fix44
{
    // Message is missing MsgType<35> tag.
    public class MsgTypeNotFoundException : Exception { }

    // FIX.4.4 message.
    public interface IMessage : Mantle.IMessage
    {
        StandardHeader Header { get; }
    }

    // Message from FIX client to FIX server (e.g., Exchange).
    public interface IClientMessage : IMessage
    {
        T Visit<T>(IClientMessageVisitor<T> visitor);
    }

    // Message from FIX server (e.g., Exchange) to FIX client.
    public interface IServerMessage : IMessage
    {
        T Visit<T>(IServerMessageVisitor<T> visitor);
    }

    // FIX 4.4 message. Could be either client or server message.
    // Each message implements IClientMessage, IServerMessage or both.
    public abstract class Message : FieldSet, IMessage
    {
        public StandardHeader StandardHeader = new StandardHeader();
        public string Protocol { get { return Fix44.Protocol.Value; } }
        public StandardHeader Header { get { return StandardHeader; } }

        public override string ToString()
        {
            var res = new StringBuilder();
            foreach (Field field in Fields)
            {
                res.Append(field);
                res.Append((char)1);
            }
            return res.ToString();
        }
    }

    // FIX server can use this interface to handle all types of messages
    // that may be sent by the client.
    public interface IClientMessageVisitor<T>
    {
        T Visit(Logon msg);
        T Visit(Logout msg);
        T Visit(TestRequest msg);
        T Visit(Heartbeat msg);
        T Visit(Reject msg);
        T Visit(SequenceReset msg);
        T Visit(ResendRequest msg);
        T Visit(NewOrderSingle msg);
        T Visit(OrderCancelRequest msg);
        T Visit(OrderCancelReplaceRequest msg);
        T Visit(OrderMassCancelRequest msg);
        T Visit(OrderStatusRequest msg);
        T Visit(MarketDataRequest msg);
        T Visit(OkCoinAccountInfoRequest msg);
        T Visit(HuobiAccountInfoRequest msg);
        T Visit(OrderMassStatusRequest msg);
        T Visit(BtccAccountInfoRequest msg);
    }

    // FIX client can use this interface to handle all types of messages
    // that may be sent by the server.
    public interface IServerMessageVisitor<T>
    {
        T Visit(Logon msg);
        T Visit(Logout msg);
        T Visit(TestRequest msg);
        T Visit(Heartbeat msg);
        T Visit(Reject msg);
        T Visit(SequenceReset msg);
        T Visit(ResendRequest msg);
        T Visit(ExecutionReport msg);
        T Visit(OrderCancelReject msg);
        T Visit(OrderMassCancelReport msg);
        T Visit(MarketDataResponse msg);
        T Visit(MarketDataIncrementalRefresh msg);
        T Visit(AccountInfoResponse msg);
        T Visit(HuobiOrderInfoResponse msg);
        T Visit(BtccAccountInfoResponse msg);
    }

    // Factory and parser for FIX 4.4 client and server messages.
    public class MessageFactory : IMessageFactory
    {
        // Throws if the message is malformed.
        // Returns null if message type isn't recognized.
        // If the result is not null, it's guaranteed to inherit from Fix44.Message
        // and implement IClientMessage, IServerMessage, or both.
        public Mantle.IMessage CreateMessage(IEnumerator<Field> fields)
        {
            MsgType msgType = FindMsgType(fields);
            IMessage msg = NewMessage(msgType);
            if (msg != null)
            {
                while (fields.MoveNext())
                {
                    int tag = Deserialization.ParseInt(fields.Current.Tag);
                    msg.AcceptField(tag, fields.Current.Value);
                }
            }
            return msg;
        }

        static IMessage NewMessage(MsgType msgType)
        {
            // TODO: remove messages that don't implement IServerMessage from this list.
            // Currently they are commented out.
            if (msgType.Value == Logon.MsgType.Value) return new Logon();
            if (msgType.Value == Logout.MsgType.Value) return new Logout();
            if (msgType.Value == TestRequest.MsgType.Value) return new TestRequest();
            if (msgType.Value == Heartbeat.MsgType.Value) return new Heartbeat();
            if (msgType.Value == Reject.MsgType.Value) return new Reject();
            if (msgType.Value == SequenceReset.MsgType.Value) return new SequenceReset();
            if (msgType.Value == ResendRequest.MsgType.Value) return new ResendRequest();
            // if (msgType.Value == NewOrderSingle.MsgType.Value) return new NewOrderSingle();
            // if (msgType.Value == OrderCancelRequest.MsgType.Value) return new OrderCancelRequest();
            // if (msgType.Value == OrderCancelReplaceRequest.MsgType.Value) return new OrderCancelReplaceRequest();
            if (msgType.Value == ExecutionReport.MsgType.Value) return new ExecutionReport();
            if (msgType.Value == OrderCancelReject.MsgType.Value) return new OrderCancelReject();
            // if (msgType.Value == OrderMassCancelRequest.MsgType.Value) return new OrderMassCancelRequest();
            if (msgType.Value == OrderMassCancelReport.MsgType.Value) return new OrderMassCancelReport();
            // if (msgType.Value == OrderStatusRequest.MsgType.Value) return new OrderStatusRequest();
            // if (msgType.Value == MarketDataRequest.MsgType.Value) return new MarketDataRequest();
            if (msgType.Value == MarketDataResponse.MsgType.Value) return new MarketDataResponse();
            if (msgType.Value == MarketDataIncrementalRefresh.MsgType.Value) return new MarketDataIncrementalRefresh();
            // if (msgType.Value == OkCoinAccountInfoRequest.MsgType.Value) return new OkCoinAccountInfoRequest();
            if (msgType.Value == AccountInfoResponse.MsgType.Value) return new AccountInfoResponse();
            // This never triggers because HuobiAccountInfoRequest.MsgType is the same as
            // OkCoinAccountInfoRequest.MsgType. This doesn't matter though because NewMessage() is called
            // only for the incoming server messages.
            // if (msgType.Value == HuobiAccountInfoRequest.MsgType.Value) return new HuobiAccountInfoRequest();
            // if (msgType.Value == OrderMassStatusRequest.MsgType.Value) return new OrderMassStatusRequest();
            if (msgType.Value == HuobiOrderInfoResponse.MsgType.Value) return new HuobiOrderInfoResponse();
            // if (msgType.Value == BtccAccountInfoRequest.MsgType.Value) return new BtccAccountInfoRequest();
            if (msgType.Value == BtccAccountInfoResponse.MsgType.Value) return new BtccAccountInfoResponse();
            return null;
        }

        // Skips fields until finds MsgType. Throws if MsgType can't be found.
        static MsgType FindMsgType(IEnumerator<Field> fields)
        {
            MsgType msgType = new MsgType();
            while (fields.MoveNext())
            {
                int tag = Deserialization.ParseInt(fields.Current.Tag);
                if (msgType.AcceptField(tag, fields.Current.Value) == FieldAcceptance.Accepted)
                    return msgType;
            }
            throw new MsgTypeNotFoundException();
        }
    }

    // FIX 4.4 messages: http://www.onixs.biz/fix-dictionary/4.4/msgs_by_msg_type.html.

    // Logon <A>: http://www.onixs.biz/fix-dictionary/4.4/msgType_A_65.html.
    public class Logon : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "A" };
        public EncryptMethod EncryptMethod = new EncryptMethod();
        public HeartBtInt HeartBtInt = new HeartBtInt();
        public ResetSeqNumFlag ResetSeqNumFlag = new ResetSeqNumFlag();
        public Username Username = new Username();
        public Password Password = new Password();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return EncryptMethod;
            yield return HeartBtInt;
            yield return ResetSeqNumFlag;
            yield return Username;
            yield return Password;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Logon <5>: http://www.onixs.biz/fix-dictionary/4.4/msgType_5_5.html.
    public class Logout : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "5" };

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Test Request <1>: http://www.onixs.biz/fix-dictionary/4.4/msgType_1_1.html.
    public class TestRequest : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "1" };
        public TestReqID TestReqID = new TestReqID();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return TestReqID;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Heartbeat <0>: http://www.onixs.biz/fix-dictionary/4.4/msgType_0_0.html.
    public class Heartbeat : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "0" };
        public TestReqID TestReqID = new TestReqID();
        public Username Username = new Username();
        public Password Password = new Password();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return TestReqID;
            yield return Username;
            yield return Password;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Reject <3>: http://www.onixs.biz/fix-dictionary/4.4/msgType_3_3.html.
    public class Reject : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "3" };
        public RefSeqNum RefSeqNum = new RefSeqNum();
        public RefTagID RefTagID = new RefTagID();
        public RefMsgType RefMsgType = new RefMsgType();
        public SessionRejectReason SessionRejectReason = new SessionRejectReason();
        public Text Text = new Text();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return RefSeqNum;
            yield return RefTagID;
            yield return RefMsgType;
            yield return SessionRejectReason;
            yield return Text;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Sequence Reset <4>: http://www.onixs.biz/fix-dictionary/4.4/msgType_4_4.html.
    public class SequenceReset : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "4" };

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Resend Request <2>: http://www.onixs.biz/fix-dictionary/4.4/msgType_2_2.html.
    public class ResendRequest : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "2" };

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // New Order Single <D>: http://www.onixs.biz/fix-dictionary/4.4/msgType_D_68.html.
    public class NewOrderSingle : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "D" };
        public ClOrdID ClOrdID = new ClOrdID();
        public HuobiSignature HuobiSignature = new HuobiSignature();
        public PartyGroup PartyGroup = new PartyGroup();
        public Account Account = new Account();
        public TradingSessionIDGroup TradingSessionIDGroup = new TradingSessionIDGroup();
        public Instrument Instrument = new Instrument();
        public TimeInForce TimeInForce = new TimeInForce();
        public Side Side = new Side();
        public TransactTime TransactTime = new TransactTime();
        public OrderQtyData OrderQtyData = new OrderQtyData();
        public MinQty MinQty = new MinQty();
        public OrdType OrdType = new OrdType();
        public Price Price = new Price();
        public ExpireTime ExpireTime = new ExpireTime();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return HuobiSignature;
            yield return PartyGroup;
            yield return Account;
            yield return TradingSessionIDGroup;
            yield return Instrument;
            yield return TimeInForce;
            yield return Side;
            yield return TransactTime;
            yield return OrderQtyData;
            yield return MinQty;
            yield return OrdType;
            yield return Price;
            yield return ExpireTime;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Cancel Request <F>: http://www.onixs.biz/fix-dictionary/4.4/msgType_F_70.html.
    public class OrderCancelRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "F" };
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public ClOrdID ClOrdID = new ClOrdID();
        public HuobiSignature HuobiSignature = new HuobiSignature();
        public OrderID OrderID = new OrderID();
        public Instrument Instrument = new Instrument();
        public Side Side = new Side();
        public TransactTime TransactTime = new TransactTime();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrigClOrdID;
            yield return ClOrdID;
            yield return HuobiSignature;
            yield return OrderID;
            yield return Instrument;
            yield return Side;
            yield return TransactTime;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Cancel/Replace Request <G>: http://www.onixs.biz/fix-dictionary/4.4/msgType_G_71.html.
    public class OrderCancelReplaceRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "G" };
        public ClOrdID ClOrdID = new ClOrdID();
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public OrderID OrderID = new OrderID();
        public Account Account = new Account();
        public PartyGroup PartyGroup = new PartyGroup();
        public Instrument Instrument = new Instrument();
        public Price Price = new Price();
        public OrderQty OrderQty = new OrderQty();
        public CancelOrigOnReject CancelOrigOnReject = new CancelOrigOnReject();
        public TradingSessionIDGroup TradingSessionIDGroup = new TradingSessionIDGroup();
        public OrdType OrdType = new OrdType();
        public Side Side = new Side();
        public TransactTime TransactTime = new TransactTime();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return OrigClOrdID;
            yield return OrderID;
            yield return Account;
            yield return PartyGroup;
            yield return Instrument;
            yield return Price;
            yield return OrderQty;
            yield return CancelOrigOnReject;
            yield return TradingSessionIDGroup;
            yield return OrdType;
            yield return Side;
            yield return TransactTime;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Execution Report <8>: http://www.onixs.biz/fix-dictionary/4.4/msgType_8_8.html.
    public class ExecutionReport : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "8" };
        public OrderID OrderID = new OrderID();
        public ClOrdID ClOrdID = new ClOrdID();
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public ExecType ExecType = new ExecType();
        public OrdStatus OrdStatus = new OrdStatus();
        public Symbol Symbol = new Symbol();
        public Side Side = new Side();
        public Price Price = new Price();
        public LastQty LastQty = new LastQty();
        public LastPx LastPx = new LastPx();
        public LeavesQty LeavesQty = new LeavesQty();
        public CumQty CumQty = new CumQty();
        public AvgPx AvgPx = new AvgPx();
        public OrigOrderID OrigOrderID = new OrigOrderID();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrderID;
            yield return ClOrdID;
            yield return OrigClOrdID;
            yield return ExecType;
            yield return OrdStatus;
            yield return Symbol;
            yield return Side;
            yield return Price;
            yield return LastQty;
            yield return LastPx;
            yield return LeavesQty;
            yield return CumQty;
            yield return AvgPx;
            yield return OrigOrderID;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Cancel Reject <9>: http://www.onixs.biz/fix-dictionary/4.4/msgType_9_9.html.
    public class OrderCancelReject : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "9" };
        public ClOrdID ClOrdID = new ClOrdID();
        public CxlRejReason CxlRejReason = new CxlRejReason();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return CxlRejReason;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Mass Cancel Request <q>: http://www.onixs.biz/fix-dictionary/4.4/msgType_q_113.html.
    public class OrderMassCancelRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "q" };
        public ClOrdID ClOrdID = new ClOrdID();
        public MassCancelRequestType MassCancelRequestType = new MassCancelRequestType();
        public TradingSessionIDGroup TradingSessionIDGroup = new TradingSessionIDGroup();
        public Instrument Instrument = new Instrument();
        public TransactTime TransactTime = new TransactTime();
        public Account Account = new Account();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return MassCancelRequestType;
            yield return TradingSessionIDGroup;
            yield return Instrument;
            yield return TransactTime;
            yield return Account;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Mass Cancel Report <r>: http://www.onixs.biz/fix-dictionary/4.4/msgType_r_114.html.
    public class OrderMassCancelReport : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "r" };
        public ClOrdID ClOrdID = new ClOrdID();
        public MassCancelResponse MassCancelResponse = new MassCancelResponse();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return MassCancelResponse;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Status Request <H>: http://www.onixs.biz/fix-dictionary/4.4/msgType_H_72.html.
    public class OrderStatusRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "H" };
        public ClOrdID ClOrdID = new ClOrdID();
        public OrderID OrderID = new OrderID();
        public Account Account = new Account();
        public Instrument Instrument = new Instrument();
        public Side Side = new Side();
        public HuobiSignature HuobiSignature = new HuobiSignature();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return OrderID;
            yield return Account;
            yield return Instrument;
            yield return Side;
            yield return HuobiSignature;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Market Data Request <V>: http://www.onixs.biz/fix-dictionary/4.4/msgType_V_86.html.
    public class MarketDataRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "V" };
        public RelatedSym RelatedSym = new RelatedSym();
        public MDReqID MDReqID = new MDReqID();
        public SubscriptionRequestType SubscriptionRequestType = new SubscriptionRequestType();
        public MarketDepth MarketDepth = new MarketDepth();
        public MDUpdateType MDUpdateType = new MDUpdateType();
        public MDEntryTypes MDEntryTypes = new MDEntryTypes();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return RelatedSym;
            yield return MDReqID;
            yield return SubscriptionRequestType;
            yield return MarketDepth;
            yield return MDUpdateType;
            yield return MDEntryTypes;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Market Data Response <W>: http://www.onixs.biz/fix-dictionary/4.4/msgType_W_87.html.
    public class MarketDataResponse : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "W" };
        public OrigTime OrigTime = new OrigTime();
        public Instrument Instrument = new Instrument();
        public MDEntries MDEntries = new MDEntries();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrigTime;
            yield return Instrument;
            yield return MDEntries;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Market Data - Incremental Refresh <X>: http://www.onixs.biz/fix-dictionary/4.4/msgType_X_88.html.
    public class MarketDataIncrementalRefresh : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "X" };
        public Instrument Instrument = new Instrument();
        public MDEntries MDEntries = new MDEntries();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Instrument;
            yield return MDEntries;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Account Info Request <Z1000>: OKCoin extension.
    public class OkCoinAccountInfoRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "Z1000" };
        public Account Account = new Account();
        public AccReqID AccReqID = new AccReqID();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Account;
            yield return AccReqID;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Account Info Response <Z1001>: OKCoin and Huobi extension.
    public class AccountInfoResponse : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "Z1001" };
        // OkCoin tags.
        public Currency Currency = new Currency();
        public OkCoinFreeCurrency1 FreeCurrency1 = new OkCoinFreeCurrency1();
        public OkCoinFreeCurrency2 FreeCurrency2 = new OkCoinFreeCurrency2();
        public OkCoinFreeCurrency3 FreeCurrency3 = new OkCoinFreeCurrency3();
        public OkCoinFrozenCurrency1 FrozenCurrency1 = new OkCoinFrozenCurrency1();
        public OkCoinFrozenCurrency2 FrozenCurrency2 = new OkCoinFrozenCurrency2();
        public OkCoinFrozenCurrency3 FrozenCurrency3 = new OkCoinFrozenCurrency3();
        // Huobi tags.
        public HuobiAvailableCny HuobiAvailableCny = new HuobiAvailableCny();
        public HuobiAvailableBtc HuobiAvailableBtc = new HuobiAvailableBtc();
        public HuobiAvailableLtc HuobiAvailableLtc = new HuobiAvailableLtc();
        public HuobiFrozenCny HuobiFrozenCny = new HuobiFrozenCny();
        public HuobiFrozenBtc HuobiFrozenBtc = new HuobiFrozenBtc();
        public HuobiFrozenLtc HuobiFrozenLtc = new HuobiFrozenLtc();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Currency;
            yield return FreeCurrency1;
            yield return FreeCurrency2;
            yield return FreeCurrency3;
            yield return FrozenCurrency1;
            yield return FrozenCurrency2;
            yield return FrozenCurrency3;
            yield return HuobiAvailableCny;
            yield return HuobiAvailableBtc;
            yield return HuobiAvailableLtc;
            yield return HuobiFrozenCny;
            yield return HuobiFrozenBtc;
            yield return HuobiFrozenLtc;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Account Info Request <Z1000>: Huobi extension.
    public class HuobiAccountInfoRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "Z1000" };
        public Account Account = new Account();
        public HuobiAccReqID HuobiAccReqID = new HuobiAccReqID();
        public HuobiSignature HuobiSignature = new HuobiSignature();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Account;
            yield return HuobiAccReqID;
            yield return HuobiSignature;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Order Mass Status Request <AF>: http://www.onixs.biz/fix-dictionary/4.4/msgType_AF_6570.html
    public class OrderMassStatusRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "AF" };
        public Account Account = new Account();
        public HuobiSignature HuobiSignature = new HuobiSignature();
        public MassStatusReqID MassStatusReqID = new MassStatusReqID();
        public MassStatusReqType MassStatusReqType = new MassStatusReqType();
        public Instrument Instrument = new Instrument();
        public Side Side = new Side();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Account;
            yield return HuobiSignature;
            yield return MassStatusReqID;
            yield return MassStatusReqType;
            yield return Instrument;
            yield return Side;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Huobi Order Info Response <Z1003>: https://github.com/huobiapi/API_Docs_en/wiki/FIX-v2-Trading%20Interface
    public class HuobiOrderInfoResponse : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "Z1003" };
        public OrderID OrderID = new OrderID();
        public OrdStatus OrdStatus = new OrdStatus();
        public Quantity Quantity = new Quantity();
        public HuobiProcessedPrice HuobiProcessedPrice = new HuobiProcessedPrice();
        public HuobiProcessedAmount HuobiProcessedAmount = new HuobiProcessedAmount();
        // There are more fields in this message. Check out the docs.

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrderID;
            yield return OrdStatus;
            yield return Quantity;
            yield return HuobiProcessedPrice;
            yield return HuobiProcessedAmount;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Account Info Request <U1000>: BTCC extension.
    public class BtccAccountInfoRequest : Message, IClientMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "U1000" };
        public Account Account = new Account();
        public BtccAccReqID BtccAccReqID = new BtccAccReqID();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return Account;
            yield return BtccAccReqID;
        }

        public T Visit<T>(IClientMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    // Account Info Response <U1001>: BTCC extension.
    public class BtccAccountInfoResponse : Message, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "U1001" };
        public BtccBalances BtccBalances = new BtccBalances();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return BtccBalances;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }
}
