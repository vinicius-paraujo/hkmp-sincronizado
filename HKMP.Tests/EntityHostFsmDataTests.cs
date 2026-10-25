using System.Linq;
using System.Threading;
using Hkmp.Networking.Packet.Data;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    public class EntityHostFsmDataTests {
        private static EntityHostFsmData Floats(params float[] values) {
            var data = new EntityHostFsmData();
            data.Types.Add(EntityHostFsmData.Type.Floats);
            for (var i = 0; i < values.Length; i++) data.Floats[(byte) i] = values[i];
            return data;
        }

        [Fact]
        public void A_snapshot_keeps_the_values_of_the_moment_it_was_taken() {
            var stored = Floats(1f);
            var snapshot = stored.Snapshot();
            stored.MergeData(Floats(2f, 3f));
            Assert.Equal(new[] { 1f }, snapshot.Floats.Values.ToArray());
            Assert.Equal(2, stored.Floats.Count);
        }

        [Fact]
        public void Snapshots_taken_while_the_host_keeps_merging_can_be_read_safely() {
            var stored = Floats(0f);
            var running = true;
            var host = new Thread(() => {
                for (var i = 0; running; i++) {
                    lock (stored) stored.MergeData(Floats(Enumerable.Range(0, i % 200 + 1).Select(v => (float) v).ToArray()));
                    if (i % 200 == 0) lock (stored) stored.Floats.Clear();
                }
            });
            host.Start();
            try {
                for (var i = 0; i < 20000; i++) {
                    var snapshot = stored.Snapshot();
                    foreach (var pair in snapshot.Floats) Assert.True(pair.Value >= 0f);
                }
            } finally {
                running = false;
                host.Join();
            }
        }
    }
}
