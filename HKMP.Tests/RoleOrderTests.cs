using Hkmp.Game.Client;
using Xunit;

namespace HKMP.Tests;

public class RoleOrderTests {
    private const string Room = "Crossroads_04";
    private readonly RoleOrder _order = new();

    public RoleOrderTests() {
        _order.EnterRoom(Room);
    }

    [Fact]
    public void NewerDecisionsApply() {
        Assert.True(_order.AcceptRole(Room, 1, true));
        Assert.True(_order.AcceptRole(Room, 2, true));
    }

    [Fact]
    public void AnOlderDecisionCannotUndoANewerOne() {
        _order.AcceptRole(Room, 5, true);

        Assert.False(_order.AcceptRole(Room, 4, true));
        Assert.False(_order.AcceptRole(Room, 4, false));
    }

    [Fact]
    public void ARepeatedChangeOfHostIsIgnored() {
        _order.AcceptRole(Room, 3, true);

        Assert.False(_order.AcceptRole(Room, 3, true));
    }

    [Fact]
    public void ARoomDescriptionMayRepeatTheCurrentEpoch() {
        _order.AcceptRole(Room, 3, true);

        Assert.True(_order.AcceptRole(Room, 3, false));
    }

    [Fact]
    public void AGapIsFineBecauseOnlyTheLatestMatters() {
        _order.AcceptRole(Room, 1, true);

        Assert.True(_order.AcceptRole(Room, 9, true));
    }

    [Fact]
    public void ADecisionAboutAnotherRoomIsIgnored() {
        Assert.False(_order.AcceptRole("Crossroads_05", 1, true));
    }

    [Fact]
    public void ARoomDescriptionAppliesOnce() {
        Assert.True(_order.AcceptRoom(Room));
        Assert.False(_order.AcceptRoom(Room));
    }

    [Fact]
    public void ADescriptionOfAnotherRoomIsIgnored() {
        Assert.False(_order.AcceptRoom("Crossroads_05"));
    }

    [Fact]
    public void AHostDecisionDoesNotBlockARoomDescription() {
        _order.AcceptRole(Room, 7, true);

        Assert.True(_order.AcceptRoom(Room));
    }

    [Fact]
    public void EnteringAnotherRoomStartsItsDecisionsOver() {
        _order.AcceptRole(Room, 9, true);
        _order.AcceptRoom(Room);
        _order.EnterRoom("Crossroads_05");

        Assert.True(_order.AcceptRole("Crossroads_05", 1, true));
        Assert.True(_order.AcceptRoom("Crossroads_05"));
    }

    [Fact]
    public void LoadingTheSameRoomAgainAcceptsItsNewDescription() {
        _order.AcceptRole(Room, 9, true);
        _order.AcceptRoom(Room);
        _order.EnterRoom(Room);

        Assert.True(_order.AcceptRoom(Room));
        Assert.True(_order.AcceptRole(Room, 9, false));
    }

    [Fact]
    public void ANewConnectionStartsOver() {
        _order.AcceptRole(Room, 9, true);
        _order.AcceptRoom(Room);
        _order.Reset();
        _order.EnterRoom(Room);

        Assert.True(_order.AcceptRole(Room, 1, true));
        Assert.True(_order.AcceptRoom(Room));
    }
}
