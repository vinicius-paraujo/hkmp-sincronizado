using System.Collections.Generic;
using Hkmp.Api.Client;

namespace Hkmp.Networking.Client.Connection;

/// <summary>
/// The events the client connection reacts to.
/// </summary>
internal enum ClientConnectionEvent
{
    /// <summary>The player asks to connect.</summary>
    Connect,

    /// <summary>The handshake of the current attempt ended well.</summary>
    HandshakeSucceeded,

    /// <summary>The handshake of the current attempt failed, for any reason.</summary>
    HandshakeFailed,

    /// <summary>The server admitted the player, or took its session back.</summary>
    Accepted,

    /// <summary>
    /// The server no longer had the session of the token and admitted the player as a new one in the same answer.
    /// </summary>
    AcceptedAsNew,

    /// <summary>The server refused the player.</summary>
    Refused,

    /// <summary>The answer to the admission did not arrive in time.</summary>
    AdmissionDeadline,

    /// <summary>No packet arrived from the server for the silence deadline.</summary>
    Silence,

    /// <summary>A packet arrived through the connection that had gone silent.</summary>
    Packet,

    /// <summary>The reconnect policy allows the next attempt.</summary>
    RetryDue,

    /// <summary>The client tried to take its session back for the whole give-up deadline.</summary>
    GiveUp,

    /// <summary>The server ended the session, by a kick or because it stopped.</summary>
    Ended,

    /// <summary>The player leaves.</summary>
    Leave,
}

/// <summary>
/// What the connection does when a transition happens.
/// </summary>
internal enum ClientConnectionAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Starts an attempt.</summary>
    StartAttempt,

    /// <summary>Records the reason and publishes the state.</summary>
    RecordFailure,

    /// <summary>Cancels the attempt and publishes the state.</summary>
    CancelAttempt,

    /// <summary>Sends the client's data through the new connection and arms the admission deadline.</summary>
    SendClientInfo,

    /// <summary>Disarms the admission deadline, keeps the token and publishes the state.</summary>
    Admit,

    /// <summary>Closes the connection and records the reason.</summary>
    CloseAndRecordFailure,

    /// <summary>Closes the connection and publishes the state.</summary>
    Close,

    /// <summary>Tells the server the player leaves, closes the connection and publishes the state.</summary>
    SendLeaveAndClose,

    /// <summary>Publishes the state, schedules the first attempt by the policy and arms the give-up deadline.</summary>
    BeginResuming,

    /// <summary>Drops the scheduled attempt and the give-up deadline, and publishes the state.</summary>
    EndResumingOnOldConnection,

    /// <summary>Schedules the next attempt by the policy.</summary>
    ScheduleRetry,

    /// <summary>Keeps the new token, drops the give-up deadline and publishes the state.</summary>
    Resume,

    /// <summary>Keeps the new token, drops the give-up deadline and publishes the state of a player entered anew.</summary>
    Reenter,

    /// <summary>Cancels the attempt, drops the deadlines and records the reason.</summary>
    CancelAndRecordFailure,

    /// <summary>Cancels the attempt, drops the deadlines, closes the connection and publishes the state.</summary>
    CancelAndClose,

    /// <summary>Publishes the state.</summary>
    Publish,
}

/// <summary>
/// The next state and the action of one transition.
/// </summary>
/// <param name="Next">The state after the transition.</param>
/// <param name="Action">The action the connection performs.</param>
internal readonly record struct ClientConnectionTransition(ConnectionState Next, ClientConnectionAction Action);

