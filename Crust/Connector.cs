using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // Call Dispose() to close the connection. It shall not be called concurrently
    // with Send() or Receive() or until the tasks created by Receive() have finished.
    public interface IConnection : IDisposable
    {
        // It's allowed to write and read simultanously from two different
        // threads.

        // Concurrent calls to Send() from multiple threads are not allowed.
        // If it throws, the connection can no longer be used and shall be disposed of.
        // Returns SeqNum of the sent message.
        long Send(Mantle.Fix44.IMessage msg);

        // At most one inflight task may exist at a time. In other words,
        // concurrent reads are not allowed.
        // If it throws (either synchronously or asynchronously), the connection can
        // no longer be used and shall be disposed of.
        Task<Mantle.Fix44.IMessage> Receive(CancellationToken cancellationToken);
    }

    public interface IConnector
    {
        // Each call creates a new connection. Make sure to Dispose() of them.
        // May throw both synchronously and asynchronusly.
        Task<IConnection> CreateConnection(CancellationToken cancellationToken);
    }
}
