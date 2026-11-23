using System.Collections.Generic;

namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// The states of a session, one per player whose connection finished the handshake.
/// </summary>
internal enum SessionState
{
    /// <summary>The server is deciding whether the player may enter.</summary>
    Admitting,

    /// <summary>The player was refused, and the server waits until the reason reached it.</summary>
    Refusing,

    /// <summary>The player is in the game and its connection is alive.</summary>
    Active,

    /// <summary>The player stopped sending; the session waits for it to come back until the drop deadline.</summary>
    Suspended,

    /// <summary>The session ended; it receives nothing more.</summary>
    Ended,
}

/// <summary>
/// The events a session reacts to.
/// </summary>
internal enum SessionEvent
{
    /// <summary>The admission policy let the player in.</summary>
    Accepted,

    /// <summary>The admission policy refused the player.</summary>
    Refused,

    /// <summary>The client confirmed that the refusal reached it.</summary>
    ResponseDelivered,

    /// <summary>The admission, or the delivery of the refusal, did not finish within its deadline.</summary>
    AdmissionDeadline,

    /// <summary>A packet of the session arrived.</summary>
    Packet,

    /// <summary>The silence deadline passed without a packet.</summary>
    Silence,

    /// <summary>A new connection presented the token of the session.</summary>
    Resumption,

    /// <summary>The drop deadline passed without a packet.</summary>
    DropDeadline,

    /// <summary>The same player entered again without the token, as a game that was closed and opened again
    /// does.</summary>
    Replaced,

    /// <summary>The player left, was kicked, or the server stopped.</summary>
    Leave,
}

/// <summary>
/// What the owner of a session does when a transition happens.
/// </summary>
internal enum SessionAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Hands the data to the connection phase.</summary>
    DeliverToConnectionPhase,

    /// <summary>Issues the token, registers the player, tells the addons and sends the acceptance.</summary>
    Admit,

    /// <summary>Sends the reason of the refusal and arms the admission deadline.</summary>
    SendRefusal,

    /// <summary>Ends the connection of a session that never admitted a player.</summary>
    CloseConnection,

    /// <summary>Ends the connection and counts the attempt in the limit of its endpoint.</summary>
    CloseRefused,

    /// <summary>Hands the data on and rearms the silence deadline.</summary>
    Deliver,

    /// <summary>Tells the server manager and the addons of the silence and arms the drop deadline.</summary>
    Suspend,

    /// <summary>Swaps the connection, ends the old one, renews the token and sends the epoch of each room.</summary>
    SwapConnection,

    /// <summary>Disarms the drop deadline, hands the data on and tells that the player is back.</summary>
    ResumeFromPacket,

    /// <summary>Disarms the drop deadline, swaps the connection and tells that the player is back.</summary>
    SwapConnectionAndResume,

    /// <summary>Tells the addons that the player left and ends the connection.</summary>
    AnnounceLeave,
}

/// <summary>
/// The next state and the action of one transition.
/// </summary>
/// <param name="Next">The state after the transition.</param>
/// <param name="Action">The action the owner of the session performs.</param>
internal readonly record struct SessionTransition(SessionState Next, SessionAction Action);

/// <summary>
/// The transition table of a session. Every pair of state and event that is not listed keeps the state and does
/// nothing, so a late or unexpected event can never move a session.
/// </summary>
internal static class SessionAutomaton
{
    private static readonly Dictionary<(SessionState, SessionEvent), SessionTransition> Table = new()
    {
        [(SessionState.Admitting, SessionEvent.Packet)] =
            new(SessionState.Admitting, SessionAction.DeliverToConnectionPhase),
        [(SessionState.Admitting, SessionEvent.Accepted)] = new(SessionState.Active, SessionAction.Admit),
        [(SessionState.Admitting, SessionEvent.Refused)] = new(SessionState.Refusing, SessionAction.SendRefusal),
        [(SessionState.Admitting, SessionEvent.AdmissionDeadline)] =
            new(SessionState.Ended, SessionAction.CloseConnection),
        [(SessionState.Admitting, SessionEvent.Leave)] = new(SessionState.Ended, SessionAction.CloseConnection),
        [(SessionState.Refusing, SessionEvent.ResponseDelivered)] =
            new(SessionState.Ended, SessionAction.CloseRefused),
        [(SessionState.Refusing, SessionEvent.AdmissionDeadline)] =
            new(SessionState.Ended, SessionAction.CloseRefused),
        [(SessionState.Refusing, SessionEvent.Leave)] = new(SessionState.Ended, SessionAction.CloseRefused),
        [(SessionState.Active, SessionEvent.Packet)] = new(SessionState.Active, SessionAction.Deliver),
        [(SessionState.Active, SessionEvent.Silence)] = new(SessionState.Suspended, SessionAction.Suspend),
        [(SessionState.Active, SessionEvent.Resumption)] = new(SessionState.Active, SessionAction.SwapConnection),
        [(SessionState.Active, SessionEvent.Leave)] = new(SessionState.Ended, SessionAction.AnnounceLeave),
        [(SessionState.Suspended, SessionEvent.Packet)] =
            new(SessionState.Active, SessionAction.ResumeFromPacket),
        [(SessionState.Suspended, SessionEvent.Resumption)] =
            new(SessionState.Active, SessionAction.SwapConnectionAndResume),
        [(SessionState.Suspended, SessionEvent.DropDeadline)] =
            new(SessionState.Ended, SessionAction.AnnounceLeave),
        // Only a silent session gives way: one whose game still sends belongs to a game that is still running.
        [(SessionState.Suspended, SessionEvent.Replaced)] = new(SessionState.Ended, SessionAction.AnnounceLeave),
        [(SessionState.Suspended, SessionEvent.Leave)] = new(SessionState.Ended, SessionAction.AnnounceLeave),
    };

    /// <summary>
    /// The transition for the event in the state.
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="sessionEvent">The event.</param>
    /// <returns>The next state and the action to perform.</returns>
    public static SessionTransition Next(SessionState state, SessionEvent sessionEvent)
    {
        return Table.TryGetValue((state, sessionEvent), out SessionTransition transition)
            ? transition
            : new SessionTransition(state, SessionAction.None);
    }
}
