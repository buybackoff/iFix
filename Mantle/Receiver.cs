using iFix.Core;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Mantle
{
    // Message is missing BeginString<8> tag.
    public class MissingBeginStringException : Exception {}

    public class UnsupportedProtocolException : Exception
    {
        public UnsupportedProtocolException(string message) : base(message) { }
    }

    public class Receiver
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // Input from the exchange.
        Stream _in;
        MessageReader _reader;
        IReadOnlyDictionary<string, IMessageFactory> _protocols;

        // The 'protocols' dictionary maps supported protocols to their message factories.
        // Example of a valid entry: {Fix44.Protocol.Value, new Fix44.MessageFactory()}.
        public Receiver(Stream input, int maxMessageSize, IReadOnlyDictionary<string, IMessageFactory> protocols)
        {
            _in = input;
            _reader = new MessageReader(maxMessageSize);
            _protocols = protocols;
        }

        // Only one thread at a time is allowed to call Receive().
        // If it throws, the receiver is no longer usable and should be destroyed.
        public async Task<IMessage> Receive(CancellationToken cancellationToken)
        {
            // Loop until we get a message of known type.
            while (true)
            {
                var raw = new RawMessage(await _reader.ReadMessage(_in, cancellationToken));
                IEnumerator<Field> fields = raw.GetEnumerator();
                IMessageFactory factory = GetFactory(fields);
                IMessage msg = factory.CreateMessage(fields);
                if (msg != null) return msg;
            }
        }

        // Throws if the protocol can't be recognized.
        IMessageFactory GetFactory(IEnumerator<Field> fields)
        {
            if (!fields.MoveNext()) throw new MissingBeginStringException();
            int tag = Deserialization.ParseInt(fields.Current.Tag);
            BeginString version = new BeginString();
            if (version.AcceptField(tag, fields.Current.Value) != FieldAcceptance.Accepted)
                throw new MissingBeginStringException();
            if (!_protocols.ContainsKey(version.Value))
                throw new UnsupportedProtocolException(String.Format("Unrecognized protocol: {0}", version.Value));
            return _protocols[version.Value];
        }
    }
}
