using System;
using Hkmp.Networking.Timing;

namespace Hkmp.Networking.Client.Connection;

/// <summary>
/// Decides when a client that lost its server tries again. Every player of a session loses the server at the same
/// moment when the path to it drops, so the first attempt waits a random time, which spreads the players over the
/// server's handshake places; each later attempt waits longer, up to a limit, so a server that is down is not
/// knocked on without pause.
/// </summary>
internal sealed class ReconnectPolicy
{
    private readonly ConnectionTimeouts _timeouts;
    private readonly Func<double> _random;

    /// <summary>
    /// Creates the policy of the given deadlines.
    /// </summary>
    /// <param name="timeouts">The deadlines.</param>
    /// <param name="random">Returns a number from zero up to, but not including, one.</param>
    public ReconnectPolicy(ConnectionTimeouts timeouts, Func<double> random)
    {
        _timeouts = timeouts;
        _random = random;
    }

    /// <summary>
    /// The wait before the attempt with the given number, counted from zero at the silence.
    /// </summary>
    /// <param name="attempt">The number of the attempt.</param>
    /// <returns>The wait.</returns>
    public TimeSpan NextDelay(int attempt)
    {
        if (attempt <= 0)
        {
            return TimeSpan.FromTicks((long) (_timeouts.RetryJitter.Ticks * _random()));
        }

        double grown = _timeouts.RetryFirst.Ticks * System.Math.Pow(_timeouts.RetryGrowth, attempt - 1);
        return TimeSpan.FromTicks((long) System.Math.Min(grown, _timeouts.RetryMax.Ticks));
    }
}
