using System;
using System.Text;

namespace iFix.Core
{
    public static class ArraySegmentExtensions
    {
        public static string AsAscii(this ArraySegment<byte> bytes)
        {
            return Encoding.ASCII.GetString(bytes.Array, bytes.Offset, bytes.Count);
        }

        public static bool ElementsEqual(this ArraySegment<byte> a, ArraySegment<byte> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i != a.Count; ++i)
                if (a.Array[i + a.Offset] != b.Array[i + b.Offset])
                    return false;
            return true;
        }
    }
}
