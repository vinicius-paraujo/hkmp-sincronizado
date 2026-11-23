using System.Collections.Generic;

namespace Hkmp.Networking.Server.Lifecycle;

/// <summary>
/// The states of the server, from the request to host until every thread of it ended.
/// </summary>
internal enum ServerState
{
    /// <summary>No socket and no thread of the server exist.</summary>
    Stopped,

    /// <summary>The socket is being opened and the threads started.</summary>
    Starting,

    /// <summary>The port is open and the server accepts players.</summary>
    Open,

    /// <summary>The port could not be opened, or a thread of the server failed for good.</summary>
    Failed,

    /// <summary>The threads were asked to end, within the stop deadline.</summary>
    Stopping,
}

/// <summary>
/// The events the server lifecycle reacts to.
/// </summary>
internal enum ServerLifecycleEvent
{
    /// <summary>The player asked to host, or the standalone server starts.</summary>
    Start,

    /// <summary>The socket is bound to the port.</summary>
    PortOpened,

    /// <summary>The socket could not be bound to the port.</summary>
    PortRefused,

    /// <summary>The supervisor gave up on a network loop, or the core failed.</summary>
    LoopFailed,

    /// <summary>The player leaves, or the process closes.</summary>
    Stop,

    /// <summary>Every thread of the server ended.</summary>
    Ended,

    /// <summary>The stop deadline passed with a thread still running.</summary>
    StopDeadline,
}

/// <summary>
/// What the lifecycle does when a transition happens.
/// </summary>
internal enum ServerLifecycleAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Opens the socket and starts the loops under the supervisor.</summary>
    OpenPort,

    /// <summary>Publishes the new state.</summary>
    Publish,

    /// <summary>Records the reason and publishes the state.</summary>
    RecordFailure,

    /// <summary>Records the reason, tells the sessions and publishes the state.</summary>
    RecordFailureAndEndSessions,

    /// <summary>Asks the threads to end and arms the stop deadline.</summary>
    EndThreads,

    /// <summary>Tells the sessions, asks the threads to end and arms the stop deadline.</summary>
    EndSessionsAndThreads,

    /// <summary>Records the threads that did not end and publishes the state.</summary>
    RecordLingeringThreads,
}

/// <summary>
/// The next state and the action of one transition.
/// </summary>
/// <param name="Next">The state after the transition.</param>
/// <param name="Action">The action the lifecycle performs.</param>
internal readonly record struct ServerLifecycleTransition(ServerState Next, ServerLifecycleAction Action);

/// <summary>
/// The transition table of the server. Every pair of state and event that is not listed keeps the state and does
/// nothing, so a late report of a thread can never move the server.
/// </summary>
internal static class ServerLifecycleAutomaton
{
    private static readonly Dictionary<(ServerState, ServerLifecycleEvent), ServerLifecycleTransition> Table = new()
    {
        [(ServerState.Stopped, ServerLifecycleEvent.Start)] =
            new(ServerState.Starting, ServerLifecycleAction.OpenPort),
        [(ServerState.Starting, ServerLifecycleEvent.PortOpened)] =
            new(ServerState.Open, ServerLifecycleAction.Publish),
        [(ServerState.Starting, ServerLifecycleEvent.PortRefused)] =
            new(ServerState.Failed, ServerLifecycleAction.RecordFailure),
        [(ServerState.Starting, ServerLifecycleEvent.LoopFailed)] =
            new(ServerState.Failed, ServerLifecycleAction.RecordFailure),
        [(ServerState.Starting, ServerLifecycleEvent.Stop)] =
            new(ServerState.Stopping, ServerLifecycleAction.EndThreads),
        [(ServerState.Open, ServerLifecycleEvent.LoopFailed)] =
            new(ServerState.Failed, ServerLifecycleAction.RecordFailureAndEndSessions),
        [(ServerState.Open, ServerLifecycleEvent.Stop)] =
            new(ServerState.Stopping, ServerLifecycleAction.EndSessionsAndThreads),
        [(ServerState.Failed, ServerLifecycleEvent.Stop)] =
            new(ServerState.Stopping, ServerLifecycleAction.EndThreads),
        [(ServerState.Stopping, ServerLifecycleEvent.Ended)] =
            new(ServerState.Stopped, ServerLifecycleAction.Publish),
        [(ServerState.Stopping, ServerLifecycleEvent.StopDeadline)] =
            new(ServerState.Stopped, ServerLifecycleAction.RecordLingeringThreads),
    };

    /// <summary>
    /// The transition for the event in the state.
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="lifecycleEvent">The event.</param>
    /// <returns>The next state and the action to perform.</returns>
    public static ServerLifecycleTransition Next(ServerState state, ServerLifecycleEvent lifecycleEvent)
    {
        return Table.TryGetValue((state, lifecycleEvent), out ServerLifecycleTransition transition)
            ? transition
            : new ServerLifecycleTransition(state, ServerLifecycleAction.None);
    }
}
