using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    class ScheduledQueue<TValue>
    {
        PriorityQueue<DateTime, TValue> _data = new PriorityQueue<DateTime, TValue>();
        object _monitor = new object();

        public void Push(TValue value, DateTime when)
        {
            lock (_monitor)
            {
                _data.Push(when, value);
                Monitor.PulseAll(_monitor);
            }
        }

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
                        TimeSpan delay = TimeSpan.FromMilliseconds(-1);
                        DateTime now = DateTime.UtcNow;
                        if (_data.Any())
                        {
                            var front = _data.Front();
                            if (front.Key <= now)
                            {
                                value = front.Value;
                                _data.Pop();
                                return true;
                            }
                            delay = front.Key - now;
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
