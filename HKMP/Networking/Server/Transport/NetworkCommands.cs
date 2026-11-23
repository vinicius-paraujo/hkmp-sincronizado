using System;
using System.Collections.Concurrent;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// The inbox of the network thread. Other threads post work here instead of touching the peers, which only the
/// network thread changes.
/// </summary>
internal sealed class NetworkCommands
{
    private readonly ConcurrentQueue<Action> _queue = new();

    /// <summary>
    /// Queues work for the network thread.
    /// </summary>
    /// <param name="command">The work.</param>
    public void Post(Action command)
    {
        _queue.Enqueue(command);
    }

    /// <summary>
    /// Runs every queued command, in the order posted. Called only by the network thread.
    /// </summary>
    public void RunAll()
    {
        while (_queue.TryDequeue(out Action command))
        {
            command();
        }
    }
}
