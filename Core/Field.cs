using System;

namespace iFix.Core
{
    // FIX field in its raw form: a key-value pair where both key and value are
    // byte arrays.
    public struct Field
    {
        readonly ArraySegment<byte> _tag;
        readonly ArraySegment<byte> _value;

        public Field(ArraySegment<byte> tag, ArraySegment<byte> value)
        {
            _tag = tag;
            _value = value;
        }

        public ArraySegment<byte> Tag { get { return _tag; } }
        public ArraySegment<byte> Value { get { return _value; } }

        public override string ToString()
        {
            return String.Format("{0}={1}", _tag.AsAscii(), _value.AsAscii());
        }
    }
}
