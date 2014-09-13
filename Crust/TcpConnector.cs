using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    class TcpConnection : IConnection
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly TcpClient _client;
        Mantle.Receiver _receiver;
        long _lastSeqNum = 0;

        public TcpConnection(string host, int port)
        {
            _log.Info("Connecting to {0}:{1}...", host, port);
            _client = new TcpClient(host, port);
            var protocols = new Dictionary<string, Mantle.IMessageFactory>() {
                { Mantle.Fix44.Protocol.Value, new Mantle.Fix44.MessageFactory() }
            };
            _receiver = new Mantle.Receiver(_client.GetStream(), 1 << 20, protocols);
        }

        public long Send(Mantle.Fix44.IMessage msg)
        {
            msg.Header.MsgSeqNum.Value = ++_lastSeqNum;
            using (var buf = new MemoryStream(1 << 10))
            {
                Mantle.Publisher.Publish(buf, Mantle.Fix44.Protocol.Value, msg);
                byte[] bytes = buf.ToArray();
                _client.Client.Send(bytes);
            }
            return _lastSeqNum;
        }

        public async Task<Mantle.Fix44.IMessage> Receive(CancellationToken cancellationToken)
        {
            return (Mantle.Fix44.IServerMessage)await _receiver.Receive(cancellationToken);
        }

        public void Dispose()
        {
            _log.Info("Disconnecting...");
            try { _client.Close(); }
            catch (Exception) { }
            try { ((IDisposable)_client).Dispose(); }
            catch (Exception) { }
        }
    }

    public class TcpConnector : IConnector
    {
        readonly string _host;
        readonly int _port;

        public TcpConnector(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public Task<IConnection> CreateConnection(CancellationToken cancellationToken)
        {
            var res = new Task<IConnection>(() => new TcpConnection(_host, _port));
            res.Start();
            return res;
        }
    }
}
