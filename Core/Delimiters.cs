﻿
namespace iFix.Core
{
    // The same delimiters are used in all versions of FIX protocol.
    static class Delimiters
    {
        // Tags and values are separated by '='.
        public const byte TagValueSeparator = (byte)'=';
        // Fields in a message are delimited by ASCII 01, a.k.a. SOH.
        public const byte FieldDelimiter = 1;
    }
}
