using iFix.Common;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Core
{
    // Trying to read a FIX message that is above the maximum supported size.
    public class MessageTooLargeException : Exception
    {
        public MessageTooLargeException(string msg) : base(msg) { }
    }

    // Trying to read a FIX message from an empty stream. This exception is
    // usually thrown when reading from a closed socket.
    public class EmptyStreamException : Exception
    {
        public EmptyStreamException(string msg) : base(msg) { }
    }

    // Low level class for chunking of byte streams into FIX messages.
    // Messages are separated by SOH (ASCII 01), followed by "10=", any number
    // of bytes, and then SOH again.
    public class MessageReader
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // _buf contains data received from the stream but not yet consumed.
        // Bytes at [_startPos, _endPos) form an unfinished message; they'll be used in
        // the next call to ReadMessage(). Bytes before that contain the last message returned
        // by ReadMessage().
        readonly byte[] _buf;
        int _startPos = 0;
        int _endPos = 0;

        // Messages larger than maxMessageSize can't be received.
        public MessageReader(int maxMessageSize)
        {
            Assert.True(maxMessageSize > 0, "maxMessageSize = {0}", maxMessageSize);
            _buf = new byte[maxMessageSize];
        }

        // Reads a single FIX messages and returns it. It shall contain
        // all fields, including header and trailer. No validation is
        // performed.
        //
        // This method is NOT thread-safe.
        //
        // Throws MessageTooLargeException if incoming message exceeds
        // MaxMessageSize. After that every call to ReadMessage() will throw
        // MessageTooLargeException.
        //
        // Throws EmptyStreamException if nothing can be read from the
        // underlying stream.
        public async Task<ArraySegment<byte>> ReadMessage(Stream strm, CancellationToken cancellationToken)
        {
            Assert.True(strm != null);
            var trailerMatcher = new MessageTrailerMatcher();
            int messageEnd = trailerMatcher.FindTrailer(_buf, _startPos, _endPos);
            // Keep reading from the underlying stream until we find trailer.
            while (messageEnd == 0)
            {
                EnsureBufferSpace();
                Assert.True(_endPos < _buf.Length, "_endPos = {0}, _buf.Length = {1}", _endPos, _buf.Length);
                // TODO: NetworkStream doesn't really support cancellation. Figure out how to cancel.
                int read = await strm.ReadAsync(_buf, _endPos, _buf.Length - _endPos, cancellationToken);
                if (read <= 0)
                {
                    var partial = new ArraySegment<byte>(_buf, _startPos, _endPos - _startPos);
                    throw new EmptyStreamException("Read so far: " + partial.AsAscii());
                }
                messageEnd = trailerMatcher.FindTrailer(_buf, _endPos, _endPos + read);
                _endPos += read;
            }
            var res = new ArraySegment<byte>(_buf, _startPos, messageEnd - _startPos);
            _startPos = messageEnd;
            if (_log.IsDebugEnabled) _log.Debug("IN: {0}", res.AsAscii());
            else _log.Info("IN: {0}", Truncate(res.AsAscii(), 1024));
            return res;
        }

        // Ensures that there is space in the buffer to receive data.
        // More specifically, it ensures that _endPos < buf_.Length.
        void EnsureBufferSpace()
        {
            if (_endPos < _buf.Length)
                return;
            if (_startPos == 0)
            {
                // See constructor's parameter maxMessageSize.
                throw new MessageTooLargeException("Read so far: " + new ArraySegment<byte>(_buf).AsAscii());
            }
            for (int i = _startPos; i != _endPos; ++i)
                _buf[i - _startPos] = _buf[i];
            _endPos -= _startPos;
            _startPos = 0;
        }

        // Truncates string for logging. Strings shorter than maxLength are returned as is.
        // Longer strings get truncated at maxLength and then a short suffix is appended.
        //
        // The maximum length of the returned string is maxLength + C where C is a small
        // positive number.
        //
        //   Truncate("abc", 3") => "abc"
        //   Truncate("Lorem ipsum dolor sit amet", 3") => "Lor ... (23 more chars)"
        //   Truncate("Lorem ipsum", 3") => "Lorem Ipsum" (the truncated version would be longer)
        static string Truncate(string s, int maxLength)
        {
            if (s.Length <= maxLength) return s;
            maxLength = Math.Max(maxLength, 0);
            string res = String.Format("{0} ... ({1} more chars)", s.Substring(0, maxLength), s.Length - maxLength);
            return res.Length < s.Length ? res : s;
        }
    }
}
