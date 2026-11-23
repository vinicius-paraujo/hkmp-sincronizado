using System;
using Hkmp.Logging;
using Hkmp.Networking.Server.Lifecycle;

namespace Hkmp.Game.Client.Hosting;

/// <summary>
/// Why hosting ended before or while the player played.
/// </summary>
internal enum HostingFailure
{
    /// <summary>Hosting did not fail.</summary>
    None,

    /// <summary>Another program holds the port.</summary>
    PortInUse,

    /// <summary>The port could not be opened for another reason.</summary>
    PortUnavailable,

    /// <summary>The server failed while it ran.</summary>
    ServerFailed,

    /// <summary>The local client could not connect to the server, or lost its connection.</summary>
    LocalConnectionFailed,

    /// <summary>The player left; nothing failed.</summary>
    Left,
}

/// <summary>
/// What the player chose when asking to host.
/// </summary>
/// <param name="Port">The port the server opens.</param>
/// <param name="Username">The name of the local player.</param>
/// <param name="NewGame">Whether the chosen save is a new game.</param>
/// <param name="LoadSave">Loads the chosen save; hosting calls it only once the local client is connected.</param>
internal sealed record HostingRequest(int Port, string Username, bool NewGame, Action LoadSave);

/// <summary>
/// The server as hosting drives it. Its state changes reach hosting on the game's main thread.
/// </summary>
internal interface IHostedServer
{
    /// <summary>The state of the server's lifecycle.</summary>
    ServerState State { get; }

    /// <summary>Called on the main thread whenever the server publishes its state.</summary>
    event Action<ServerState, ServerFailure> StateChanged;

    /// <summary>Starts the server with the chosen save.</summary>
    /// <param name="port">The port.</param>
    /// <param name="newGame">Whether the chosen save is a new game.</param>
    void Start(int port, bool newGame);

    /// <summary>Stops the server; it is stopped when this returns.</summary>
    void Stop();
}

/// <summary>
/// The client of the hosting player, as hosting drives it.
/// </summary>
internal interface ILocalClient
{
    /// <summary>Called on the main thread when the local client is connected.</summary>
    event Action Connected;

    /// <summary>Called on the main thread when the local client could not connect or lost its connection.</summary>
    event Action Failed;

    /// <summary>Connects the local client to the server on this machine.</summary>
    /// <param name="port">The port of the server.</param>
    /// <param name="username">The name of the local player.</param>
    void Connect(int port, string username);

    /// <summary>Disconnects the local client.</summary>
    void Disconnect();
}

/// <summary>
/// The game and its menus, as hosting drives them.
/// </summary>
internal interface IHostScreens
{
    /// <summary>Called on the main thread when the chosen save finished loading.</summary>
    event Action GameLoaded;

    /// <summary>
    /// Brings the player back to the multiplayer menu, leaving the game first if it had loaded, and shows why
    /// hosting ended.
    /// </summary>
    /// <param name="reason">Why hosting ended; <see cref="HostingFailure.Left"/> shows nothing.</param>
    /// <param name="gameLoaded">Whether the game had loaded.</param>
    void Finish(HostingFailure reason, bool gameLoaded);
}

/// <summary>
/// Conducts hosting: starts the server, waits until it is open, connects the local client and only then loads the
/// save. On any failure it undoes what it started and shows the reason. It runs on the game's main thread.
/// </summary>
internal sealed class HostingFlow
{
    private readonly IHostedServer _server;
    private readonly ILocalClient _client;
    private readonly IHostScreens _screens;
    private HostingRequest _request;
    private HostingFailure _reason;
    private bool _loaded;

    /// <summary>
    /// Creates idle hosting.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="client">The local client.</param>
    /// <param name="screens">The game and its menus.</param>
    public HostingFlow(IHostedServer server, ILocalClient client, IHostScreens screens)
    {
        _server = server;
        _client = client;
        _screens = screens;

        _server.StateChanged += OnServerState;
        _client.Connected += () => Handle(HostingEvent.ClientConnected);
        _client.Failed += () => Handle(HostingEvent.ClientFailed, HostingFailure.LocalConnectionFailed);
        _screens.GameLoaded += () => Handle(HostingEvent.GameLoaded);
    }

    /// <summary>The current state.</summary>
    public HostingState State { get; private set; } = HostingState.Idle;

    /// <summary>Why the last hosting ended; <see cref="HostingFailure.None"/> until one ended.</summary>
    public HostingFailure LastFailure { get; private set; }

    /// <summary>
    /// Hosts with the chosen save. It does nothing unless hosting is idle.
    /// </summary>
    /// <param name="request">What the player chose.</param>
    public void Host(HostingRequest request)
    {
        if (State != HostingState.Idle)
        {
            Logger.Debug($"Hosting requested while {State}, ignoring");
            return;
        }

        _request = request;
        Handle(HostingEvent.Host);
    }

    /// <summary>
    /// The player leaves the game; what hosting started is undone without showing a reason.
    /// </summary>
    public void Leave()
    {
        Handle(HostingEvent.Leave, HostingFailure.Left);
    }

    private void OnServerState(ServerState state, ServerFailure failure)
    {
        switch (state)
        {
            case ServerState.Open:
                Handle(HostingEvent.ServerOpen);
                break;
            case ServerState.Failed:
                Handle(HostingEvent.ServerFailed, ReasonOf(failure));
                break;
            case ServerState.Stopped:
                Handle(HostingEvent.ServerStopped);
                break;
        }
    }

    // The first reason is kept: what failed first is what the player is told.
    private void Handle(HostingEvent hostingEvent, HostingFailure reason = HostingFailure.None)
    {
        HostingTransition transition = HostingAutomaton.Next(State, hostingEvent);
        if (transition.Action == HostingAction.None)
        {
            return;
        }

        Logger.Debug($"Hosting: {State} --{hostingEvent}--> {transition.Next}");
        State = transition.Next;
        if (_reason == HostingFailure.None)
        {
            _reason = reason;
        }

        Perform(transition.Action);
    }

    private void Perform(HostingAction action)
    {
        switch (action)
        {
            case HostingAction.StartServer:
                _reason = HostingFailure.None;
                _loaded = false;
                _server.Start(_request.Port, _request.NewGame);
                break;
            case HostingAction.ConnectLocal:
                _client.Connect(_request.Port, _request.Username);
                break;
            case HostingAction.LoadSave:
                _request.LoadSave();
                break;
            case HostingAction.MarkLoaded:
                _loaded = true;
                break;
            case HostingAction.StopServer:
                StopServer();
                break;
            case HostingAction.DisconnectAndStopServer:
                _client.Disconnect();
                StopServer();
                break;
            case HostingAction.Finish:
                HostingFailure reason = _reason;
                _request = null;
                _reason = HostingFailure.None;
                LastFailure = reason;
                Logger.Info($"Hosting ended: {reason}");
                _screens.Finish(reason, _loaded);
                break;
        }
    }

    // The stop is complete when it returns, so the flow goes on at once; the report of the stop that reaches the
    // main thread later finds hosting idle and changes nothing.
    private void StopServer()
    {
        _server.Stop();
        if (_server.State == ServerState.Stopped)
        {
            Handle(HostingEvent.ServerStopped);
        }
    }

    private static HostingFailure ReasonOf(ServerFailure failure)
    {
        return failure switch
        {
            ServerFailure.PortInUse => HostingFailure.PortInUse,
            ServerFailure.PortUnavailable => HostingFailure.PortUnavailable,
            _ => HostingFailure.ServerFailed,
        };
    }
}
