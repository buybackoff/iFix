using System;
using System.Globalization;

namespace iFix.Core
{
    // FIX message is malformed and cannot be parsed.
    public class MalformedMessageException : Exception
    {
        public MalformedMessageException(string message) : base(message) { }
    }

    // Implements parsing of fields and primitive FIX value types from ArraySegment<byte>.
    public static class Deserialization
    {
        static readonly string TimestampFormatWithMillis = "yyyyMMdd-HH:mm:ss.fff";
        static readonly string TimestampFormatWithoutMillis = "yyyyMMdd-HH:mm:ss";
        static readonly string[] TimestampFormats = { TimestampFormatWithMillis, TimestampFormatWithoutMillis };

        // Static methods ParseXXX() implement deserialization of FIX value types.
        // See http://www.onixs.biz/fix-dictionary/4.4/#DataTypes.

        public static string ParseString(ArraySegment<byte> bytes)
        {
            return bytes.AsAscii();
        }

        public static int ParseInt(ArraySegment<byte> bytes)
        {
            return (int)ParseLong(bytes);
        }

        public static long ParseLong(ArraySegment<byte> bytes)
        {
            // Parsing integers is a very common operation, so we want it to be fast.
            if (bytes.Count == 0)
                throw new MalformedMessageException("Can't parse long from empty byte sequence");
            int i = 0;
            int sign = 1;
            if (bytes.Array[bytes.Offset] == (byte)'-')
            {
                sign = -1;
                i = 1;
            }
            long abs = 0;
            for (; i != bytes.Count; ++i)
            {
                long digit = bytes.Array[bytes.Offset + i] - (byte)'0';
                if (digit < 0 || digit > 9)
                    throw new MalformedMessageException(String.Format("Can't parse long from {0}", bytes.AsAscii()));
                abs *= 10;
                abs += digit;
            }
            return sign * abs;
        }

        public static decimal ParseDecimal(ArraySegment<byte> bytes)
        {
            return decimal.Parse(ParseString(bytes));
        }

        public static bool ParseBool(ArraySegment<byte> bytes)
        {
            if (bytes.Count == 1)
            {
                if (bytes.Array[bytes.Offset] == (byte)'Y')
                    return true;
                if (bytes.Array[bytes.Offset] == (byte)'N')
                    return false;
            }
            throw new MalformedMessageException(String.Format("Can't parse bool from {0}", bytes.AsAscii()));
        }

        public static char ParseChar(ArraySegment<byte> bytes)
        {
            if (bytes.Count != 1)
                throw new MalformedMessageException(String.Format("Can't parse char from {0}", bytes.AsAscii()));
            return (char)bytes.Array[bytes.Offset];
        }

        public static DateTime ParseTimestamp(ArraySegment<byte> bytes)
        {
            return DateTime.ParseExact(ParseString(bytes), TimestampFormats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        // Parses a field from bytes starting from position 'start' and ending at 'end'.
        // Advances 'start' to the start of the next field. Throws if there is no field between
        // start and end.
        public static Field ParseField(byte[] bytes, ref int start, int end)
        {
            int fieldMiddle = Find(Delimiters.TagValueSeparator, bytes, start, end);
            int fieldEnd = Find(Delimiters.FieldDelimiter, bytes, fieldMiddle + 1, end);
            var res = new Field(
                new ArraySegment<byte>(bytes, start, fieldMiddle - start),
                new ArraySegment<byte>(bytes, fieldMiddle + 1, fieldEnd - fieldMiddle - 1));
            start = fieldEnd + 1;
            return res;
        }

        // Finds the specified byte in the byte array and returns its index.
        // Throws if the byte can't be found.
        static int Find(byte needle, byte[] haystack, int offset, int end)
        {
            for (int i = offset; i != end; ++i)
                if (haystack[i] == needle)
                    return i;
            throw new MalformedMessageException(String.Format(
                "Expected {0} in {1}", (char)needle,
                new ArraySegment<byte>(haystack, offset, end - offset).AsAscii()));
        }
    }

    // Stateful class for finding FIX message delimiters in a stream of bytes.
    class MessageTrailerMatcher
    {
        // SOH followed by "10=". Note that all characters are different, which makes it
        // easier to find this sequence in a stream. As soon as we find the sequence, we
        // just wait for one more SOH to arrive and that's the end of the message.
        readonly static byte[] Trailer = { Delimiters.FieldDelimiter, (byte)'1', (byte)'0', (byte)'=' };

        // Number of bytes from the trailer that have been found so far.
        int _numBytesMatched = 0;

        // Searches for trailer in the chunked stream of data. Returns 0 if trailer wasn't
        // found, otherwise returns past the end index of the trailer (that is, index
        // pointing to the beginning of the next message).
        //
        // Chunks of data can be supplied in several successive calls. The head of the trailer
        // might be found in one chunk and the tail in the next.
        public int FindTrailer(byte[] bytes, int start, int end)
        {
            for (int i = start; i != end; ++i)
            {
                // If SOH and "10=" have been found, we just need to find the last SOH.
                if (_numBytesMatched == Trailer.Length)
                {
                    if (bytes[i] == Delimiters.FieldDelimiter)
                        return i + 1;
                }
                else
                {
                    if (bytes[i] == Trailer[_numBytesMatched])
                        ++_numBytesMatched;
                    else if (bytes[i] == Trailer[0])
                        _numBytesMatched = 1;
                    else
                        _numBytesMatched = 0;
                }
            }
            return 0;
        }
    }
}
