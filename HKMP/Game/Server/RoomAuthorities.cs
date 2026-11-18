using System.Collections.Concurrent;

namespace Hkmp.Game.Server;

/// <summary>
/// The server's authority over every room that has players, by scene name.
/// </summary>
internal sealed class RoomAuthorities {
    private readonly ConcurrentDictionary<string, RoomAuthority> _rooms =
        new ConcurrentDictionary<string, RoomAuthority>();

    /// <summary>The authority over the given room, created when the room is first used.</summary>
    /// <param name="scene">The name of the scene of the room.</param>
    public RoomAuthority For(string scene) => _rooms.GetOrAdd(scene, _ => new RoomAuthority());

    /// <summary>
    /// Forgets a room once its last player left. The game loads the room again on the next visit, with every entity
    /// alive and its epochs starting over, so nothing recorded about the previous visit applies.
    /// </summary>
    /// <param name="scene">The name of the scene of the room.</param>
    public void Forget(string scene) => _rooms.TryRemove(scene, out _);

    /// <summary>Forgets every room, when the server stops.</summary>
    public void Clear() => _rooms.Clear();
}
