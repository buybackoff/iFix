using iFix.Crust.Fix44;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // MessagePump reads incoming messages in a loop and passes them to the specified callback.
    class MessagePump : IDisposable
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly CancellationTokenSource _dispose = new CancellationTokenSource();
        readonly DurableConnection _connection;
        readonly Action<Mantle.Fix44.IServerMessage, long> _onMessage;
        readonly Task _loop;

        // Immediately starts reading messages from the connection and passing them to onMessage.
        // Stops reading when Dispose() is called.
        public MessagePump(DurableConnection connection, Action<Mantle.Fix44.IServerMessage, long> onMessage)
        {
            _connection = connection;
            _onMessage = onMessage;
            _loop = ReceiveLoop();
        }

        public void StartDispose()
        {
            _dispose.Cancel();
        }

        // Blocks until the reading stops.
        public void Dispose()
        {
            _log.Info("Disposing of iFix.Crust.MessagePump.");
            _dispose.Cancel();
            try { _loop.Wait(); } catch { }
            _log.Info("iFix.Crust.MessagePump successfully disposed of");
        }

        async Task ReceiveLoop()
        {
            while (!_dispose.IsCancellationRequested)
            {
                DurableMessage msg;
                try
                {
                    msg = await _connection.Receive();
                }
                catch (Exception e)
                {
                    if (!_dispose.IsCancellationRequested) _log.Error(e, "Failed to read a message");
                    continue;
                }
                try
                {
                    _onMessage.Invoke((Mantle.Fix44.IServerMessage)msg.Message, msg.SessionID);
                }
                catch (Exception e)
                {
                    if (!_dispose.IsCancellationRequested)
                        _log.Error(e, "Failed to handle a message received from the exchange: {0}.", msg.Message);
                }
            }
            _log.Info("Message pump terminated");
        }
    }
}
