using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Hkmp.Networking.Packet;

/// <summary>
/// Splits a packet that exceeds the MTU into datagrams and puts them back together on the other side. UDP can lose or
/// reorder any datagram, so each fragment says which packet it belongs to and where it goes. A packet that misses a
/// fragment is dropped whole, which the sequence and resend mechanism already treats as an ordinary lost packet.
/// </summary>
internal sealed class PacketFragments {
    /// <summary>
    /// First two bytes of a fragment. A whole datagram starts with its packet length, which never gets this high
    /// because longer packets are always split.
    /// </summary>
    private const ushort Marker = ushort.MaxValue;

    /// <summary>
    /// The size of the marker, packet ID, fragment index and fragment count.
    /// </summary>
    private const int HeaderSize = 6;

    /// <summary>
    /// The time in milliseconds to wait for the rest of a packet. The resend logic has given up on it by then.
    /// </summary>
    private const long ExpiryMillis = 1000;

    /// <summary>
    /// The number of unfinished packets to keep, so that a burst of bad datagrams cannot grow without limit.
    /// </summary>
    private const int MaxPending = 16;

    private readonly Dictionary<ushort, Pending> _pending = new();
    private readonly Func<long> _clock;

    public PacketFragments() : this(StopwatchMillis) {
    }

    public PacketFragments(Func<long> clock) {
        _clock = clock;
    }

    private static long StopwatchMillis() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>
    /// Split packet bytes into datagrams of at most the given size.
    /// </summary>
    /// <param name="data">The whole packet, including its length prefix.</param>
    /// <param name="packetId">The ID that identifies this packet among recent split packets.</param>
    /// <param name="maxDatagram">The maximum size of a datagram.</param>
    /// <returns>The datagrams to send.</returns>
    public static List<byte[]> Split(byte[] data, ushort packetId, int maxDatagram) {
        var payload = maxDatagram - HeaderSize;
        var count = (data.Length + payload - 1) / payload;
        if (count > byte.MaxValue) {
            throw new ArgumentException($"Packet of {data.Length} bytes needs too many fragments");
        }

        var datagrams = new List<byte[]>(count);
        for (var index = 0; index < count; index++) {
            var offset = index * payload;
            var length = System.Math.Min(payload, data.Length - offset);
            var datagram = new byte[HeaderSize + length];
            Array.Copy(BitConverter.GetBytes(Marker), datagram, 2);
            Array.Copy(BitConverter.GetBytes(packetId), 0, datagram, 2, 2);
            datagram[4] = (byte) index;
            datagram[5] = (byte) count;
            Array.Copy(data, offset, datagram, HeaderSize, length);
            datagrams.Add(datagram);
        }

        return datagrams;
    }

    /// <summary>
    /// Take in a received datagram.
    /// </summary>
    /// <param name="datagram">The received bytes.</param>
    /// <returns>Bytes that can be read as packets, or null while the packet is incomplete or was invalid.</returns>
    public byte[] Receive(byte[] datagram) {
        if (datagram.Length < 2 || BitConverter.ToUInt16(datagram, 0) != Marker) {
            return datagram;
        }

        if (datagram.Length <= HeaderSize) {
            return null;
        }

        var now = _clock();
        foreach (var key in _pending.Where(pair => now - pair.Value.Started > ExpiryMillis).Select(pair => pair.Key).ToList()) {
            _pending.Remove(key);
        }

        var packetId = BitConverter.ToUInt16(datagram, 2);
        var index = datagram[4];
        var count = datagram[5];
        if (count == 0 || index >= count) {
            return null;
        }

        if (!_pending.TryGetValue(packetId, out var pending) || pending.Parts.Length != count) {
            if (_pending.Count >= MaxPending) {
                _pending.Remove(_pending.OrderBy(pair => pair.Value.Started).First().Key);
            }

            pending = new Pending { Parts = new byte[count][], Started = now };
            _pending[packetId] = pending;
        }

        var part = new byte[datagram.Length - HeaderSize];
        Array.Copy(datagram, HeaderSize, part, 0, part.Length);
        pending.Parts[index] = part;
        if (pending.Parts.Any(existing => existing == null)) {
            return null;
        }

        _pending.Remove(packetId);
        return pending.Parts.SelectMany(existing => existing).ToArray();
    }

    /// <summary>
    /// The received fragments of one packet.
    /// </summary>
    private sealed class Pending {
        public byte[][] Parts;
        public long Started;
    }
}
