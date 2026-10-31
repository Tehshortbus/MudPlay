using System.Text;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// In the dark the game can show the room again, ahead of the next move's echo, in
// the same burst as the last arrival. An engine that sends its next move on each
// landing has that move in flight when the redisplay is read, and the redisplay
// must not be taken as where the move went (report paradigm-20261030-213114: the
// pyramid solver ran a room ahead of the tracker and walked off its route).
public sealed class DarkRoomRedisplayTests
{
    private static readonly RoomKey Lit = new(1, 1);

    // A lit room, then three dark ones east of it in a line.
    private static string Corridor() => TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 1, "Hall", ("E", "1/2")),
        TrackerWireRig.Room(1, 2, "Dark Way", ("W", "1/1"), ("E", "1/3")),
        TrackerWireRig.Room(1, 3, "Dark Way", ("W", "1/2"), ("E", "1/4")),
        TrackerWireRig.Room(1, 4, "Dark Way", ("W", "1/3")),
    });

    private const string DarkLine = "The room is very dark - you can't see anything";

    [Fact]
    public void ADarkRedisplayAheadOfTheNextMovesEcho_IsNotThatMovesLanding()
    {
        using TrackerWireRig rig = new(Corridor()) { Position = Lit };
        rig.ShowRoom();
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        foreach (int room in new[] { 2, 3, 4 }) rig.DarkRooms.Add(new RoomKey(1, room));

        // The first dark arrival comes with the room shown again behind it.
        rig.NextMoveShows.Enqueue(DarkLine + "\r\n[HP=100/100]:\r\n" + DarkLine);

        // Like the solver: on each landing short of the end, the next step east. Each
        // landing is noted beside where the character really is when it is read.
        List<(RoomKey Believed, RoomKey Really)> landings = new();
        rig.Tracker.StateChanged += t =>
        {
            if (t.NewConfidence != RoomConfidence.Confirmed || t.NewRoom is not { } here) return;
            landings.Add((here.Key, rig.Position));
            if (here.Key.Room is 2 or 3) rig.Send(Encoding.Latin1.GetBytes("e\r"));
        };

        rig.Do("e");

        Assert.Equal(new[] { "e", "e", "e" }, rig.Sent);
        Assert.Equal(new[] { new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 4) }, landings.Select(l => l.Believed));
        Assert.All(landings, l => Assert.Equal(l.Really, l.Believed));
    }
}
