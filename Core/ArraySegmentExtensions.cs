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

        public static void CopyTo(this ArraySegment<byte> source, byte[] destination, int destinationOffset)
        {
            for (int i = 0; i != source.Count; ++i)
                destination[destinationOffset + i] = source.Array[source.Offset + i];
        }
    }
}
