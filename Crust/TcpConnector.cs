using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    public class SslOptions
    {
        // The default is host name (the constructor argument of TcpConnection).
        public string CertificateName = null;
        // If not set, it'll be loaded from the certificate store. (I think.)
        public string CertificateFilename = null;
        // Must be set iff CertificateFilename is set.
        public string CertificateFilePassword = null;
        // Allow expired certificates provided that they are otherwise valid.
        // Enable it if the server you are connecting to doesn't care about renewing certificates.
        public bool AllowExpiredCertificate = false;
        // Allow certificate chains that can't be built to a trusted root authority.
        // Enable it if you are OK with shady self issued certificates.
        public bool AllowPartialChain = false;
        // Certificate missing? Invalid? Name mismatch? Whatever, man. YOLO!
        public bool AllowAllErrors = false;
    }

    class TcpConnection : IConnection
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly TcpClient _client;
        Mantle.Receiver _receiver;
        Stream _strm;
        long _lastSeqNum = 0;

        // Establishes SSL connection iff ssl is not null.
        public TcpConnection(string host, int port, SslOptions ssl)
        {
            _log.Info("Connecting to {0}:{1}...", host, port);
            _client = new TcpClient(host, port);
            if (ssl == null)
            {
                _strm = _client.GetStream();
            }
            else
            {
                try
                {
                    RemoteCertificateValidationCallback cb =
                        (object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) =>
                    {
                        if (errors == SslPolicyErrors.None)
                            return true;
                        if (errors != SslPolicyErrors.RemoteCertificateChainErrors)
                        {
                            _log.Error("SSL handshake error: {0}", errors);
                            return ssl.AllowAllErrors;
                        }
                        foreach (X509ChainStatus ch in chain.ChainStatus)
                        {
                            if (ch.Status == X509ChainStatusFlags.NotTimeValid && ssl.AllowExpiredCertificate)
                            {
                                _log.Warn("Ignoring NotTimeValid error in SSL handshake.");
                                continue;
                            }
                            if (ch.Status == X509ChainStatusFlags.PartialChain)
                            {
                                _log.Warn("Ignoring PartialChain error in SSL handshake.");
                                continue;
                            }
                            _log.Error("SSL handshake error: {0} {1}", ch.Status, ch.StatusInformation);
                            return ssl.AllowAllErrors;
                        }
                        return true;
                    };
                    var sslStrm = new SslStream(_client.GetStream(), leaveInnerStreamOpen: false,
                                                userCertificateValidationCallback: cb);
                    var certs = new X509CertificateCollection();
                    if (ssl.CertificateFilename != null)
                        certs.Add(new X509Certificate(ssl.CertificateFilename, ssl.CertificateFilePassword));
                    sslStrm.AuthenticateAsClient(ssl.CertificateName ?? host, certs,
                                                 System.Security.Authentication.SslProtocols.Default,
                                                 checkCertificateRevocation: false);
                    _strm = sslStrm;
                }
                catch
                {
                    Dispose();
                    throw;
                }
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
            Action onTimeout = () =>
            {
                _log.Warn("Haven't received anything from the exchange for {0}. " +
                          "Going to close the socket and reconnect.", ReadTimeout);
                try { _client.Close(); }
                catch { }
            };
            Action onCancel = () =>
            {
                _log.Warn("Forcefully closing the connection");
                try { _client.Close(); }
                catch { }
            };
            // Trivia: TcpClient doesn't respect ReceiveTimeout. Its stream doesn't respect ReadTimeout.
            // ReadAsync() doesn't respect cancellation token.
            using (var timeout = new CancellationTokenSource(ReadTimeout))
            using (timeout.Token.Register(onTimeout))
            using (cancellationToken.Register(onCancel))
            {
                return (Mantle.Fix44.IServerMessage)await _receiver.Receive();
            }
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
        readonly SslOptions _ssl;

        public TcpConnector(string host, int port, SslOptions ssl = null)
        {
            _host = host;
            _port = port;
            _ssl = ssl;
        }

        public Task<IConnection> CreateConnection(CancellationToken cancellationToken)
        {
            var res = new Task<IConnection>(() => new TcpConnection(_host, _port, _ssl));
            res.Start();
            return res;
        }
    }
}
