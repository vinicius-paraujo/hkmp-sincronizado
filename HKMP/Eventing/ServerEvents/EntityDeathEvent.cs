using Hkmp.Api.Eventing.ServerEvents;

namespace Hkmp.Eventing.ServerEvents;

/// <inheritdoc cref="Hkmp.Api.Eventing.ServerEvents.IEntityDeathEvent" />
internal class EntityDeathEvent : IEntityDeathEvent {
    /// <inheritdoc />
    public string Scene { get; }

    /// <inheritdoc />
    public ushort EntityId { get; }

    /// <inheritdoc />
    public string EntityType { get; }

    /// <inheritdoc />
    public string EntityName { get; }

    public EntityDeathEvent(string scene, ushort entityId, string entityType, string entityName) {
        Scene = scene;
        EntityId = entityId;
        EntityType = entityType;
        EntityName = entityName;
    }
}
