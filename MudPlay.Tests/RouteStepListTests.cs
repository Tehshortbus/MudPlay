using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Pins RouteStepList.Build — the route picker's "Show steps" row builder. It turns
// an expanded WalkStep sequence into the numbered "N> map/room name < command"
// rows, reconstructs each step's room from the moves, and drops an acquire row at
// the gate that needs it.
public sealed class RouteStepListTests
{
    private static readonly Dictionary<RoomKey, string> Names = new()
    {
        [new RoomKey(13, 497)] = "Rugged Shoreline",
        [new RoomKey(13, 498)] = "Sea Cavern",
        [new RoomKey(13, 499)] = "Hidden Vault",
    };

    private static string? Name(RoomKey k) => Names.GetValueOrDefault(k);
    private static string? Item(int id) => id == 5 ? "a raft" : null;

    [Fact]
    public void Build_MovesAndLeverDetour_ShowRoomAndWireCommand()
    {
        RoomKey source = new(13, 497);
        var steps = new WalkStep[]
        {
            new MoveStep(Direction.S, new RoomKey(13, 498)),
            new CommandStep("pull lever"),
            new MoveStep(Direction.E, new RoomKey(13, 499)),
        };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, Array.Empty<RouteGateStop>(), Name, Item, _ => null);

        Assert.Collection(rows,
            r => { Assert.Equal(1, r.Number); Assert.Equal("13/497 Rugged Shoreline", r.Location); Assert.Equal("s", r.Command); Assert.False(r.IsAcquire); },
            r => { Assert.Equal(2, r.Number); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("pull lever", r.Command); },
            r => { Assert.Equal(3, r.Number); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("e", r.Command); });
    }

    [Fact]
    public void Build_GatedCrossing_DropsAcquireRowBeforeIt()
    {
        RoomKey source = new(13, 497);
        var steps = new WalkStep[]
        {
            new MoveStep(Direction.S, new RoomKey(13, 498)),
            new MoveStep(Direction.E, new RoomKey(13, 499)),   // this hop needs a raft
        };
        var stops = new[]
        {
            new RouteGateStop(new RoomKey(13, 498), Direction.E,
                new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 })),
        };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, stops, Name, Item, _ => "buy at General Store");

        Assert.Collection(rows,
            r => Assert.Equal("s", r.Command),
            // The acquire row is marked at the gate room it detours from, and its
            // ◆-marked Line names the item + source.
            r => { Assert.True(r.IsAcquire); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("obtain a raft (buy at General Store)", r.Command); Assert.Contains("◆", r.Line); },
            r => { Assert.False(r.IsAcquire); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("e", r.Command); });
        // Numbered continuously, acquire row included.
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Number));
    }

    [Fact]
    public void Build_UnknownRoomName_ShowsBareMapRoom()
    {
        RoomKey source = new(99, 1);
        var steps = new WalkStep[] { new MoveStep(Direction.U, new RoomKey(99, 2)) };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, Array.Empty<RouteGateStop>(), _ => null, Item, _ => null);

        Assert.Equal("99/1", rows[0].Location);
        Assert.Equal("u", rows[0].Command);
    }

    [Fact]
    public void Build_RowsCarryTheRoomTheCommandIsSentFrom()
    {
        RoomKey source = new(13, 497);
        var steps = new WalkStep[]
        {
            new MoveStep(Direction.S, new RoomKey(13, 498)),
            new CommandStep("pull lever"),
            new MoveStep(Direction.E, new RoomKey(13, 499)),
        };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, Array.Empty<RouteGateStop>(), Name, Item, _ => null);

        // Room is the (map,room) the command is issued from — source for the first
        // move, then the room each prior move landed in. Feeds the Details view's
        // per-room lair lookup.
        Assert.Equal(new RoomKey(13, 497), rows[0].Room);
        Assert.Equal(new RoomKey(13, 498), rows[1].Room);   // lever pulled after arriving
        Assert.Equal(new RoomKey(13, 498), rows[2].Room);
    }

    [Fact]
    public void Build_SameGateReCrossed_AnnouncesAcquireOnce()
    {
        RoomKey source = new(13, 497);
        var steps = new WalkStep[]
        {
            new MoveStep(Direction.E, new RoomKey(13, 498)),
            new MoveStep(Direction.W, new RoomKey(13, 497)),
            new MoveStep(Direction.E, new RoomKey(13, 498)),   // re-crosses the same gate
        };
        var stops = new[]
        {
            new RouteGateStop(new RoomKey(13, 497), Direction.E,
                new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 })),
        };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, stops, Name, Item, _ => "buy at General Store");

        Assert.Single(rows, r => r.IsAcquire);
    }
}
