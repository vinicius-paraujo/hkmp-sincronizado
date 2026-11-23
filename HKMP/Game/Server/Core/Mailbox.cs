using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Hkmp.Game.Server.Core;

/// <summary>
/// The inbox of the server core. Any thread posts; only the core thread takes.
/// </summary>
internal sealed class Mailbox
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _posted = new(false);

    /// <summary>
    /// Signalled when something was posted since the core last waited.
    /// </summary>
    public WaitHandle Posted => _posted;

    /// <summary>
    /// Queues work for the core.
    /// </summary>
    /// <param name="message">The work.</param>
    public void Post(Action message)
    {
        _queue.Enqueue(message);
        _posted.Set();
    }

    /// <summary>
    /// Takes the oldest message, if there is one.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>False if the mailbox is empty.</returns>
    public bool TryTake(out Action message)
    {
        return _queue.TryDequeue(out message);
    }
}
