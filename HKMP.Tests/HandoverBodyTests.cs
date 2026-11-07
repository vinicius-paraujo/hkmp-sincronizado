using Hkmp.Game.Client.Entity;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    public class HandoverBodyTests {
        [Fact]
        public void An_enemy_the_previous_host_kept_still_stays_still_instead_of_falling_through_the_floor() =>
            Assert.True(HandoverBody.Kinematic(true, false, makeDynamic: true));

        [Fact]
        public void A_boss_its_fight_made_dynamic_keeps_its_physics() =>
            Assert.False(HandoverBody.Kinematic(false, true, makeDynamic: true));

        [Fact]
        public void Without_a_report_a_body_that_can_stand_is_made_dynamic_as_before() =>
            Assert.False(HandoverBody.Kinematic(null, true, makeDynamic: true));

        [Fact]
        public void Without_a_report_a_body_left_alone_by_the_earlier_rule_keeps_its_own_value() {
            Assert.True(HandoverBody.Kinematic(null, true, makeDynamic: false));
            Assert.False(HandoverBody.Kinematic(null, false, makeDynamic: false));
        }
    }
}
