using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Packet;
using Xunit;

namespace HKMP.Tests;

public class PacketFragmentsTests {
    private const int Mtu = 1200;
    private long _now;

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(value => (byte) (value % 251)).ToArray();

    private PacketFragments Receiver() => new PacketFragments(() => _now);

    [Fact]
    public void AWholeDatagramPassesUntouched() {
        var data = new byte[] { 3, 0, 1, 2, 3 };

        Assert.Same(data, Receiver().Receive(data));
    }

    [Fact]
    public void FragmentsRebuildThePacketInAnyOrder() {
        var data = Data(5000);
        var fragments = PacketFragments.Split(data, 7, Mtu);
        fragments.Reverse();
        var receiver = Receiver();

        var results = fragments.Select(receiver.Receive).ToList();

        Assert.All(fragments, fragment => Assert.True(fragment.Length <= Mtu));
        Assert.All(results.Take(results.Count - 1), Assert.Null);
        Assert.Equal(data, results.Last());
    }

    [Fact]
    public void ALostFragmentDropsOnlyItsOwnPacket() {
        var lost = PacketFragments.Split(Data(3000), 1, Mtu);
        var receiver = Receiver();
        receiver.Receive(lost[0]);

        var next = new byte[] { 1, 0, 9 };

        Assert.Same(next, receiver.Receive(next));
        var other = Data(2000);
        var rebuilt = PacketFragments.Split(other, 2, Mtu).Select(receiver.Receive).Last();
        Assert.Equal(other, rebuilt);
    }

    [Fact]
    public void ADuplicateFragmentDoesNotCompleteThePacket() {
        var fragments = PacketFragments.Split(Data(3000), 1, Mtu);
        var receiver = Receiver();

        receiver.Receive(fragments[0]);

        Assert.Null(receiver.Receive(fragments[0]));
    }

    [Fact]
    public void AnExpiredPacketIsForgotten() {
        var fragments = PacketFragments.Split(Data(2000), 1, Mtu);
        var receiver = Receiver();
        receiver.Receive(fragments[0]);

        _now = 2000;

        Assert.Null(receiver.Receive(fragments[1]));
        Assert.Equal(Data(2000), receiver.Receive(fragments[0]));
    }

    [Fact]
    public void AReusedIdWithAnotherCountStartsOver() {
        var receiver = Receiver();
        receiver.Receive(PacketFragments.Split(Data(5000), 1, Mtu)[0]);

        var data = Data(2000);
        var rebuilt = PacketFragments.Split(data, 1, Mtu).Select(receiver.Receive).Last();

        Assert.Equal(data, rebuilt);
    }

    [Theory]
    [InlineData(new byte[] { 255, 255 })]
    [InlineData(new byte[] { 255, 255, 1, 0, 0, 0, 9 })]
    [InlineData(new byte[] { 255, 255, 1, 0, 3, 2, 9 })]
    public void AnInvalidFragmentIsIgnored(byte[] datagram) {
        Assert.Null(Receiver().Receive(datagram));
    }

    [Fact]
    public void RandomLossNeverCorruptsAPacket() {
        var random = new Random(1234);
        var receiver = Receiver();
        ushort id = 0;

        for (var round = 0; round < 500; round++) {
            var data = Data(1500 + random.Next(4000));
            var kept = PacketFragments.Split(data, ++id, Mtu).Where(_ => random.NextDouble() > 0.2)
                .OrderBy(_ => random.Next()).ToList();
            _now += 300;

            foreach (var result in kept.Select(receiver.Receive).Where(result => result != null)) {
                Assert.Equal(data, result);
            }
        }
    }
}
