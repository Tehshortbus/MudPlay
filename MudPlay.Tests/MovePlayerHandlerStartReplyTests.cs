using System.IO;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A movement command answers "started" only when the movement did start; a start
// the engine turned down is answered with the engine's reason.
public sealed class MovePlayerHandlerStartReplyTests : IDisposable
{
    private readonly string _root;

    public MovePlayerHandlerStartReplyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-start-reply-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3, and 1/9 that nothing leads to or from.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Island",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required RemoteCommandManager Engine { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required MovementController Controller { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LairTimerStore Timers { get; init; }

        public string Ask(string command, params string[] args)
        {
            List<string> replies = new();
            Engine.TryInvokeLocal(command, args, replies.Add);
            return Assert.Single(replies);
        }

        public void Dispose()
        {
            Controller.Dispose();
            AutoLair.Dispose();
            Timers.Dispose();
        }
    }

    private Rig Build()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LoopRunner loopRunner = new(tracker, coord, graph: graph, bfs: bfs, walker: walker);
        loopRunner.SetWireSender(_ => { });
        LoopManager loops = new(bfs, graph);
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers, log: null, coordinator: coord);
        MovementController controller = new(walker, loopRunner, autoLair, coord);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        FavoritesStore favorites = new(profile, cache, () => cache.ActiveSet);
        BossStore bosses = new();
        RoomSearchService search = new(graph, cache, bfs, new RoomBlacklistStore(), favorites: favorites, bosses: bosses);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), new PlayerDatabase(new ProfileService(), () => null));
        _ = new MovePlayerHandler(engine, search, graph, tracker, walker, loops, loopRunner,
            new LairManager(), autoLair, coord, controller, favorites, bosses, bfs, new LoopShareHandler(loops));

        return new Rig
        {
            Engine = engine, Walker = walker, Loop = loopRunner, Controller = controller,
            Tracker = tracker, AutoLair = autoLair, Timers = timers,
        };
    }

    [Fact]
    public void Loop_ThatStarts_SaysSo()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("looping 2 rooms", r.Ask("@loop", "1/1,", "1/2"));
        Assert.NotEqual(LoopState.Idle, r.Loop.State);
    }

    // None of the loop can be reached from where we stand: the runner turns it
    // down, and the answer is its reason, not "looping".
    [Fact]
    public void Loop_ThatCannotBeReached_AnswersWhy_NotThatItStarted()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 9));

        string reply = r.Ask("@loop", "1/1,", "1/2");

        Assert.StartsWith("loop '@loop from (local api)' not started: ", reply);
        Assert.DoesNotContain("looping", reply);
        Assert.Equal(LoopState.Idle, r.Loop.State);
    }

    [Fact]
    public void LoopLast_ThatCannotBeReached_AnswersWhy()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Ask("@loop", "1/1,", "1/2");
        r.Loop.Stop("test");
        r.Tracker.SetLocated(new RoomKey(1, 9));

        string reply = r.Ask("@loop", "last");

        Assert.Contains("not started: ", reply);
        Assert.DoesNotContain("restarting", reply);
        Assert.Equal(LoopState.Idle, r.Loop.State);
    }

    [Fact]
    public void Lair_ThatStarts_SaysSo()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("auto-lair: 2 lairs", r.Ask("@lair", "1/1,", "1/3"));
        Assert.True(r.AutoLair.IsActive);
    }

    // Refused before anything is touched: the marks we had are still there.
    [Fact]
    public void Lair_WithNoKnownRoom_IsRefused_AndOurMarksAreKept()
    {
        using Rig r = Build();
        r.AutoLair.Mark(new RoomKey(1, 2));

        Assert.Equal("auto-lair not started: I don't know what room I'm in", r.Ask("@lair", "1/1,", "1/3"));
        Assert.False(r.AutoLair.IsActive);
        Assert.True(r.AutoLair.IsMarked(new RoomKey(1, 2)));
    }

    [Fact]
    public void Lair_OfOneRoomTwice_IsRefused()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("auto-lair not started: it needs at least two lair rooms", r.Ask("@lair", "1/1,", "1/1"));
        Assert.False(r.AutoLair.IsActive);
    }

    // Asked for other lairs, a run under way keeps running: the marks are swapped
    // under it and it is never stopped, so its clocks and what it has learnt stay.
    [Fact]
    public void Lair_WithOtherLairs_WhileRunning_SwapsTheMarks_AndKeepsTheRun()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Ask("@lair", "1/1,", "1/3");
        List<bool> activeFlips = new();
        r.AutoLair.ActiveChanged += activeFlips.Add;

        Assert.Equal("auto-lair updated: 2 lairs", r.Ask("@lair", "1/2,", "1/3"));

        Assert.Empty(activeFlips);
        Assert.True(r.AutoLair.IsActive);
        Assert.False(r.AutoLair.IsMarked(new RoomKey(1, 1)));
        Assert.True(r.AutoLair.IsMarked(new RoomKey(1, 2)));
    }

    // Asked for the lairs it already has, nothing is touched and the answer says
    // it was running already, not that it started.
    [Fact]
    public void Lair_WithTheSameLairs_WhileRunning_ChangesNothing()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Ask("@lair", "1/1,", "1/3");
        int markChanges = 0;
        List<bool> activeFlips = new();
        r.AutoLair.MarkedChanged += () => markChanges++;
        r.AutoLair.ActiveChanged += activeFlips.Add;

        Assert.Equal("auto-lair already running (2 lairs)", r.Ask("@lair", "1/3,", "1/1"));

        Assert.Equal(0, markChanges);
        Assert.Empty(activeFlips);
        Assert.True(r.AutoLair.IsActive);
    }

    // The same lairs asked for again leave a standing @stop where it is.
    [Fact]
    public void Lair_WithTheSameLairs_WhilePausedByStop_StaysPaused()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Ask("@lair", "1/1,", "1/3");
        r.Ask("@stop");
        Assert.True(r.Controller.IsUserPaused);

        Assert.Equal("auto-lair already running (2 lairs), paused", r.Ask("@lair", "1/1,", "1/3"));

        Assert.True(r.Controller.IsUserPaused);
        Assert.True(r.AutoLair.IsActive);
    }

    [Fact]
    public void Goto_ToTheRoomWeStandIn_SaysWeAreThere()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("already at A (1/1)", r.Ask("@goto", "1/1"));
        Assert.NotEqual(WalkState.Walking, r.Walker.State);
    }

    [Fact]
    public void Goto_Elsewhere_SaysWalking()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("walking to C (1/3)", r.Ask("@goto", "1/3"));
    }

    [Fact]
    public void Goto_WithNoWayThere_SaysNoPath()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal("no path to Island", r.Ask("@goto", "1/9"));
    }

    // With a move of ours still in flight the walker plans only once it has
    // settled, so nothing is answered until then: not "walking" for a walk that
    // turns out to have no path.
    [Fact]
    public void Goto_AskedWhileAMoveIsInFlight_AnswersOnceTheWalkIsPlanned()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Ask("@goto", "1/3");
        Assert.Equal(RoomConfidence.Pending, r.Tracker.State.Confidence);

        List<string> replies = new();
        r.Engine.TryInvokeLocal("@goto", new[] { "1/9" }, replies.Add);
        Assert.Empty(replies);

        r.Tracker.SetLocated(new RoomKey(1, 2));

        Assert.Equal("no path to Island", Assert.Single(replies));
    }
}
