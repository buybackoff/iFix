using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Core
{
    interface IField
    {
        ArraySegment<byte> Tag { get; }
        bool Empty { get; }
        void Clear();
        ArraySegment<byte> ValueBytes { get; set; }
    }

    abstract class StructField<T> : IField where T : struct
    {
        T? _parsed;
        ArraySegment<byte> _bytes;

        public bool Empty { get { return !_parsed.HasValue; } }
        public void Clear()
        {
            _parsed = null;
            _bytes = new ArraySegment<byte>();
        }
        public T Value
        {
            get { return _parsed.Value; }
            set
            {
                _bytes = Serialize(value);
                _parsed = value;
            }
        }

        public ArraySegment<byte> ValueBytes
        {
            get { return _bytes; }
            set
            {
                _parsed = Deserialize(value);
                _bytes = value;
            }
        }

        public abstract ArraySegment<byte> Tag { get; }

        protected abstract ArraySegment<byte> Serialize(T value);
        protected abstract T Deserialize(ArraySegment<byte> bytes);
    }

    abstract class LongField : StructField<long>
    {
        protected override ArraySegment<byte> Serialize(long value) { return Serialization.SerializeLong(value); }
        protected override long Deserialize(ArraySegment<byte> bytes) { return Deserialization.ParseLong(bytes); }
    }

    class MsgSeqNum : LongField
    {
        static readonly ArraySegment<byte> _tag = Serialization.SerializeInt(34);

        public MsgSeqNum() { }
        public MsgSeqNum(long value) { Value = value; }
        public MsgSeqNum(ArraySegment<byte> bytes) { ValueBytes = bytes; }
        public static implicit operator MsgSeqNum(long value) { return new MsgSeqNum(value); }

        public override ArraySegment<byte> Tag { get { return _tag; } }
    }

    // TODO: support groups.

    interface IMessage : IEnumerable<IField>
    {
        void Parse(RawMessage msg);
    }

    abstract class MessageBase : IMessage
    {
        public void Parse(RawMessage msg)
        {
            foreach (IField ifield in this)
                ifield.Clear();

            foreach (Field field in msg)
            {
                foreach (IField ifield in this)
                {
                    if (field.Tag.ElementsEqual(ifield.Tag))
                    {
                        if (!ifield.Empty)
                            throw new MalformedMessageException(String.Format("Duplicate tag: {0}", field.Tag.AsAscii()));
                        ifield.ValueBytes = field.Value;
                    }
                }
            }
        }
    }

    // TODO: classes such as logon should export their fields. Then parsing and serialization
    // can be done generically.
    class Logon : MessageBase
    {
        // TODO: at this point we also need to specify whether it's required or not.
        public MsgSeqNum MsgSeqNum = new MsgSeqNum();

        public IEnumerator<IField> GetEnumerator()
        {
            yield return MsgSeqNum;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }

    // TODO: remove this file. This project is done.
    // In Quant define a bunch of struct messages with no logic in them.
    // Also define Session, which can connect, reply to hearbeat, send messages and
    // allows polling for messages (blocking). No fix shit should stick out of the
    // session. If an exception occurs, the session should close and all subsequence
    // calls should throw "not connected". The session by itself shouldn't cancel
    // all orders but it should support a message to do that.
}
