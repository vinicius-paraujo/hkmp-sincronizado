using System;
using Hkmp.Networking.Client.Connection;
using Hkmp.Game.Server;
using Hkmp.Networking.Server.Lifecycle;
using Hkmp.Util;

namespace Hkmp.Game.Client.Hosting;

/// <summary>
/// The server of this game, as hosting drives it. The server publishes its state on its own core, and this hands
/// each change to the game's main thread, where hosting runs.
/// </summary>
internal sealed class HostedServer : IHostedServer
{
    private readonly ModServerManager _server;

    /// <summary>
    /// Wraps the server of this game.
    /// </summary>
    /// <param name="server">The server manager.</param>
    public HostedServer(ModServerManager server)
    {
        _server = server;
        _server.LifecycleChanged += (state, failure) =>
            MainThreadMailbox.Shared.Post(() => StateChanged?.Invoke(state, failure));
    }

    /// <inheritdoc />
    public event Action<ServerState, ServerFailure> StateChanged;

    /// <inheritdoc />
    public ServerState State => _server.LifecycleState;

    /// <inheritdoc />
    public void Start(int port, bool newGame)
    {
        _server.StartHost(port, newGame);
    }

    /// <inheritdoc />
    public void Stop()
    {
        _server.Stop();
    }
}
