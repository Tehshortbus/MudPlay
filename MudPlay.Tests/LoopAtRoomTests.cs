using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Report stock-20261120-072254: the loop you're standing on is offered first.
public sealed class LoopAtRoomTests
{
    private static readonly RoomKey Here = new(1, 5);

    private static Loop MakeLoop(string name, bool favorite, params string[] rooms) => new()
    {
        Name = name,
        Favorite = favorite,
        Waypoints = rooms.Select(r => new LoopWaypoint { Room = r }).ToList(),
    };

    [Fact]
    public void ARoomOnNoLoop_PicksNothing() =>
        Assert.Null(LoopAtRoom.Pick(new[] { MakeLoop("Ring", false, "1/1", "1/2") }, Here));

    [Fact]
    public void ALoopStartingHere_BeatsAFavouritePassingThrough()
    {
        Loop passing = MakeLoop("A passing", favorite: true, "1/1", "1/5");
        Loop starting = MakeLoop("Z starting", favorite: false, "1/5", "1/1");

        Assert.Same(starting, LoopAtRoom.Pick(new[] { passing, starting }, Here));
    }

    [Fact]
    public void AnAlwaysStartHereRoom_IsTheLoopsStart_NotItsFirstWaypoint()
    {
        Loop firstOnly = MakeLoop("A first only", false, "1/5", "1/1");
        firstOnly.Waypoints[1].StartHere = true;
        Loop marked = MakeLoop("Z marked", false, "1/1", "1/5");
        marked.Waypoints[1].StartHere = true;

        Assert.Same(marked, LoopAtRoom.Pick(new[] { firstOnly, marked }, Here));
    }

    [Fact]
    public void AmongEquals_AFavouriteComesFirst_ThenTheName()
    {
        Loop plain = MakeLoop("A plain", false, "1/1", "1/5");
        Loop favourite = MakeLoop("Z favourite", true, "1/1", "1/5");
        Loop later = MakeLoop("B plain", false, "1/1", "1/5");

        Assert.Same(favourite, LoopAtRoom.Pick(new[] { plain, later, favourite }, Here));
        Assert.Same(plain, LoopAtRoom.Pick(new[] { later, plain }, Here));
    }
}
