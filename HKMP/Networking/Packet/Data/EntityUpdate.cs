using System;
using System.Collections.Generic;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Logging;
using Hkmp.Math;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Base entity update class for reliable and non-reliable entity update data.
/// </summary>
internal abstract class BaseEntityUpdate : IPacketData {
    /// <inheritdoc />
    public abstract bool IsReliable { get; }

    /// <inheritdoc />
    public abstract bool DropReliableDataIfNewerExists { get; }
    
    /// <summary>
    /// The ID of the entity.
    /// </summary>
    public ushort Id { get; set; }

    /// <inheritdoc />
    public abstract void WriteData(IPacket packet);

    /// <inheritdoc />
    public abstract void ReadData(IPacket packet);
}

/// <summary>
/// Packet data for the non-reliable part of an entity update.
/// </summary>
internal class EntityUpdate : BaseEntityUpdate {
    /// <inheritdoc />
    public override bool IsReliable => false;

    /// <inheritdoc />
    public override bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// A set containing the types of updates contained in this packet.
    /// </summary>
    public HashSet<EntityUpdateType> UpdateTypes { get; }

    /// <summary>
    /// The position of the entity.
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// The scale data of the entity.
    /// </summary>
    public ScaleData Scale { get; set; }
        
    /// <summary>
    /// The ID of the animation of the entity.
    /// </summary>
    public byte AnimationId { get; set; }
    /// <summary>
    /// The wrap mode of the animation.
    /// </summary>
    public byte AnimationWrapMode { get; set; }

