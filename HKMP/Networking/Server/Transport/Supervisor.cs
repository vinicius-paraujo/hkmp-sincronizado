using System;
using System.Diagnostics;
using System.Threading;
using Hkmp.Logging;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Runs a loop on its own thread and restarts it after a failure, waiting longer after each one. No loop ends
/// without its cause being logged, and a loop that keeps failing is reported instead of restarted forever.
/// </summary>
internal sealed class Supervisor
{
    /// <summary>
    /// The number of failures in a row after which the loop is no longer restarted.
    /// </summary>
    public const int MaxFailuresInARow = 5;

    private const int FirstDelayMillis = 100;
    private const int MaxDelayMillis = 5000;

    // A loop that ran this long before failing is considered healthy again.
    private const int HealthyRunMillis = 60000;

    private Thread _thread;

    /// <summary>
    /// Called on the loop's thread when the loop failed too often in a row and was given up.
    /// </summary>
    public event Action<string, Exception> Faulted;

    /// <summary>
    /// Starts the loop on a new thread.
    /// </summary>
    /// <param name="name">The name of the loop, for the thread and the log.</param>
    /// <param name="loop">The loop, which returns when its token is cancelled.</param>
    /// <param name="token">Stops the loop and its restarts.</param>
    public void Start(string name, Action<CancellationToken> loop, CancellationToken token)
    {
        _thread = new Thread(() => Supervise(name, loop, token))
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
    }

    /// <summary>
    /// Waits for the loop's thread to end.
    /// </summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns>False if the thread is still running after the timeout.</returns>
    public bool Join(TimeSpan timeout)
    {
        return _thread == null || _thread.Join(timeout);
    }

    private void Supervise(string name, Action<CancellationToken> loop, CancellationToken token)
    {
        int failures = 0;
        int delay = FirstDelayMillis;
        while (!token.IsCancellationRequested)
        {
            var running = Stopwatch.StartNew();
            try
            {
                loop(token);
                return;
            }
            catch (Exception e)
            {
                if (running.ElapsedMilliseconds >= HealthyRunMillis)
                {
                    failures = 0;
                    delay = FirstDelayMillis;
                }

                failures++;
                Logger.Error($"{name} failed ({failures} in a row):\n{e}");
                if (failures >= MaxFailuresInARow)
                {
                    Faulted?.Invoke(name, e);
                    return;
                }
            }

            token.WaitHandle.WaitOne(delay);
            delay = System.Math.Min(delay * 2, MaxDelayMillis);
        }
    }
}
