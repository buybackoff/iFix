using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace iFix.Core
{
    // Immutable FIX message that wraps serialized representation.
    // The only operation it supports is iteration over all
    // fields in order.
    public class RawMessage : IEnumerable<Field>
    {
        // Serialized FIX message.
        readonly ArraySegment<byte> _serialized;

        // Does NOT verify that the message is well formed.
        public RawMessage(ArraySegment<byte> serialized)
        {
            Debug.Assert(serialized != null);
            _serialized = serialized;
        }

        // From IEnumerable<Field>.
        // Enumerating fields may result in MalformedMessageException.
        public IEnumerator<Field> GetEnumerator()
        {
            int start = _serialized.Offset;
            int end = _serialized.Offset + _serialized.Count;
            while (start != end)
                yield return Deserialization.ParseField(_serialized.Array, ref start, end);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        public override string ToString()
        {
            return _serialized.AsAscii();
        }
    }
}