    /// <summary>
    /// Construct the entity update data.
    /// </summary>
    public EntityUpdate() {
        UpdateTypes = new HashSet<EntityUpdateType>();
        Scale = new ScaleData();
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Id);

        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (UpdateTypes.Contains((EntityUpdateType) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);

        // Conditionally write the state and data fields
        if (UpdateTypes.Contains(EntityUpdateType.Position)) {
            packet.Write(Position);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Scale)) {
            Scale.WriteData(packet);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Animation)) {
            packet.Write(AnimationId);
            packet.Write(AnimationWrapMode);
        }
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Id = packet.ReadUShort();

        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                UpdateTypes.Add((EntityUpdateType) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }

        // Based on the update types, we read the corresponding values
        if (UpdateTypes.Contains(EntityUpdateType.Position)) {
            Position = packet.ReadVector2();
        }
            
        if (UpdateTypes.Contains(EntityUpdateType.Scale)) {
            Scale.ReadData(packet);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Animation)) {
            AnimationId = packet.ReadByte();
            AnimationWrapMode = packet.ReadByte();
        }
    }

    /// <summary>
    /// Data class containing compact information about an entity's scale, which can be more efficiently networked.
    /// </summary>
    public class ScaleData {
        /// <summary>
        /// Whether this instance originates from a client. This influences how to write certain data.
        /// </summary>
        public bool origin { private get; init; }
        
        /// <summary>
        /// Whether the x of the scale is defined.
        /// </summary>
        public bool x { get; set; }
        /// <summary>
        /// Whether the y of the scale is defined.
        /// </summary>
        public bool y { get; set; }
        /// <summary>
        /// Whether the z of the scale is defined.
        /// </summary>
        public bool z { get; set; }

        /// <summary>
        /// Whether the x of the scale is only flipped from positive to negative or vice versa.
        /// </summary>
        public bool xFlipped { get; set; }
        /// <summary>
        /// Whether the y of the scale is only flipped from positive to negative or vice versa.
        /// </summary>
        public bool yFlipped { get; set; }
        /// <summary>
        /// Whether the z of the scale is only flipped from positive to negative or vice versa.
        /// </summary>
        public bool zFlipped { get; set; }

        /// <summary>
        /// The float value for the x of the scale.
        /// </summary>
        public float xScale { get; set; }
        /// <summary>
        /// The float value for the y of the scale.
        /// </summary>
        public float yScale { get; set; }
        /// <summary>
        /// The float value for the z of the scale.
        /// </summary>
        public float zScale { get; set; }

        /// <summary>
        /// Whether the x of the scale is positive if it was only flipped.
        /// </summary>
        public bool xPos { get; private set; }
        /// <summary>
        /// Whether the y of the scale is positive if it was only flipped.
        /// </summary>
        public bool yPos { get; private set; }
        /// <summary>
        /// Whether the z of the scale is positive if it was only flipped.
        /// </summary>
        public bool zPos { get; private set; }

        /// <summary>
        /// Whether this data instance is empty (no x, y and z defined).
        /// </summary>
        public bool IsEmpty => !x && !y && !z;

        /// <inheritdoc cref="IPacketData.WriteData" />
        public void WriteData(IPacket packet) {
            // Logger.Debug($"ScaleData.WriteData x: {x}, y: {y}, z: {z}, xFlipped: {xFlipped}, yFlipped: {yFlipped}, zFlipped: {zFlipped}, xScale: {xScale}, yScale: {yScale}, zScale: {zScale}");
            
            // 0 0 0 0 0 0 0 0
            byte flagByte = 0;

            if (x && !y && !z) {
                // Only x defined
                // ( 1 0 ) 0 0 0 0 0 0
                flagByte |= 1;
            } else if (!x && y && !z) {
                // Only y defined
                // ( 0 1 ) 0 0 0 0 0 0
                flagByte |= 2;
            } else if (x && y && !z) {
                // Only x and y defined
                // ( 1 1 ) 0 0 0 0 0 0
                flagByte |= 3;
            }

            if (xFlipped) {
                // 1 x ( 1 ) 0 0 0 0 0
                flagByte |= 4;

                if ((origin && xScale > 0) || (!origin && xPos)) {
                    // 1 x 1 ( 1 ) 0 0 0 0
                    flagByte |= 8;
                }
            }
            
            if (yFlipped) {
                // 1 x x x ( 1 ) 0 0 0
                flagByte |= 16;

                if ((origin && yScale > 0) || (!origin && yPos)) {
                    // 1 x x x 1 ( 1 ) 0 0
                    flagByte |= 32;
                }
            }
            
            if (zFlipped) {
                // 1 x x x x x ( 1 ) 0
                flagByte |= 64;

                if ((origin && zScale > 0) || (!origin && zPos)) {
                    // 1 x x x x x 1 ( 1 )
                    flagByte |= 128;
                }
            }
            
            // Logger.Debug($"  Flag: {flagByte}");
            packet.Write(flagByte);

            if (x && !xFlipped) {
                // Logger.Debug($"  xScale: {xScale}");
                packet.Write(xScale);
            }

            if (y && !yFlipped) {
                // Logger.Debug($"  yScale: {yScale}");
                packet.Write(yScale);
            }

            if (z && !zFlipped) {
                // Logger.Debug($"  zScale: {zScale}");
                packet.Write(zScale);
            }
        }

        /// <inheritdoc cref="IPacketData.ReadData "/>
        public void ReadData(IPacket packet) {
            var flagByte = packet.ReadByte();
            // Logger.Debug($"ScaleData.ReadData flag: {flagByte}");

            var firstBit = (flagByte & 1) != 0;
            var secondBit = (flagByte & 2) != 0;

            if (firstBit) {
                x = true;
            }

            if (secondBit) {
                y = true;
            }

            if (!firstBit && !secondBit) {
                x = y = z = true;
            }

            if ((flagByte & 4) != 0) {
                xFlipped = true;

                if ((flagByte & 8) != 0) {
                    xPos = true;
                }
            }

            if ((flagByte & 16) != 0) {
                yFlipped = true;

                if ((flagByte & 32) != 0) {
                    yPos = true;
                }
            }

            if ((flagByte & 64) != 0) {
                zFlipped = true;

                if ((flagByte & 128) != 0) {
                    zPos = true;
                }
            }

            if (x && !xFlipped) {
                xScale = packet.ReadFloat();
                // Logger.Debug($"  xScale: {xScale}");
            }

            if (y && !yFlipped) {
                yScale = packet.ReadFloat();
                // Logger.Debug($"  yScale: {yScale}");
            }

            if (z && !zFlipped) {
                zScale = packet.ReadFloat();
                // Logger.Debug($"  zScale: {zScale}");
            }
            
            // Logger.Debug($"  x: {x}, y: {y}, z: {z}, xFlipped: {xFlipped}, yFlipped: {yFlipped}, zFlipped: {zFlipped}, xPos: {xPos}, yPos: {yPos}, zPos: {zPos}");
        }

        /// <summary>
        /// Merge the given data into this instance.
        /// </summary>
        /// <param name="data">Another instance of ScaleData.</param>
        public void Merge(ScaleData data) {
            x |= data.x;
            y |= data.y;
            z |= data.z;

            if (data.x) {
                xFlipped = data.xFlipped;

                if (data.xFlipped) {
                    xPos = data.xPos;
                } else {
                    xScale = data.xScale;
                }
            }
            
            if (data.y) {
                yFlipped = data.yFlipped;

                if (data.yFlipped) {
                    yPos = data.yPos;
                } else {
                    yScale = data.yScale;
                }
            }
            
            if (data.z) {
                zFlipped = data.zFlipped;

                if (data.zFlipped) {
                    zPos = data.zPos;
                } else {
                    zScale = data.zScale;
                }
            }
        }

        /// <inheritdoc />
        public override string ToString() {
            return
                $"ScaleData: x: {x}, y: {y}, z: {z}, xFlipped: {xFlipped}, yFlipped: {yFlipped}, zFlipped: {zFlipped}, xPos: {xPos}, yPos: {yPos}, zPos: {zPos}, xScale: {xScale}, yScale: {yScale}, zScale: {zScale}";
        }
    }
}

