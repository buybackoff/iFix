using System.Collections.Generic;

// This file defines a subset of fields, groups and component blocks supported by FIX 4.4.

namespace iFix.Mantle.Fix44
{
    // Protocol version string.
    public static class Protocol
    {
        public static readonly string Value = "FIX.4.4";
    }

    // Fields: http://www.onixs.biz/fix-dictionary/4.4/fields_by_tag.html.

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

    public class OrderID : StringField
    {
        protected override int Tag { get { return 37; } }
    }

    public class OrigClOrdID : StringField
    {
        protected override int Tag { get { return 41; } }
    }

    public class ExecType : CharField
    {
        protected override int Tag { get { return 150; } }
    }

    public class OrdStatus : CharField
    {
        protected override int Tag { get { return 39; } }
    }

    public class OrdRejReason : IntField
    {
        protected override int Tag { get { return 103; } }
    }

    public class LastQty : DecimalField
    {
        protected override int Tag { get { return 32; } }
    }

    public class LastPx : DecimalField
    {
        protected override int Tag { get { return 31; } }
    }

    public class LeavesQty : DecimalField
    {
        protected override int Tag { get { return 151; } }
    }

    public class CumQty : DecimalField
    {
        protected override int Tag { get { return 14; } }
    }

    public class OrigOrderID : StringField
    {
        protected override int Tag { get { return 9945; } }
    }

    public class MDEntryID : StringField
    {
        protected override int Tag { get { return 278; } }
    }

    public class CancelOrigOnReject : BoolField
    {
        protected override int Tag { get { return 9619; } }
    }

    public class MassCancelRequestType : CharField
    {
        protected override int Tag { get { return 530; } }
    }

    public class MassCancelResponse : CharField
    {
        protected override int Tag { get { return 531; } }
    }

    public class PartyID : StringField
    {
        protected override int Tag { get { return 448; } }
    }

    public class PartyIDSource : CharField
    {
        protected override int Tag { get { return 447; } }
    }

    public class PartyRole : IntField
    {
        protected override int Tag { get { return 452; } }
    }

    // Component blocks: http://www.onixs.biz/fix-dictionary/4.4/#ComponentBlocks.

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

    public class Party : FieldSet
    {
        public PartyID PartyID = new PartyID();
        public PartyIDSource PartyIDSource = new PartyIDSource();
        public PartyRole PartyRole = new PartyRole();

        public override IEnumerator<IFields> GetEnumerator()
        {
            yield return PartyID;
            yield return PartyIDSource;
            yield return PartyRole;
        }
    }

    // Groups: http://fixwiki.org/fixwiki/FPL:Tag_Value_Syntax#Repeating_Groups.

    public class TradingSessionIDGroup : FieldGroup<TradingSessionID>
    {
        protected override int GroupSizeTag { get { return 386; } }
    }

    public class PartyGroup : FieldGroup<Party>
    {
        protected override int GroupSizeTag { get { return 453; } }
    }
}
