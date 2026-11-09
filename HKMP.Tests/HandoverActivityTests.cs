using Hkmp.Game.Client.Entity;
using Xunit;

namespace HKMP.Tests;

public class HandoverActivityTests {
    [Fact]
    public void A_replica_switched_off_on_this_copy_alone_comes_back_on_when_the_host_last_showed_it_alive() =>
        Assert.True(HandoverActivity.OriginalActive(false, false, reportedShown: true, reportedAlive: true,
            replicaActive: false, replicaDead: false));

    [Fact]
    public void An_enemy_that_really_died_stays_dead_and_does_not_revive() =>
        Assert.False(HandoverActivity.OriginalActive(false, true, reportedShown: true, reportedAlive: true,
            replicaActive: true, replicaDead: true));

    [Fact]
    public void An_enemy_the_host_last_reported_dead_stays_off() =>
        Assert.False(HandoverActivity.OriginalActive(false, false, reportedShown: true, reportedAlive: false,
            replicaActive: true, replicaDead: false));

    [Fact]
    public void An_enemy_the_host_last_hid_stays_hidden() =>
        Assert.False(HandoverActivity.OriginalActive(false, false, reportedShown: false, reportedAlive: true,
            replicaActive: true, replicaDead: false));

    [Fact]
    public void Without_a_report_the_replica_decides() {
        Assert.True(HandoverActivity.OriginalActive(false, false, null, null, replicaActive: true, replicaDead: false));
        Assert.False(HandoverActivity.OriginalActive(false, true, null, null, replicaActive: false, replicaDead: false));
    }

    [Fact]
    public void An_original_that_ran_all_along_keeps_its_own_state() {
        Assert.True(HandoverActivity.OriginalActive(true, true, false, false, replicaActive: false, replicaDead: false));
        Assert.False(HandoverActivity.OriginalActive(true, false, true, true, replicaActive: true, replicaDead: false));
    }

    [Fact]
    public void Handing_over_back_and_forth_keeps_the_enemy_alive_and_shown() {
        // A to B, then back to A: each new host receives what the previous one showed.
        var onB = HandoverActivity.OriginalActive(false, false, true, true, replicaActive: true, replicaDead: false);
        var onA = HandoverActivity.OriginalActive(false, false, onB, onB, replicaActive: false, replicaDead: false);
        Assert.True(onB);
        Assert.True(onA);
    }
}
