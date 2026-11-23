using System;
using Hkmp.Networking.Timing;

namespace Hkmp.Game.Server.Core;

/// <summary>
/// A timer whose elapses are deadlines of the server core, so its event runs on the core thread. Starting or
/// stopping it cancels the pending deadline, which keeps an old elapse from firing after a restart.
/// </summary>
internal sealed class CoreTimer : IUpdateTimer
{
    private readonly ServerCore _core;
    private readonly object _sync = new();
    private ScheduledTask _pending;

    /// <summary>
    /// Creates a stopped timer.
    /// </summary>
    /// <param name="core">The core that runs the elapses.</param>
    /// <param name="interval">The interval in milliseconds.</param>
    /// <param name="autoReset">Whether the timer starts again by itself after each elapse.</param>
    public CoreTimer(ServerCore core, double interval, bool autoReset)
    {
        _core = core;
        Interval = interval;
        AutoReset = autoReset;
    }

    /// <inheritdoc />
    public event Action Elapsed;

    /// <inheritdoc />
    public double Interval { get; set; }

    /// <inheritdoc />
    public bool AutoReset { get; set; }

    /// <inheritdoc />
    public void Start()
    {
        // A lock, because the managers start and stop their timers from the core and, on the client side of a
        // shared manager, from other threads; the elapse itself always runs on the core.
        lock (_sync)
        {
            _pending?.Cancel();
            _pending = _core.Schedule(TimeSpan.FromMilliseconds(Interval), OnElapsed);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }

    private void OnElapsed()
    {
        lock (_sync)
        {
            _pending = AutoReset ? _core.Schedule(TimeSpan.FromMilliseconds(Interval), OnElapsed) : null;
        }

        Elapsed?.Invoke();
    }
}

/// <summary>
/// Creates timers that run on the server core.
/// </summary>
internal sealed class CoreTimerFactory : IUpdateTimerFactory
{
    private readonly ServerCore _core;

    /// <summary>
    /// Creates the factory.
    /// </summary>
    /// <param name="core">The core that runs the elapses.</param>
    public CoreTimerFactory(ServerCore core)
    {
        _core = core;
    }

    /// <inheritdoc />
    public IUpdateTimer Create(double interval, bool autoReset)
    {
        return new CoreTimer(_core, interval, autoReset);
    }
}
