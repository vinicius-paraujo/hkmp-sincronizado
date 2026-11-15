using System;
using System.Collections.Generic;

namespace Hkmp.Networking.Client;

/// <summary>
/// The update packets that arrive between the server accepting the connection and the client registering its packet
/// handlers. Those handlers are registered on the main thread a frame or more later, while the network thread
/// already reads and acknowledges every packet, so the server never resends one that finds no handler. Holding them
/// and handing them over in order, once the handlers exist, keeps a change made while someone joins, such as a door
/// opened in the same moment, from being lost for that player.
/// </summary>
/// <typeparam name="T">The type of the update packet.</typeparam>
internal sealed class HeldUpdates<T> {
    private readonly object _lock = new();
    private readonly List<T> _held = new();
    private bool _holding;

    /// <summary>The number of packets waiting to be handed over.</summary>
    public int Count {
        get {
            lock (_lock) return _held.Count;
        }
    }

    /// <summary>
    /// Start holding: a connection was just accepted and its handlers are not registered yet.
    /// </summary>
    public void Hold() {
        lock (_lock) {
            _held.Clear();
            _holding = true;
        }
    }

    /// <summary>
    /// Keep the packet for later while holding.
    /// </summary>
    /// <returns>True if the packet was kept; false if it can be handled now.</returns>
    public bool Keep(T packet) {
        lock (_lock) {
            if (!_holding) return false;
            _held.Add(packet);
            return true;
        }
    }

    /// <summary>
    /// Stop holding and hand over the kept packets in the order they arrived. They are handed over while still locked,
    /// so a packet arriving meanwhile waits and cannot overtake an older one.
    /// </summary>
    /// <returns>The number of packets handed over.</returns>
    public int Release(Action<T> handle) {
        lock (_lock) {
            _holding = false;
            var count = _held.Count;
            foreach (var packet in _held) handle(packet);
            _held.Clear();
            return count;
        }
    }

    /// <summary>
    /// Forget the kept packets and stop holding, because the connection they belong to is gone.
    /// </summary>
    public void Clear() {
        lock (_lock) {
            _held.Clear();
            _holding = false;
        }
    }
}
