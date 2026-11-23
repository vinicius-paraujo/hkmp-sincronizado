using System;

namespace Hkmp.Networking.Timing;

/// <summary>
/// A timer of the update and connection managers. The client runs them on thread-pool timers; the server runs
/// them as deadlines of its core, so that their events change server state only on the core thread.
/// </summary>
internal interface IUpdateTimer
{
    /// <summary>
    /// Called when the interval has passed since the timer started.
    /// </summary>
    public event Action Elapsed;

    /// <summary>
    /// The interval in milliseconds. A change applies from the next start or repetition.
    /// </summary>
    public double Interval { get; set; }

    /// <summary>
    /// Whether the timer starts again by itself after each elapse.
    /// </summary>
    public bool AutoReset { get; set; }

    /// <summary>
    /// Starts counting the interval from now. Starting a running timer restarts it.
    /// </summary>
    public void Start();

    /// <summary>
    /// Stops the timer; an elapse that was due no longer happens.
    /// </summary>
    public void Stop();
}

/// <summary>
/// Creates the timers of the update and connection managers.
/// </summary>
internal interface IUpdateTimerFactory
{
    /// <summary>
    /// Creates a stopped timer.
    /// </summary>
    /// <param name="interval">The interval in milliseconds.</param>
    /// <param name="autoReset">Whether the timer starts again by itself after each elapse.</param>
    /// <returns>The timer.</returns>
    public IUpdateTimer Create(double interval, bool autoReset);
}
