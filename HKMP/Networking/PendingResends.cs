using System;
using System.Collections.Generic;

namespace Hkmp.Networking;

/// <summary>
/// The reliable data that was lost and is not confirmed yet. Each lost packet is one entry that stays until the
/// other side confirms it, so resends do not pile up inside each other. A round of resends is limited in size:
/// the rest waits for a later packet, which keeps a burst of losses from building packets that are easier to lose.
/// </summary>
/// <typeparam name="T">The type of the lost packet.</typeparam>
internal sealed class PendingResends<T> {
    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Add a lost packet. It can be resent right away.
    /// </summary>
    public void Add(ushort sequence, T packet, int size) {
        if (_entries.Exists(entry => entry.Sequence == sequence)) {
            return;
        }

        _entries.Add(new Entry { Sequence = sequence, Packet = packet, Size = size, LastSent = long.MinValue / 2 });
    }

    /// <summary>
    /// Forget everything, because a new connection starts with nothing to resend.
    /// </summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Remove the entry of a packet that the other side confirmed.
    /// </summary>
    public void Confirm(ushort sequence) {
        _entries.RemoveAll(entry => entry.Sequence == sequence);
    }

    /// <summary>
    /// Resend the oldest entries that are due. The first one always goes, however large, so nothing starves.
    /// </summary>
    /// <param name="now">The current time in milliseconds.</param>
    /// <param name="retryMillis">The time to wait before sending the same entry again.</param>
    /// <param name="budget">The size this round may use.</param>
    /// <param name="resend">Puts a packet in the outgoing packet; false means it has nothing left to resend.</param>
    public void Resend(long now, long retryMillis, int budget, Func<T, bool> resend) {
        foreach (var entry in _entries.ToArray()) {
            if (now - entry.LastSent < retryMillis) {
                continue;
            }

            if (!resend(entry.Packet)) {
                _entries.Remove(entry);
                continue;
            }

            entry.LastSent = now;
            budget -= entry.Size;
            if (budget <= 0) {
                return;
            }
        }
    }

    private sealed class Entry {
        public ushort Sequence;
        public T Packet;
        public int Size;
        public long LastSent;
    }
}
