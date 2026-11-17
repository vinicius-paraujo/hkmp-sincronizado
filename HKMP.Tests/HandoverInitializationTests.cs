using Hkmp.Game.Client.Entity;
using Xunit;

namespace HKMP.Tests;

public class HandoverInitializationTests {
    private static bool Alive(string value) => value != "destroyed";

    [Fact]
    public void An_original_never_initialized_is_initialized_once() {
        Assert.True(HandoverInitialization.ShouldInitialize(initialized: false));
    }

    [Fact]
    public void An_original_initialized_by_the_game_or_an_earlier_handover_is_not_initialized_again() {
        Assert.False(HandoverInitialization.ShouldInitialize(initialized: true));
    }

    [Fact]
    public void A_search_that_finds_its_target_stores_it() {
        Assert.Equal("range", HandoverInitialization.Keep("range", null, Alive));
        Assert.Equal("new range", HandoverInitialization.Keep("new range", "old range", Alive));
    }

    [Fact]
    public void A_search_that_finds_nothing_keeps_the_working_reference() {
        // The range was detached from the body by the first initialization, so a later search cannot find it.
        Assert.Equal("range", HandoverInitialization.Keep<string>(null, "range", Alive));
    }

    [Fact]
    public void A_destroyed_reference_is_not_kept() {
        Assert.Null(HandoverInitialization.Keep<string>(null, "destroyed", Alive));
    }

    [Fact]
    public void Nothing_found_and_nothing_stored_stays_empty() {
        Assert.Null(HandoverInitialization.Keep<string>(null, null, Alive));
    }
}
