using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet.Data;
using Xunit;

namespace HKMP.Tests;

public class KnownDeathsTests {
    private const ushort GruzMother = 1, Young = 2;

    [Fact]
    public void The_server_list_replaces_what_was_known() {
        var deaths = new KnownDeaths();
        deaths.Add(Young, 0);
        deaths.Replace(new[] { new EntityLife(GruzMother, 0) });
        Assert.True(deaths.Contains(GruzMother));
        Assert.False(deaths.Contains(Young));
    }

    // The player that killed Gruz Mother while hosting never received her death, since it sent it; the server's list
    // at the next change of host tells it.
    [Fact]
    public void A_death_announced_after_the_list_is_known_too() {
        var deaths = new KnownDeaths();
        deaths.Replace(new EntityLife[0]);
        deaths.Add(GruzMother, 0);
        Assert.True(deaths.Contains(GruzMother));
    }

    [Fact]
    public void Leaving_the_room_forgets_its_deaths() {
        var deaths = new KnownDeaths();
        deaths.Replace(new[] { new EntityLife(GruzMother, 0), new EntityLife(Young, 0) });
        deaths.Clear();
        Assert.False(deaths.Contains(GruzMother));
        Assert.False(deaths.Contains(Young));
    }

    [Fact]
    public void A_dead_entity_reported_alive_in_the_same_life_stays_dead() {
        var deaths = new KnownDeaths();
        deaths.Add(GruzMother, 2);
        deaths.Alive(GruzMother, 2);
        Assert.True(deaths.Contains(GruzMother));
    }

    [Fact]
    public void A_dead_entity_comes_back_only_in_a_later_life() {
        var deaths = new KnownDeaths();
        deaths.Add(Young, 1);
        deaths.Alive(Young, 2);
        Assert.False(deaths.Contains(Young));
        Assert.Equal(2, deaths.LifeOf(Young));
    }

    [Fact]
    public void Switching_a_dead_entity_on_starts_its_next_life() {
        var deaths = new KnownDeaths();
        deaths.Add(Young, 3);
        Assert.Equal(4, deaths.BeginLife(Young));
        Assert.False(deaths.Contains(Young));
        Assert.Equal(4, deaths.BeginLife(Young));
    }
}
