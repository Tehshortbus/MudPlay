using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Tests;

// The real engines an event run spans (graph, BFS, tracker, walker, loop runner,
// Auto-Lair) around an EventManager, for the tests of what a run does to them.
internal sealed class EventEngineHarness : IDisposable
{
    // 1/1 ↔ 1/2 ↔ 1/3 linear strip.
    public const string StripGraphJson = """
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

    public required RoomTracker Tracker { get; init; }
    public required MovementCoordinator Coordinator { get; init; }
    public required AutoWalkManager Walker { get; init; }
    public required LoopRunner Runner { get; init; }
    public required AutoLairManager AutoLair { get; init; }
    public required LoopManager Loops { get; init; }
    public required LairManager Lairs { get; init; }
    public required EventManager Events { get; init; }
    public required LairTimerStore Timers { get; init; }
    public required LogService Log { get; init; }
    public required ProfileService Profile { get; init; }

    // Every command the walker or the loop runner put on the wire, in order.
    public List<string> Sent { get; } = new();
    // The clock the event manager reads: the real one unless a test sets its own.
    public Func<DateTimeOffset> EventsNow { get; set; } = () => DateTimeOffset.UtcNow;
    // What the event manager told the terminal.
    public List<string> Notices { get; } = new();

    // What the event manager posted past the engine raise it was called from, when
    // the harness was made with deferPosts: the app's dispatcher, by hand.
    private List<Action>? Posted { get; init; }

    public void Pump()
    {
        while (Posted is { Count: > 0 } posted)
        {
            Action next = posted[0];
            posted.RemoveAt(0);
            next();
        }
    }

    // The event manager's own program-log lines.
    public IEnumerable<string> EventLog =>
        Log.Snapshot().Where(e => e.Source == "Events").Select(e => e.Message);

    public void Dispose()
    {
        AutoLair.Dispose();
        Timers.Dispose();
    }

    // An EventManager as the client builds it, over engines with no game data
    // loaded: for what the manager does with its event list and its actions alone.
    // Its posts run at once.
    public static EventManager ManagerWithoutGameData(ProfileService? profile = null, LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        GameDataCache cache = new(Path.Combine(Path.GetTempPath(), "mudplay-no-game-data-" + Guid.NewGuid().ToString("N")));
        RoomGraphManager graph = new(cache);
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs, postToUi: run => run());
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, new LairTimerStore(cache, graph, tracker));
        return new EventManager(profile ?? TestProfiles.Loaded(), new LoopManager(bfs, graph), new LairManager(),
            runner, autoLair, walker, log, post: act => act(), now: now);
    }

    // deferPosts: what the event manager posts waits for Pump, as it waits for the
    // dispatcher in the app. Off, it runs at once, which suits a test that feeds
    // the manager its engine events by hand.
    // loopWalksItself: the loop runner gets the walker, so a loop started off its
    // rooms walks to them (and that walk can be refused). Off, a loop started
    // anywhere begins at once.
    public static EventEngineHarness Create(
        string root, string loopSet, string graphJson = StripGraphJson, string? tbinfoJson = null,
        bool loopWalksItself = false, bool deferPosts = false)
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), graphJson);
        if (tbinfoJson is not null) File.WriteAllText(Path.Combine(root, "alpha", "TBInfo.json"), tbinfoJson);
        GameDataCache cache = new(root);
        cache.SwitchSet("alpha");
        // A room command only reads as a teleport when the TBInfo script behind it
        // teleports to the exit's room.
        TBInfoStore? tbinfo = tbinfoJson is null ? null : new TBInfoStore(cache);
        tbinfo?.OnActiveSetChanged("alpha");
        RoomGraphManager graph = tbinfo is null ? new(cache) : new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs,
            walker: loopWalksItself ? walker : null, postToUi: run => run());
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers);
        LoopManager loops = new(bfs, graph);
        // LoopManager.Save no-ops until LoadAll binds the catalogue to a game-data
        // set (no folder yet, so it seeds an empty collection); a test that saves a
        // loop writes it under AppPaths.GameDataSetLoopsFolder(loopSet), which the
        // test class reclaims.
        loops.LoadAll(loopSet);
        LairManager lairs = new();

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        LogService log = new();
        List<Action>? posted = deferPosts ? new List<Action>() : null;
        EventEngineHarness h = null!;
        EventManager events = new(profile, loops, lairs, runner, autoLair, walker, log,
            post: act => { if (posted is null) act(); else posted.Add(act); }, now: () => h.EventsNow());

        h = new()
        {
            Tracker = tracker, Coordinator = coord, Walker = walker, Runner = runner, AutoLair = autoLair,
            Loops = loops, Lairs = lairs, Events = events, Timers = timers, Log = log, Profile = profile,
            Posted = posted,
        };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        runner.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        events.SetNotice(h.Notices.Add);
        return h;
    }
}
