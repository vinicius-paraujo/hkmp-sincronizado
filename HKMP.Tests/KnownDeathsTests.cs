using Hkmp.Game.Client.Entity;
using Xunit;

namespace HKMP.Tests;

public class KnownDeathsTests {
    private const ushort GruzMother = 1, Young = 2;

    [Fact]
    public void The_server_list_replaces_what_was_known() {
        var deaths = new KnownDeaths();
        deaths.Add(Young);
        deaths.Replace(new[] { GruzMother });
        Assert.True(deaths.Contains(GruzMother));
        Assert.False(deaths.Contains(Young));
    }

    // The player that killed Gruz Mother while hosting never received her death, since it sent it; the server's list
    // at the next change of host tells it.
    [Fact]
    public void A_death_announced_after_the_list_is_known_too() {
        var deaths = new KnownDeaths();
        deaths.Replace(new ushort[0]);
        deaths.Add(GruzMother);
        Assert.True(deaths.Contains(GruzMother));
    }

    [Fact]
    public void Leaving_the_room_forgets_its_deaths() {
        var deaths = new KnownDeaths();
        deaths.Replace(new[] { GruzMother, Young });
        deaths.Clear();
        Assert.False(deaths.Contains(GruzMother));
        Assert.False(deaths.Contains(Young));
    }
}
