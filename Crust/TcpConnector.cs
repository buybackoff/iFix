using System;
using System.IO;
using System.Net.Sockets;

namespace iFix.Crust
{
    class TcpConnection : Connection
    {
        readonly TcpClient _client;
        readonly Stream _strm;

        public TcpConnection(string host, int port)
        {
            _client = new TcpClient(host, port);
            _strm = _client.GetStream();
        }

        public Stream In { get { return _strm; } }
        public Stream Out { get { return _strm; } }

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

    public class TcpConnector : Connector
    {
        readonly string _host;
        readonly int _port;

        public TcpConnector(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public Connection CreateConnection()
        {
            return new TcpConnection(_host, _port);
        }
    }
}
