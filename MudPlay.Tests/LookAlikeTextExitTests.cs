using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// A text exit into a room that reads exactly like the one it leaves (issue #1204,
// report stock-20261109-225059): two Strange Mansion hallways, each showing north
// and south, joined by `move rubble`. Driven over the wire (TrackerWireRig).
public sealed class LookAlikeTextExitTests
{
    private static readonly RoomKey Stairs = new(1, 400);
    private static readonly RoomKey HallA = new(1, 428);
    private static readonly RoomKey HallB = new(1, 193);

    private static readonly string World = TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 400, "Stairs", ("N", "1/428")),
        TrackerWireRig.Room(1, 428, "Strange Mansion, Hallway",
            ("N", "1/464"), ("S", "1/400"), ("E", "1/193 (Text: move rubble)")),
        TrackerWireRig.Room(1, 193, "Strange Mansion, Hallway",
            ("N", "1/500"), ("S", "1/501"), ("W", "1/428 (Text: move rubble)")),
        TrackerWireRig.Room(1, 464, "Strange Mansion, Study", ("S", "1/428")),
        TrackerWireRig.Room(1, 500, "Strange Mansion, Library", ("S", "1/193")),
        TrackerWireRig.Room(1, 501, "Strange Mansion, Kitchen", ("N", "1/193")),
    });

    private static TrackerWireRig AtHallA()
    {
        TrackerWireRig rig = new(World) { Position = Stairs };
        rig.ShowRoom();
        Assert.True(rig.Walker.WalkTo(HallA));
        rig.Pump();
        Assert.Equal(HallA, rig.Believed);
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        return rig;
    }

    // The game echoes the text exit's own command: that echo is the move's, so the
    // hallway that follows is the landing, not a re-look of the room left.
    [Fact]
    public void TheEchoOfATextExit_ConfirmsALandingThatLooksLikeTheRoomLeft()
    {
        using TrackerWireRig rig = AtHallA();

        Assert.True(rig.Walker.WalkTo(HallB));
        rig.Pump();

        Assert.Equal("move rubble", rig.Sent[^1]);
        Assert.Equal(HallB, rig.Believed);
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
    }

    // A look at a shut door is refused with `The door is closed in that direction!`:
    // the look's refusal, which leaves the text exit still in flight alone.
    [Fact]
    public void ALookRefusedAtAShutDoor_DoesntUndoTheTextExitInFlight()
    {
        using TrackerWireRig rig = AtHallA();
        rig.NextMoveShows.Enqueue("You push your way through the rubble, and make it to the other side.");
        rig.GameAnswer = command => command == "look north" ? "The door is closed in that direction!" : null;

        Assert.True(rig.Walker.WalkTo(HallB));
        rig.Type("look north");
        rig.Pump();

        Assert.Equal(new[] { "move rubble", "look north" }, rig.Sent.TakeLast(2));
        Assert.Equal(RoomConfidence.Pending, rig.Tracker.State.Confidence);
    }
}
