namespace Hkmp.Api.Eventing.ServerEvents;

/// <summary>
/// Event for when the server records the death of an entity in a room. It is raised once per death, by the server's
/// authority over the room, whichever player hosted the room at the time.
/// </summary>
public interface IEntityDeathEvent {
    /// <summary>
    /// The name of the scene of the room.
    /// </summary>
    public string Scene { get; }

    /// <summary>
    /// The ID of the entity in the room.
    /// </summary>
    public ushort EntityId { get; }

    /// <summary>
    /// The name of the entity's type, such as "GruzMother".
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// The name of the entity's object in the room, such as "Giant Fly".
    /// </summary>
    public string EntityName { get; }
}
