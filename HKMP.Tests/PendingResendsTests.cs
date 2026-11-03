using System.Collections.Generic;
using Hkmp.Networking;
using Xunit;

namespace HKMP.Tests;

public class PendingResendsTests {
    private const int Retry = 250;
    private readonly PendingResends<string> _pending = new();

    private List<string> Round(long now, int budget = 1000) {
        var sent = new List<string>();
        _pending.Resend(now, Retry, budget, packet => { sent.Add(packet); return true; });
        return sent;
    }

    [Fact]
    public void ALostPacketIsResentOncePerInterval() {
        _pending.Add(1, "a", 100);

        Assert.Equal(new[] { "a" }, Round(1000));
        Assert.Empty(Round(1100));
        Assert.Equal(new[] { "a" }, Round(1250));
    }

    [Fact]
    public void AConfirmedPacketIsNotResent() {
        _pending.Add(1, "a", 100);
        Round(1000);

        _pending.Confirm(1);

        Assert.Empty(Round(2000));
        Assert.Equal(0, _pending.Count);
    }

    [Fact]
    public void TheBudgetDelaysTheRestInOrder() {
        _pending.Add(1, "a", 600);
        _pending.Add(2, "b", 600);
        _pending.Add(3, "c", 600);

        Assert.Equal(new[] { "a", "b" }, Round(1000));
        Assert.Equal(new[] { "c" }, Round(1017));
    }

    [Fact]
    public void AnEntryLargerThanTheBudgetStillGoes() {
        _pending.Add(1, "large", 5000);

        Assert.Equal(new[] { "large" }, Round(1000));
    }

    [Fact]
    public void AnEntryWithNothingLeftIsDropped() {
        _pending.Add(1, "a", 100);

        _pending.Resend(1000, Retry, 1000, _ => false);

        Assert.Equal(0, _pending.Count);
    }

    [Fact]
    public void TheSameLostPacketCountsOnce() {
        _pending.Add(1, "a", 100);
        _pending.Add(1, "a", 100);

        Assert.Equal(1, _pending.Count);
    }

    [Fact]
    public void ClearForgetsEverything() {
        _pending.Add(1, "a", 100);
        _pending.Clear();

        Assert.Empty(Round(1000));
    }

    [Fact]
    public void LossesDoNotGrowTheResend() {
        for (ushort sequence = 0; sequence < 500; sequence++) {
            _pending.Add(sequence, "a", 40);
        }

        Assert.Equal(25, Round(1000).Count);
    }
}
