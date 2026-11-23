using System;
using System.Collections.Generic;
using System.Linq;

namespace Hkmp.Game.Server.Rooms;

/// <summary>
/// Chooses the scene host of a room among the players in it.
/// </summary>
internal sealed class HostCandidatePolicy
{
    private readonly Func<ushort, bool> _isSilent;
    private readonly Func<ushort, bool> _isServerOwner;

    /// <summary>
    /// Creates the policy.
    /// </summary>
    /// <param name="isSilent">Whether a player stopped sending updates and has not resumed.</param>
    /// <param name="isServerOwner">Whether a player is the client of whoever runs the server.</param>
    public HostCandidatePolicy(Func<ushort, bool> isSilent, Func<ushort, bool> isServerOwner)
    {
        _isSilent = isSilent;
        _isServerOwner = isServerOwner;
    }

    /// <summary>
    /// Chooses the host among the candidates. A silent candidate is chosen only when every candidate is silent,
    /// because its room would freeze for the others. Among the rest, the server owner's client comes first: it
    /// reaches the server through the local interface and cannot be cut off by the network. The lowest ID breaks
    /// the remaining ties, so the same room always gives the same answer.
    /// </summary>
    /// <param name="candidates">The players that may host the room.</param>
    /// <returns>The chosen player, or null when there is no candidate.</returns>
    public ushort? Choose(IEnumerable<ushort> candidates)
    {
        var ordered = candidates
            .OrderBy(player => _isSilent(player) ? 1 : 0)
            .ThenBy(player => _isServerOwner(player) ? 0 : 1)
            .ThenBy(player => player)
            .ToList();
        return ordered.Count == 0 ? null : ordered[0];
    }
}
