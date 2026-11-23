using System;
using System.Collections.Generic;
using Hkmp.Logging;

namespace Hkmp.Networking.Client.Connection;

/// <summary>
/// The only way into the game's main thread. Any thread may post work; the main thread drains it once a frame, in
/// the order it was posted, and then gives the owners of deadlines their tick, so a deadline never runs beside the
/// work that would have changed it.
/// </summary>
internal sealed class MainThreadMailbox
{
    private readonly object _lock = new();
    private readonly List<Action> _posted = [];

    /// <summary>The mailbox of the game.</summary>
    public static MainThreadMailbox Shared { get; } = new();

    /// <summary>Raised on the main thread after each drain, for the owners of deadlines.</summary>
    public event Action Drained;

    /// <summary>
    /// Queues work for the main thread. Safe from any thread.
    /// </summary>
    /// <param name="action">The work.</param>
    public void Post(Action action)
    {
        lock (_lock)
        {
            _posted.Add(action);
        }
    }

    /// <summary>
    /// Runs the posted work on the calling thread, which is the main thread in the game. Work posted while it runs waits
    /// for the next drain, so one drain always ends. A failing piece of work is logged and the rest still runs: the
    /// results of the connection come through here, and losing one would leave the connection waiting forever.
    /// </summary>
    public void Drain()
    {
        Action[] actions;
        lock (_lock)
        {
            actions = _posted.ToArray();
            _posted.Clear();
        }

        foreach (Action action in actions)
        {
            Run(action);
        }

        // Each owner of deadlines gets its tick even when another one fails.
        foreach (Delegate tick in Drained?.GetInvocationList() ?? [])
        {
            Run((Action) tick);
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Error($"Work on the main thread failed:\n{exception}");
        }
    }
}
