using Hkmp.Game.Server;
using Xunit;

namespace HKMP.Tests;

public class RoomAuthorityTests {
    private const ushort Gog = 1, Steam = 2, GruzMother = 1, Young = 2;

    [Fact]
    public void Each_scene_host_starts_a_new_epoch() {
        var room = new RoomAuthority();
        Assert.Equal(0u, room.Epoch);
        Assert.Equal(1u, room.BeginHost(Gog));
        Assert.Equal(2u, room.BeginHost(Steam));
        Assert.Equal(3u, room.BeginHost(Gog));
        Assert.Equal(3u, room.Epoch);
    }

    [Fact]
    public void The_host_of_the_epoch_records_a_death() {
        var room = new RoomAuthority();
        var epoch = room.BeginHost(Gog);
        Assert.True(room.RecordDeath(Gog, epoch, GruzMother));
        Assert.True(room.IsDead(GruzMother));
    }

    // Gruz Mother died on the scene host in the moment the room changed hands: the death arrived from a player that
    // no longer hosted the room, and the new host took her over alive.
    [Fact]
    public void A_death_from_the_previous_host_counts_after_the_room_changed_hands() {
        var room = new RoomAuthority();
        var gogEpoch = room.BeginHost(Gog);
        room.BeginHost(Steam);
        Assert.True(room.RecordDeath(Gog, gogEpoch, GruzMother));
        Assert.Equal(new ushort[] { GruzMother }, room.DeadEntities());
    }

    [Fact]
    public void A_death_from_a_player_that_did_not_host_that_epoch_is_refused() {
        var room = new RoomAuthority();
        var epoch = room.BeginHost(Gog);
        Assert.False(room.RecordDeath(Steam, epoch, GruzMother));
        Assert.False(room.RecordDeath(Gog, epoch + 1, GruzMother));
        Assert.False(room.RecordDeath(Gog, 0, GruzMother));
        Assert.Empty(room.DeadEntities());
    }

    [Fact]
    public void A_death_is_recorded_once() {
        var room = new RoomAuthority();
        var epoch = room.BeginHost(Gog);
        Assert.True(room.RecordDeath(Gog, epoch, Young));
        Assert.False(room.RecordDeath(Gog, epoch, Young));
    }

    [Fact]
    public void A_pooled_body_that_starts_another_life_is_no_longer_dead() {
        var room = new RoomAuthority();
        var epoch = room.BeginHost(Gog);
        room.RecordDeath(Gog, epoch, Young);
        room.Revive(Young);
        Assert.False(room.IsDead(Young));
    }

    [Fact]
    public void Deaths_are_listed_in_order_of_entity() {
        var room = new RoomAuthority();
        var epoch = room.BeginHost(Gog);
        room.RecordDeath(Gog, epoch, 8);
        room.RecordDeath(Gog, epoch, 2);
        room.RecordDeath(Gog, epoch, 5);
        Assert.Equal(new ushort[] { 2, 5, 8 }, room.DeadEntities());
    }

    // The next case of the battery entered a room the previous case had left, and its scene host received the deaths
    // of the earlier visit: Gruz Mother started the fight dead.
    [Fact]
    public void A_room_forgotten_after_its_last_player_left_starts_over() {
        var rooms = new RoomAuthorities();
        var room = rooms.For("Crossroads_04");
        room.RecordDeath(Gog, room.BeginHost(Gog), GruzMother);
        rooms.Forget("Crossroads_04");
        var next = rooms.For("Crossroads_04");
        Assert.Equal(0u, next.Epoch);
        Assert.False(next.IsDead(GruzMother));
    }

    [Fact]
    public void A_stopped_server_forgets_every_room() {
        var rooms = new RoomAuthorities();
        var room = rooms.For("Crossroads_04");
        room.RecordDeath(Gog, room.BeginHost(Gog), GruzMother);
        rooms.Clear();
        Assert.False(rooms.For("Crossroads_04").IsDead(GruzMother));
    }

    [Fact]
    public void Each_room_has_its_own_authority() {
        var rooms = new RoomAuthorities();
        Assert.Same(rooms.For("Crossroads_04"), rooms.For("Crossroads_04"));
        Assert.NotSame(rooms.For("Crossroads_04"), rooms.For("Fungus1_04_boss"));
    }
}
