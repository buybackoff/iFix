using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    public enum ConnectionType
    {
        Insecure,
        Secure,
    }

    class TcpConnection : IConnection
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly TcpClient _client;
        Mantle.Receiver _receiver;
        Stream _strm;
        long _lastSeqNum = 0;

        public TcpConnection(string host, int port, ConnectionType type)
        {
            _log.Info("Connecting to {0}:{1}...", host, port);
            _client = new TcpClient(host, port);
            switch (type)
            {
                case ConnectionType.Insecure:
                    _strm = _client.GetStream();
                    break;
                case ConnectionType.Secure:
                    try
                    {
                        var ssl = new SslStream(_client.GetStream(), false);
                        ssl.AuthenticateAsClient(host);
                        _strm = ssl;
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                    break;
            }
            var protocols = new Dictionary<string, Mantle.IMessageFactory>() {
                { Mantle.Fix44.Protocol.Value, new Mantle.Fix44.MessageFactory() }
            };
            _receiver = new Mantle.Receiver(_strm, 1 << 20, protocols);
        }

        public long Send(Mantle.Fix44.IMessage msg)
        {
            msg.Header.MsgSeqNum.Value = ++_lastSeqNum;
            using (var buf = new MemoryStream(1 << 10))
            {
                Mantle.Publisher.Publish(buf, Mantle.Fix44.Protocol.Value, msg);
                byte[] bytes = buf.ToArray();
                _strm.Write(bytes, 0, bytes.Length);
                _strm.Flush();
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
        readonly ConnectionType _type;

        public TcpConnector(string host, int port, ConnectionType type = ConnectionType.Insecure)
        {
            _host = host;
            _port = port;
            _type = type;
        }

        public Task<IConnection> CreateConnection(CancellationToken cancellationToken)
        {
            var res = new Task<IConnection>(() => new TcpConnection(_host, _port, _type));
            res.Start();
            return res;
        }
    }
}
