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

        ScheduledQueue<Action> _actions = new ScheduledQueue<Action>();
        CancellationTokenSource _dispose = new CancellationTokenSource();
        Task _loop;

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
            _dispose.Cancel();
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
