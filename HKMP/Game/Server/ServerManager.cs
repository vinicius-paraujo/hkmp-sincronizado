using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Animation;
using Hkmp.Api.Command.Server;
using Hkmp.Api.Eventing.ServerEvents;
using Hkmp.Api.Server;
using Hkmp.Eventing;
using Hkmp.Eventing.ServerEvents;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Game.Client.Save;
using Hkmp.Game.Command.Server;
using Hkmp.Game.Server.Core;
using Hkmp.Game.Server.Auth;
using Hkmp.Game.Server.Rooms;
using Hkmp.Game.Server.Save;
using Hkmp.Game.Server.Sessions;
using Hkmp.Game.Settings;
using Hkmp.Logging;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using Hkmp.Networking.Server.Lifecycle;
using Hkmp.Util;

namespace Hkmp.Game.Server;

/// <summary>
/// Class that manages the server state (similar to ClientManager). For example the current scene of
/// each player, to prevent sending redundant traffic.
/// </summary>
internal abstract class ServerManager : IServerManager {
    #region Internal server manager variables and properties

    /// <summary>
    /// The maximum length of a username, for validation purposes in multiple places.
    /// </summary>
    public const int UsernameMaxLength = 20;
    
    /// <summary>
    /// The name of the authorized file.
    /// </summary>
    private const string AuthorizedFileName = "authorized.json";

    /// <summary>
    /// The maximum length of a username, for validation purposes in multiple places.
    /// </summary>
    public const int MaxUsernameLength = 20;

    /// <summary>
    /// The net server instance.
    /// </summary>
    private readonly NetServer _netServer;

    /// <summary>
    /// The packet manager instance for register and deregistering packet handlers.
    /// </summary>
    private readonly PacketManager _packetManager;

    /// <summary>
    /// Dictionary mapping player IDs to their server player data instances.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, ServerPlayerData> _playerData;

    /// <summary>
    /// IDs of the players that stopped sending updates and have not resumed yet.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, byte> _silentPlayers = new ConcurrentDictionary<ushort, byte>();

    private readonly ConcurrentDictionary<ServerEntityKey, ServerEntityData> _entityData;
    
    /// <summary>
    /// The white-list for managing player logins.
    /// </summary>
    private readonly WhiteList _whiteList;

    /// <summary>
    /// Authorized list for managing player permission.
    /// </summary>
    private readonly AuthKeyList _authorizedList;

    /// <summary>
    /// The list of banned users.
    /// </summary>
    private readonly BanList _banList;

    /// <summary>
    /// Decides who enters and who takes a session back.
    /// </summary>
    private readonly AdmissionPolicy _admission;

    /// <summary>
    /// The server settings.
    /// </summary>
    public readonly ServerSettings InternalServerSettings;

    /// <summary>
    /// The server command manager instance.
    /// </summary>
    protected readonly ServerCommandManager CommandManager;

    /// <summary>
    /// The addon manager instance.
    /// </summary>
    protected readonly ServerAddonManager AddonManager;

    /// <summary>
    /// Whether full synchronisation is enabled for the server.
    /// </summary>
    protected bool FullSynchronisation;

    /// <summary>
    /// The save data for the server. The instance will be created in the constructor and is passed around to other
    /// objects. Therefore, it should not change instances.
    /// </summary>
    protected ServerSaveData ServerSaveData;

    #endregion
    
    #region Internal server manager commands

    /// <summary>
    /// The list command.
    /// </summary>
    private readonly IServerCommand _listCommand;
    /// <summary>
    /// The whitelist command.
    /// </summary>
    private readonly IServerCommand _whiteListCommand;
    /// <summary>
    /// The authorize command.
    /// </summary>
    private readonly IServerCommand _authorizeCommand;
    /// <summary>
    /// The announce command.
    /// </summary>
    private readonly IServerCommand _announceCommand;
    /// <summary>
    /// The ban command.
    /// </summary>
    private readonly IServerCommand _banCommand;
    /// <summary>
    /// The kick command.
    /// </summary>
    private readonly IServerCommand _kickCommand;
    /// <summary>
    /// The team command.
    /// </summary>
    private readonly IServerCommand _teamCommand;
    /// <summary>
    /// The skin command.
    /// </summary>
    private readonly IServerCommand _skinCommand;
    /// <summary>
    /// The copy save command.
    /// </summary>
    private readonly IServerCommand _copySaveCommand;
    
    #endregion

    #region IServerManager properties

    /// <inheritdoc />
    public IReadOnlyCollection<IServerPlayer> Players {
        get {
            _netServer.Guard.Check(nameof(Players));
            return new List<IServerPlayer>(_playerData.Values);
        }
    }

    /// <summary>
    /// The thread that runs the server. Work that comes from other threads is handed to it.
    /// </summary>
    protected Hkmp.Game.Server.Core.ServerCore Core => _netServer.Core;

    /// <inheritdoc />
    public IServerSettings ServerSettings => InternalServerSettings;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerConnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerDisconnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerEnterSceneEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerLeaveSceneEvent;

    /// <inheritdoc />
    public event Action<IPlayerChatEvent> PlayerChatEvent;

    /// <inheritdoc />
    public event Action<IEntityDeathEvent> EntityDeathEvent;

    #endregion

    /// <summary>
    /// The only owner of each room's scene host, epochs and dead entities.
    /// </summary>
    private readonly RoomHostAssignment _rooms;

    /// <summary>
    /// How often every player is told the round trip time of every player.
    /// </summary>
    private const int PingReportMillis = 2000;

    /// <summary>
    /// The timer of the ping report, on the core.
    /// </summary>
    private readonly CoreTimer _pingReport;

    /// <summary>
    /// The authentication key of whoever runs the server, whose client is preferred as scene host; null for a
    /// server without a local player.
    /// </summary>
    protected string ServerOwnerKey { get; set; }

    /// <summary>
    /// The state of the server's lifecycle.
    /// </summary>
    public ServerState LifecycleState => _netServer.State;

    /// <summary>
    /// Called on the core whenever the server's lifecycle publishes its state, with the reason of a failure.
    /// </summary>
    public event Action<ServerState, ServerFailure> LifecycleChanged {
        add => _netServer.LifecycleChanged += value;
        remove => _netServer.LifecycleChanged -= value;
    }

    /// <summary>
    /// Constructs the server manager.
    /// </summary>
    /// <param name="netServer">The net server instance.</param>
    /// <param name="packetManager">The packet manager instance.</param>
    /// <param name="serverSettings">The server settings.</param>
    protected ServerManager(
        NetServer netServer,
        PacketManager packetManager,
        ServerSettings serverSettings
    ) {
        _netServer = netServer;
        _packetManager = packetManager;
        InternalServerSettings = serverSettings;
        _playerData = new ConcurrentDictionary<ushort, ServerPlayerData>();
        _entityData = new ConcurrentDictionary<ServerEntityKey, ServerEntityData>();
        _rooms = new RoomHostAssignment(new HostCandidatePolicy(
            id => _silentPlayers.ContainsKey(id),
            id => ServerOwnerKey != null && _playerData.TryGetValue(id, out var player) && player.AuthKey == ServerOwnerKey
        ));

        _pingReport = new CoreTimer(_netServer.Core, PingReportMillis, true);
        _pingReport.Elapsed += ReportPings;

        CommandManager = new ServerCommandManager();
        var eventAggregator = new EventAggregator();

        var serverApi = new ServerApi(this, CommandManager, _netServer, eventAggregator, _netServer.Core);
        AddonManager = new ServerAddonManager(serverApi);

        ServerSaveData = new ServerSaveData();

        // Load the lists
        _whiteList = WhiteList.LoadFromFile();
        _authorizedList = AuthKeyList.LoadFromFile(AuthorizedFileName);
        _banList = BanList.LoadFromFile();
        _admission = new AdmissionPolicy(new AdmissionRules(this));

        _listCommand = new ListCommand(this);
        _whiteListCommand = new WhiteListCommand(_whiteList, this);
        _authorizeCommand = new AuthorizeCommand(_authorizedList, this);
        _announceCommand = new AnnounceCommand(_playerData, _netServer);
        _banCommand = new BanCommand(_banList, this);
        _kickCommand = new KickCommand(this);
        _teamCommand = new TeamCommand(this);
        _skinCommand = new SkinCommand(this);
        _copySaveCommand = new CopySaveCommand(this, ServerSaveData);
    }

    #region Internal server manager methods

