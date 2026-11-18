using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity.Action;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HKMP.Tests;

public class ReentryRulesTests {
    private static readonly ReentryRules Rules = new(new Dictionary<string, ReentryCategory> {
        ["BoolTest"] = ReentryCategory.Decide,
        ["SendEventByName"] = ReentryCategory.Decide,
        ["Wait"] = ReentryCategory.Wait,
        ["SetGravity2dScale"] = ReentryCategory.Set,
        ["FloatAdd"] = ReentryCategory.Accumulate,
        ["SetIntValue"] = ReentryCategory.Restored,
        ["SpawnObjectFromGlobalPool"] = ReentryCategory.Create,
        ["AudioPlaySimple"] = ReentryCategory.Cosmetic,
        ["SetParent"] = ReentryCategory.Structure,
        ["GotoPreviousState"] = ReentryCategory.Unsafe,
    });

    private static bool Runs(string type, bool everyFrame = false, bool offBody = false, bool marked = false) =>
        Rules.RunsOnReentry(type, everyFrame, offBody, marked);

    // The pilot boss stayed in an attack choice for good: the old rule kept only continuous actions and dropped the
    // test that chooses the way out.
    [Fact]
    public void A_state_that_chooses_its_way_out_chooses_again() {
        Assert.True(Runs("BoolTest"));
        Assert.True(Runs("Wait"));
    }

    // The pilot boss fell out of the room: the gravity a state sets was not set again.
    [Fact]
    public void A_state_sets_its_own_values_again() {
        Assert.True(Runs("SetGravity2dScale"));
    }

    [Fact]
    public void Effects_that_must_not_happen_twice_are_left_out() {
        Assert.False(Runs("FloatAdd"));
        Assert.False(Runs("SpawnObjectFromGlobalPool"));
        Assert.False(Runs("AudioPlaySimple"));
        Assert.False(Runs("SetParent"));
        Assert.False(Runs("GotoPreviousState"));
    }

    // Gruz Mother's arena counted the young she left down to three, and each new scene host set the count back to
    // seven when it entered the waiting state again, so the fight never ended.
    [Fact]
    public void A_value_written_to_the_machine_is_kept_from_the_snapshot() {
        Assert.False(Runs("SetIntValue"));
        Assert.True(Runs("SetIntValue", everyFrame: true));
    }

    [Fact]
    public void An_action_repeated_every_frame_is_the_state_behaviour_and_runs() {
        Assert.True(Runs("FloatAdd", everyFrame: true));
        Assert.Equal(ReentryCategory.Move, Rules.Categorize("FloatAdd", true, false));
    }

    [Fact]
    public void An_event_to_the_own_body_is_a_choice_and_an_event_elsewhere_a_notification() {
        Assert.True(Runs("SendEventByName"));
        Assert.False(Runs("SendEventByName", offBody: true));
        Assert.Equal(ReentryCategory.Notify, Rules.Categorize("SendEventByName", false, true));
    }

    [Fact]
    public void An_unknown_action_does_not_run_unless_marked_reentrant() {
        Assert.Null(Rules.Categorize("SomeAddonAction", false, false));
        Assert.False(Runs("SomeAddonAction"));
        Assert.True(Runs("SomeAddonAction", marked: true));
    }

    [Fact]
    public void A_state_with_an_anchor_is_entered_through_it_and_any_other_as_it_is() {
        var anchors = new ReentryAnchors();
        anchors.Add("WatcherKnight", "Black Knight", "Flip Jump Dir", "Jump Start");
        Assert.Equal("Jump Start", anchors.StateToEnter("WatcherKnight", "Black Knight", "Flip Jump Dir"));
        Assert.Equal("Jump Start", anchors.StateToEnter("WatcherKnight", "Black Knight", "Jump Start"));
        Assert.Equal("Flip Jump Dir", anchors.StateToEnter("Hornet", "Black Knight", "Flip Jump Dir"));
        Assert.Null(anchors.StateToEnter("WatcherKnight", "Black Knight", null));
    }

    [Fact]
    public void The_shipped_table_names_a_known_category_for_every_action_and_anchors_unsafe_states_only() {
        var file = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "reentry-registry.json")));
        var actions = ((JObject) file["actions"]).Properties().ToList();
        Assert.True(actions.Count > 200);
        foreach (var action in actions) {
            Assert.True(Enum.TryParse<ReentryCategory>((string) action.Value, out _), action.Name);
        }

        var unsafeTypes = actions.Where(a => (string) a.Value == nameof(ReentryCategory.Unsafe)).Select(a => a.Name);
        Assert.Equal(new[] { "GotoPreviousState" }, unsafeTypes);
        foreach (var anchor in (JArray) file["anchors"]) {
            Assert.NotEqual((string) anchor["state"], (string) anchor["anchor"]);
        }
    }
}
