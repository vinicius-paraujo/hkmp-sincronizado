using System.Collections.Generic;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Xunit;

namespace HKMP.Tests;

public class DeathPacketTests {
    private static Packet Reread(Packet written) => new Packet(written.ToArray());

    [Fact]
    public void A_change_of_host_carries_the_epoch_and_the_dead_entities() {
        var written = new Packet();
        var dead = new List<EntityLife> { new(1, 0), new(2, 3), new(8, 1) };
        new HostTransfer {
            SceneName = "Crossroads_04", IsHost = true, Epoch = 3, DeadEntities = dead
        }.WriteData(written);

        var read = new HostTransfer();
        read.ReadData(Reread(written));
        Assert.Equal("Crossroads_04", read.SceneName);
        Assert.True(read.IsHost);
        Assert.Equal(3u, read.Epoch);
        Assert.Equal(dead, read.DeadEntities);
    }

    [Fact]
    public void A_change_of_host_without_deaths_carries_an_empty_list() {
        var written = new Packet();
        new HostTransfer { SceneName = "Room", Epoch = 1 }.WriteData(written);
        var read = new HostTransfer();
        read.ReadData(Reread(written));
        Assert.Empty(read.DeadEntities);
    }

    [Fact]
    public void A_death_carries_the_entity_type_and_the_killing_hit() {
        var written = new Packet();
        new EntityDeathData {
            EntityType = EntityType.GruzMother, AttackDirection = 180f, AttackType = 2, IgnoreEvasion = true
        }.WriteData(written);

        var read = EntityDeathData.ReadData(Reread(written));
        Assert.Equal(EntityType.GruzMother, read.EntityType);
        Assert.Equal(180f, read.AttackDirection);
        Assert.Equal(2, read.AttackType);
        Assert.True(read.IgnoreEvasion);
    }

    [Fact]
    public void A_death_without_a_direction_reads_back_without_one() {
        var written = new Packet();
        new EntityDeathData { EntityType = EntityType.HornetNeedle }.WriteData(written);
        Assert.Null(EntityDeathData.ReadData(Reread(written)).AttackDirection);
    }
}
