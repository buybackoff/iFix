using System.Collections.Generic;

namespace iFix.Mantle.Fix44
{
    // Protocol signature.
    public class Protocol
    {
        public static readonly string Value = "FIX.4.4";
    }

    // Individual fields.

    public class MsgType : StringField
    {
        protected override int Tag { get { return 35; } }
    }

    public class SenderCompID : StringField
    {
        protected override int Tag { get { return 49; } }
    }

    public class TargetCompID : StringField
    {
        protected override int Tag { get { return 56; } }
    }

    public class MsgSeqNum : LongField
    {
        protected override int Tag { get { return 34; } }
    }

    public class SendingTime : TimestampField
    {
        protected override int Tag { get { return 52; } }
    }

    public class EncryptMethod : IntField
    {
        protected override int Tag { get { return 98; } }
    }

    public class HeartBtInt : IntField
    {
        protected override int Tag { get { return 108; } }
    }

    public class ResetSeqNumFlag : BoolField
    {
        protected override int Tag { get { return 141; } }
    }

    public class Password : StringField
    {
        protected override int Tag { get { return 554; } }
    }

    public class TestReqID : StringField
    {
        protected override int Tag { get { return 112; } }
    }

    public class RefSeqNum : LongField
    {
        protected override int Tag { get { return 45; } }
    }

    public class RefTagID : IntField
    {
        protected override int Tag { get { return 371; } }
    }

    public class RefMsgType : StringField
    {
        protected override int Tag { get { return 372; } }
    }

    public class SessionRejectReason : StringField
    {
        protected override int Tag { get { return 373; } }
    }

    public class Text : StringField
    {
        protected override int Tag { get { return 58; } }
    }

    public class Symbol : StringField
    {
        protected override int Tag { get { return 55; } }
    }

    public class OrderQty : DecimalField
    {
        protected override int Tag { get { return 38; } }
    }

    public class ClOrdID : StringField
    {
        protected override int Tag { get { return 11; } }
    }

    public class Account : StringField
    {
        protected override int Tag { get { return 1; } }
    }

    public class TradingSessionID : StringField
    {
        protected override int Tag { get { return 336; } }
    }

    public class Side : CharField
    {
        protected override int Tag { get { return 54; } }
    }

    public class TransactTime : TimestampField
    {
        protected override int Tag { get { return 60; } }
    }

    public class OrdType : CharField
    {
        protected override int Tag { get { return 40; } }
    }

    public class Price : DecimalField
    {
        protected override int Tag { get { return 44; } }
    }

    // Blocks.

    // BeginString, BodyLength and MsgType are intentionally missing.
    // They are special enough to be handled separately together with
    // StandardTrailer.
    public class StandardHeader : FieldSet
    {
        public SenderCompID SenderCompID = new SenderCompID();
        public TargetCompID TargetCompID = new TargetCompID();
        public MsgSeqNum MsgSeqNum = new MsgSeqNum();
        public SendingTime SendingTime = new SendingTime();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return SenderCompID;
            yield return TargetCompID;
            yield return MsgSeqNum;
            yield return SendingTime;
        }
    }

    public class Instrument : FieldSet
    {
        public Symbol Symbol = new Symbol();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return Symbol;
        }
    }

    public class OrderQtyData : FieldSet
    {
        public OrderQty OrderQty = new OrderQty();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return OrderQty;
        }
    }

    // Groups.

    public class TradingSessionIDGroup : FieldGroup<TradingSessionID>
    {
        protected override int GroupSizeTag { get { return 386; } }
    }
}
