using System;
using System.Text;

namespace iFix.Core
{
    public static class ArraySegmentExtensions
    {
        // Decodes an ASCII string from its serialized representation.
        public static string AsAscii(this ArraySegment<byte> bytes)
        {
            return Encoding.ASCII.GetString(bytes.Array, bytes.Offset, bytes.Count);
        }
    }
}
