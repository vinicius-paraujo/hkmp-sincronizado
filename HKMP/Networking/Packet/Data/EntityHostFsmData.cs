using System;
using System.Collections.Generic;
using Hkmp.Math;

namespace Hkmp.Networking.Packet.Data;
/// <summary>
/// Class containing data for host FSMs including state and FSM variables.
/// Used to make host transfer easier since all clients receive updates on host FSM details.
/// </summary>
internal class EntityHostFsmData {
    /// <summary>
    /// The types of content that is in this data class.
    /// </summary>
    public HashSet<Type> Types { get; }
    
    /// <summary>
    /// The index of the current (or last) state of the FSM.
    /// </summary>
    public byte CurrentState { get; set; }
    
    /// <summary>
    /// Dictionary containing indices of float variables to their respective values.
    /// </summary>
    public Dictionary<byte, float> Floats { get; }
    /// <summary>
    /// Dictionary containing indices of int variables to their respective values.
    /// </summary>
    public Dictionary<byte, int> Ints { get; }
    /// <summary>
    /// Dictionary containing indices of bool variables to their respective values.
    /// </summary>
    public Dictionary<byte, bool> Bools { get; }
    /// <summary>
    /// Dictionary containing indices of string variables to their respective values.
    /// </summary>
    public Dictionary<byte, string> Strings { get; }
    /// <summary>
    /// Dictionary containing indices of vector2 variables to their respective values.
    /// </summary>
    public Dictionary<byte, Vector2> Vec2s { get; }
    /// <summary>
    /// Dictionary containing indices of vector3 variables to their respective values.
    /// </summary>
    public Dictionary<byte, Vector3> Vec3s { get; }

    public EntityHostFsmData() {
        Types = new HashSet<Type>();

        Floats = new Dictionary<byte, float>();
        Ints = new Dictionary<byte, int>();
        Bools = new Dictionary<byte, bool>();
        Strings = new Dictionary<byte, string>();
        Vec2s = new Dictionary<byte, Vector2>();
        Vec3s = new Dictionary<byte, Vector3>();
    }

    /// <summary>
    /// Merges the data from the given data class into the current one.
    /// </summary>
    /// <param name="otherData">The other <see cref="EntityHostFsmData"/> instance.</param>
    /// <summary>
    /// Copies this data, holding its lock so a concurrent <see cref="MergeData"/> under the same lock cannot
    /// change it halfway.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public EntityHostFsmData Snapshot() {
        var copy = new EntityHostFsmData();
        lock (this) {
            copy.MergeData(this);
        }

        return copy;
    }

    public void MergeData(EntityHostFsmData otherData) {
        if (otherData.Types.Contains(Type.State)) {
            Types.Add(Type.State);

            CurrentState = otherData.CurrentState;
        }

        if (otherData.Types.Contains(Type.Floats)) {
            Types.Add(Type.Floats);

            foreach (var pair in otherData.Floats) {
                Floats[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Ints)) {
            Types.Add(Type.Ints);
            
            foreach (var pair in otherData.Ints) {
                Ints[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Bools)) {
            Types.Add(Type.Bools);
            
            foreach (var pair in otherData.Bools) {
                Bools[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Strings)) {
            Types.Add(Type.Strings);
            
            foreach (var pair in otherData.Strings) {
                Strings[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Vector2s)) {
            Types.Add(Type.Vector2s);
            
            foreach (var pair in otherData.Vec2s) {
                Vec2s[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Vector3s)) {
            Types.Add(Type.Vector3s);
            
            foreach (var pair in otherData.Vec3s) {
                Vec3s[pair.Key] = pair.Value;
            }
        }
    }

    /// <inheritdoc cref="IPacketData.WriteData" />
    public void WriteData(IPacket packet) {
        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(Type)).Length; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (Types.Contains((Type) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);

        if (Types.Contains(Type.State)) {
            packet.Write(CurrentState);
        }

        void WriteVarDict<T>(Type type, Dictionary<byte, T> dict, Action<T> writeValue) {
            if (Types.Contains(type)) {
                var length = (byte) dict.Count;
                packet.Write(length);

                foreach (var pair in dict) {
                    packet.Write(pair.Key);
                    writeValue.Invoke(pair.Value);
                }
            }
        }

        WriteVarDict(Type.Floats, Floats, packet.Write);
        WriteVarDict(Type.Ints, Ints, packet.Write);
        WriteVarDict(Type.Bools, Bools, packet.Write);
        WriteVarDict(Type.Strings, Strings, packet.Write);
        WriteVarDict(Type.Vector2s, Vec2s, packet.Write);
        WriteVarDict(Type.Vector3s, Vec3s, packet.Write);
    }

    /// <inheritdoc cref="IPacketData.ReadData" />
    public void ReadData(IPacket packet) {
        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(Type)).Length; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                Types.Add((Type) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }

        if (Types.Contains(Type.State)) {
            CurrentState = packet.ReadByte();
        }

        void ReadVarDict<T>(Type type, Dictionary<byte, T> dict, Func<T> readValue) {
            if (Types.Contains(type)) {
                var length = packet.ReadByte();

                for (var i = 0; i < length; i++) {
                    dict.Add(packet.ReadByte(), readValue.Invoke());
                }
            }
        }
        
        ReadVarDict(Type.Floats, Floats, packet.ReadFloat);
        ReadVarDict(Type.Ints, Ints, packet.ReadInt);
        ReadVarDict(Type.Bools, Bools, packet.ReadBool);
        ReadVarDict(Type.Strings, Strings, packet.ReadString);
        ReadVarDict(Type.Vector2s, Vec2s, packet.ReadVector2);
        ReadVarDict(Type.Vector3s, Vec3s, packet.ReadVector3);
    }

    /// <summary>
    /// Enum for update types of this class.
    /// </summary>
    public enum Type : byte {
        State,
        Floats,
        Ints,
        Bools,
        Strings,
        Vector2s,
        Vector3s
    }
}