/// <summary>
/// The transition table of the client connection. Every pair of state and event that is not listed keeps the state
/// and does nothing, so the result of an attempt that was already cancelled can never move the connection.
/// </summary>
internal static class ClientConnectionAutomaton
{
    private static readonly Dictionary<(ConnectionState, ClientConnectionEvent), ClientConnectionTransition> Table = new()
    {
        [(ConnectionState.Disconnected, ClientConnectionEvent.Connect)] =
            new(ConnectionState.Connecting, ClientConnectionAction.StartAttempt),

        [(ConnectionState.Connecting, ClientConnectionEvent.HandshakeSucceeded)] =
            new(ConnectionState.Admitting, ClientConnectionAction.SendClientInfo),
        [(ConnectionState.Connecting, ClientConnectionEvent.HandshakeFailed)] =
            new(ConnectionState.Failed, ClientConnectionAction.RecordFailure),
        [(ConnectionState.Connecting, ClientConnectionEvent.Leave)] =
            new(ConnectionState.Disconnected, ClientConnectionAction.CancelAttempt),

        [(ConnectionState.Admitting, ClientConnectionEvent.Accepted)] =
            new(ConnectionState.Connected, ClientConnectionAction.Admit),
        [(ConnectionState.Admitting, ClientConnectionEvent.AcceptedAsNew)] =
            new(ConnectionState.Connected, ClientConnectionAction.Admit),
        [(ConnectionState.Admitting, ClientConnectionEvent.Refused)] =
            new(ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure),
        [(ConnectionState.Admitting, ClientConnectionEvent.AdmissionDeadline)] =
            new(ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure),
        [(ConnectionState.Admitting, ClientConnectionEvent.Leave)] =
            new(ConnectionState.Disconnected, ClientConnectionAction.Close),

        [(ConnectionState.Connected, ClientConnectionEvent.Silence)] =
            new(ConnectionState.Resuming, ClientConnectionAction.BeginResuming),
        [(ConnectionState.Connected, ClientConnectionEvent.Ended)] =
            new(ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure),
        [(ConnectionState.Connected, ClientConnectionEvent.Leave)] =
            new(ConnectionState.Disconnected, ClientConnectionAction.SendLeaveAndClose),

        [(ConnectionState.Resuming, ClientConnectionEvent.Packet)] =
            new(ConnectionState.Connected, ClientConnectionAction.EndResumingOnOldConnection),
        [(ConnectionState.Resuming, ClientConnectionEvent.RetryDue)] =
            new(ConnectionState.Resuming, ClientConnectionAction.StartAttempt),
        [(ConnectionState.Resuming, ClientConnectionEvent.HandshakeSucceeded)] =
            new(ConnectionState.Resuming, ClientConnectionAction.SendClientInfo),
        [(ConnectionState.Resuming, ClientConnectionEvent.HandshakeFailed)] =
            new(ConnectionState.Resuming, ClientConnectionAction.ScheduleRetry),
        [(ConnectionState.Resuming, ClientConnectionEvent.AdmissionDeadline)] =
            new(ConnectionState.Resuming, ClientConnectionAction.ScheduleRetry),
        [(ConnectionState.Resuming, ClientConnectionEvent.Accepted)] =
            new(ConnectionState.Connected, ClientConnectionAction.Resume),
        [(ConnectionState.Resuming, ClientConnectionEvent.AcceptedAsNew)] =
            new(ConnectionState.Connected, ClientConnectionAction.Reenter),
        [(ConnectionState.Resuming, ClientConnectionEvent.Refused)] =
            new(ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure),
        [(ConnectionState.Resuming, ClientConnectionEvent.GiveUp)] =
            new(ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure),
        [(ConnectionState.Resuming, ClientConnectionEvent.Ended)] =
            new(ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure),
        [(ConnectionState.Resuming, ClientConnectionEvent.Leave)] =
            new(ConnectionState.Disconnected, ClientConnectionAction.CancelAndClose),

        [(ConnectionState.Failed, ClientConnectionEvent.Leave)] =
            new(ConnectionState.Disconnected, ClientConnectionAction.Publish),
    };

    /// <summary>
    /// The transition for the event in the state.
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="connectionEvent">The event.</param>
    /// <returns>The next state and the action to perform.</returns>
    public static ClientConnectionTransition Next(ConnectionState state, ClientConnectionEvent connectionEvent)
    {
        return Table.TryGetValue((state, connectionEvent), out ClientConnectionTransition transition)
            ? transition
            : new ClientConnectionTransition(state, ClientConnectionAction.None);
    }
}
