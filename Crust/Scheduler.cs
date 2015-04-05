using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // Scheduler runs all actions asynchronously in a dedicated thread.
    // Actions can be scheduled in the future. The execution order is what you
    // would expect (stable sorting by the scheduled time).
    class Scheduler : IDisposable
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly ScheduledQueue<Action> _actions = new ScheduledQueue<Action>();
        readonly CancellationTokenSource _dispose = new CancellationTokenSource();
        readonly Task _loop;

        // Launches a background thread. Call Dispose() to stop it.
        public Scheduler()
        {
            _loop = new Task(ActionLoop);
            _loop.Start();
        }

        // Schedules the specified action to run at the specified time.
        public void Schedule(Action action, DateTime when)
        {
            _actions.Push(action, when);
        }

        // Schedules the specified action to run ASAP.
        public void Schedule(Action action)
        {
            Schedule(action, DateTime.UtcNow);
        }

        // Blocks until the background thread is stopped.
        public void Dispose()
        {
            _log.Info("Disposing of iFix.Crust.Scheduler");
            _dispose.Cancel();
            try { _loop.Wait(); } catch { }
            _log.Info("iFix.Crust.Scheduler successfully disposed of");
        }

        // This loop runs in a dedicated thread.
        void ActionLoop()
        {
            while (!_dispose.IsCancellationRequested)
            {
                try
                {
                    Action action;
                    if (!_actions.Wait(out action, _dispose.Token)) break;
                    action.Invoke();
                }
                catch (Exception e)
                {
                    // It's OK if exceptions such as ObjectDisposedException are flying
                    // during the disposal. No need to spread panic by logging them.
                    if (!_dispose.IsCancellationRequested) _log.Error("Exception in the action loop", e);
                }
            }
            _log.Info("Action loop terminated");
        }
    }
}
