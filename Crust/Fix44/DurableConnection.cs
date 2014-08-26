using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust.Fix44
{
    // Wrapper around IConnection that implements safe disconnects.
    class Session
    {
        // If null, the object has been disposed.
        IConnection _connection;
        // Used only in Dispose() to make it reentrant and thread-safe.
        Object _monitor = new Object();
        // When Dispose() is called, we signal all inflight Send() and Receive() calls
        // to terminate.
        CancellationTokenSource _cancellation = new CancellationTokenSource();
        // When _refCount reaches zero, it's safe to close the connection.
        CountdownEvent _refCount = new CountdownEvent(0);

        public Session(IConnection connection)
        {
            _connection = connection;
        }

        public void IncRef()
        {
            _refCount.AddCount();
        }

        public void DecRef()
        {
            _refCount.Signal();
        }

        // Requires: ref count is not zero for the whole duration of Receive(), both
        // it synchronous and asynchronous parts.
        public Task<Mantle.Fix44.IMessage> Receive()
        {
            return _connection.Receive(_cancellation.Token);
        }

        // Requires: ref count is not zero for the whole duration of Send().
        public void Send(Mantle.Fix44.IMessage msg)
        {
            _connection.Send(msg);
        }

        // Waits for the ref count to drop to zero and closes the connection.
        // The whole point of this class is to avoid closing the connection while
        // someone is using it.
        //
        // Dispose() is reentrant and thread-safe. It may be called concurrently with
        // Send() and Receive().
        public void Dispose()
        {
            lock (_monitor)
            {
                if (_connection == null) return;
                _cancellation.Cancel();
                _refCount.Wait();
                _connection.Dispose();
                _cancellation.Dispose();
                _refCount.Dispose();
                // To mark that the object has been disposed of.
                _connection = null;
            }
        }
    }

    // A connection with the exchange with automatic reconnects on failures.
    class DurableConnection
    {
        readonly IConnector _connector;
        // The current session that we believe to be valid.
        // Access to the reference is protected by _sessionMonitor.
        Session _session = null;
        // Protects access to _session.
        readonly Object _sessionMonitor = new Object();
        // This monitor is used to guarantee that at most one thread is sending
        // data at a time.
        readonly Object _sendMonitor = new Object();
        // When Dispose() is called, we signal all inflight Send() and Receive() calls
        // to terminate. If it's in signalling state, Send() will return false and
        // Receive() will return null wrapped in Task.
        readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        public DurableConnection(IConnector connector)
        {
            _connector = connector;
        }

        // Returns a non-null task containing null message if the object has been
        // disposed of.
        public async Task<Mantle.Fix44.IMessage> Receive()
        {
            Session session = null;
            while (true)
            {
                try
                {
                    session = GetSession(session);
                    if (session == null) return null;
                    Mantle.Fix44.IMessage res;
                    try
                    {
                        res = await session.Receive();
                    }
                    finally
                    {
                        session.DecRef();
                    }
                    return res;
                }
                catch (Exception)
                {
                    // TODO: log.
                }
            }
        }

        // Returns false if the object has been disposed of.
        // Never throws.
        public bool Send(Mantle.Fix44.IMessage msg)
        {
            lock (_sendMonitor)
            {
                Session session = null;
                while (true)
                {
                    try
                    {
                        session = GetSession(session);
                        if (session == null) return false;
                        try
                        {
                            session.Send(msg);
                        }
                        finally
                        {
                            session.DecRef();
                        }
                    }
                    catch (Exception)
                    {
                        // TODO: log.
                    }
                }
            }
        }

        // Dispose() is reentrant and thread-safe. It may be called concurrently with
        // Send() and Receive().
        //
        // It causes all current and future calls to Send() to return false and calls
        // to Receive() to return null wrapped in Task.
        public void Dispose()
        {
            _cancellation.Cancel();
            lock (_sessionMonitor)
            {
                if (_session != null) _session.Dispose();
                _session = null;
            }
        }

        // Returns fully initialized session that is NOT the same as 'old'
        // (supposedly the old one is malfunctioning).
        //
        // Returns null if the object has been disposed of. Never throws.
        // Disposes of the old session.
        Session GetSession(Session old)
        {
            if (old != null) old.Dispose();
            lock (_sessionMonitor)
            {
                if (_session != old)
                {
                    if (_session != null) _session.IncRef();
                    return _session;
                }
                if (_session != null) _session.Dispose();
                _session = null;
                while (true)
                {
                    if (_cancellation.IsCancellationRequested) return null;
                    try
                    {
                        _session = new Session(_connector.CreateConnection(_cancellation.Token).Result);
                        _session.IncRef();
                        return _session;
                    }
                    catch (Exception)
                    {
                        // TODO: log.
                    }
                }
            }
        }
    }
}
