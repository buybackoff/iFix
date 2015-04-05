using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    class ClOrdIDGenerator
    {
        readonly string _prefix;
        readonly Object _monitor = new Object();
        uint _last = 0;

        // If prefix is not null, all IDs will start with it.
        public ClOrdIDGenerator(string prefix)
        {
            _prefix = prefix == null ? "" : prefix;
            _prefix += SessionID();
        }

        // Generates a new unique ID. The format of the ID is prefix + session + seq.
        //   - Prefix is specified in the constructor.
        //   - Session is a 3-character-long string that is supposed to uniquely identify the process.
        //     It's expected to be different if the binary is restarted on the same day. Uniqueness
        //     across days is not required nor guaranteed.
        //   - Seq is a 6-charecter-long string, which is different on every invocation of the method.
        public string GenerateID()
        {
            uint n;
            lock (_monitor) { n = ++_last; }
            // 32 bit integer can be encoded as 6 base64 characters, plus 2 characters for padding.
            // We don't need the padding.
            return _prefix + System.Convert.ToBase64String(BitConverter.GetBytes(n)).Substring(0, 6);
        }

        static string SessionID()
        {
            var t = DateTime.UtcNow;
            // The session ID is time of the startup measured in the number
            // of second since midnight. If two instances of the app
            // are launched within a second or less of each other, they'll
            // have the same ID (which would be bad, but the circumstances are unlikely).
            uint sec = (uint)(t.Hour * 3600 + t.Minute * 60 + t.Second);
            // Note that sec has no more than 18 bits set. It's actually even less
            // than that, but we only care that it's not more than 18, so that it can
            // be encoded as 3 base64 characters.
            byte[] bytes = BitConverter.GetBytes(sec);
            for (int i = 0; i != bytes.Length; ++i)
            {
                bytes[i] = ReverseBits(bytes[i]);
            }
            return System.Convert.ToBase64String(bytes).Substring(0, 3);
        }

        static byte ReverseBits(byte b)
        {
            byte res = 0;
            for (int i = 0; i != 8; ++i)
            {
                res <<= 1;
                res |= (byte)(b & 1);
                b >>= 1;
            }
            return res;
        }
    }
}
