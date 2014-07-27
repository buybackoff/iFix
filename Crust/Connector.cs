using System;
using System.IO;

namespace iFix.Crust
{
    // Call Dispose() to close the connection.
    public interface Connection : IDisposable
    {
        // It's allowed to use the input and output streams simultanously
        // from two different threads. Each stream can only be used
        // by one thread at a time.

        // Returns an input stream for communication with the
        // remote party. The stream can only be READ from.
        Stream In { get; }

        // Returns an output stream for communication with the
        // remote party. The stream can only be WRITTEN to.
        Stream Out { get; }
    }

    public interface Connector
    {
        // Each call creates a new connection. Make sure to Dispose() of them.
        Connection CreateConnection();
    }
}
