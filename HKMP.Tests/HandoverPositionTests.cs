using Hkmp.Game.Client.Entity;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    public class HandoverPositionTests {
        [Fact]
        public void A_still_hidden_boss_keeps_its_own_place_instead_of_the_fallen_replica() =>
            Assert.Equal(-12f, HandoverPosition.Choose(false, -1106f, -12f));

        [Fact]
        public void A_moving_boss_continues_from_the_last_position_the_previous_host_sent() =>
            Assert.Equal(19.7f, HandoverPosition.Choose(true, 19.7f, -12f));
    }
}
