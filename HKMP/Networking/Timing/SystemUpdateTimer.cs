using System;
using System.Timers;

namespace Hkmp.Networking.Timing;

/// <summary>
/// A timer on the thread pool, as the client has always used.
/// </summary>
internal sealed class SystemUpdateTimer : IUpdateTimer
{
    private readonly Timer _timer;

    /// <summary>
    /// Creates a stopped timer.
    /// </summary>
    /// <param name="interval">The interval in milliseconds.</param>
    /// <param name="autoReset">Whether the timer starts again by itself after each elapse.</param>
    public SystemUpdateTimer(double interval, bool autoReset)
    {
        _timer = new Timer { Interval = interval, AutoReset = autoReset };
        _timer.Elapsed += (_, _) => Elapsed?.Invoke();
    }

    /// <inheritdoc />
    public event Action Elapsed;

    /// <inheritdoc />
    public double Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    /// <inheritdoc />
    public bool AutoReset
    {
        get => _timer.AutoReset;
        set => _timer.AutoReset = value;
    }

    /// <inheritdoc />
    public void Start()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _timer.Stop();
    }
}

/// <summary>
/// Creates thread-pool timers.
/// </summary>
internal sealed class SystemUpdateTimerFactory : IUpdateTimerFactory
{
    /// <summary>
    /// The shared instance.
    /// </summary>
    public static readonly SystemUpdateTimerFactory Instance = new();

    /// <inheritdoc />
    public IUpdateTimer Create(double interval, bool autoReset)
    {
        return new SystemUpdateTimer(interval, autoReset);
    }
}