    /// <summary>
    /// Initializes the server manager.
    /// </summary>
    public virtual void Initialize() {
        // Register a timeout handler
        _netServer.ClientTimeoutEvent += OnClientTimeout;
        _netServer.ClientSilenceEvent += OnClientSilence;
        _netServer.ClientResumeEvent += id => _silentPlayers.TryRemove(id, out _);

        // Register server shutdown handler
        _netServer.ShutdownEvent += OnServerShutdown;

        // Register a handler for when a client wants to connect
        _netServer.ConnectionRequestEvent += OnConnectionRequest;
        _netServer.ResumptionRequestEvent += OnResumptionRequest;
        _netServer.SessionResumedEvent += OnSessionResumed;
    }

    /// <summary>
    /// The facts of this server that the admission policy decides on.
    /// </summary>
    private sealed class AdmissionRules : IAdmissionRules {
        private readonly ServerManager _server;

        public AdmissionRules(ServerManager server) {
            _server = server;
        }

        public bool IsKeyBanned(string identity) => _server._banList.Contains(identity);

        public bool IsAddressBanned(string address) => _server._banList.IsIpBanned(address);

        public bool WhitelistEnabled => _server._whiteList.IsEnabled;

        public bool IsWhitelisted(string identity) => _server._whiteList.Contains(identity);

        public bool IsPreListed(string username) => _server._whiteList.IsPreListed(username);

        public bool IsNameInUse(string username) => _server._playerData.Values.Any(
            player => string.Equals(player.Username, username, StringComparison.OrdinalIgnoreCase)
        );

        public bool HasLiveSession(string identity) => _server._netServer.HasLiveSession(identity);
    }

    /// <summary>
    /// Register the default server commands.
    /// </summary>
    protected virtual void RegisterCommands() {
        CommandManager.RegisterCommand(_listCommand);
        CommandManager.RegisterCommand(_whiteListCommand);
        CommandManager.RegisterCommand(_authorizeCommand);
        CommandManager.RegisterCommand(_announceCommand);
        CommandManager.RegisterCommand(_banCommand);
        CommandManager.RegisterCommand(_kickCommand);
        CommandManager.RegisterCommand(_teamCommand);
        CommandManager.RegisterCommand(_skinCommand);

        if (FullSynchronisation) {
            CommandManager.RegisterCommand(_copySaveCommand);
        }
    }

    /// <summary>
    /// Deregister the default server commands.
    /// </summary>
    protected virtual void DeregisterCommands() {
        CommandManager.DeregisterCommand(_listCommand);
        CommandManager.DeregisterCommand(_whiteListCommand);
        CommandManager.DeregisterCommand(_authorizeCommand);
        CommandManager.DeregisterCommand(_announceCommand);
        CommandManager.DeregisterCommand(_banCommand);
        CommandManager.DeregisterCommand(_kickCommand);
        CommandManager.DeregisterCommand(_teamCommand);
        CommandManager.DeregisterCommand(_skinCommand);

        if (FullSynchronisation) {
            CommandManager.DeregisterCommand(_copySaveCommand);
        }
    }

