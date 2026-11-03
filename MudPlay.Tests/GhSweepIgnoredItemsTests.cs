using MudPlay.Game.Map;
using Xunit;
using static MudPlay.Tests.RoombaHouse;

namespace MudPlay.Tests;

// An item on Roomba's ignore list is left wherever the sweep finds it: never
// picked up, never moved, and reported as ignored. Everything else in the house
// is still sorted. Played out in RoombaHouse against the real sweep manager.
public sealed class GhSweepIgnoredItemsTests : IDisposable
{
    private readonly RoombaHouse _house = new();

    public void Dispose() => _house.Dispose();

    [Fact]
    public void AnIgnoredItem_IsNeverPickedUp_AndTheRestIsStillSorted()
    {
        _house.Labels.SetIgnored("mace", true);
        _house.Floor[B]["mace"] = 3;
        _house.Floor[A]["chain shirt"] = 1;
        Assert.True(_house.Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, _house.Sweep.Phase);
        Assert.Equal(0, _house.Sends("get") - _house.Sends("get chain shirt"));
        Assert.Equal(3, _house.Floor[B].GetValueOrDefault("mace"));
        Assert.Equal(1, _house.Floor[B].GetValueOrDefault("chain shirt"));
        GhSweepItemFound left = Assert.Single(_house.Sweep.LeftInPlace);
        Assert.Equal(B, left.Room);
        Assert.Equal(GhLeftReason.Ignored, left.Reason);
    }

    [Fact]
    public void TakenOffTheList_TheItemIsSortedAgain()
    {
        _house.Labels.SetIgnored("mace", true);
        _house.Labels.SetIgnored("mace", false);
        _house.Floor[B]["mace"] = 3;
        Assert.True(_house.Sweep.Start());
        _house.PlayOn();

        Assert.Empty(_house.Sweep.LeftInPlace);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
    }
}
