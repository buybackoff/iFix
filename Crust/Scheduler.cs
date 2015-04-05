using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    class Scheduler : IDisposable
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        readonly ScheduledQueue<Action> _actions = new ScheduledQueue<Action>();
        readonly CancellationTokenSource _dispose = new CancellationTokenSource();
        readonly Task _loop;

        public Scheduler()
        {
            _loop = new Task(ActionLoop);
            _loop.Start();
        }

        public void Schedule(Action action, DateTime when)
        {
            _actions.Push(action, when);
        }

        public void Schedule(Action action)
        {
            Schedule(action, DateTime.UtcNow);
        }

        public void Dispose()
        {
            _log.Info("Disposing of iFix.Crust.Scheduler");
            _dispose.Cancel();
            try { _loop.Wait(); } catch { }
            _log.Info("iFix.Crust.Scheduler successfully disposed of");
        }

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
