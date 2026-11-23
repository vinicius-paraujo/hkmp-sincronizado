using System;
using System.Collections.Generic;

using Hkmp.Api.Server;

namespace Hkmp.Game.Server.Core;

/// <summary>
/// The deadlines of the server core. Only the core thread uses it, so it has no locks. Deadlines due at the same
/// moment run in the order they were scheduled.
/// </summary>
internal sealed class Scheduler
{
    private readonly SortedDictionary<(long Due, long Order), ScheduledTask> _queue = new();
    private readonly Func<long> _clock;
    private long _order;

    /// <summary>
    /// Creates the scheduler.
    /// </summary>
    /// <param name="clock">The current time in milliseconds, from any fixed origin.</param>
    public Scheduler(Func<long> clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// The current time of the scheduler's clock in milliseconds.
    /// </summary>
    public long Now => _clock();

    /// <summary>
    /// The number of deadlines waiting, cancelled ones included until their time comes.
    /// </summary>
    public int Count => _queue.Count;

    /// <summary>
    /// Runs the action once after the delay.
    /// </summary>
    /// <param name="delayMillis">The delay in milliseconds.</param>
    /// <param name="action">The action.</param>
    /// <returns>The deadline, which can be cancelled.</returns>
    public ScheduledTask After(long delayMillis, Action action)
    {
        ScheduledTask task = new(action);
        _queue.Add((_clock() + System.Math.Max(0, delayMillis), _order++), task);
        return task;
    }

    /// <summary>
    /// How long until the next deadline in milliseconds, or null if there is none.
    /// </summary>
    /// <returns>The time to wait, zero if a deadline is already due.</returns>
    public long? MillisUntilNext()
    {
        foreach (KeyValuePair<(long Due, long Order), ScheduledTask> first in _queue)
        {
            return System.Math.Max(0, first.Key.Due - _clock());
        }

        return null;
    }

    /// <summary>
    /// Runs every deadline that is due, in order. A deadline scheduled by one of them for now runs too.
    /// </summary>
    /// <param name="run">Runs one action, so that the caller decides how a failure is handled.</param>
    public void RunDue(Action<Action> run)
    {
        while (_queue.Count > 0)
        {
            KeyValuePair<(long Due, long Order), ScheduledTask> first = default;
            foreach (KeyValuePair<(long Due, long Order), ScheduledTask> entry in _queue)
            {
                first = entry;
                break;
            }

            if (first.Key.Due > _clock())
            {
                return;
            }

            _queue.Remove(first.Key);
            if (!first.Value.IsCancelled)
            {
                run(first.Value.Action);
            }
        }
    }
}

/// <summary>
/// A deadline of the server core.
/// </summary>
internal sealed class ScheduledTask : IScheduledTask
{
    private volatile bool _cancelled;

    internal ScheduledTask(Action action)
    {
        Action = action;
    }

    /// <summary>
    /// Whether the deadline was cancelled.
    /// </summary>
    public bool IsCancelled => _cancelled;

    internal Action Action { get; }

    /// <summary>
    /// Cancels the deadline. Safe from any thread; a deadline that already ran is not affected.
    /// </summary>
    public void Cancel()
    {
        _cancelled = true;
    }
}
