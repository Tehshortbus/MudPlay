using System.IO;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Integration: a new explicit destination (@goto) must abandon a standing @stop
// pause and walk, instead of leaving the fresh walk gated behind the UserGate
// until a separate @rego. Reported live: "@stop, then @goto won't move — only
// @rego does."
public sealed class MovePlayerHandlerStopGotoTests : IDisposable
{
    private readonly string _root;

    public MovePlayerHandlerStopGotoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-goto-stop-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip; 1/1 is the start.
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
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required RemoteCommandManager Engine { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required MovementCoordinator Coord { get; init; }
        public required MovementController Controller { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required MovePlayerHandler Handler { get; init; }

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
        LoopRunner loopRunner = new(tracker, coord, graph: graph, bfs: bfs);
        loopRunner.SetWireSender(_ => { });
        LoopManager loops = new(bfs, graph);
        LairManager lairs = new();
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers, log: null, coordinator: coord);
        MovementController controller = new(walker, loopRunner, autoLair, coord);
        RoomBlacklistStore blacklist = new();
        ProfileService favoritesProfile = TestProfiles.Service();
        favoritesProfile.LoadDefaultProfile();
        FavoritesStore favorites = new(favoritesProfile, cache, () => cache.ActiveSet);
        BossStore bosses = new();
        RoomSearchService search = new(graph, cache, bfs, blacklist, favorites: favorites, bosses: bosses);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        RemoteCommandManager engine = new(chat, new PartyState(), new PlayerDatabase(new ProfileService(), () => null));

        // Registers @goto / @stop / @rego / … on the engine.
        MovePlayerHandler handler = new(engine, search, graph, tracker, walker, loops, loopRunner,
            lairs, autoLair, coord, controller, favorites, bosses, bfs, new LoopShareHandler(loops));

        return new Rig
        {
            Engine = engine, Walker = walker, Coord = coord, Controller = controller,
            Tracker = tracker, AutoLair = autoLair, Timers = timers, Handler = handler,
        };
    }

    [Fact]
    public void Goto_AfterStop_AbandonsThePause_AndWalks()
    {
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(r.Walker.WalkTo(new RoomKey(1, 3)));

        // @stop pauses (asserts the UserGate).
        r.Engine.TryInvokeLocal("@stop", null, _ => { });
        Assert.True(r.Controller.IsUserPaused);

        // @goto a DIFFERENT fresh coordinate must abandon the @stop and walk there —
        // not sit paused waiting on a separate @rego. The changed destination proves
        // the new walk actually dispatched, and the cleared pause proves it's not gated.
        r.Engine.TryInvokeLocal("@goto", new[] { "1/2" }, _ => { });
        Assert.False(r.Controller.IsUserPaused);
        Assert.Equal(new RoomKey(1, 2), r.Walker.Destination);
    }

    // An @stop that takes hold says so once, which is what sends the auto toggles
    // back to the character's base modes. Repeating it while already stopped doesn't.
    [Fact]
    public void Stop_AnnouncesItself_OncePerHold_WalkingOrIdle()
    {
        using Rig r = Build();
        int stops = 0;
        r.Handler.Stopped = () => stops++;
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Walker.WalkTo(new RoomKey(1, 3));

        r.Engine.TryInvokeLocal("@stop", null, _ => { });
        r.Engine.TryInvokeLocal("@stop", null, _ => { });
        Assert.Equal(1, stops);

        // Standing still counts too: the toggles a finished trip left off come back.
        r.Engine.TryInvokeLocal("@rego", null, _ => { });
        r.Walker.Stop("test");
        r.Engine.TryInvokeLocal("@stop", null, _ => { });
        Assert.Equal(2, stops);
    }

    [Fact]
    public void Stop_ThenRego_StillResumesNormally()
    {
        // Guard: the fix doesn't disturb the plain @stop → @rego path.
        using Rig r = Build();
        r.Tracker.SetLocated(new RoomKey(1, 1));
        r.Walker.WalkTo(new RoomKey(1, 3));

        r.Engine.TryInvokeLocal("@stop", null, _ => { });
        Assert.True(r.Controller.IsUserPaused);

        r.Engine.TryInvokeLocal("@rego", null, _ => { });
        Assert.False(r.Controller.IsUserPaused);
    }
}