    /// <summary>
    /// Register the packet handlers for handling incoming packet data.
    /// </summary>
    private void RegisterPacketHandlers() {
        Logger.Debug("Registering packet handlers");
        
        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerEnterScene>(
            ServerUpdatePacketId.PlayerEnterScene,
            OnClientEnterScene
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerLeaveScene>(
            ServerUpdatePacketId.PlayerLeaveScene,
            OnClientLeaveScene
        );
        _packetManager.RegisterServerUpdatePacketHandler<PlayerUpdate>(
            ServerUpdatePacketId.PlayerUpdate,
            OnPlayerUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<PlayerMapUpdate>(
            ServerUpdatePacketId.PlayerMapUpdate,
            OnPlayerMapUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.PlayerDisconnect,
            OnPlayerDisconnect
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.PlayerDeath,
            OnPlayerDeath
        );
        _packetManager.RegisterServerUpdatePacketHandler<ChatMessage>(
            ServerUpdatePacketId.ChatMessage,
            OnChatMessage
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerSettingsUpdate>(
            ServerUpdatePacketId.ServerSettings,
            OnServerSettingsUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerSettingUpdate>(
            ServerUpdatePacketId.PlayerSetting,
            OnPlayerSettingUpdate
        );

        if (FullSynchronisation) {
            _packetManager.RegisterServerUpdatePacketHandler<EntitySpawn>(
                ServerUpdatePacketId.EntitySpawn,
                OnEntitySpawn
            );
            _packetManager.RegisterServerUpdatePacketHandler<EntityUpdate>(
                ServerUpdatePacketId.EntityUpdate,
                OnEntityUpdate
            );
            _packetManager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(
                ServerUpdatePacketId.ReliableEntityUpdate,
                OnReliableEntityUpdate
            );
            _packetManager.RegisterServerUpdatePacketHandler<SaveUpdate>(
                ServerUpdatePacketId.SaveUpdate,
                OnSaveUpdate
            );
        }
    }

    /// <summary>
    /// Deregister the packet handlers for handling incoming packet data.
    /// </summary>
    private void DeregisterPacketHandlers() {
        Logger.Debug("Deregistering packet handlers");
        
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerEnterScene);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerLeaveScene);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerMapUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerDisconnect);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerDeath);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ChatMessage);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ServerSettings);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerSetting);

        if (FullSynchronisation) {
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.EntitySpawn);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.EntityUpdate);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ReliableEntityUpdate);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.SaveUpdate);
        }
    }

    /// <summary>
    /// Starts a server with the given port.
    /// </summary>
    /// <param name="port">The port the server should run on.</param>
    /// <param name="fullSynchronisation">Whether full synchronisation should be enabled.</param>
    public virtual void Start(int port, bool fullSynchronisation) {
        Core.Invoke(() => StartOnCore(port, fullSynchronisation));
    }

    /// <summary>
    /// Starts the server, on the core thread.
    /// </summary>
    /// <param name="port">The port the server should run on.</param>
    /// <param name="fullSynchronisation">Whether full synchronisation should be enabled.</param>
    private void StartOnCore(int port, bool fullSynchronisation) {
        // Stop existing server, whether it runs or failed
        if (_netServer.State != ServerState.Stopped) {
            Logger.Info("Server was running, shutting it down before starting");
            // The full stop also deregisters the commands and handlers, which are registered again below and
            // refuse to be registered twice
            Stop();
        }

        FullSynchronisation = fullSynchronisation;
        
        RegisterCommands();
        RegisterPacketHandlers();

        // Start server again with given port
        _netServer.Start(port);
        _pingReport.Start();
    }

    /// <summary>
    /// Tells every player the round trip time of every player. Runs on the core.
    /// </summary>
    private void ReportPings() {
        var pings = _netServer.PingsOfAllClients().FindAll(ping => _playerData.ContainsKey(ping.Id));
        foreach (var ping in pings) {
            _netServer.GetUpdateManagerForClient(ping.Id)?.SetPlayerPings(pings);
        }
    }

    /// <summary>
    /// Stops the currently running server.
    /// </summary>
    public void Stop() {
        Core.Invoke(StopOnCore);
    }

    /// <summary>
    /// Stops the currently running server, on the core thread.
    /// </summary>
    private void StopOnCore() {
        // A server that failed to open still has its commands and handlers registered
        if (_netServer.State != ServerState.Stopped) {
            // Before shutting down, send TCP packets to all clients indicating
            // that the server is shutting down
            _netServer.SetDataForAllClients(updateManager => {
                updateManager.SetDisconnect(DisconnectReason.Shutdown);
            });

            _pingReport.Stop();
            _netServer.Stop();

            DeregisterCommands();
            DeregisterPacketHandlers();

            // This manager outlives the server it ran, so no room of this session reaches the next one.
            _entityData.Clear();
            _rooms.Clear();
        }
    }

    /// <summary>
    /// Forgets a room once no player is in it: the data of its entities and the authority over it. The game loads
    /// the room again on the next visit, with its entities alive and in their starting places.
    /// </summary>
    /// <param name="sceneName">The scene of the room.</param>
    private void ForgetRoom(string sceneName) {
        foreach (var keyDataPair in _entityData) {
            if (keyDataPair.Key.Scene == sceneName) {
                _entityData.TryRemove(keyDataPair.Key, out _);
            }
        }

        _rooms.Forget(sceneName);
        Logger.Info($"Room {sceneName} forgotten, no player is in it");
    }

    /// <summary>
    /// Authorizes a given authentication key.
    /// </summary>
    /// <param name="authKey">The authentication key to authorize.</param>
    public void AuthorizeKey(string authKey) {
        Core.Invoke(() => _authorizedList.Add(authKey));
    }

    /// <summary>
    /// Called when the server settings are updated, and need to be broadcast.
    /// </summary>
    public void OnUpdateServerSettings() {
        if (!_netServer.IsStarted) {
            return;
        }

        _netServer.SetDataForAllClients(updateManager => { updateManager.UpdateServerSettings(InternalServerSettings); });
    }

    /// <summary>
    /// Callback method for when a player enters a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerEnterScene">The ServerPlayerEnterScene packet data.</param>
    private void OnClientEnterScene(ushort id, ServerPlayerEnterScene playerEnterScene) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EnterScene data from {id}, but player is not in mapping");
            return;
        }

        var newSceneName = playerEnterScene.NewSceneName;

        Logger.Info($"Received EnterScene data from ({id}, {playerData.Username}), new scene: {newSceneName}");

        // Store it in their PlayerData object
        playerData.CurrentScene = newSceneName;
        playerData.Position = playerEnterScene.Position;
        playerData.Scale = playerEnterScene.Scale;
        playerData.AnimationId = playerEnterScene.AnimationClipId;

        OnClientEnterScene(playerData);

        try {
            PlayerEnterSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerEnterScene event:\n{e}");
        }
    }

    /// <summary>
    /// Method that handles a player entering a scene.
    /// </summary>
    /// <param name="playerData">The ServerPlayerData corresponding to the player.</param>
    private void OnClientEnterScene(ServerPlayerData playerData) {
        var enterSceneList = new List<ClientPlayerEnterScene>();
        var alreadyPlayersInScene = false;

        // Edge case where the scene of the player is empty (uninitialized) and we don't want to match with other
        // uninitialized players. Otherwise, it causes issues where other parts of the player data for other players
        // could be null and result in NREs further down the line
        if (string.IsNullOrEmpty(playerData.CurrentScene)) {
            _netServer.GetUpdateManagerForClient(playerData.Id)?.AddPlayerAlreadyInSceneData(
                enterSceneList
            );
        }

        foreach (var idPlayerDataPair in _playerData) {
            // Skip source player
            if (idPlayerDataPair.Key == playerData.Id) {
                continue;
            }

            var otherPlayerData = idPlayerDataPair.Value;

            // Send the packet to all clients on the new scene
            // to indicate that this client has entered their scene
            if (otherPlayerData.CurrentScene.Equals(playerData.CurrentScene)) {
                Logger.Debug($"Sending EnterScene data to {idPlayerDataPair.Key}");

                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddPlayerEnterSceneData(
                    playerData.Id,
                    playerData.Username,
                    playerData.Position,
                    playerData.Scale,
                    playerData.Team,
                    playerData.SkinId,
                    playerData.AnimationId
                );

                Logger.Debug($"Sending that {idPlayerDataPair.Key} is already in scene to {playerData.Id}");

                alreadyPlayersInScene = true;

                // Also send a packet to the client that switched scenes,
                // notifying that these players are already in this new scene.
                enterSceneList.Add(new ClientPlayerEnterScene {
                    Id = idPlayerDataPair.Key,
                    Username = otherPlayerData.Username,
                    Position = otherPlayerData.Position,
                    Scale = otherPlayerData.Scale,
                    Team = otherPlayerData.Team,
                    SkinId = otherPlayerData.SkinId,
                    AnimationClipId = otherPlayerData.AnimationId
                });
            }
        }

        var entitySpawnList = new List<EntitySpawn>();
        var entityUpdateList = new List<EntityUpdate>();
        var reliableEntityUpdateList = new List<ReliableEntityUpdate>();

        if (FullSynchronisation) {
            foreach (var keyDataPair in _entityData) {
                var entityKey = keyDataPair.Key;

                // Check which entities are actually in the scene that the player is entering
                if (!entityKey.Scene.Equals(playerData.CurrentScene)) {
                    continue;
                }

                var entityData = keyDataPair.Value;
                if (entityData.Spawned) {
                    Logger.Info(
                        $"Sending that entity '{entityKey.EntityId}' has spawned in the scene to '{playerData.Id}'");

                    var entitySpawn = new EntitySpawn {
                        Id = entityKey.EntityId,
                        SpawningType = entityData.SpawningType,
                        SpawnedType = entityData.SpawnedType
                    };

                    entitySpawnList.Add(entitySpawn);
                }

                Logger.Debug($"Sending that entity '{entityKey.EntityId}' is already in scene to '{playerData.Id}'");

                var entityUpdate = new EntityUpdate {
                    Id = entityKey.EntityId
                };

                if (entityData.Position != null) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Position);
                    entityUpdate.Position = entityData.Position;
                }

                if (!entityData.Scale.IsEmpty) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Scale);
                    entityUpdate.Scale = entityData.Scale;
                }

                if (entityData.AnimationId.HasValue) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Animation);

                    entityUpdate.AnimationId = entityData.AnimationId.Value;
                    entityUpdate.AnimationWrapMode = entityData.AnimationWrapMode;
                }

                var reliableEntityUpdate = new ReliableEntityUpdate {
                    Id = entityKey.EntityId
                };

                if (entityData.IsActive.HasValue) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.Active);
                    reliableEntityUpdate.IsActive = entityData.IsActive.Value;
                }

                if (entityData.GenericData.Count > 0) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.Data);
                    reliableEntityUpdate.GenericData.AddRange(entityData.GenericData);
                }

                if (entityData.HostFsmData.Count > 0) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.HostFsm);

                    foreach (var pair in entityData.HostFsmData) {
                        // The packet is written later on the send thread while the scene host keeps merging new
                        // values into the stored data; a copy taken now cannot change under the writer.
                        reliableEntityUpdate.HostFsmData[pair.Key] = pair.Value.Snapshot();
                    }
                }

                entityUpdateList.Add(entityUpdate);
                reliableEntityUpdateList.Add(reliableEntityUpdate);
            }

        }

        // A player without a scene yet has no room, and so no epoch nor deaths.
        var entry = new RoomEntry(false, 0, new List<EntityLife>());
        if (!string.IsNullOrEmpty(playerData.CurrentScene)) {
            entry = _rooms.Enter(playerData.CurrentScene, playerData.Id, FullSynchronisation);
            if (entry.IsHost) {
                Logger.Debug($"No one hosts {playerData.CurrentScene}, making {playerData.Id} the scene host");
            }

            SyncHostFlags();
        }

        var epoch = entry.Epoch;

        // The stored state is the room's present, so it carries the current epoch and no copy discards it as old.
        foreach (var update in reliableEntityUpdateList) {
            update.Epoch = epoch;
        }

        _netServer.GetUpdateManagerForClient(playerData.Id)?.AddPlayerAlreadyInSceneData(
            enterSceneList,
            entitySpawnList,
            entityUpdateList,
            reliableEntityUpdateList,
            entry.IsHost,
            epoch,
            entry.Dead,
            playerData.CurrentScene
        );
    }

    /// <summary>
    /// Callback method for when a player leaves a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerLeaveScene">The leave scene data for the player.</param>
    private void OnClientLeaveScene(ushort id, ServerPlayerLeaveScene playerLeaveScene) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received LeaveScene data from {id}, but player is not in mapping");
            return;
        }

        HandlePlayerLeaveScene(id, false, false, playerLeaveScene.SceneName);

        try {
            PlayerLeaveSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerLeaveScene event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player update is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerUpdate">The PlayerUpdate packet data.</param>
    private void OnPlayerUpdate(ushort id, PlayerUpdate playerUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Position)) {
            playerData.Position = playerUpdate.Position;

            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerPosition(id, playerUpdate.Position);
                }
            );
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            playerData.Scale = playerUpdate.Scale;

            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => { _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerScale(id, playerUpdate.Scale); }
            );
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.MapPosition)) {
            playerData.MapPosition = playerUpdate.MapPosition;

            // If the player does not have an active map icon, we do not send the map position update
            if (!playerData.HasMapIcon) {
                return;
            }

            // If the map icons need to be broadcast, we add the data to the next packet
            if (InternalServerSettings.AlwaysShowMapIcons || InternalServerSettings.OnlyBroadcastMapIconWithWaywardCompass) {
                foreach (var idPlayerDataPair in _playerData) {
                    if (idPlayerDataPair.Key == id) {
                        continue;
                    }

                    _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                        .UpdatePlayerMapPosition(id, playerUpdate.MapPosition);
                }
            }
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Animation)) {
            var animationInfos = playerUpdate.AnimationInfos;

            // Check whether there is any animation info to be stored
            if (animationInfos.Count != 0) {
                // Find the last animation clip that is not a custom clip to set as the players animation ID
                // Since that is the last clip that the player updated
                for (var i = animationInfos.Count - 1; i >= 0; i--) {
                    var clipId = animationInfos[i].ClipId;
                    if (clipId < (ushort) AnimationClip.DashEnd) {
                        playerData.AnimationId = clipId;
                        break;
                    }
                }

                // Set the animation data for each player in the same scene
                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        foreach (var animationInfo in animationInfos) {
                            _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerAnimation(
                                id,
                                animationInfo.ClipId,
                                animationInfo.Frame,
                                animationInfo.EffectInfo
                            );
                        }
                    }
                );
            }
        }
    }

    /// <summary>
    /// Callback method for when a player map update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerMapUpdate">The PlayerMapUpdate packet data.</param>
    private void OnPlayerMapUpdate(ushort id, PlayerMapUpdate playerMapUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerMapUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        playerData.HasMapIcon = playerMapUpdate.HasIcon;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                .UpdatePlayerMapIcon(id, playerData.HasMapIcon);

            if (playerData.HasMapIcon && playerData.MapPosition != null) {
                // If the player now has a map icon, we also send the map position
                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                    .UpdatePlayerMapPosition(id, playerData.MapPosition);
            }
        }
    }
    
    /// <summary>
    /// Callback method for when an entity spawn is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entitySpawn">The EntitySpawn packet data.</param>
    private void OnEntitySpawn(ushort id, EntitySpawn entitySpawn) {
        if (!FullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Info($"Received EntitySpawn data, but player with ID {id} is not in mapping");
            return;
        }

        // If the player is not the scene host, ignore this data
        if (!_rooms.IsHost(playerData.CurrentScene, id)) {
            return;
        }
        
        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entitySpawn.Id
        );
        
        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }
        
        Logger.Info($"Received EntitySpawn from {id}, with entity {entitySpawn.Id}, {entitySpawn.SpawningType}, {entitySpawn.SpawnedType}");

        entityData.Spawned = true;
        entityData.SpawningType = entitySpawn.SpawningType;
        entityData.SpawnedType = entitySpawn.SpawnedType;
        
        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => {
                _netServer.GetUpdateManagerForClient(otherId)?.SetEntitySpawn(
                    entitySpawn.Id,
                    entitySpawn.SpawningType,
                    entitySpawn.SpawnedType
                );
            }
        );
    }

    /// <summary>
    /// Callback method for when an entity update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entityUpdate">The EntityUpdate packet data.</param>
    private void OnEntityUpdate(ushort id, EntityUpdate entityUpdate) {
        if (!FullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EntityUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        // Only the scene host runs the room's entities; anything else is from a host that already lost the role.
        if (!_rooms.IsHost(playerData.CurrentScene, id)) {
            return;
        }
        
        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entityUpdate.Id
        );
        
        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Position)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityPosition(
                        entityUpdate.Id,
                        entityUpdate.Position
                    );
                }
            );

            entityData.Position = entityUpdate.Position;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Scale)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityScale(
                        entityUpdate.Id,
                        entityUpdate.Scale
                    );
                }
            );

            entityData.Scale.Merge(entityUpdate.Scale);
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Animation)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityAnimation(
                        entityUpdate.Id,
                        entityUpdate.AnimationId,
                        entityUpdate.AnimationWrapMode
                    );
                }
            );

            entityData.AnimationId = entityUpdate.AnimationId;
            entityData.AnimationWrapMode = entityUpdate.AnimationWrapMode;
        }
    }

    /// <summary>
    /// Callback method for when a reliable entity update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entityUpdate">The ReliableEntityUpdate packet data.</param>
    private void OnReliableEntityUpdate(ushort id, ReliableEntityUpdate entityUpdate) {
        if (!FullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received ReliableEntityUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        var recordedDeaths = entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)
            ? RecordDeaths(id, playerData.CurrentScene, entityUpdate)
            : new List<EntityNetworkData>();

        // Only the scene host of the room's current epoch changes the room. An update written in an earlier epoch,
        // or by a player that does not host the room, describes a state that is no longer the room's, and would undo
        // what the current host did; only the deaths it reports still count, as facts.
        if (!_rooms.IsHost(playerData.CurrentScene, id) || entityUpdate.Epoch != _rooms.EpochOf(playerData.CurrentScene)) {
            if (recordedDeaths.Count == 0) {
                return;
            }

            entityUpdate.UpdateTypes.Clear();
            entityUpdate.UpdateTypes.Add(EntityUpdateType.Data);
            entityUpdate.GenericData.Clear();
            entityUpdate.GenericData.AddRange(recordedDeaths);
        }

        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entityUpdate.Id
        );

        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }
        
        // A dead entity only comes back in a later life. A host that took the room over before it heard of the death
        // reports the entity active in the life in which it died, and that report must not bring it back.
        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active) && entityUpdate.IsActive
            && !_rooms.Revive(playerData.CurrentScene, new EntityLife(entityUpdate.Id, entityUpdate.Life))) {
            Logger.Info($"Entity {entityUpdate.Id} in {playerData.CurrentScene} stays dead, life {entityUpdate.Life} already ended");
            entityUpdate.UpdateTypes.Remove(EntityUpdateType.Active);
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityIsActive(
                        entityUpdate.Id,
                        entityUpdate.IsActive,
                        entityUpdate.Epoch,
                        entityUpdate.Life
                    );
                }
            );

            // Pooled bodies can start another life without receiving a new entity ID.
            if (entityUpdate.IsActive && entityData.IsActive == false) {
                entityData.GenericData.RemoveAll(data => data.Type == EntityComponentType.Death);
            }
            entityData.IsActive = entityUpdate.IsActive;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.AddEntityData(
                        entityUpdate.Id,
                        entityUpdate.GenericData,
                        entityUpdate.Epoch,
                        entityUpdate.Life
                    );
                }
            );

            void ReplaceExistingDataWithSameType(EntityComponentType type, Packet data) {
                var existingData = entityData.GenericData.Find(
                    d => d.Type == type
                );
                if (existingData == null) {
                    entityData.GenericData.Add(new EntityNetworkData {
                        Type = type,
                        Packet = data
                    });
                } else {
                    existingData.Packet = data;
                }
            }

            foreach (var updateData in entityUpdate.GenericData) {
                // Death is terminal state too: a later room arrival must not receive a living replica.
                // FSM actions remain transient, since replaying them can restart attacks or cutscenes.
                if (updateData.Type >= EntityComponentType.Death) {
                    ReplaceExistingDataWithSameType(updateData.Type, updateData.Packet);
                }
            }
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            foreach (var pair in entityUpdate.HostFsmData) {
                var fsmIndex = pair.Key;
                var data = pair.Value;

                if (!entityData.HostFsmData.TryGetValue(fsmIndex, out var existingData)) {
                    existingData = new EntityHostFsmData();
                    entityData.HostFsmData[fsmIndex] = existingData;
                }

                lock (existingData) {
                    existingData.MergeData(data);
                }
                
                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        _netServer.GetUpdateManagerForClient(otherId)?.AddEntityHostFsmData(
                            entityUpdate.Id,
                            fsmIndex,
                            // Each queue merges into what it holds, so sharing one instance lets a merge change
                            // data another queue is writing, and the receiver cannot parse the result.
                            data.Snapshot(),
                            entityUpdate.Epoch,
                            entityUpdate.Life
                        );
                    }
                );
            }
        }
    }

    /// <summary>
    /// Callback method for when a player disconnect is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDisconnect(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDisconnect data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDisconnect data from ({id}, {playerData.Username})");

        ProcessPlayerDisconnect(id);
    }

    /// <summary>
    /// Internal method for disconnecting a player with the given ID for the given reason.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="reason">The reason for the disconnect.</param>
    public void InternalDisconnectPlayer(ushort id, DisconnectReason reason) {
        _netServer.GetUpdateManagerForClient(id)?.SetDisconnect(reason);

        ProcessPlayerDisconnect(id);
    }

    /// <summary>
    /// Handle a player leaving a scene by transition, disconnect or timeout.
    /// </summary>
    /// <param name="id">The ID of the player that left the scene.</param>
    /// <param name="disconnected">Whether the player disconnected from the server.</param>
    /// <param name="timeout">Whether the disconnect was due to connection timeout.</param>
    /// <param name="sceneName">The name of the scene that the player left (if known from a received packet), or null
    /// if no such name is known.</param>
    private void HandlePlayerLeaveScene(ushort id, bool disconnected, bool timeout = false, string sceneName = null) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Handling player leave scene (dc: {disconnected}) for ID {id}, but player is not in mapping");
            return;
        }

        sceneName ??= playerData.CurrentScene;

        if (!disconnected && sceneName.Length == 0) {
            Logger.Warn($"Handling player leave scene for ID {id}, but there was no last scene registered");
            return;
        }
        
        Logger.Info($"Handling player leave scene (dc: {disconnected}) for ID {id}, left scene: {sceneName}");

        // If the current scene of the player is the one being left, we can set it to an empty string
        // It can happen that enter scene data arrives earlier than the leave scene packet, in which case this will
        // not be true, and we don't want to set the current scene (which is now already something else) to empty
        if (playerData.CurrentScene == sceneName) {
            playerData.CurrentScene = "";
        }

        var username = playerData.Username;
        
        // Keep track of whether the scene that the player has left is now empty
        var isSceneNowEmpty = true;

        // A host that leaves hands the room over before anyone hears it left, so the room is never without a host
        var change = _rooms.Transfer(
            sceneName,
            id,
            PlayersIn(sceneName).Where(other => other != id),
            disconnected ? HostChangeReason.Disconnected : HostChangeReason.LeftRoom
        );

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            var otherPlayerData = idPlayerDataPair.Value;
            
            // Send a packet to all clients in the scene that the player has left their scene
            if (otherPlayerData.CurrentScene == sceneName) {
                Logger.Info($"Sending leave scene packet to {idPlayerDataPair.Key}");

                // We have now found at least one player that is still in this scene
                isSceneNowEmpty = false;

                var updateManager = _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key);

                if (change != null && change.NewHost == idPlayerDataPair.Key) {
                    updateManager.SetSceneHostTransfer(sceneName, true, change.Epoch, change.Dead);
                    Logger.Info($"  {idPlayerDataPair.Key} has become scene host");
                }

                if (disconnected) {
                    updateManager.AddPlayerDisconnectData(id, username, timeout);
                } else {
                    updateManager.AddPlayerLeaveSceneData(id, sceneName);
                }
            }
        }

        if (FullSynchronisation && isSceneNowEmpty) {
            ForgetRoom(sceneName);
        }

        SyncHostFlags();

        if (disconnected) {
            // Now remove the client from the player data mapping
            _playerData.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Process a disconnect for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="timeout">Whether this player timed out or disconnected normally.</param>
    private void ProcessPlayerDisconnect(ushort id, bool timeout = false) {
        if (!timeout) {
            // If this isn't a timeout, then we need to propagate this packet to the NetServer
            _netServer.OnClientDisconnect(id);
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            return;
        }

        var username = playerData.Username;
        var sceneName = playerData.CurrentScene;

        // The leave-scene handling below runs after the player is removed from the mapping and so never reaches
        // its scene host transfer; without this, a room whose host disconnects freezes for everyone left in it.
        TransferSceneHost(id, HostChangeReason.Disconnected);
        _silentPlayers.TryRemove(id, out _);

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddPlayerDisconnectData(
                id,
                username,
                timeout
            );
        }

        // Now remove the client from the player data mapping
        _playerData.TryRemove(id, out _);
        
        HandlePlayerLeaveScene(id, true, timeout);

        // That handling no longer finds the player either, so a room it was the last one in is forgotten here;
        // otherwise the next visit receives the entities and deaths of this one.
        if (FullSynchronisation && !string.IsNullOrEmpty(sceneName)
            && !_playerData.Values.Any(other => other.CurrentScene == sceneName)) {
            ForgetRoom(sceneName);
        }

        try {
            PlayerDisconnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerDisconnect event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player dies. 
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDeath(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDeath data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDeath data from ({id}, {playerData.Username})");

        if (ServerSaveData.IsSteelSoul()) {
            // We are running a Steel Soul save file, so we wipe the player-specific data for the player
            ServerSaveData.PlayerSaveData.Remove(playerData.AuthKey);
            
            Logger.Info("  Wiped player save data (Steel Soul)");
        }

        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => { _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerDeathData(id); }
        );
    }

    /// <summary>
    /// Try to update the team for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="team">The team to change the player to.</param>
    /// <param name="reason">The reason if the team could not be updated, otherwise null.</param>
    /// <returns>True if the player's team was updated, false otherwise.</returns>
    public bool TryUpdatePlayerTeam(ushort id, Team team, out string reason) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerTeamUpdate data, but player with ID {id} is not in mapping");

            reason = "Could not find player";
            return false;
        }

        Logger.Info($"Received PlayerTeamUpdate data from ({id}, {playerData.Username}) for team: {team}");

        if (!ServerSettings.TeamsEnabled) {
            Logger.Info("  Teams are not enabled, won't update team");

            reason = "Unable to change team";
            return false;
        }

        // Update the team in the player data
        playerData.Team = team;

        // Broadcast the packet to all players except the player we received the update from
        foreach (var playerId in _playerData.Keys) {
            if (id == playerId) {
                _netServer.GetUpdateManagerForClient(playerId)?.AddPlayerSettingUpdateData(team: team);
                continue;
            }

            _netServer.GetUpdateManagerForClient(playerId)?.AddOtherPlayerSettingUpdateData(
                id,
                team: team
            );
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Try to update the skin for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="skinId">The ID of the skin to change the player to.</param>
    /// <param name="reason">The reason if the skin could not be updated, otherwise null.</param>
    /// <returns>True if the player's team was updated, false otherwise.</returns>
    public bool TryUpdatePlayerSkin(ushort id, byte skinId, out string reason) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerSkinUpdate data, but player with ID {id} is not in mapping");
            
            reason = "Could not find player";
            return false;
        }
        
        Logger.Info($"Received PlayerSkinUpdate data from ({id}, {playerData.Username}) for skin ID: {skinId}");
        
        if (!ServerSettings.AllowSkins) {
            Logger.Info("  Skins are not allowed, won't update skin");
            
            reason = "Unable to change skin";
            return false;
        }

        if (playerData.SkinId == skinId) {
            Logger.Info("  Skins is the same as current, won't update skin");
            
            reason = "Skin is already in use";
            return false;
        }

        // Update the skin ID in the player data
        playerData.SkinId = skinId;
        
        foreach (var idPlayerDataPair in _playerData) {
            var otherId = idPlayerDataPair.Key;
            
            if (otherId == id) {
                _netServer.GetUpdateManagerForClient(id)?.AddPlayerSettingUpdateData(skinId: skinId);
                continue;
            }
            
            var otherPd = idPlayerDataPair.Value;
            
            // Skip sending skin to players not in the same scene
            if (!string.Equals(otherPd.CurrentScene, playerData.CurrentScene)) {
                continue;
            }
            
            _netServer.GetUpdateManagerForClient(otherId)?.AddOtherPlayerSettingUpdateData(id, skinId: skinId);
        }
        
        reason = null;
        return true;
    }

    /// <summary>
    /// Callback method for when the server is shut down.
    /// </summary>
    private void OnServerShutdown() {
        // Every player still connected leaves with the server. Addons forget a player only on this event, so without
        // it they would keep the players of this run into the next one the same process hosts.
        var players = _playerData.Values.ToList();
        _playerData.Clear();

        foreach (var playerData in players) {
            try {
                PlayerDisconnectEvent?.Invoke(playerData);
            } catch (Exception e) {
                Logger.Error($"Exception thrown while invoking PlayerDisconnect event on shutdown:\n{e}");
            }
        }
    }

    /// <summary>
    /// Handle a login request for a client that has invalid addons.
    /// </summary>
    /// <param name="serverInfo">The server info instance to send to the client containing the invalid addons
    /// result.</param>
    private void HandleInvalidLoginAddons(ServerInfo serverInfo) {
        serverInfo.ConnectionResult = ServerConnectionResult.InvalidAddons;
        serverInfo.AddonData.AddRange(AddonManager.GetNetworkedAddonData());
    }

    /// <summary>
    /// Method for handling a login request for a new client.
    /// </summary>
    /// <param name="netServerClient">The net server client that tries to connect.</param>
    /// <param name="clientInfo">The information from the client.</param>
    /// <param name="serverInfo">The server info instance to modify based on whether the client should be accepted
    /// or not.</param>
    private void OnConnectionRequest(NetServerClient netServerClient, ClientInfo clientInfo, ServerInfo serverInfo) {
        Logger.Info($"Received connection request from {netServerClient.EndPoint}, username: {clientInfo.Username}");

        var decision = _admission.DecideEntry(AdmissionRequestOf(netServerClient, clientInfo));
        if (!decision.Accepted) {
            Logger.Info($"  Connection rejected: {decision.Reason}");
            Refuse(serverInfo, decision.Reason);
            return;
        }

        if (!TryOrderAddons(clientInfo, serverInfo, out var addonOrder)) {
            return;
        }

        if (decision.PromoteFromPreList) {
            Logger.Info("  Username was pre-listed, auth key has been added to whitelist");

            _whiteList.Add(clientInfo.AuthKey);
            _whiteList.RemovePreList(clientInfo.Username);
        }

        Logger.Debug("  Accepting client connection, preparing server info");
        FillAcceptance(serverInfo, netServerClient.Id, clientInfo, addonOrder);

        // Send to the other players that this client has just connected
        foreach (var otherId in _playerData.Keys) {
            _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerConnectData(
                netServerClient.Id,
                clientInfo.Username
            );
        }

        // Create new player data and store it
        var playerData = new ServerPlayerData(
            netServerClient.Id,
            netServerClient.EndPoint.Address.ToString(),
            clientInfo.Username,
            clientInfo.AuthKey,
            _authorizedList
        );
        _playerData[netServerClient.Id] = playerData;

        try {
            PlayerConnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerConnect event:\n{e}");
        }
    }

    /// <summary>
    /// Handles a client that presented the token of its live session. Only the version and the bans can keep it
    /// out, since its place in the game is already its own; on acceptance it gets everything a new client gets.
    /// </summary>
    /// <param name="netServerClient">The arriving connection.</param>
    /// <param name="playerId">The ID of the session's player.</param>
    /// <param name="clientInfo">The information from the client.</param>
    /// <param name="serverInfo">The answer to fill in.</param>
    private void OnResumptionRequest(
        NetServerClient netServerClient,
        ushort playerId,
        ClientInfo clientInfo,
        ServerInfo serverInfo
    ) {
        Logger.Info($"Player {playerId} asks to resume its session from {netServerClient.EndPoint}");

        if (!_playerData.ContainsKey(playerId)) {
            Refuse(serverInfo, "The session could not be resumed");
            return;
        }

        var decision = _admission.DecideResumption(AdmissionRequestOf(netServerClient, clientInfo));
        if (!decision.Accepted) {
            Logger.Info($"  Resumption rejected: {decision.Reason}");
            Refuse(serverInfo, decision.Reason);
            return;
        }

        if (!TryOrderAddons(clientInfo, serverInfo, out var addonOrder)) {
            return;
        }

        FillAcceptance(serverInfo, playerId, clientInfo, addonOrder);
        _playerData[playerId].IpAddressString = netServerClient.EndPoint.Address.ToString();
    }

    /// <summary>
    /// Handles a player that took its session back on a new connection. The player keeps its ID and stays in the
    /// game for everyone else, but its client starts the connection anew and enters its room again, so here it
    /// leaves its room: another player takes the room over, and the returning player enters it again as a replica.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="wasSuspended">Whether the session had been suspended.</param>
    private void OnSessionResumed(ushort id, bool wasSuspended) {
        Logger.Info($"Player {id} resumed its session (suspended: {wasSuspended})");
        _silentPlayers.TryRemove(id, out _);

        if (_playerData.TryGetValue(id, out var playerData) && playerData.CurrentScene.Length > 0) {
            HandlePlayerLeaveScene(id, false);
        }
    }

    /// <summary>
    /// The request the admission policy decides on.
    /// </summary>
    private static AdmissionRequest AdmissionRequestOf(NetServerClient netServerClient, ClientInfo clientInfo) {
        return new AdmissionRequest(
            clientInfo.ProtocolVersion,
            clientInfo.AuthKey,
            clientInfo.Username,
            netServerClient.EndPoint.Address.ToString()
        );
    }

    /// <summary>
    /// Fills in a refusal with its reason.
    /// </summary>
    private static void Refuse(ServerInfo serverInfo, string reason) {
        serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
        serverInfo.ConnectionRejectedMessage = reason;
    }

    /// <summary>
    /// Matches the client's networked addons with the server's. On a mismatch the server info is filled in with the
    /// server's addons, so the client can tell the player which ones differ.
    /// </summary>
    /// <param name="clientInfo">The information from the client.</param>
    /// <param name="serverInfo">The answer, filled in on a mismatch.</param>
    /// <param name="addonOrder">The order in which the server assigned IDs to the client's addons.</param>
    /// <returns>True if the addons match.</returns>
    private bool TryOrderAddons(ClientInfo clientInfo, ServerInfo serverInfo, out List<byte> addonOrder) {
        addonOrder = new List<byte>();
        var addonData = clientInfo.AddonData;

        // Construct a string that contains all addons and respective versions by mapping the items in the addon data
        var addonStringList = string.Join(", ", addonData.Select(addon => $"{addon.Identifier} v{addon.Version}"));
        Logger.Info($"  Client tries to connect with following addons: {addonStringList}");

        // If there is a mismatch between the number of networked addons of the client and the server,
        // we can immediately invalidate the request
        if (addonData.Count != AddonManager.GetNetworkedAddonData().Count) {
            Logger.Debug("  Client addons are invalid, rejected connection");

            HandleInvalidLoginAddons(serverInfo);
            return false;
        }

        foreach (var addon in addonData) {
            // Check and retrieve the server addon with the same name and version
            if (!AddonManager.TryGetNetworkedAddon(
                    addon.Identifier,
                    addon.Version,
                    out var correspondingServerAddon
                )) {
                Logger.Debug("  Client addons are invalid, rejected connection");

                // There was no corresponding server addon, so we send a login response with an invalid status
                // and the addon data that is present on the server, so the client knows what is invalid
                HandleInvalidLoginAddons(serverInfo);
                return false;
            }

            if (!correspondingServerAddon.Id.HasValue) {
                continue;
            }

            // If the addon is also present on the server, we append the addon order with the correct index
            addonOrder.Add(correspondingServerAddon.Id.Value);
        }

        return true;
    }

    /// <summary>
    /// Fills in the acceptance of a player: the server's settings, the save and the other players.
    /// </summary>
    /// <param name="serverInfo">The answer to fill in.</param>
    /// <param name="playerId">The ID of the accepted player.</param>
    /// <param name="clientInfo">The information from the client.</param>
    /// <param name="addonOrder">The order of the client's addons.</param>
    private void FillAcceptance(ServerInfo serverInfo, ushort playerId, ClientInfo clientInfo, List<byte> addonOrder) {
        serverInfo.ConnectionResult = ServerConnectionResult.Accepted;
        serverInfo.AddonOrder = addonOrder.ToArray();

        serverInfo.ServerSettingsUpdate = new ServerSettingsUpdate {
            ServerSettings = InternalServerSettings
        };

        serverInfo.FullSynchronisation = FullSynchronisation;

        // Construct the player info to send to the new client in the server info
        var playerInfo = new List<(ushort, string)>();

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == playerId) {
                continue;
            }

            playerInfo.Add((idPlayerDataPair.Key, idPlayerDataPair.Value.Username));
        }

        serverInfo.PlayerInfo = playerInfo;

        // Obtain the save data for the connecting client and add it to the server info
        serverInfo.CurrentSave = ServerSaveData.GetCurrentSaveData(clientInfo.AuthKey);
    }

    /// <summary>
    /// Callback method for when a client stops sending updates. A stalled scene host would freeze its room for
    /// everyone else until the connection times out, so the role moves to another player in the room right away.
    /// The stalled player does not get it back when it resumes.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    private void OnClientSilence(ushort id) {
        if (!_playerData.ContainsKey(id)) {
            return;
        }

        Logger.Info($"Player {id} stopped sending updates");
        _silentPlayers[id] = 0;
        TransferSceneHost(id, HostChangeReason.Silent);
    }

    /// <summary>
    /// Records in the room's authority the deaths an entity update reports, and tells addons about each new one.
    /// </summary>
    /// <param name="id">The ID of the player that sent the update.</param>
    /// <param name="sceneName">The scene of the room.</param>
    /// <param name="entityUpdate">The update, with the epoch in which the sender wrote it.</param>
    /// <returns>The death data the room's authority accepted, new or already recorded.</returns>
    private List<EntityNetworkData> RecordDeaths(ushort id, string sceneName, ReliableEntityUpdate entityUpdate) {
        var accepted = new List<EntityNetworkData>();
foreach (var data in entityUpdate.GenericData) {
            if (data.Type != EntityComponentType.Death) {
                continue;
            }

            var death = new EntityLife(entityUpdate.Id, entityUpdate.Life);
            if (!_rooms.RecordDeath(sceneName, id, entityUpdate.Epoch, death)) {
                // A repeated report of a death already recorded from this host still reaches the replicas.
                if (_rooms.IsDead(sceneName, death) && _rooms.HostedIn(sceneName, id, entityUpdate.Epoch)) {
                    accepted.Add(data);
                }

                continue;
            }

            accepted.Add(data);

            // Read from a copy, so the stored and relayed data keeps its own reading position.
            var deathData = EntityDeathData.ReadData(new Packet(data.Packet.ToArray()));
            Logger.Info($"Death of entity {entityUpdate.Id} ({deathData.EntityType}) in {sceneName} recorded, epoch {entityUpdate.Epoch}, life {entityUpdate.Life}");
            EntityDeathEvent?.Invoke(new EntityDeathEvent(sceneName, entityUpdate.Id, deathData.EntityType.ToString(),
                deathData.EntityName));
        }

        return accepted;
    }

    /// <summary>
    /// Moves the scene host role of the given player to another player in the same room, as the room host
    /// assignment chooses. Nothing changes when the player does not host its room or no one else is in it.
    /// </summary>
    /// <param name="id">The ID of the current scene host.</param>
    /// <param name="reason">Why the role moves; a player that disconnected is not told it lost the role.</param>
    private void TransferSceneHost(ushort id, HostChangeReason reason) {
        if (!FullSynchronisation || !_playerData.TryGetValue(id, out var playerData)) {
            return;
        }

        var sceneName = playerData.CurrentScene;
        var change = _rooms.Transfer(sceneName, id, PlayersIn(sceneName), reason);
        SyncHostFlags();
        if (change == null) {
            return;
        }

        _netServer.GetUpdateManagerForClient(change.NewHost)?.SetSceneHostTransfer(sceneName, true, change.Epoch, change.Dead);
        // A stalled host still receives; without this it would keep acting as host once it resumes.
        if (reason != HostChangeReason.Disconnected) {
            _netServer.GetUpdateManagerForClient(id)?.SetSceneHostTransfer(sceneName, false, change.Epoch, change.Dead);
        }

        Logger.Info($"Scene host of {sceneName} moved from {id} to {change.NewHost}, epoch {change.Epoch}");
    }

    /// <summary>
    /// The players whose current scene is the given one.
    /// </summary>
    /// <param name="sceneName">The scene.</param>
    /// <returns>Their IDs.</returns>
    private IEnumerable<ushort> PlayersIn(string sceneName) {
        return _playerData.Where(pair => pair.Value.CurrentScene == sceneName).Select(pair => pair.Key).ToList();
    }

    /// <summary>
    /// Mirrors the room host assignment on each player's data, which only reports it and never decides it.
    /// </summary>
    private void SyncHostFlags() {
        foreach (var player in _playerData.Values) {
            player.IsSceneHost = !string.IsNullOrEmpty(player.CurrentScene) && _rooms.IsHost(player.CurrentScene, player.Id);
        }
    }

    /// <summary>
    /// Callback method for when a client times out.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    private void OnClientTimeout(ushort id) {
        if (!_playerData.TryGetValue(id, out _)) {
            Logger.Debug($"Received timeout from unknown player with ID: {id}");
            return;
        }

        // Since the client has timed out, we can formally disconnect them
        ProcessPlayerDisconnect(id, true);
    }

    /// <summary>
    /// Execute a given action by passing the ID of each player that is in the same scene as the given
    /// scene name except for the source ID.
    /// </summary>
    /// <param name="sourceId">The ID of the source player.</param>
    /// <param name="sceneName">The name of the scene to send to.</param>
    /// <param name="dataAction">The action to execute with each ID.</param>
    private void SendDataInSameScene(ushort sourceId, string sceneName, Action<ushort> dataAction) {
        foreach (var idPlayerDataPair in _playerData) {
            // Skip sending to same ID
            if (idPlayerDataPair.Key == sourceId) {
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            // Skip sending to players not in the same scene
            if (!string.Equals(otherPd.CurrentScene, sceneName)) {
                continue;
            }

            dataAction(idPlayerDataPair.Key);
        }
    }

    /// <summary>
    /// Try and process a given message by a given command sender as a command.
    /// </summary>
    /// <param name="commandSender">The command sender that sent the message.</param>
    /// <param name="message">The message that was sent.</param>
    /// <returns>true if the message was processed as a command, false otherwise.</returns>
    public bool TryProcessCommand(ICommandSender commandSender, string message) {
        return CommandManager.ProcessCommand(commandSender, message);
    }

    /// <summary>
    /// Callback method for when a chat message is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="chatMessage">The ChatMessage packet data.</param>
    private void OnChatMessage(ushort id, ChatMessage chatMessage) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process chat message from unknown player ID: {id}");
            return;
        }

        Logger.Info($"Chat from ({id}, {playerData.Username}): \"{chatMessage.Message}\"");

        if (TryProcessCommand(
                new PlayerCommandSender(
                    _authorizedList.Contains(playerData.AuthKey),
                    id,
                    _netServer.GetUpdateManagerForClient(id)
                ),
                chatMessage.Message
            )) {
            Logger.Debug("Chat message was processed as command");
            return;
        }

        var playerChatEvent = new PlayerChatEvent(playerData, chatMessage.Message);
        
        try {
            PlayerChatEvent?.Invoke(playerChatEvent);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerChat event:\n{e}");
        }

        // If the event has been cancelled, we don't proceed with sending the chat message to other players
        if (playerChatEvent.Cancelled) {
            return;
        }

        var messages = playerChatEvent.Message.Split('\n');
        foreach (var message in messages) {
            var formattedMsg = $"[{playerData.Username}]: {message}";

            foreach (var idPlayerDataPair in _playerData) {
                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddChatMessage(formattedMsg);
            }
        }
    }

    /// <summary>
    /// Callback method for when a server settings update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="serverSettingsUpdate">The <see cref="ServerSettingsUpdate"/> packet data.</param>
    private void OnServerSettingsUpdate(ushort id, ServerSettingsUpdate serverSettingsUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process server settings update from unknown player ID: {id}");
            return;
        }
        
        Logger.Info($"Received server settings update from ({id}, {playerData.Username})");

        if (!playerData.IsAuthorized) {
            Logger.Info("  Player is not authorized");
            
            SendMessage(id, "You are not authorized to change server settings");
            _netServer.GetUpdateManagerForClient(id).UpdateServerSettings(InternalServerSettings);
            
            return;
        }
        
        InternalServerSettings.SetAllProperties(serverSettingsUpdate.ServerSettings);
        OnUpdateServerSettings();
    }

    /// <summary>
    /// Callback method for when a player setting update is received from a player. This can include changes to their
    /// team, skin, etc.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerSettingUpdate">The <see cref="ServerPlayerSettingUpdate"/> packet data.</param>
    private void OnPlayerSettingUpdate(ushort id, ServerPlayerSettingUpdate playerSettingUpdate) {
        if (playerSettingUpdate.UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            if (TryUpdatePlayerTeam(id, playerSettingUpdate.Team, out var reason)) {
                SendMessage(id, $"Team changed to '{playerSettingUpdate.Team}'");
            } else {
                SendMessage(id, reason);
            }
        }

        if (playerSettingUpdate.UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            if (TryUpdatePlayerSkin(id, playerSettingUpdate.SkinId, out var reason)) {
                SendMessage(id, $"Skin ID changed to '{playerSettingUpdate.SkinId}'");
            } else {
                SendMessage(id, reason);
            }
        }
    }

    /// <summary>
    /// Callback method for when a save update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="packet">The SaveUpdate packet data.</param>
    protected virtual void OnSaveUpdate(ushort id, SaveUpdate packet) {
        if (!FullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process save update from unknown player ID: {id}");
            return;
        }

        Logger.Debug($"Save update from ({id}, {playerData.Username}), index: {packet.SaveDataIndex}");

        // Find the properties for syncing this save update, based on whether it is a geo rock, player data or 
        // persistent bool/int item
        SaveDataMapping.VarProperties varProps;
        string pdVarName = null;
        if (SaveDataMapping.Instance.GeoRockIndices.TryGetValue(packet.SaveDataIndex, out var persistentItemData)) {
            Logger.Debug($"  Found GeoRockData: {persistentItemData.Id}, {persistentItemData.SceneName}");
            
            if (!SaveDataMapping.Instance.GeoRockBools.TryGetValue(persistentItemData, out _)) {
                return;
            }

            varProps = new SaveDataMapping.VarProperties {
                Sync = true,
                SyncType = SaveDataMapping.SyncType.Server,
                IgnoreSceneHost = false
            };
        } else if (SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(packet.SaveDataIndex, out pdVarName)) {
            Logger.Debug($"  Found PlayerData: {pdVarName}");
            
            if (!SaveDataMapping.Instance.PlayerDataVarProperties.TryGetValue(pdVarName, out varProps)) {
                return;
            }
        } else if (SaveDataMapping.Instance.PersistentBoolIndices.TryGetValue(
            packet.SaveDataIndex, 
            out persistentItemData)
        ) {
            Logger.Debug($"  Found PersistentBoolData: {persistentItemData.Id}, {persistentItemData.SceneName}");
            
            if (!SaveDataMapping.Instance.PersistentBoolVarProperties.TryGetValue(persistentItemData, out varProps)) {
                return;
            }
        } else if (SaveDataMapping.Instance.PersistentIntIndices.TryGetValue(
            packet.SaveDataIndex, 
            out persistentItemData)
        ) {
            Logger.Debug($"  Found PersistentIntData: {persistentItemData.Id}, {persistentItemData.SceneName}");
            
            if (!SaveDataMapping.Instance.PersistentIntVarProperties.TryGetValue(persistentItemData, out varProps)) {
                return;
            }
        } else {
            Logger.Debug("  Could not find sync props for save update");
            return;
        }

        // Check whether this save update requires the player to be scene host and do the check for it. Persistent
        // bools set to true come from the player's own action and are accepted from anyone, as the client sends them.
        var isPersistentBoolSet = SaveDataMapping.Instance.PersistentBoolIndices.TryGetValue(packet.SaveDataIndex, out _)
                                  && packet.Value is { Length: > 0 } && packet.Value[0] != 0;
        var isGeoRock = SaveDataMapping.Instance.GeoRockIndices.TryGetValue(packet.SaveDataIndex, out _);
        if (isGeoRock && ServerSaveData.GlobalSaveData.TryGetValue(packet.SaveDataIndex, out var storedHits)
                      && packet.Value is { Length: > 0 } && storedHits.Length > 0 && packet.Value[0] >= storedHits[0]) {
            Logger.Debug("  Geo rock already has as few hits left, not broadcasting");
            return;
        }

        if (!varProps.IgnoreSceneHost && !_rooms.IsHost(playerData.CurrentScene, id) && !isPersistentBoolSet && !isGeoRock) {
            Logger.Debug("  Player is not scene host, but should be for update, not broadcasting");
            return;
        }

        if (varProps.SyncType == SaveDataMapping.SyncType.Player) {
            Logger.Debug("  SyncType is Player");
            
            if (!ServerSaveData.PlayerSaveData.TryGetValue(playerData.AuthKey, out var playerSaveData)) {
                Logger.Debug("  No PlayerSaveData for player yet, creating one");
                playerSaveData = new Dictionary<ushort, byte[]>();
                ServerSaveData.PlayerSaveData[playerData.AuthKey] = playerSaveData;
            }
            
            Logger.Debug("  Storing player data");

            playerSaveData[packet.SaveDataIndex] = packet.Value;
        } else if (varProps.SyncType == SaveDataMapping.SyncType.Server) {
            if (varProps.Additive) {
                if (pdVarName == null) {
                    Logger.Debug("  Cannot decode value, name for variable is null");
                    return;
                }

                object decodedCurrentValue = null;
                var decodedDeltaValue = EncodeUtil.DecodeSaveDataValue(pdVarName, packet.Value);

                if (!ServerSaveData.GlobalSaveData.TryGetValue(packet.SaveDataIndex, out var currentValue)) {
                    Logger.Debug($"No current value is stored in the global save data for: {pdVarName}");

                    if (varProps.InitialValue != null) {
                        Logger.Debug($"  Taking initial value: {varProps.InitialValue}");
                        decodedCurrentValue = varProps.InitialValue;
                    } else {
                        Logger.Debug("  No initial value defined, using delta as absolute");
                        packet.Value = EncodeUtil.EncodeSaveDataValue(decodedDeltaValue);
                    }
                } else {
                    decodedCurrentValue = EncodeUtil.DecodeSaveDataValue(pdVarName, currentValue);
                }

                if (decodedCurrentValue != null) {
                    object decodedNewValue;

                    if (decodedCurrentValue is int decodedCurrentInt && decodedDeltaValue is int decodedDeltaInt) {
                        decodedNewValue = decodedCurrentInt + decodedDeltaInt;
                    } else if (decodedCurrentValue is List<string> decodedCurrentStringList &&
                               decodedDeltaValue is List<string> decodedDeltaStringList) {

                        // Loop over the delta list and add only non-duplicates
                        foreach (var str in decodedDeltaStringList) {
                            if (!decodedCurrentStringList.Contains(str)) {
                                decodedCurrentStringList.Add(str);
                            }
                        }

                        decodedNewValue = decodedCurrentStringList;
                    } else {
                        Logger.Debug($"  Type of decoded values did not match: {decodedCurrentValue.GetType()}");
                        return;
                    }

                    packet.Value = EncodeUtil.EncodeSaveDataValue(decodedNewValue);
                }
            }
            
            Logger.Debug("  SyncType is Server, broadcasting save update");
            
            ServerSaveData.GlobalSaveData[packet.SaveDataIndex] = packet.Value;
            
            foreach (var idPlayerDataPair in _playerData) {
                var otherId = idPlayerDataPair.Key;
                // For additive properties, it might happen (due to race conditions) that the resulting value needs to
                // be sent to the sender of this packet as well
                if (id == otherId && !varProps.Additive) {
                    continue;
                }

                _netServer.GetUpdateManagerForClient(otherId).SetSaveUpdate(packet.SaveDataIndex, packet.Value);
            }
        }
    }
    
    #endregion

    #region IServerManager methods

    /// <inheritdoc />
    public IServerPlayer GetPlayer(ushort id) {
        _netServer.Guard.Check(nameof(GetPlayer));
        return TryGetPlayer(id, out var player) ? player : null;
    }

    /// <inheritdoc />
    public bool TryGetPlayer(ushort id, out IServerPlayer player) {
        _netServer.Guard.Check(nameof(TryGetPlayer));
        var found = _playerData.TryGetValue(id, out var playerData);
        player = playerData;

        return found;
    }

    /// <summary>
    /// Check whether a given string message is valid for sending over the network.
    /// </summary>
    /// <param name="message">The string message to check.</param>
    /// <exception cref="ArgumentException">Thrown if the message is null, exceeds the max length or contains
    /// invalid characters.</exception>
    private void CheckValidMessage(string message) {
        if (message == null) {
            throw new ArgumentException("Message cannot be null");
        }

        if (message.Length > ChatMessage.MaxMessageLength) {
            throw new ArgumentException($"Message length exceeds max length of {ChatMessage.MaxMessageLength}");
        }
    }

    /// <inheritdoc />
    public void SendMessage(ushort id, string message) {
        _netServer.Guard.Check(nameof(SendMessage));
        CheckValidMessage(message);

        var updateManager = _netServer.GetUpdateManagerForClient(id);
        
        // Break message up in parts denoted by newline
        var messages = message.Split('\n');
        foreach (var line in messages) {
            updateManager?.AddChatMessage(line);
        }
    }

    /// <inheritdoc />
    public void SendMessage(IServerPlayer player, string message) {
        _netServer.Guard.Check(nameof(SendMessage));
        if (player == null) {
            throw new ArgumentException("Player cannot be null");
        }

        SendMessage(player.Id, message);
    }

    /// <inheritdoc />
    public void BroadcastMessage(string message) {
        _netServer.Guard.Check(nameof(BroadcastMessage));
        CheckValidMessage(message);

        foreach (var player in _playerData.Values) {
            SendMessage(player.Id, message);
        }
    }

    /// <inheritdoc />
    public void ResetSemiPersistentItems() {
        _netServer.Guard.Check(nameof(ResetSemiPersistentItems));
        var mapping = SaveDataMapping.Instance;
        var removed = 0;
        foreach (var pair in mapping.PersistentBoolVarProperties) {
            if (pair.Value.SemiPersistent && mapping.PersistentBoolIndices.TryGetValue(pair.Key, out var index)
                && ServerSaveData.GlobalSaveData.Remove(index)) {
                removed++;
            }
        }

        foreach (var pair in mapping.PersistentIntVarProperties) {
            if (pair.Value.SemiPersistent && mapping.PersistentIntIndices.TryGetValue(pair.Key, out var index)
                && ServerSaveData.GlobalSaveData.Remove(index)) {
                removed++;
            }
        }

        Logger.Info($"Reset {removed} stored semi-persistent items");
    }

    /// <inheritdoc />
    public void DisconnectPlayer(ushort id, DisconnectReason reason) {
        _netServer.Guard.Check(nameof(DisconnectPlayer));
        if (!_playerData.TryGetValue(id, out _)) {
            throw new ArgumentException("There is no player connected with the given ID");
        }

        InternalDisconnectPlayer(id, reason);
    }

    /// <inheritdoc />
    public void ApplyServerSettings(ServerSettings serverSettings) {
        _netServer.Guard.Check(nameof(ApplyServerSettings));
        if (serverSettings == null) {
            throw new ArgumentException("Cannot apply null ServerSettings", nameof(serverSettings));
        }
    
        // If these ServerSettings instances are equal in value, we can immediately return
        if (InternalServerSettings.Equals(serverSettings)) {
            return;
        }
        
        // Set all properties of the given instance and then call the OnUpdate method to network the changes
        InternalServerSettings.SetAllProperties(serverSettings);
        OnUpdateServerSettings();
    }

    #endregion
}
