using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
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
        // Session identifier. Unique within the process.
        long _id;
        // Used only in Dispose() to make it reentrant and thread-safe.
        Object _monitor = new Object();
        // When Dispose() is called, we signal all inflight Send() and Receive() calls
        // to terminate.
        CancellationTokenSource _cancellation = new CancellationTokenSource();
        // When _refCount reaches zero, it's safe to close the connection.
        CountdownEvent _refCount = new CountdownEvent(0);

        // The initial reference count is zero.
        public Session(IConnection connection, long id)
        {
            _connection = connection;
            _id = id;
        }

        public long ID { get { return _id; } }

        public void IncRef()
        {
            _refCount.AddCount();
        }

        public void DecRef()
        {
            _refCount.Signal();
        }

        // Requires: ref count is not zero for the whole duration of Receive(), both
        // its synchronous and asynchronous parts.
        //
        // It's not allowed to called Receive() until the task created by the previous call
        // to Receive() has finished.
        public Task<Mantle.Fix44.IMessage> Receive()
        {
            return _connection.Receive(_cancellation.Token);
        }

        // Requires: ref count is not zero for the whole duration of Send().
        // Concurrent calls are not allowed.
        public long Send(Mantle.Fix44.IMessage msg)
        {
            return _connection.Send(msg);
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
                if (_connection == null) return;  // Already disposed of.
                _cancellation.Cancel();
                _refCount.Wait();
                _connection.Dispose();
                _cancellation.Dispose();
                _refCount.Dispose();
                // Mark that the object has been disposed of.
                _connection = null;
            }
        }
    }

    class DurableSeqNum : IEquatable<DurableSeqNum>
    {
        public long SessionID;
        public long SeqNum;

        public bool Equals(DurableSeqNum other)
        {
            return other != null && SessionID == other.SessionID && SeqNum == other.SeqNum;
        }

        public override bool Equals(Object obj)
        {
            return Equals(obj as DurableSeqNum);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (int)(SessionID * 2654435761 + SeqNum);
            }
        }
    }

    class DurableMessage
    {
        public long SessionID;
        public Mantle.Fix44.IMessage Message;
    }

    // A connection with the exchange with automatic reconnects on failures.
    class DurableConnection
    {
        readonly IConnector _connector;
        // The current session that we believe to be valid.
        // Access to the reference is protected by _sessionMonitor.
        Session _session = null;
        long _sessionID = 0;
        // Protects access to _session and _sessionID.
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

        // It's not allowed to called Receive() until the task created by the previous call
        // to Receive() has finished.
        //
        // Returns a non-null task containing null message if the object has been
        // disposed of.
        public async Task<DurableMessage> Receive()
        {
            Session session = null;
            while (true)
            {
                try
                {
                    session = GetSession(session);
                    if (session == null) return null;
                    try
                    {
                        return new DurableMessage { SessionID = session.ID, Message = await session.Receive() };
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

        // Returns null if the object has been disposed of, otherwise returns
        // sequence number of the sent message.
        //
        // Never throws. Can be called concurrently -- all calls will be
        // serialized internally.
        public DurableSeqNum Send(Mantle.Fix44.IMessage msg)
        {
            lock (_sendMonitor)
            {
                Session session = null;
                while (true)
                {
                    try
                    {
                        session = GetSession(session);
                        if (session == null) return null;
                        try
                        {
                            return new DurableSeqNum { SessionID = session.ID, SeqNum = session.Send(msg) };
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
                        _session = new Session(_connector.CreateConnection(_cancellation.Token).Result, ++_sessionID);
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
