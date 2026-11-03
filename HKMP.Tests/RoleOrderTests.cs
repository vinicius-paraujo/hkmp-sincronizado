using Hkmp.Game.Client;
using Xunit;

namespace HKMP.Tests;

public class RoleOrderTests {
    private readonly RoleOrder _order = new();

    [Fact]
    public void NewerDecisionsApply() {
        Assert.True(_order.Accept(1));
        Assert.True(_order.Accept(2));
    }

    [Fact]
    public void AnOlderDecisionCannotUndoANewerOne() {
        _order.Accept(5);

        Assert.False(_order.Accept(4));
    }

    [Fact]
    public void ARepeatedDecisionIsIgnored() {
        _order.Accept(3);

        Assert.False(_order.Accept(3));
    }

    [Fact]
    public void AGapIsFineBecauseOnlyTheLatestMatters() {
        _order.Accept(1);

        Assert.True(_order.Accept(9));
    }

    [Fact]
    public void ARoomDescriptionAppliesOnce() {
        Assert.True(_order.AcceptRoom(4));
        Assert.False(_order.AcceptRoom(4));
    }

    [Fact]
    public void AnOlderRoomDescriptionIsIgnored() {
        _order.AcceptRoom(6);

        Assert.False(_order.AcceptRoom(5));
    }

    [Fact]
    public void AHostDecisionDoesNotBlockARoomDescription() {
        _order.Accept(7);

        Assert.True(_order.AcceptRoom(6));
    }

    [Fact]
    public void ANewConnectionStartsOver() {
        _order.Accept(9);
        _order.AcceptRoom(9);
        _order.Reset();

        Assert.True(_order.Accept(1));
        Assert.True(_order.AcceptRoom(1));
    }
}
