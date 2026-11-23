using System;
using System.Collections.Generic;

namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// Hands out player ids. An id that was given back stays out of use for a while, so data still on its way for a
/// player that left never lands on a player that just joined.
/// </summary>
internal sealed class PlayerIdPool
{
    private readonly HashSet<ushort> _inUse = [];
    private readonly Dictionary<ushort, TimeSpan> _resting = [];
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _rest;
    private ushort _next;

    /// <summary>
    /// Creates the pool.
    /// </summary>
    /// <param name="clock">The current time.</param>
    /// <param name="rest">How long a given back id stays out of use.</param>
    public PlayerIdPool(Func<TimeSpan> clock, TimeSpan rest)
    {
        _clock = clock;
        _rest = rest;
    }

    /// <summary>
    /// An id that is not in use and did not end recently.
    /// </summary>
    /// <returns>The id, now in use.</returns>
    /// <exception cref="InvalidOperationException">Every id is in use or resting.</exception>
    public ushort Take()
    {
        TimeSpan now = _clock();
        for (int tries = 0; tries <= ushort.MaxValue; tries++)
        {
            ushort id = _next++;
            if (_inUse.Contains(id))
            {
                continue;
            }

            if (_resting.TryGetValue(id, out TimeSpan since))
            {
                if (now - since < _rest)
                {
                    continue;
                }

                _resting.Remove(id);
            }

            _inUse.Add(id);
            return id;
        }

        throw new InvalidOperationException("Every player id is in use or ended too recently.");
    }

    /// <summary>
    /// Gives an id back; it rests before anyone gets it again.
    /// </summary>
    /// <param name="id">The id.</param>
    public void Release(ushort id)
    {
        if (_inUse.Remove(id))
        {
            _resting[id] = _clock();
        }
    }

    /// <summary>
    /// Whether the id is in use.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if it was taken and not given back.</returns>
    public bool InUse(ushort id)
    {
        return _inUse.Contains(id);
    }
}
