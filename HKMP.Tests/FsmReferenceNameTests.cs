using Hkmp.Game.Client.Entity;
using Xunit;

namespace HKMP.Tests;

public class FsmReferenceNameTests {
    [Theory]
    [InlineData("Hero")]
    [InlineData("Target")]
    [InlineData("hero obj")]
    [InlineData("KNIGHT")]
    public void A_chased_target_is_restored_as_the_hero(string name) =>
        Assert.Equal(FsmReferenceName.Kind.Hero, FsmReferenceName.Of(name));

    [Theory]
    [InlineData("Self")]
    [InlineData("owner")]
    public void The_machine_s_own_object_is_restored_as_itself(string name) =>
        Assert.Equal(FsmReferenceName.Kind.Self, FsmReferenceName.Of(name));

    [Theory]
    [InlineData("Spawn Point")]
    [InlineData("Heroic")]
    [InlineData("")]
    public void Any_other_reference_is_left_alone(string name) =>
        Assert.Equal(FsmReferenceName.Kind.Unknown, FsmReferenceName.Of(name));
}
