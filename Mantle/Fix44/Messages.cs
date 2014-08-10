using iFix.Core;
using System;
using System.Collections.Generic;

// This file defines a subset of messages supported by FIX 4.4.

namespace iFix.Mantle.Fix44
{
    // Message is missing MsgType<35> tag.
    public class MsgTypeNotFoundException : Exception {}

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

        public string Protocol { get { return Fix44.Protocol.Value; } }
    }

    // FIX server can use this interface to handle all types of messages
    // that may be sent by the client.
    public interface IClientMessageVisitor<T>
    {
        T Visit(Logon msg);
        T Visit(TestRequest msg);
        T Visit(Heartbeat msg);
        T Visit(Reject msg);
        T Visit(SequenceReset msg);
        T Visit(ResendRequest msg);
        T Visit(NewOrderSingle msg);
        T Visit(OrderCancelRequest msg);
        T Visit(OrderCancelReplaceRequest msg);
    }

    // FIX client can use this interface to handle all types of messages
    // that may be sent by the server.
    public interface IServerMessageVisitor<T>
    {
        T Visit(Logon msg);
        T Visit(TestRequest msg);
        T Visit(Heartbeat msg);
        T Visit(Reject msg);
        T Visit(SequenceReset msg);
        T Visit(ResendRequest msg);
        T Visit(ExecutionReport msg);
    }

    // Factory and parser for FIX 4.4 client and server messages.
    public class MessageFactory : IMessageFactory
    {
        // Throws if the message is malformed.
        // Returns null if message type isn't recognized.
        // If the result is not null, it's guaranteed to inherit from Fix44.Message
        // and implement IClientMessage, IServerMessage, or both.
        public IMessage CreateMessage(IEnumerator<Field> fields)
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
            if (msgType.Value == Logon.MsgType.Value) return new Logon();
            if (msgType.Value == TestRequest.MsgType.Value) return new TestRequest();
            if (msgType.Value == Heartbeat.MsgType.Value) return new Heartbeat();
            if (msgType.Value == Reject.MsgType.Value) return new Reject();
            if (msgType.Value == SequenceReset.MsgType.Value) return new SequenceReset();
            if (msgType.Value == ResendRequest.MsgType.Value) return new ResendRequest();
            if (msgType.Value == NewOrderSingle.MsgType.Value) return new NewOrderSingle();
            if (msgType.Value == OrderCancelRequest.MsgType.Value) return new OrderCancelRequest();
            if (msgType.Value == OrderCancelReplaceRequest.MsgType.Value) return new OrderCancelReplaceRequest();
            if (msgType.Value == ExecutionReport.MsgType.Value) return new ExecutionReport();
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
        public StandardHeader StandardHeader = new StandardHeader();
        public EncryptMethod EncryptMethod = new EncryptMethod();
        public HeartBtInt HeartBtInt = new HeartBtInt();
        public ResetSeqNumFlag ResetSeqNumFlag = new ResetSeqNumFlag();
        public Password Password = new Password();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return EncryptMethod;
            yield return HeartBtInt;
            yield return ResetSeqNumFlag;
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

    // Test Request <1>: http://www.onixs.biz/fix-dictionary/4.4/msgType_1_1.html.
    public class TestRequest : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "1" };
        public StandardHeader StandardHeader = new StandardHeader();
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
        public StandardHeader StandardHeader = new StandardHeader();
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

    // Reject <3>: http://www.onixs.biz/fix-dictionary/4.4/msgType_3_3.html.
    public class Reject : Message, IClientMessage, IServerMessage
    {
        public static readonly MsgType MsgType = new MsgType { Value = "3" };
        public StandardHeader StandardHeader = new StandardHeader();
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
        public StandardHeader StandardHeader = new StandardHeader();

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
        public StandardHeader StandardHeader = new StandardHeader();

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
        public StandardHeader StandardHeader = new StandardHeader();
        public ClOrdID ClOrdID = new ClOrdID();
        public Account Account = new Account();
        public TradingSessionIDGroup TradingSessionIDGroup = new TradingSessionIDGroup();
        public Instrument Instrument = new Instrument();
        public Side Side = new Side();
        public TransactTime TransactTime = new TransactTime();
        public OrderQtyData OrderQtyData = new OrderQtyData();
        public OrdType OrdType = new OrdType();
        public Price Price = new Price();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return ClOrdID;
            yield return Account;
            yield return TradingSessionIDGroup;
            yield return Instrument;
            yield return Side;
            yield return TransactTime;
            yield return OrderQtyData;
            yield return OrdType;
            yield return Price;
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
        public StandardHeader StandardHeader = new StandardHeader();
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public ClOrdID ClOrdID = new ClOrdID();
        public Side Side = new Side();
        public TransactTime TransactTime = new TransactTime();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrigClOrdID;
            yield return ClOrdID;
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
        public StandardHeader StandardHeader = new StandardHeader();
        public ClOrdID ClOrdID = new ClOrdID();
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public Account Account = new Account();
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
            yield return Account;
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
        public StandardHeader StandardHeader = new StandardHeader();
        public OrderID OrderID = new OrderID();
        public ClOrdID ClOrdID = new ClOrdID();
        public OrigClOrdID OrigClOrdID = new OrigClOrdID();
        public ExecType ExecType = new ExecType();
        public OrdStatus OrdStatus = new OrdStatus();
        public OrdRejReason OrdRejReason = new OrdRejReason();
        public LastQty LastQty = new LastQty();
        public LastPx LastPx = new LastPx();
        public LeavesQty LeavesQty = new LeavesQty();
        public OrigOrderID OrigOrderID = new OrigOrderID();
        public Text Text = new Text();
        public MDEntryID MDEntryID = new MDEntryID();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
            yield return OrderID;
            yield return ClOrdID;
            yield return OrigClOrdID;
            yield return ExecType;
            yield return OrdStatus;
            yield return OrdRejReason;
            yield return LastQty;
            yield return LastPx;
            yield return LeavesQty;
            yield return OrigOrderID;
            yield return Text;
            yield return MDEntryID;
        }

        public T Visit<T>(IServerMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }
}
