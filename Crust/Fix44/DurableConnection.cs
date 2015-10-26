using NLog;
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
        readonly long _id;
        // Used only in Dispose() to make it reentrant and thread-safe.
        Object _monitor = new Object();
        // When Dispose() is called, we signal all inflight Send() and Receive() calls
        // to terminate.
        CancellationTokenSource _cancellation = new CancellationTokenSource();
        // When _refCount reaches zero, it's safe to close the connection.
        CountdownEvent _refCount = new CountdownEvent(1);

        // The initial reference count is one. The first call to Dispose() will drop it.
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
        // It's not allowed to call Receive() until the task created by the previous call
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

        // If it's not the first call to Dispose(), does nothing.
        //
        // Otherwise decrements the ref count (it mirror the constructor, which initializes
        // the ref count with one), waits for the ref count to drop to zero and closes the connection.
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
                _refCount.Signal();
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

    // Messages sent within a single FIX session have unique sequence numbers.
    // However, two messages from different FIX sessions may have the same sequence numbers.
    // DurableSeqNum is a pair of two numbers:
    //   - SessionID is a unique session identifier within a process.
    //   - SeqNum is a unique message identifier within a session.
    //
    // DurableSeqNum is used only for matching Reject messages.
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

        public override string ToString()
        {
            return String.Format("(SessionID = {0}, SeqNum = {1})", SessionID, SeqNum);
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
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly IConnector _connector;
        // The current session that we believe to be valid.
        // Access to the reference is protected by _sessionMonitor.
        Session _session = null;
        // Protects access to _session.
        readonly Object _sessionMonitor = new Object();
        long _sessionID = 0;
        // This monitor is held while the session is being initialized.
        // It also protects _sessionID.
        readonly Object _sessionInitMonitor = new Object();
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

        // It's not allowed to call Receive() until the task created by the previous call
        // to Receive() has finished.
        //
        // Throws ObjectDisposedExpection() either synchronously or asynchronously if the connection has
        // been disposed of. Otherwise doesn't throw and returns non-null message.
        public async Task<DurableMessage> Receive()
        {
            Session session = null;
            while (true)
            {
                session = await Task.Run(() => GetSession(session));
                try
                {
                    try
                    {
                        return new DurableMessage { SessionID = session.ID, Message = await session.Receive() };
                    }
                    finally
                    {
                        session.DecRef();
                    }
                }
                catch (Exception e)
                {
                    if (!_cancellation.IsCancellationRequested)
                        _log.Error(e, "Failed to read a message. Will reconnect and retry.");
                }
            }
        }

        // Throws ObjectDisposedExpection if the connection has been disposed of. Returns null if not
        // connected or if send fails. Otherwise returns sequence number of the sent message.
        //
        // Can be called concurrently -- all calls are serialized internally.
        public DurableSeqNum Send(Mantle.Fix44.IMessage msg)
        {
            lock (_sendMonitor)
            {
                Session session = TryGetSession(null);
                if (session == null)
                {
                    _log.Warn("Unable to publish a messge: not connected.");
                    return null;
                }
                try
                {
                    return new DurableSeqNum { SessionID = session.ID, SeqNum = session.Send(msg) };
                }
                catch (Exception e)
                {
                    if (!_cancellation.IsCancellationRequested)
                        _log.Error(e, "Failed to publish a message.");
                    // Invalidate current session.
                    TryGetSession(session);
                    return null;
                }
                finally
                {
                    session.DecRef();
                }
            }
        }

        // Throws ObjectDisposedException if disposed.
        // If not connected, does nothing.
        // If connected, marks the current connection invalid. It'll be closed and the
        // new connection will be opened when all calls to Send() and Receive() finish.
        // Doesn't block.
        public void Reconnect()
        {
            // Wow, look at this weird contraption!
            TryGetSession(TryGetSession(null));
        }

        // Dispose() is reentrant and thread-safe. It may be called concurrently with
        // Send() and Receive().
        //
        // It causes all current and future calls to Send() to return false and calls
        // to Receive() to return null wrapped in Task.
        public void Dispose()
        {
            _cancellation.Cancel();
            lock (_sessionInitMonitor)
            lock (_sessionMonitor)
            {
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }
            }
        }

        // Returns fully initialized session that is NOT the same as 'invalid'
        // (supposedly that one is malfunctioning). Returns null if the current
        // session is not initialized or if it's equal to the invalid.
        //
        // Never returns null. Disposes of the invalid session.
        Session TryGetSession(Session invalid)
        {
            if (_cancellation.IsCancellationRequested) throw new ObjectDisposedException("DurableConnection");
            if (invalid != null) invalid.Dispose();
            lock (_sessionMonitor)
            {
                if (_session != null)
                {
                    if (_session == invalid) _session = null;
                    else _session.IncRef();
                }
                return _session;
            }
        }

        // Returns fully initialized session that is NOT the same as 'invalid'
        // (supposedly that one is malfunctioning). Initializes a new session if
        // necessary.
        //
        // Throws if the object has been disposed of. Never returns null.
        // Disposes of the invalid session.
        Session GetSession(Session invalid)
        {
            Session current = TryGetSession(invalid);
            if (current != null) return current;
            lock (_sessionInitMonitor)
            {
                // Check if we managed to grab _sessionInitMonitor before anyone else.
                current = TryGetSession(null);
                if (current != null) return current;
                while (true)
                {
                    if (_cancellation.IsCancellationRequested) throw new ObjectDisposedException("DurableConnection");
                    try
                    {
                        current = new Session(_connector.CreateConnection(_cancellation.Token).Result, ++_sessionID);
                        current.IncRef();
                        lock (_sessionMonitor) _session = current;
                        return current;
                    }
                    catch (Exception e)
                    {
                        if (!_cancellation.IsCancellationRequested)
                            _log.Warn(e, "Failed to connect. Will retry in 1s.");
                    }
                    // Wait for 1 second before trying to reconnect.
                    if (!_cancellation.IsCancellationRequested)
                        Thread.Sleep(1000);
                }
            }
        }
    }
}
