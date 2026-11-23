using System;

namespace Hkmp.Networking.Timing;

/// <summary>
/// The deadlines of the connection and of the session, in one table, so their relations can be checked together.
/// The game always runs with <see cref="Default"/>; another table only shortens the waits where the deadlines
/// themselves are under test.
/// </summary>
/// <param name="Admission">How long the admission may take, from the end of the handshake to the answer.</param>
/// <param name="Silence">How long without a packet before a session counts as silent.</param>
/// <param name="Drop">How long without a packet before a session ends.</param>
/// <param name="IdRest">How long the id of an ended session stays out of use.</param>
internal sealed record ConnectionTimeouts(TimeSpan Admission, TimeSpan Silence, TimeSpan Drop, TimeSpan IdRest)
{
    /// <summary>
    /// Creates a table with every deadline given. The four-deadline form keeps the game's values for the rest.
    /// </summary>
    public ConnectionTimeouts(
        TimeSpan admission,
        TimeSpan silence,
        TimeSpan drop,
        TimeSpan idRest,
        TimeSpan serverHandshake,
        TimeSpan clientHandshake,
        TimeSpan retryJitter,
        TimeSpan retryFirst,
        double retryGrowth,
        TimeSpan retryMax,
        TimeSpan giveUp
    ) : this(admission, silence, drop, idRest)
    {
        ServerHandshake = serverHandshake;
        ClientHandshake = clientHandshake;
        RetryJitter = retryJitter;
        RetryFirst = retryFirst;
        RetryGrowth = retryGrowth;
        RetryMax = retryMax;
        GiveUp = giveUp;
    }

    /// <summary>How long the server gives a handshake before it frees the place.</summary>
    public TimeSpan ServerHandshake { get; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the client waits for a handshake before the attempt fails.</summary>
    public TimeSpan ClientHandshake { get; } = TimeSpan.FromSeconds(10);

    /// <summary>The largest random delay before the first attempt to take a silent session back.</summary>
    public TimeSpan RetryJitter { get; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The wait before the second attempt; each later wait grows by <see cref="RetryGrowth"/>.</summary>
    public TimeSpan RetryFirst { get; } = TimeSpan.FromSeconds(1);

    /// <summary>How much each wait between attempts grows over the previous one.</summary>
    public double RetryGrowth { get; } = 1.5;

    /// <summary>The longest wait between attempts.</summary>
    public TimeSpan RetryMax { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the client keeps trying to take its session back before it gives up.</summary>
    public TimeSpan GiveUp { get; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a stopping server waits for its threads before it records them as left running.</summary>
    public TimeSpan ServerStop { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The deadlines the game uses.</summary>
    public static ConnectionTimeouts Default { get; } = new(
        Admission: TimeSpan.FromSeconds(20),
        Silence: TimeSpan.FromSeconds(5),
        Drop: TimeSpan.FromSeconds(30),
        IdRest: TimeSpan.FromSeconds(60)
    );

    /// <summary>
    /// How long the client waits, with the largest random delay, from the silence until it starts the attempt with
    /// the given number, counted from zero.
    /// </summary>
    /// <param name="attempts">How many attempts start in that time.</param>
    /// <returns>The time from the silence until the last of them starts.</returns>
    public TimeSpan LongestTimeUntilAttempt(int attempts)
    {
        TimeSpan total = Silence + RetryJitter;
        TimeSpan wait = RetryFirst;
        for (int attempt = 1; attempt < attempts; attempt++)
        {
            total += wait;
            wait = TimeSpan.FromTicks(System.Math.Min((long) (wait.Ticks * RetryGrowth), RetryMax.Ticks));
        }

        return total;
    }

    /// <summary>
    /// Whether the deadlines follow one another as the session needs: the server frees a handshake before the client
    /// gives up on it, at least three attempts start between the silence and the drop, a session is silent before it
    /// ends, the client keeps trying past the drop so it can still enter anew, and an id only returns after its session
    /// can no longer come back.
    /// </summary>
    public bool KeepsItsRelations =>
        ServerHandshake < ClientHandshake
        && LongestTimeUntilAttempt(3) < Drop
        && Silence < Drop
        && Drop < GiveUp
        && Drop < IdRest;
}
