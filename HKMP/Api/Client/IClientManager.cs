using System;
using System.Collections.Generic;
using Hkmp.Game;

namespace Hkmp.Api.Client;

/// <summary>
/// Client manager that handles the local client and related data.
/// </summary>
public interface IClientManager {
    /// <summary>
    /// Class that manages player locations on the in-game map.
    /// </summary>
    IMapManager MapManager { get; }

    /// <summary>
    /// Class that manages pause-related operations.
    /// </summary>
    IPauseManager PauseManager { get; }

    /// <summary>
    /// The current username of the local player.
    /// </summary>
    string Username { get; }

    /// <summary>
    /// The current team of the local player.
    /// </summary>
    Team Team { get; }

    /// <summary>
    /// A read-only collection of all connected players.
    /// </summary>
    IReadOnlyCollection<IClientPlayer> Players { get; }

    /// <summary>
    /// Disconnect the local client from the server.
    /// </summary>
    void Disconnect();

    /// <summary>
    /// Whether a connection timeout keeps the player in the game instead of returning to the main menu, so the
    /// caller can reconnect with <see cref="Reconnect"/>.
    /// </summary>
    bool StayInGameOnTimeout { get; set; }

    /// <summary>
    /// Whether the local player hosts the server it is connected to.
    /// </summary>
    bool IsHostingServer { get; }

    /// <summary>
    /// Whether the local player simulates the entities of its current scene for the other players in it.
    /// </summary>
    bool IsSceneHost { get; }

    /// <summary>
    /// Connect again to the last server with the same username while staying in the game. The current save is
    /// replaced by the server's when the connection is accepted.
    /// </summary>
    void Reconnect();

    /// <summary>
    /// Get a specific player by their ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <returns>The player with the given ID, or null if no such player exists.</returns>
    IClientPlayer GetPlayer(ushort id);

    /// <summary>
    /// Try to get a specific player by their ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="player">The parameter that will contain the player if it exists.</param>
    /// <returns>True if the player was found, false otherwise.</returns>
    bool TryGetPlayer(ushort id, out IClientPlayer player);

    /// <summary>
    /// Changes the team of the local player.
    /// </summary>
    /// <param name="team">The team value.</param>
    [Obsolete("ChangeTeam is deprecated. Team changes are handled by the IServerManager.")]
    void ChangeTeam(Team team);

    /// <summary>
    /// Changes the skin of the local player.
    /// </summary>
    /// <param name="skinId">The ID of the skin.</param>
    [Obsolete("ChangeSkin is deprecated. Skin changes are handled by the IServerManager.")]
    void ChangeSkin(byte skinId);

    /// <summary>
    /// Event that is called when the local user connects to a server.
    /// </summary>
    event Action ConnectEvent;

    /// <summary>
    /// Event that is called when the local user disconnects from the server.
    /// </summary>
    event Action DisconnectEvent;

    /// <summary>
    /// Event that is called when no update has arrived from the server for a few seconds while connected.
    /// </summary>
    event Action ConnectionStalledEvent;

    /// <summary>
    /// Event that is called when updates arrive again after <see cref="ConnectionStalledEvent"/>.
    /// </summary>
    event Action ConnectionResumedEvent;

    /// <summary>
    /// Event that is called when another player connects to the server.
    /// </summary>
    event Action<IClientPlayer> PlayerConnectEvent;

    /// <summary>
    /// Event that is called when another player disconnects from the server.
    /// IClientPlayer could possibly be null.
    /// </summary>
    event Action<IClientPlayer> PlayerDisconnectEvent;

    /// <summary>
    /// Event that is called when another player enters the local scene.
    /// </summary>
    event Action<IClientPlayer> PlayerEnterSceneEvent;

    /// <summary>
    /// Event that is called when another player leaves the local scene.
    /// </summary>
    event Action<IClientPlayer> PlayerLeaveSceneEvent;
}
