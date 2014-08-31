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
        readonly TcpClient _client;
        readonly Stream _strm;
        Mantle.Receiver _receiver;
        long _lastSeqNum = 0;

        public TcpConnection(string host, int port)
        {
            _client = new TcpClient(host, port);
            _strm = _client.GetStream();
            var protocols = new Dictionary<string, Mantle.IMessageFactory>() {
                { Mantle.Fix44.Protocol.Value, new Mantle.Fix44.MessageFactory() }
            };
            _receiver = new Mantle.Receiver(_strm, 1 << 20, protocols);
        }

        public long Send(Mantle.Fix44.IMessage msg)
        {
            msg.Header.MsgSeqNum.Value = ++_lastSeqNum;
            Mantle.Publisher.Publish(_strm, Mantle.Fix44.Protocol.Value, msg);
            return _lastSeqNum;
        }

        public async Task<Mantle.Fix44.IMessage> Receive(CancellationToken cancellationToken)
        {
            return (Mantle.Fix44.IServerMessage)await _receiver.Receive(cancellationToken);
        }

        public void Dispose()
        {
            try { _strm.Close(); }
            catch (Exception) { }
            try { _strm.Dispose(); }
            catch (Exception) { }
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
