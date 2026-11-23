using System;
using System.Collections.Concurrent;
using System.Threading;
using Hkmp.Logging;

namespace Hkmp.Game.Server.Core;

/// <summary>
/// Checks that the server API is used only on the core thread. A debug build throws at the offending call; a
/// release build logs the first violation of each member, with its stack, and counts every one.
/// </summary>
internal sealed class CoreThreadGuard
{
    private readonly ServerCore _core;
    private readonly ConcurrentDictionary<string, byte> _logged = new();
    private int _violations;

    /// <summary>
    /// Creates the guard of a core.
    /// </summary>
    /// <param name="core">The core whose thread owns the server state.</param>
    public CoreThreadGuard(ServerCore core)
    {
        _core = core;
    }

    /// <summary>
    /// The number of calls made from another thread so far.
    /// </summary>
    public int Violations => Volatile.Read(ref _violations);

    /// <summary>
    /// Records a call of the member from another thread than the core.
    /// </summary>
    /// <param name="member">The name of the API member called.</param>
    public void Check(string member)
    {
        if (_core.IsCoreThread)
        {
            return;
        }

        int count = Interlocked.Increment(ref _violations);
#if DEBUG
        throw new InvalidOperationException($"{member} was called outside the server core thread.");
#else
        if (_logged.TryAdd(member, 0))
        {
            Logger.Warn($"Server thread violation {count}: {member} called from thread "
                + $"'{Thread.CurrentThread.Name}':\n{Environment.StackTrace}");
        }
#endif
    }
}