/// <summary>
/// Packet data for the reliable part of an entity update.
/// </summary>
internal class ReliableEntityUpdate : BaseEntityUpdate {
    /// <inheritdoc />
    public override bool IsReliable => true;

    /// <inheritdoc />
    public override bool DropReliableDataIfNewerExists => false;
    
    /// <summary>
    /// A set containing the types of updates contained in this packet.
    /// </summary>
    public HashSet<EntityUpdateType> UpdateTypes { get; }
    
    /// <summary>
    /// Whether the entity is active or not.
    /// </summary>
    public bool IsActive { get; set; }
    
    /// <summary>
    /// List of generic entity network data for entity components and FSM updates.
    /// </summary>
    public List<EntityNetworkData> GenericData { get; }
    
    /// <summary>
    /// Dictionary of data for a host entity's FSMs.
    /// </summary>
    public Dictionary<byte, EntityHostFsmData> HostFsmData { get; }

    /// <summary>
    /// Construct the reliable entity update data.
    /// </summary>
    public ReliableEntityUpdate() {
        UpdateTypes = new HashSet<EntityUpdateType>();
        GenericData = new List<EntityNetworkData>();
        HostFsmData = new Dictionary<byte, EntityHostFsmData>();
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Id);

        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (UpdateTypes.Contains((EntityUpdateType) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);
        
        if (UpdateTypes.Contains(EntityUpdateType.Active)) {
            packet.Write(IsActive);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Data)) {
            if (GenericData.Count > byte.MaxValue) {
                Logger.Error("Length of entity network data instances exceeded max value of byte");
            }
                
            var length = (byte)System.Math.Min(GenericData.Count, byte.MaxValue);

            packet.Write(length);
            for (var i = 0; i < length; i++) {
                GenericData[i].WriteData(packet);
            }
        }

        if (UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            var length = (byte) HostFsmData.Count;
            packet.Write(length);

            foreach (var pair in HostFsmData) {
                packet.Write(pair.Key);

                pair.Value.WriteData(packet);
            }
        }
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Id = packet.ReadUShort();

        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                UpdateTypes.Add((EntityUpdateType) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }
        
        if (UpdateTypes.Contains(EntityUpdateType.Active)) {
            IsActive = packet.ReadBool();
        }

        if (UpdateTypes.Contains(EntityUpdateType.Data)) {
            var length = packet.ReadByte();

            for (var i = 0; i < length; i++) {
                var entityNetworkData = new EntityNetworkData();
                entityNetworkData.ReadData(packet);
                    
                GenericData.Add(entityNetworkData);
            }
        }

        if (UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            var length = packet.ReadByte();

            for (var i = 0; i < length; i++) {
                var key = packet.ReadByte();

                var data = new EntityHostFsmData();
                data.ReadData(packet);
                
                HostFsmData.Add(key, data);
            }
        }
    }
}

/// <summary>
/// Generic data for a networked entity.
/// </summary>
internal class EntityNetworkData {
    /// <summary>
    /// The type of the data.
    /// </summary>
    public EntityComponentType Type { get; set; }
    /// <summary>
    /// Packet instance containing the data for easy reading and writing of data.
    /// </summary>
    public Packet Packet { get; set; }

    public EntityNetworkData() {
        Packet = new Packet();
    }

    /// <inheritdoc cref="IPacketData.WriteData" />
    public void WriteData(IPacket packet) {
        packet.Write((ushort) Type);

        var data = Packet.ToArray();
        
        if (data.Length > ushort.MaxValue) {
            Logger.Error("Length of entity network data exceeded max value of ushort");
        }
            
        var length = (ushort) System.Math.Min(data.Length, ushort.MaxValue);

        packet.Write(length);
        for (var i = 0; i < length; i++) {
            packet.Write(data[i]);
        }
    }

    /// <inheritdoc cref="IPacketData.ReadData" />
    public void ReadData(IPacket packet) {
        Type = (EntityComponentType) packet.ReadUShort();

        var length = packet.ReadUShort();
        var data = new byte[length];
        
        for (var i = 0; i < length; i++) {
            data[i] = packet.ReadByte();
        }

        Packet = new Packet(data);
    }
}

/// <summary>
/// Enumeration of entity update types.
/// </summary>
internal enum EntityUpdateType {
    Position = 0,
    Scale,
    Animation,
    Active,
    Data,
    HostFsm
}
