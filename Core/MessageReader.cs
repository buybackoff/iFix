using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Core
{
    // Trying to read a FIX message that is above the maximum supported size.
    public class MessageTooLargeException : Exception { }

    // Trying to read a FIX message from an empty stream. This exception is
    // usually thrown when reading from a closed socket.
    public class EmptyStreamException : Exception { }

    // Low level class for chunking of byte streams into FIX messages.
    // Messages are separated by SOH (ASCII 01), followed by "10=", any number
    // of bytes, and then SOH again.
    public class MessageReader
    {
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
            Debug.Assert(maxMessageSize > 0);
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
            Debug.Assert(strm != null);
            var trailerMatcher = new MessageTrailerMatcher();
            int messageEnd = trailerMatcher.FindTrailer(_buf, _startPos, _endPos);
            // Keep reading from the underlying stream until we find trailer.
            while (messageEnd == 0)
            {
                EnsureBufferSpace();
                Debug.Assert(_endPos < _buf.Length);
                int read = await strm.ReadAsync(_buf, _endPos, _buf.Length - _endPos, cancellationToken);
                if (read <= 0)
                    throw new EmptyStreamException();
                messageEnd = trailerMatcher.FindTrailer(_buf, _endPos, _endPos + read);
                _endPos += read;
            }
            var res = new ArraySegment<byte>(_buf, _startPos, messageEnd - _startPos);
            _startPos = messageEnd;
            return res;
        }

        // Ensures that there is space in the buffer to receive data.
        // More specifically, it ensures that _endPos < buf_.Length.
        void EnsureBufferSpace()
        {
            if (_endPos < _buf.Length)
                return;
            if (_startPos == 0)
                throw new MessageTooLargeException();  // See constructor's parameter maxMessageSize.
            for (int i = _startPos; i != _endPos; ++i)
                _buf[i - _startPos] = _buf[i];
            _startPos = 0;
        }
    }
}
