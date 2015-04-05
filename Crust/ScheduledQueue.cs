using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // A queue of items with their expected processing time.
    // Thread-safe.
    class ScheduledQueue<TValue>
    {
        readonly PriorityQueue<DateTime, TValue> _data = new PriorityQueue<DateTime, TValue>();
        readonly object _monitor = new object();

        // Adds an element to the queue. It'll be ready for processing at the specified time.
        public void Push(TValue value, DateTime when)
        {
            lock (_monitor)
            {
                _data.Push(when, value);
                Monitor.PulseAll(_monitor);
            }
        }

        // Blocks until the head of the queue is ready for processing and pops it.
        // Returns false if cancelled, true otherwise.
        public bool Wait(out TValue value, CancellationToken cancel)
        {
            value = default(TValue);
            bool cancelled = false;
            CancellationTokenRegistration registration = cancel.Register(() =>
            {
                lock (_monitor)
                {
                    cancelled = true;
                    Monitor.PulseAll(_monitor);
                }
            });
            using (registration)
            {
                lock (_monitor)
                {
                    while (true)
                    {
                        if (cancelled) return false;
                        // TimeSpan.FromMilliseconds(-1) is infinity for Monitor.Wait().
                        TimeSpan delay = TimeSpan.FromMilliseconds(-1);
                        if (_data.Any())
                        {
                            DateTime now = DateTime.UtcNow;
                            if (_data.Front().Key <= now)
                            {
                                value = _data.Pop().Value;
                                return true;
                            }
                            delay = _data.Front().Key - now;
                            // Monitor.Wait() can't handle values above TimeSpan.FromMilliseconds(Int32.MaxValue).
                            if (delay > TimeSpan.FromMilliseconds(Int32.MaxValue))
                                delay = TimeSpan.FromMilliseconds(Int32.MaxValue);
                        }
                        Monitor.Wait(_monitor, delay);
                    }
                }
            }
        }
    }
}
