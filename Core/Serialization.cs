using System;
using System.Collections.Generic;
using System.IO;

namespace iFix.Core
{
    public class Serialization
    {
        // ChecksumTable[checksum] contains serialized value of the checksum.
        // For example, ChecksumTable[42] is "042".
        // ChecksumTable.Length is 256.
        static readonly ArraySegment<byte>[] ChecksumTable = SerializeNumbers(256, 3);
        // Precomputed serialized values for integers in [0, 10000).
        // For example, IntTable[42] is "42".
        static readonly ArraySegment<byte>[] IntTable = SerializeNumbers(10000, 0);

        static readonly ArraySegment<byte> TrueValue = SerializeChar('Y');
        static readonly ArraySegment<byte> FalseValue = SerializeChar('N');

        static readonly ArraySegment<byte> VersionTag = SerializeInt(8);
        static readonly ArraySegment<byte> BodyLengthTag = SerializeInt(9);
        static readonly ArraySegment<byte> CheckSumTag = SerializeInt(10);

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
            return SerializeString(value.ToString("yyyyMMdd-HH:mm:ss.fff"));
        }

        // Version should be something like "FIX.4.4".
        // The list of fields should NOT contain tags Version<8>, BodyLength<9> and CheckSum<10>. Those
        // are added automatically. Fields may be traversed more than once by the function and should have
        // consistent values.
        public static void WriteMessage(Stream strm, ArraySegment<byte> version, IEnumerable<Field> fields)
        {
            int bodyLength = 0;
            foreach (Field field in fields)
                bodyLength += field.Tag.Count + 1 + field.Value.Count + 1;
            byte checksum = 0;
            Console.Write("OUT: ");
            WriteField(strm, new Field(VersionTag, version), ref checksum);
            WriteField(strm, new Field(BodyLengthTag, SerializeInt(bodyLength)), ref checksum);
            foreach (Field field in fields)
                WriteField(strm, field, ref checksum);
            WriteField(strm, new Field(CheckSumTag, ChecksumTable[checksum]), ref checksum);
            Console.WriteLine();
        }

        static void WriteField(Stream strm, Field field, ref byte checksum)
        {
            Console.Write(field);
            Console.Write((char)Delimiters.FieldDelimiter);
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
        // a padded with leading zeros.
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
