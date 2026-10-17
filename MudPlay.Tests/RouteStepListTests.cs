using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Pins RouteStepList.Build — the route details row builder. It turns an expanded
// WalkStep sequence into the numbered "N> map/room name < command" rows and
// reconstructs each step's room from the moves.
public sealed class RouteStepListTests
{
    private static readonly Dictionary<RoomKey, string> Names = new()
    {
        [new RoomKey(13, 497)] = "Rugged Shoreline",
        [new RoomKey(13, 498)] = "Sea Cavern",
        [new RoomKey(13, 499)] = "Hidden Vault",
    };

    private static string? Name(RoomKey k) => Names.GetValueOrDefault(k);

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
            source, steps, Name);

        Assert.Collection(rows,
            r => { Assert.Equal(1, r.Number); Assert.Equal("13/497 Rugged Shoreline", r.Location); Assert.Equal("s", r.Command); },
            r => { Assert.Equal(2, r.Number); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("pull lever", r.Command); },
            r => { Assert.Equal(3, r.Number); Assert.Equal("13/498 Sea Cavern", r.Location); Assert.Equal("e", r.Command); });
    }

    [Fact]
    public void Build_UnknownRoomName_ShowsBareMapRoom()
    {
        RoomKey source = new(99, 1);
        var steps = new WalkStep[] { new MoveStep(Direction.U, new RoomKey(99, 2)) };

        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, _ => null);

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
            source, steps, Name);

        // Room is the (map,room) the command is issued from — source for the first
        // move, then the room each prior move landed in. Feeds the Details view's
        // per-room lair lookup.
        Assert.Equal(new RoomKey(13, 497), rows[0].Room);
        Assert.Equal(new RoomKey(13, 498), rows[1].Room);   // lever pulled after arriving
        Assert.Equal(new RoomKey(13, 498), rows[2].Room);
    }

}
