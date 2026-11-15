using System.Collections.Generic;
using System.Threading;
using Hkmp.Networking.Client;
using Xunit;

namespace HKMP.Tests;

public class HeldUpdatesTests {
    private readonly HeldUpdates<string> _updates = new();

    [Fact]
    public void PacketsPassStraightThroughWhenNothingIsHeld() {
        Assert.False(_updates.Keep("a"));
        Assert.Equal(0, _updates.Count);
    }

    [Fact]
    public void HeldPacketsAreHandedOverInTheOrderTheyArrived() {
        _updates.Hold();
        Assert.True(_updates.Keep("a"));
        Assert.True(_updates.Keep("b"));
        Assert.True(_updates.Keep("c"));

        var handled = new List<string>();
        Assert.Equal(3, _updates.Release(handled.Add));

        Assert.Equal(new[] { "a", "b", "c" }, handled);
        Assert.Equal(0, _updates.Count);
    }

    [Fact]
    public void PacketsAfterTheReleasePassStraightThrough() {
        _updates.Hold();
        _updates.Keep("a");
        _updates.Release(_ => { });

        Assert.False(_updates.Keep("b"));
    }

    [Fact]
    public void ADisconnectForgetsTheHeldPackets() {
        _updates.Hold();
        _updates.Keep("a");
        _updates.Clear();

        var handled = new List<string>();
        Assert.Equal(0, _updates.Release(handled.Add));
        Assert.Empty(handled);
        Assert.False(_updates.Keep("b"));
    }

    [Fact]
    public void ANewConnectionStartsWithoutThePacketsOfTheLastOne() {
        _updates.Hold();
        _updates.Keep("old");
        _updates.Hold();
        _updates.Keep("new");

        var handled = new List<string>();
        _updates.Release(handled.Add);
        Assert.Equal(new[] { "new" }, handled);
    }

    [Fact]
    public void APacketArrivingDuringTheReleaseWaitsForTheOlderOnes() {
        _updates.Hold();
        _updates.Keep("a");
        _updates.Keep("b");

        var handled = new List<string>();
        var started = new ManualResetEventSlim();
        Thread late = null;
        _updates.Release(packet => {
            lock (handled) handled.Add(packet);
            if (late != null) return;
            // The network thread receives a packet while the held ones are still being handed over.
            late = new Thread(() => {
                started.Set();
                if (!_updates.Keep("late")) lock (handled) handled.Add("late");
            });
            late.Start();
            started.Wait();
            Thread.Sleep(50);
        });
        late.Join();

        Assert.Equal(new[] { "a", "b", "late" }, handled);
    }
}
