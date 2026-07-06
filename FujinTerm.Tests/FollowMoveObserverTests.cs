using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="FollowMoveObserver"/> keeps a dragged party follower located.
/// The fixture is three identically-named "Forest" rooms — the same trap the
/// live capture hit (a whole zone of "Darkwood Forest" rooms) — where the only
/// thing that can tell 1/2 from 1/3 is which direction the leader dragged us.
/// Without the drag line the tracker can't choose and stalls in Suspect; with
/// it, the direction resolves the landing room and the follower stays Confirmed.
/// </summary>
public sealed class FollowMoveObserverTests : IDisposable
{
    private readonly string _root;

    public FollowMoveObserverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-followmove-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // 1/1 "Forest" {N,E}: N → 1/2, E → 1/3. Both 1/2 and 1/3 are "Forest" {S},
    // so they are indistinguishable by name + exits alone — only the move
    // direction picks one.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    private static MessageRouter NewRouter()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        return router;
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    [Fact]
    public void DragLine_RecordsMove_ResolvesLandingRoomAmongIdenticalRooms()
    {
        RoomTracker tracker = NewTracker();
        MessageRouter router = NewRouter();
        _ = new FollowMoveObserver(router, tracker);

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);

        // Leader walks north; the game drags us and prints the follow line.
        router.Dispatch(Line(" -- Following your Party leader north --"));
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence); // the move was recorded

        // The new room display lands — indistinguishable from 1/3 by itself, but
        // the north drag pins it to 1/2.
        tracker.NoteRoomObserved(Obs("Forest", Direction.S));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void WithoutDragLine_IdenticalRoomsCannotBeResolved()
    {
        RoomTracker tracker = NewTracker();

        tracker.SetLocated(new RoomKey(1, 1));
        // No drag signal: the follower "moved" as far as the tracker knows, but it
        // never learned the direction, so an identical-looking room can't be
        // pinned to 1/2 over 1/3 — the exact drift the observer exists to prevent.
        tracker.NoteRoomObserved(Obs("Forest", Direction.S));

        Assert.NotEqual(new RoomKey(1, 2), tracker.State.CurrentRoom?.Key);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    [Fact]
    public void DragLine_FromUnknown_DoesNotThrow_AndStaysUnlocated()
    {
        RoomTracker tracker = NewTracker();
        MessageRouter router = NewRouter();
        _ = new FollowMoveObserver(router, tracker);

        // A drag with no confirmed anchor just records the step; nothing to
        // predict from, so we stay unlocated rather than fabricate a room.
        router.Dispatch(Line(" -- Following your Party leader southeast --"));
        Assert.Null(tracker.State.CurrentRoom);
    }
}
