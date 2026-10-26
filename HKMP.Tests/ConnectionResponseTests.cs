using System;
using System.Threading;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Xunit;

namespace Hkmp.Tests {
    public class ConnectionResponseTests {
        [Fact]
        public void Response_completion_waits_for_ack_and_fires_once() {
            var sender = new Sender();
            var completed = 0;
            sender.Start();
            try {
                sender.EnqueuePacket(new Packet(new byte[] { 1 }), () => Interlocked.Increment(ref completed));
                Assert.True(sender.Sent.Wait(2000));
                Assert.Equal(0, Volatile.Read(ref completed));
                sender.Acknowledge();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed) == 1, 2000));
                sender.Acknowledge();
                sender.EnqueuePacket(new Packet(new byte[] { 2 }));
                Assert.Equal(1, Volatile.Read(ref completed));
            } finally { sender.Stop(); }
        }

        [Fact]
        public void Cancelled_response_does_not_complete_admission() {
            var sender = new Sender();
            var completed = 0;
            sender.EnqueuePacket(new Packet(new byte[] { 1 }), () => Interlocked.Increment(ref completed));
            sender.Start();
            Assert.True(sender.Sent.Wait(2000));
            sender.Stop();
            sender.Start(); // Joins the cancelled worker before checking its completion.
            sender.Stop();
            Assert.Equal(0, Volatile.Read(ref completed));
        }

        private sealed class Sender : ChunkSender {
            public readonly ManualResetEventSlim Sent = new ManualResetEventSlim();
            private byte _chunk;
            private byte _slices;
            protected override void SetSliceData(byte chunkId, byte sliceId, byte numSlices, byte[] data) {
                _chunk = chunkId; _slices = numSlices; Sent.Set();
            }
            public void Acknowledge() => ProcessReceivedData(new SliceAckData {
                ChunkId = _chunk, NumSlices = _slices, Acked = new[] { true }
            });
        }
    }
}

namespace Hkmp.Logging {
    internal static class Logger { public static void Warn(string text) {} public static void Error(string text) {} }
}
namespace Hkmp.Networking {
    internal static class ConnectionManager {
        public const int MaxSlicesPerChunk = 256, MaxSliceSize = 1024, MaxChunkSize = 262144;
    }
}
namespace Hkmp.Networking.Packet {
    internal sealed class Packet {
        private readonly byte[] _bytes;
        public Packet(byte[] bytes) { _bytes = bytes; }
        public byte[] ToArray() => _bytes;
    }
}
namespace Hkmp.Networking.Packet.Data {
    internal sealed class SliceAckData {
        public byte ChunkId; public ushort NumSlices; public bool[] Acked;
    }
}
