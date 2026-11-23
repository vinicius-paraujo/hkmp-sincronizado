using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Hkmp.Logging;

namespace Hkmp.Game.Server.Core;

/// <summary>
/// The one thread that changes server state. Every other thread hands its work to the mailbox, and every deadline
/// runs from the scheduler, so the rules of the server never run at the same time as each other.
/// </summary>
internal sealed class ServerCore : IDisposable
{
    // How long the core sleeps at most when nothing is due; it wakes earlier on every post.
    private const int IdleWaitMillis = 1000;

    // An Invoke that waits this long most likely waits on a core that waits on the caller.
    private const int SlowInvokeMillis = 5000;

    private readonly Mailbox _mailbox = new();
    private readonly Scheduler _scheduler;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopping = new();
    private volatile Action _running;

    /// <summary>
    /// Creates the core and starts its thread.
    /// </summary>
    public ServerCore()
    {
        var clock = Stopwatch.StartNew();
        _scheduler = new Scheduler(() => clock.ElapsedMilliseconds);
        _thread = new Thread(Run) { IsBackground = true, Name = "Server core" };
        _thread.Start();
    }

    /// <summary>
    /// Whether the calling thread is the core thread.
    /// </summary>
    public bool IsCoreThread => Thread.CurrentThread == _thread;

    /// <summary>
    /// Queues work for the core thread. Messages run in the order posted.
    /// </summary>
    /// <param name="message">The work.</param>
    public void Post(Action message)
    {
        _mailbox.Post(message);
    }

    /// <summary>
    /// Runs the work on the core thread and waits for it. On the core thread itself, it runs at once.
    /// </summary>
    /// <param name="work">The work.</param>
    public void Invoke(Action work)
    {
        if (IsCoreThread)
        {
            work();
            return;
        }

        ExceptionDispatchInfo failure = null;
        using ManualResetEventSlim done = new();
        Post(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(SlowInvokeMillis))
        {
            Action running = _running;
            Logger.Warn($"Waited {SlowInvokeMillis} ms for the server core, which is running "
                + $"{running?.Method.DeclaringType}.{running?.Method.Name}. Caller:\n{Environment.StackTrace}");
            done.Wait();
        }

        failure?.Throw();
    }

    /// <summary>
    /// Runs the action on the core thread after the delay.
    /// </summary>
    /// <param name="delay">The delay.</param>
    /// <param name="action">The action.</param>
    /// <returns>The deadline, which can be cancelled from any thread.</returns>
    public ScheduledTask Schedule(TimeSpan delay, Action action)
    {
        long millis = (long) delay.TotalMilliseconds;
        if (IsCoreThread)
        {
            return _scheduler.After(millis, action);
        }

        // The deadline is created now so that the caller can cancel it before the core has registered it.
        ScheduledTask handle = new(action);
        Post(() =>
        {
            if (!handle.IsCancelled)
            {
                _scheduler.After(millis, () =>
                {
                    if (!handle.IsCancelled)
                    {
                        action();
                    }
                });
            }
        });
        return handle;
    }

    /// <summary>
    /// Runs slow work, such as disk access, away from the core, and its continuation back on the core.
    /// </summary>
    /// <param name="work">The slow work, which must not touch server state.</param>
    /// <param name="onFinished">Runs on the core thread afterwards, with the failure or null.</param>
    public void RunInBackground(Action work, Action<Exception> onFinished)
    {
        Task.Run(() =>
        {
            Exception failure = null;
            try
            {
                work();
            }
            catch (Exception e)
            {
                failure = e;
            }

            Post(() => onFinished?.Invoke(failure));
        });
    }

    /// <summary>
    /// Stops the core thread after the message it is running.
    /// </summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _mailbox.Post(() => { });
        if (!IsCoreThread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void Run()
    {
        WaitHandle[] wake = [_mailbox.Posted, _stopping.Token.WaitHandle];
        while (!_stopping.IsCancellationRequested)
        {
            while (_mailbox.TryTake(out Action message))
            {
                Execute(message);
            }

            _scheduler.RunDue(Execute);

            long wait = _scheduler.MillisUntilNext() ?? IdleWaitMillis;
            WaitHandle.WaitAny(wake, (int) System.Math.Min(wait, IdleWaitMillis));
        }
    }

    /// <summary>
    /// A failing message concerns only itself; the core goes on with the next one.
    /// </summary>
    private void Execute(Action message)
    {
        _running = message;
        try
        {
            message();
        }
        catch (Exception e)
        {
            Logger.Error($"Error in the server core:\n{e}");
        }
        finally
        {
            _running = null;
        }
    }
}
