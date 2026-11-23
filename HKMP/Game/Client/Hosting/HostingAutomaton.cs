using System.Collections.Generic;

namespace Hkmp.Game.Client.Hosting;

/// <summary>
/// The states of hosting, from the chosen save until the player plays on its own server.
/// </summary>
internal enum HostingState
{
    /// <summary>Nothing is hosted.</summary>
    Idle,

    /// <summary>The server is opening its port; the save waits.</summary>
    StartingServer,

    /// <summary>The server is open and the local client connects to it; the save waits.</summary>
    ConnectingLocal,

    /// <summary>The local client is connected and the save loads.</summary>
    Loading,

    /// <summary>The player plays on its own server.</summary>
    Hosting,

    /// <summary>What was started is being undone, until the server is stopped.</summary>
    Undoing,
}

/// <summary>
/// The events hosting reacts to.
/// </summary>
internal enum HostingEvent
{
    /// <summary>The player chose a save and asked to host.</summary>
    Host,

    /// <summary>The server's lifecycle reached open.</summary>
    ServerOpen,

    /// <summary>The server's lifecycle reached failed.</summary>
    ServerFailed,

    /// <summary>The server's lifecycle reached stopped.</summary>
    ServerStopped,

    /// <summary>The local client is connected to the server.</summary>
    ClientConnected,

    /// <summary>The local client could not connect, or lost its connection.</summary>
    ClientFailed,

    /// <summary>The save finished loading.</summary>
    GameLoaded,

    /// <summary>The player leaves the game.</summary>
    Leave,
}

/// <summary>
/// What hosting does when a transition happens.
/// </summary>
internal enum HostingAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Starts the server on the chosen port.</summary>
    StartServer,

    /// <summary>Connects the local client to the server.</summary>
    ConnectLocal,

    /// <summary>Loads the chosen save.</summary>
    LoadSave,

    /// <summary>Marks that the game is loaded.</summary>
    MarkLoaded,

    /// <summary>Stops the server and keeps the reason.</summary>
    StopServer,

    /// <summary>Disconnects the local client, stops the server and keeps the reason.</summary>
    DisconnectAndStopServer,

    /// <summary>Returns to the menu if the game had loaded, and shows the reason unless the player left.</summary>
    Finish,
}

/// <summary>
/// The next state and the action of one transition.
/// </summary>
/// <param name="Next">The state after the transition.</param>
/// <param name="Action">The action hosting performs.</param>
internal readonly record struct HostingTransition(HostingState Next, HostingAction Action);

/// <summary>
/// The transition table of hosting. Every pair of state and event that is not listed keeps the state and does
/// nothing, so a late report of the server or of the local client can never move it.
/// </summary>
internal static class HostingAutomaton
{
    private static readonly Dictionary<(HostingState, HostingEvent), HostingTransition> Table = new()
    {
        [(HostingState.Idle, HostingEvent.Host)] = new(HostingState.StartingServer, HostingAction.StartServer),
        [(HostingState.StartingServer, HostingEvent.ServerOpen)] =
            new(HostingState.ConnectingLocal, HostingAction.ConnectLocal),
        [(HostingState.StartingServer, HostingEvent.ServerFailed)] =
            new(HostingState.Undoing, HostingAction.StopServer),
        [(HostingState.StartingServer, HostingEvent.Leave)] = new(HostingState.Undoing, HostingAction.StopServer),
        [(HostingState.ConnectingLocal, HostingEvent.ClientConnected)] =
            new(HostingState.Loading, HostingAction.LoadSave),
        [(HostingState.ConnectingLocal, HostingEvent.ClientFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.ConnectingLocal, HostingEvent.ServerFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.ConnectingLocal, HostingEvent.Leave)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Loading, HostingEvent.GameLoaded)] = new(HostingState.Hosting, HostingAction.MarkLoaded),
        [(HostingState.Loading, HostingEvent.ClientFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Loading, HostingEvent.ServerFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Loading, HostingEvent.Leave)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Hosting, HostingEvent.ClientFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Hosting, HostingEvent.ServerFailed)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Hosting, HostingEvent.Leave)] =
            new(HostingState.Undoing, HostingAction.DisconnectAndStopServer),
        [(HostingState.Undoing, HostingEvent.ServerStopped)] = new(HostingState.Idle, HostingAction.Finish),
    };

    /// <summary>
    /// The transition for the event in the state.
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="hostingEvent">The event.</param>
    /// <returns>The next state and the action to perform.</returns>
    public static HostingTransition Next(HostingState state, HostingEvent hostingEvent)
    {
        return Table.TryGetValue((state, hostingEvent), out HostingTransition transition)
            ? transition
            : new HostingTransition(state, HostingAction.None);
    }
}
