using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace iFix.Core
{
    // Implements serialization of different value types into ArraySegment<byte> and
    // serialization of IEnumerable<Field> into Stream.
    public static class Serialization
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // ThreeDigitTable[n] contains serialized value of n, padded with leading zeros
        // if it has less than three.
        // For example, ThreeDigitTable[42] is "042".
        static readonly ArraySegment<byte>[] ThreeDigitTable = SerializeNumbers(1000, 3);
        static readonly ArraySegment<byte>[] TwoDigitTable = SerializeNumbers(100, 2);
        // Precomputed serialized values for integers in [0, 10000).
        // For example, IntTable[42] is "42".
        static readonly ArraySegment<byte>[] IntTable = SerializeNumbers(10000, 0);

        static readonly ArraySegment<byte> TrueValue = SerializeChar('Y');
        static readonly ArraySegment<byte> FalseValue = SerializeChar('N');

        static readonly ArraySegment<byte> VersionTag = SerializeInt(8);
        static readonly ArraySegment<byte> BodyLengthTag = SerializeInt(9);
        static readonly ArraySegment<byte> CheckSumTag = SerializeInt(10);

        // Static methods SerializeXXX() implement serialization of FIX value types.
        // See http://www.onixs.biz/fix-dictionary/4.4/#DataTypes.

        public static ArraySegment<byte> SerializeString(string value)
        {
            byte[] res = new byte[value.Length];
            for (int i = 0; i != value.Length; ++i)
                res[i] = (byte)value[i];
            return new ArraySegment<byte>(res);
        }

        public static ArraySegment<byte> SerializeInt(int value)
        {
            return SerializeLong(value);
        }

        public static ArraySegment<byte> SerializeLong(long value)
        {
            if (value >= 0 && value < IntTable.Length)
                return IntTable[value];
            return SerializeString(value.ToString());
        }

        public static ArraySegment<byte> SerializeDecimal(decimal value)
        {
            return SerializeString(value.ToString());
        }

        public static ArraySegment<byte> SerializeBool(bool value)
        {
            return value ? TrueValue : FalseValue;
        }

        public static ArraySegment<byte> SerializeChar(char value)
        {
            byte[] res = new byte[1];
            res[0] = (byte)value;
            return new ArraySegment<byte>(res);
        }

        public static ArraySegment<byte> SerializeTimestamp(DateTime value)
        {
            var year = IntTable[value.Year];
            var month = TwoDigitTable[value.Month];
            var day = TwoDigitTable[value.Day];
            var hour = TwoDigitTable[value.Hour];
            var minute = TwoDigitTable[value.Minute];
            var second = TwoDigitTable[value.Second];
            var millisecond = ThreeDigitTable[value.Millisecond];
            // yyyyMMdd-HH:mm:ss.fff
            byte[] res = new byte[21];
            year.CopyTo(res, 0);
            month.CopyTo(res, 4);
            day.CopyTo(res, 6);
            res[8] = (byte)'-';
            hour.CopyTo(res, 9);
            res[11] = (byte)':';
            minute.CopyTo(res, 12);
            res[14] = (byte)':';
            second.CopyTo(res, 15);
            res[17] = (byte)'.';
            millisecond.CopyTo(res, 18);
            return new ArraySegment<byte>(res);
        }

        public static ArraySegment<byte> SerializeTimeOnly(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentException(String.Format("TimeOnly field too small: {0}", value));
            if (value >= TimeSpan.FromHours(24))
                throw new ArgumentException(String.Format("TimeOnly field too large: {0}", value));
            var hour = TwoDigitTable[value.Hours];
            var minute = TwoDigitTable[value.Minutes];
            var second = TwoDigitTable[value.Seconds];
            var millisecond = ThreeDigitTable[value.Milliseconds];
            // HH:mm:ss.fff
            byte[] res = new byte[12];
            hour.CopyTo(res, 0);
            res[2] = (byte)':';
            minute.CopyTo(res, 3);
            res[5] = (byte)':';
            second.CopyTo(res, 6);
            res[8] = (byte)'.';
            millisecond.CopyTo(res, 9);
            return new ArraySegment<byte>(res);
        }

        // Version is the value of the BeginString<8> tag (e.g., "FIX.4.4").
        // The list of fields should NOT contain tags Version<8>, BodyLength<9> and CheckSum<10>. Those
        // are added automatically. Fields may be traversed more than once by the function and should have
        // consistent values. Does not flush the stream.
        public static void WriteMessage(Stream strm, ArraySegment<byte> version, IEnumerable<Field> fields)
        {
            StringBuilder log = _log.IsInfoEnabled ? new StringBuilder("OUT: ") : null;
            using (var buf = new MemoryStream(1 << 10))
            {
                byte checksum = 0;
                foreach (Field field in fields)
                    WriteField(buf, field, ref checksum, null);
                byte[] payload = buf.ToArray();
                WriteField(strm, new Field(VersionTag, version), ref checksum, log);
                WriteField(strm, new Field(BodyLengthTag, SerializeInt(payload.Length)), ref checksum, log);
                strm.Write(payload, 0, payload.Length);
                if (log != null)
                    log.Append(new ArraySegment<byte>(payload).AsAscii());
                WriteField(strm, new Field(CheckSumTag, ThreeDigitTable[checksum]), ref checksum, log);
            }
            if (log != null)
                _log.Info(log.ToString());
        }

        static void WriteField(Stream strm, Field field, ref byte checksum, StringBuilder log)
        {
            if (log != null)
            {
                log.Append(field);
                log.Append((char)Delimiters.FieldDelimiter);
            }
            WriteBytes(strm, field.Tag, ref checksum);
            WriteByte(strm, Delimiters.TagValueSeparator, ref checksum);
            WriteBytes(strm, field.Value, ref checksum);
            WriteByte(strm, Delimiters.FieldDelimiter, ref checksum);
        }

        static void WriteByte(Stream strm, byte value, ref byte checksum)
        {
            strm.WriteByte(value);
            checksum += value;
        }

        static void WriteBytes(Stream strm, ArraySegment<byte> bytes, ref byte checksum)
        {
            strm.Write(bytes.Array, bytes.Offset, bytes.Count);
            for (int i = 0; i != bytes.Count; ++i)
                checksum += bytes.Array[i + bytes.Offset];
        }

        // Serializes numbers [0, n). Numbers with less than width decimal digits
        // are padded with leading zeros.
        static ArraySegment<byte>[] SerializeNumbers(int n, int width)
        {
            ArraySegment<byte>[] res = new ArraySegment<byte>[n];
            string format = "D" + width.ToString();
            for (int i = 0; i != n; ++i)
                res[i] = SerializeString(i.ToString(format));
            return res;
        }
    }
}
