using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Covers ParadigmPositionResolver's realm gating, the `rm`
// request/reply loop, throttling, and the timeout fallback. Uses an isolated
// temp game-data root per test and the no-timer test ctor so nothing depends
// on a real Avalonia dispatcher.
public sealed class ParadigmPositionResolverTests : IDisposable
{
    private readonly string _root;

    public ParadigmPositionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-paradigm-resync-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Void",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Feed: the game's output through the router the resolver reads.
    private sealed record Harness(
        ParadigmPositionResolver Resolver,
        RoomTracker Tracker,
        EngineRecoveryGate Gate,
        List<byte[]> Sent,
        ServerFeed Feed,
        ManualTimers Timeout);

    // Gaol -> Dungeon -> Vault in a line, southeast each time.
    private const string ThreeRoomGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Gaol",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "1/2", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dungeon",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "1/1", "SE": "1/3", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "1/2", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private Harness Build(bool paradigm, string graphJson = GraphJson)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), graphJson);
        // Legit == 2 flags the set as ParaMud; omit it for a stock realm.
        if (paradigm)
            File.WriteAllText(Path.Combine(dir, "Info.json"), "[{\"Legit\":2}]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        EngineRecoveryGate gate = new(graph, tracker);

        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);   // Subscribe needs the pattern in the catalog
        ManualTimers timeout = new();
        ParadigmPositionResolver resolver = new(router, tracker, gate, cache, log: null, schedule: timeout.Arm);
        List<byte[]> sent = new();
        resolver.SetWireSender(sent.Add);
        return new Harness(resolver, tracker, gate, sent, new ServerFeed(router), timeout);
    }

    [Fact]
    public void Stock_TryRequestResync_ReturnsFalse_AndSendsNothing()
    {
        Harness h = Build(paradigm: false);

        Assert.False(h.Resolver.TryRequestResync("drift"));
        Assert.Empty(h.Sent);
        Assert.False(h.Resolver.RequestInFlight);
        Assert.False(h.Resolver.Enabled);
    }

    [Fact]
    public void Paradigm_FirstRequest_SendsRm_AndFlagsInFlight()
    {
        Harness h = Build(paradigm: true);

        Assert.True(h.Resolver.TryRequestResync("drift"));
        Assert.Equal("rm\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.True(h.Resolver.RequestInFlight);
        Assert.True(h.Resolver.Enabled);
    }

    [Fact]
    public void Paradigm_RequestWhileInFlight_Coalesces_NoSecondSend()
    {
        Harness h = Build(paradigm: true);

        Assert.True(h.Resolver.TryRequestResync("first"));
        Assert.True(h.Resolver.TryRequestResync("second"));   // coalesced onto the in-flight one
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Paradigm_LocationWhileInFlight_LocatesTracker_ClearsInFlight()
    {
        Harness h = Build(paradigm: true);
        h.Resolver.TryRequestResync("drift");

        h.Feed.Line("Location: 1,1");

        Assert.False(h.Resolver.RequestInFlight);
        Assert.Equal(new RoomKey(1, 1), h.Resolver.LastResolved);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public void Paradigm_UnsolicitedLocation_StillLatchesTracker()
    {
        // A user typing `rm` by hand after a bonk lost the heuristic position:
        // no request is in flight, but `rm` is authoritative so the reply must
        // still re-anchor the tracker.
        Harness h = Build(paradigm: true);

        h.Feed.Line("Location: 1,1");

        Assert.False(h.Resolver.RequestInFlight);
        Assert.Equal(new RoomKey(1, 1), h.Resolver.LastResolved);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public void Paradigm_RequestAfterReply_IsThrottled_NoSecondSend()
    {
        Harness h = Build(paradigm: true);
        h.Resolver.TryRequestResync("first");
        h.Feed.Line("Location: 1,1");   // clears in-flight, but the throttle window is still open

        Assert.False(h.Resolver.TryRequestResync("second"));
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Paradigm_Timeout_FiresResyncFailed_AndClearsInFlight()
    {
        Harness h = Build(paradigm: true);
        bool failed = false;
        h.Resolver.ResyncFailed += () => failed = true;
        h.Resolver.TryRequestResync("drift");

        h.Timeout.RunAll();

        Assert.True(failed);
        Assert.False(h.Resolver.RequestInFlight);
    }

    [Fact]
    public void RequestResyncOnce_Resolved_InvokesOnResolvedExactlyOnce()
    {
        Harness h = Build(paradigm: true);
        int resolved = 0, failed = 0;
        RoomKey? key = null;

        Assert.True(h.Resolver.RequestResyncOnce("where", k => { resolved++; key = k; }, () => failed++));
        Assert.Equal("rm\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));

        h.Feed.Line("Location: 1,1");

        Assert.Equal(1, resolved);
        Assert.Equal(0, failed);
        Assert.Equal(new RoomKey(1, 1), key);

        // One-shot: the callback detached itself, so a later `rm` reply doesn't
        // re-fire it.
        h.Feed.Line("Location: 1,1");
        Assert.Equal(1, resolved);
    }

    [Fact]
    public void RequestResyncOnce_Timeout_InvokesOnFailedExactlyOnce()
    {
        Harness h = Build(paradigm: true);
        int resolved = 0, failed = 0;

        Assert.True(h.Resolver.RequestResyncOnce("where", _ => resolved++, () => failed++));
        h.Timeout.RunAll();

        Assert.Equal(0, resolved);
        Assert.Equal(1, failed);
    }

    // Report paradigm-20260924-053941: a move went out behind the `rm`, and the answer
    // (the room that move was leaving) was taken as where the character stood — the
    // move was forgotten and the walk replanned from the room it had just left. The
    // answer agrees with the tracker here, so the move stays in flight and its own
    // room display confirms it.
    [Fact]
    public void AnswerToAnRmAskedBeforeAMoveStillInFlight_KeepsTheMove()
    {
        Harness h = Build(paradigm: true, ThreeRoomGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        RoomKey? resolved = null;
        Assert.True(h.Resolver.RequestResyncOnce("desync", k => resolved = k, () => { }));
        h.Tracker.NoteMoveSent(Direction.SE);

        h.Feed.Line("Location: 1,1");

        Assert.Equal(new RoomKey(1, 1), resolved);
        Assert.False(h.Resolver.RequestInFlight);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Tracker.NoteRoomObserved(new RoomObservation("Dungeon",
            new HashSet<Direction> { Direction.NW, Direction.SE }));
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom?.Key);
    }

    // The stall case the resync exists for: the move went out first, never
    // confirmed, and the `rm` asked afterwards says we never left. That answer is
    // newer than the move, so it settles it.
    [Fact]
    public void AnswerToAnRmAskedAfterTheMove_StillLocates()
    {
        Harness h = Build(paradigm: true, ThreeRoomGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Tracker.NoteMoveSent(Direction.SE, DateTimeOffset.UtcNow.AddSeconds(-10));
        h.Resolver.TryRequestResync("in-flight stall");

        h.Feed.Line("Location: 1,1");

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom?.Key);
    }

    // An answer naming a room the tracker doesn't hold means the tracker was wrong,
    // whatever is in flight.
    [Fact]
    public void AnswerNamingAnotherRoom_LocatesEvenWithAMoveInFlight()
    {
        Harness h = Build(paradigm: true, ThreeRoomGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Resolver.TryRequestResync("desync");
        h.Tracker.NoteMoveSent(Direction.SE);

        h.Feed.Line("Location: 1,3");

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), h.Tracker.State.CurrentRoom?.Key);
    }

    [Fact]
    public void RequestResyncOnce_Stock_ReturnsFalse_AndNeverCallsBack()
    {
        Harness h = Build(paradigm: false);
        int resolved = 0, failed = 0;

        Assert.False(h.Resolver.RequestResyncOnce("where", _ => resolved++, () => failed++));
        Assert.Empty(h.Sent);
        Assert.Equal(0, resolved);
        Assert.Equal(0, failed);
    }
}
