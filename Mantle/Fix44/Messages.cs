using iFix.Core;
using System;
using System.Collections.Generic;

namespace iFix.Mantle.Fix44
{
    // Message is missing MsgType<35> tag.
    public class MsgTypeNotFound : Exception {}

    public abstract class Message : FieldSet, IMessage
    {
        public abstract T Visit<T>(IMessageVisitor<T> visitor);

        public string Protocol { get { return Fix44.Protocol.Value; } }
    }

    public interface IMessageVisitor<T>
    {
        T Visit(Logon msg);
        T Visit(TestRequest msg);
        T Visit(Heartbeat msg);
        T Visit(Reject msg);
        T Visit(SequenceReset msg);
        T Visit(ResendRequest msg);
        T Visit(NewOrderSingle msg);
    }

    public class MessageFactory : IMessageFactory
    {

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
            return null;
        }

        // Skips fields until it finds MsgType. Throws if MsgType can't be found.
        static MsgType FindMsgType(IEnumerator<Field> fields)
        {
            MsgType msgType = new MsgType();
            while (fields.MoveNext())
            {
                int tag = Deserialization.ParseInt(fields.Current.Tag);
                if (msgType.AcceptField(tag, fields.Current.Value) == FieldAcceptance.Accepted)
                    return msgType;
            }
            throw new MsgTypeNotFound();
        }
    }

    public class Logon : Message
    {
        public static readonly MsgType MsgType = new MsgType { Value = "A" };
        public StandardHeader StandardHeader = new StandardHeader();
        public EncryptMethod EncryptMethod = new EncryptMethod();
        public HeartBtInt HeartBtInt = new HeartBtInt();
        // Only 'true' is supported at the moment.
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

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class TestRequest : Message
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

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class Heartbeat : Message
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

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class Reject : Message
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

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class SequenceReset : Message
    {
        public static readonly MsgType MsgType = new MsgType { Value = "4" };
        public StandardHeader StandardHeader = new StandardHeader();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
        }

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class ResendRequest : Message
    {
        public static readonly MsgType MsgType = new MsgType { Value = "2" };
        public StandardHeader StandardHeader = new StandardHeader();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return MsgType;
            yield return StandardHeader;
        }

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }

    public class NewOrderSingle : Message
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

        public override T Visit<T>(IMessageVisitor<T> visitor)
        {
            return visitor.Visit(this);
        }
    }
}
