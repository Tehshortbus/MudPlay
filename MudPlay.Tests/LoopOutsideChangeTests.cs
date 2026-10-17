using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Two clients running on one game-data set share its Loops folder. "This" client is
// the one under test, with the watcher the app gives it; "the other" is a plain
// manager on the same folder (or a file written straight into it), standing in for
// another running MudPlay. The walk, the read and the hand-back to the UI thread run
// inline, so no test depends on timing: a beat is OnHeartbeat plus awaiting its work.
public sealed class LoopOutsideChangeTests : IDisposable
{
    private readonly string _setName = "test-outside-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public LoopOutsideChangeTests() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        foreach (string name in new[] { _setName, _setName + "-away" })
        {
            try
            {
                string setFolder = Path.Combine(AppPaths.GameDataRoot, name);
                if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    private string Folder => AppPaths.GameDataSetLoopsFolder(_setName);

    private sealed class Client
    {
        public required LogService Log { get; init; }
        public required LoopManager Loops { get; init; }
        public required LairManager Lairs { get; init; }
        public required NavFolderManager Folders { get; init; }
        public NavFolderWatcher Watcher { get; set; } = null!;
        public int Walks { get; set; }
        // Runs after each walk, before its result is handed back: a write this client
        // makes while the walk is out.
        public Action? WhileWalking { get; set; }
        public int LoopEvents { get; set; }
        public int LairEvents { get; set; }
        public int FolderEvents { get; set; }
    }

    private Client NewClient(int pollEvery = 1, bool watch = true)
    {
        LogService log = new();
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager loops = new(new BfsMapper(graph), graph, log);
        LairManager lairs = new(log);
        loops.LoadAll(_setName);
        lairs.LoadAll(_setName);
        Client c = new() { Log = log, Loops = loops, Lairs = lairs, Folders = new NavFolderManager(loops, lairs, log) };
        loops.LoopsChanged += () => c.LoopEvents++;
        lairs.SetupsChanged += () => c.LairEvents++;
        c.Folders.FoldersChanged += () => c.FolderEvents++;
        if (watch)
            c.Watcher = new NavFolderWatcher(loops, lairs, c.Folders, log, pollEvery,
                walk: root =>
                {
                    c.Walks++;
                    NavTreeScan scan = NavTreeScan.Walk(root, LoopManager.LoopFileSuffix, LairManager.LairFileSuffix);
                    c.WhileWalking?.Invoke();
                    return Task.FromResult(scan);
                },
                read: job => Task.FromResult(job()),
                post: action => action());
        return c;
    }

    private LoopManager NewLoops()
    {
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager m = new(new BfsMapper(graph), graph);
        m.LoadAll(_setName);
        return m;
    }

    private LairManager NewLairs()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        return m;
    }

    private static Loop NewLoop(string name, int room = 2) =>
        new(name, new[] { new LoopWaypoint(new RoomKey(1, 1)), new LoopWaypoint(new RoomKey(1, room)) });

    private static LairSetup NewSetup(string name, int room = 10) =>
        new(name, new[] { new LairMarker(1, room) });

    private string LoopPath(string name) => Path.Combine(Folder, name + LoopManager.LoopFileSuffix);

    private const string LoopJson = """{ "SchemaVersion": 3, "Name": "NAME", "Waypoints": [ { "Room": "1/1" }, { "Room": "1/2" } ] }""";

    private static async Task Beat(Client c, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            c.Watcher.OnHeartbeat();
            await c.Watcher.InFlight;
        }
    }

    // A change is seen on one beat and read on the next.
    private static Task Settle(Client c) => Beat(c, 2);

    private static IEnumerable<string> Messages(Client c) => c.Log.Snapshot().Select(e => e.Message);

    // ----- loops ----------------------------------------------------

    [Fact]
    public async Task LoopWrittenByAnotherClient_AppearsOnTheSecondBeat_NotTheFirst()
    {
        Client mine = NewClient();
        File.WriteAllText(LoopPath("Hand"), LoopJson.Replace("NAME", "Hand"));

        await Beat(mine);
        Assert.Null(mine.Loops.Get("Hand"));
        Assert.Equal(0, mine.LoopEvents);
        await Beat(mine);

        Assert.NotNull(mine.Loops.Get("Hand"));
        Assert.Equal(1, mine.LoopEvents);
        Assert.NotNull(mine.Loops.LastOutsideReadAt);
        await Beat(mine, 3);
        Assert.Equal(1, mine.LoopEvents);
    }

    [Fact]
    public async Task LoopSavedByAnotherManager_AppearsAfterTwoBeats()
    {
        Client mine = NewClient();
        NewLoops().Save(NewLoop("Sewers"));

        await Settle(mine);

        Assert.Equal(2, mine.Loops.Get("Sewers")!.Waypoints.Count);
    }

    [Fact]
    public async Task LoopDeletedByAnotherClient_GoesAfterTwoBeats()
    {
        NewLoops().Save(NewLoop("Gone"));
        Client mine = NewClient();
        File.Delete(LoopPath("Gone"));

        await Beat(mine);
        Assert.NotNull(mine.Loops.Get("Gone"));
        await Beat(mine);
        Assert.Null(mine.Loops.Get("Gone"));
    }

    [Fact]
    public async Task LoopEditedByAnotherClient_IsReplacedByANewObject()
    {
        NewLoops().Save(NewLoop("Edited"));
        Client mine = NewClient();
        Loop before = mine.Loops.Get("Edited")!;

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Edited")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);
        await Settle(mine);

        Loop after = mine.Loops.Get("Edited")!;
        Assert.NotSame(before, after);
        Assert.Equal(3, after.Waypoints.Count);
        Assert.Equal(2, before.Waypoints.Count);
    }

    [Fact]
    public async Task LoopRenamedByAnotherClient_FollowsAsOneGoneAndOneNew()
    {
        NewLoops().Save(NewLoop("Old name"));
        Client mine = NewClient();

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Old name")!;
        other.Delete("Old name");
        theirs.Name = "New name";
        other.Save(theirs);
        await Settle(mine);

        Assert.Null(mine.Loops.Get("Old name"));
        Assert.NotNull(mine.Loops.Get("New name"));
    }

    [Fact]
    public async Task LoopMovedToAFolderByAnotherClient_FollowsWithItsFolder()
    {
        NewLoops().Save(NewLoop("Mover"));
        Client mine = NewClient();
        Assert.Equal("", mine.Loops.Get("Mover")!.Folder);

        NewLoops().Move("Mover", "Sewers/Lower");
        await Settle(mine);

        Assert.Equal("Sewers/Lower", mine.Loops.Get("Mover")!.Folder);
        Assert.Single(mine.Loops.Loops);
    }

    [Fact]
    public async Task EmptyFolderMadeByAnotherClient_RaisesOnlyTheFolderEvent()
    {
        Client mine = NewClient();
        await Beat(mine);   // the first walk takes the folders as they are
        int loopEvents = mine.LoopEvents, lairEvents = mine.LairEvents;

        Directory.CreateDirectory(Path.Combine(Folder, "Fresh"));
        await Settle(mine);

        Assert.Equal(1, mine.FolderEvents);
        Assert.Equal(loopEvents, mine.LoopEvents);
        Assert.Equal(lairEvents, mine.LairEvents);
        Assert.Null(mine.Loops.LastOutsideReadAt);
        Assert.Null(mine.Lairs.LastOutsideReadAt);
    }

    [Fact]
    public async Task OwnNewFolder_NeitherCatalogueNorTheFolderListTakesItAsAnOutsideChange()
    {
        Client mine = NewClient();
        mine.Folders.CreateFolder("Mine");
        mine.Loops.Save(NewLoop("Deep", 2));
        mine.Loops.Move("Deep", "Other/Place");
        int loopEvents = mine.LoopEvents, lairEvents = mine.LairEvents, folderEvents = mine.FolderEvents;

        await Beat(mine, 4);

        Assert.Equal(loopEvents, mine.LoopEvents);
        Assert.Equal(lairEvents, mine.LairEvents);
        Assert.Equal(folderEvents, mine.FolderEvents);
        Assert.Null(mine.Loops.LastOutsideReadAt);
    }

    [Fact]
    public async Task LoopInANewFolderFromAnotherClient_RaisesTheLoopEventAndNotTheLairEvent()
    {
        Client mine = NewClient();
        int lairEvents = mine.LairEvents;

        NewLoops().Save(new Loop("Elsewhere", NewLoop("x").Waypoints) { Folder = "Brand/New" });
        await Settle(mine);

        Assert.Equal("Brand/New", mine.Loops.Get("Elsewhere")!.Folder);
        Assert.Equal(lairEvents, mine.LairEvents);
        Assert.Null(mine.Lairs.LastOutsideReadAt);
        Assert.NotNull(mine.Loops.LastOutsideReadAt);
    }

    [Fact]
    public async Task OwnSaveMoveAndDelete_AreNotReadBackAsOutsideChanges()
    {
        Client mine = NewClient();
        mine.Loops.Save(NewLoop("Mine"));
        mine.Loops.Move("Mine", "Deep/Down");
        mine.Loops.Save(NewLoop("Other"));
        mine.Loops.Delete("Other");
        int raised = mine.LoopEvents;

        await Beat(mine, 5);

        Assert.Equal(raised, mine.LoopEvents);
        Assert.Null(mine.Loops.LastOutsideReadAt);
        Assert.Equal("Deep/Down", mine.Loops.Get("Mine")!.Folder);
    }

    [Fact]
    public async Task OwnSaveBesideAnotherClientsSave_StillTakesTheOthers()
    {
        Client mine = NewClient();
        NewLoops().Save(NewLoop("Theirs"));
        mine.Loops.Save(NewLoop("Mine"));

        await Settle(mine);

        Assert.NotNull(mine.Loops.Get("Theirs"));
        Assert.NotNull(mine.Loops.Get("Mine"));
    }

    [Fact]
    public async Task ABurstOfSaves_IsReadOnceSettled()
    {
        Client mine = NewClient();
        LoopManager other = NewLoops();
        int raised = mine.LoopEvents;

        other.Save(NewLoop("One"));
        await Beat(mine);
        other.Save(NewLoop("Two"));
        await Beat(mine);   // moved since the last beat: still waiting
        other.Save(NewLoop("Three"));
        await Beat(mine);
        Assert.Equal(raised, mine.LoopEvents);
        await Beat(mine);

        Assert.Equal(raised + 1, mine.LoopEvents);
        Assert.Equal(3, mine.Loops.Loops.Count);
    }

    [Fact]
    public void TakeInOutsideChanges_ReadsAtOnce_ForTheCheckBeforeASave()
    {
        Client mine = NewClient(watch: false);
        NewLoops().Save(NewLoop("Quick"));

        Assert.True(mine.Loops.TakeInOutsideChanges());
        Assert.NotNull(mine.Loops.Get("Quick"));
        Assert.False(mine.Loops.TakeInOutsideChanges());
    }

    [Fact]
    public async Task MovingALoop_NeverLeavesItMissingFromTheFolder()
    {
        LoopManager mine = NewLoops();
        string sub = Path.Combine(Folder, "Elsewhere");
        // A loop the watcher can find from the start, so it is known to be looking
        // before the moves begin.
        mine.Save(NewLoop("Seed"));
        string current = "Seed";
        int missing = 0;
        int samples = 0;
        using CancellationTokenSource stop = new();
        // Another client looking: the root first, then the sub-folder the loop moves
        // to, so a loop written there before it leaves the root is always found.
        Task watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string name = Volatile.Read(ref current);
                if (!File.Exists(LoopPath(name)) && !File.Exists(Path.Combine(sub, name + LoopManager.LoopFileSuffix)))
                    Interlocked.Increment(ref missing);
                Interlocked.Increment(ref samples);
            }
        });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref samples) > 0, TimeSpan.FromSeconds(10)));
        int before = Volatile.Read(ref samples);
        for (int i = 0; i < 300; i++)
        {
            string name = "Mover" + i;
            Loop loop = NewLoop(name);
            mine.Save(loop);
            Volatile.Write(ref current, name);
            loop.Folder = "Elsewhere";
            mine.Save(loop);
        }
        stop.Cancel();
        await watcher;

        Assert.True(samples > before);
        Assert.Equal(0, missing);
        Assert.Equal(301, mine.Loops.Count);
    }

    [Fact]
    public async Task UnreadableFile_KeepsTheCopyInMemory_AndIsLoggedOnce()
    {
        NewLoops().Save(NewLoop("Fragile"));
        Client mine = NewClient();
        Loop held = mine.Loops.Get("Fragile")!;

        File.WriteAllText(LoopPath("Fragile"), "{ this is not json");
        await Beat(mine, 6);

        Assert.Same(held, mine.Loops.Get("Fragile"));
        Assert.Equal(1, mine.Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be read")));
        Assert.DoesNotContain(Messages(mine), m => m.Contains("re-read after a change on disk"));
    }

    [Fact]
    public async Task FileAlreadySkippedAtLoad_IsNotReportedAsAnOutsideChange()
    {
        File.WriteAllText(LoopPath("Broken"), "{ this is not json");
        File.WriteAllText(Path.Combine(Folder, "Broken2" + LairManager.LairFileSuffix), "{ this is not json");
        Client mine = NewClient();
        int warns = mine.Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn);

        NewLoops().Save(NewLoop("Fine"));
        await Settle(mine);

        Assert.NotNull(mine.Loops.Get("Fine"));
        Assert.DoesNotContain(Messages(mine), m => m.Contains("changed on disk but can't be read"));
        Assert.Equal(warns, mine.Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn));
    }

    [Fact]
    public async Task RunningLoop_KeepsItsCopy_WhenTheFileIsEditedElsewhere()
    {
        NewLoops().Save(NewLoop("Running"));
        Client mine = NewClient();
        mine.Loops.NamesInUse = () => new[] { "Running" };
        Loop running = mine.Loops.Get("Running")!;
        List<LoopWaypoint> waypointsAtStart = running.Waypoints.ToList();

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Running")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);
        await Settle(mine);

        Assert.Equal(waypointsAtStart, running.Waypoints);
        Assert.Equal("Running", running.Name);
        Assert.NotSame(running, mine.Loops.Get("Running"));
        Assert.Contains(mine.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains("loop 'Running' was edited on disk") && e.Message.Contains("copy it started with"));
        Assert.Contains(Messages(mine), m => m.Contains("loops re-read after a change on disk: 0 added, 1 changed, 0 removed (changed: 'Running')"));
    }

    [Fact]
    public async Task RunningLoop_KeepsItsCopy_WhenTheFileIsDeletedElsewhere()
    {
        NewLoops().Save(NewLoop("Running"));
        Client mine = NewClient();
        mine.Loops.NamesInUse = () => new[] { "Running" };
        Loop running = mine.Loops.Get("Running")!;

        File.Delete(LoopPath("Running"));
        await Settle(mine);

        Assert.Null(mine.Loops.Get("Running"));
        Assert.Equal(2, running.Waypoints.Count);
        Assert.Equal("Running", running.Name);
        Assert.Contains(mine.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains("loop 'Running' was deleted or renamed on disk") && e.Message.Contains("copy it started with"));
    }

    [Fact]
    public async Task LoopsOutsideAnyRun_DoNotLogTheRunNote()
    {
        NewLoops().Save(NewLoop("Idle"));
        Client mine = NewClient();
        mine.Loops.NamesInUse = () => new[] { "Something else" };

        File.Delete(LoopPath("Idle"));
        await Settle(mine);

        Assert.DoesNotContain(Messages(mine), m => m.Contains("copy it started with"));
    }

    [Fact]
    public async Task ReReadLog_NamesTheLoops_OnlyWhenFiveOrFewer()
    {
        Client mine = NewClient();
        LoopManager other = NewLoops();
        for (int i = 0; i < 6; i++) other.Save(NewLoop("Bulk " + i));
        await Settle(mine);
        other.Save(NewLoop("Lone"));
        await Settle(mine);

        string[] lines = Messages(mine).Where(m => m.Contains("re-read after a change on disk")).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("loops re-read after a change on disk: 6 added, 0 changed, 0 removed", lines[0]);
        Assert.Equal("loops re-read after a change on disk: 1 added, 0 changed, 0 removed (added: 'Lone')", lines[1]);
    }

    [Fact]
    public async Task FavouriteNamedBeforeTheLoopExists_ShowsWhenItArrivesFromAnotherClient()
    {
        Client mine = NewClient();
        HashSet<string> favourites = new(StringComparer.OrdinalIgnoreCase) { "Later" };
        mine.Loops.AttachFavorites(favourites.Contains, (n, on) => { if (on) favourites.Add(n); else favourites.Remove(n); });

        NewLoops().Save(NewLoop("Later"));
        await Settle(mine);

        Assert.True(mine.Loops.Get("Later")!.Favorite);
    }

    [Fact]
    public async Task EditorOverwriteCheck_SeesALoopSavedByAnotherClient()
    {
        LoopManager mine = NewLoops();
        RoomGraphManager graph = new(new GameDataCache());
        LoopEditorDialogViewModel vm = new(NewLoop("Draft"), mine, graph, isNew: true) { Name = "Shared" };

        NewLoops().Save(NewLoop("Shared", room: 9));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("already exists", vm.SaveError);
        Assert.Equal(9, NewLoops().Get("Shared")!.Waypoints[1].Key.Room);
    }

    [Fact]
    public async Task DotNamedLoopAndDotNamedFolder_StayInTheCatalogue()
    {
        LoopManager first = NewLoops();
        first.Save(NewLoop(".dot"));
        first.Save(new Loop("Archived", NewLoop("x").Waypoints) { Folder = ".archive/Old" });
        Client mine = NewClient();
        Assert.Equal(2, mine.Loops.Loops.Count);
        int raised = mine.LoopEvents;

        await Beat(mine, 5);
        Assert.Equal(raised, mine.LoopEvents);

        // A re-read for some other change must not take them for deleted files.
        NewLoops().Save(NewLoop("Trigger"));
        await Settle(mine);

        Assert.NotNull(mine.Loops.Get("Trigger"));
        Assert.NotNull(mine.Loops.Get(".dot"));
        Assert.Equal(".archive/Old", mine.Loops.Get("Archived")!.Folder);
        Assert.True(File.Exists(LoopPath(".dot")));
    }

    [Fact]
    public async Task SuffixCase_IsMatchedAsLoadAllMatchesIt()
    {
        File.WriteAllText(Path.Combine(Folder, "Shout.LOOP"), LoopJson.Replace("NAME", "Shout"));
        Client mine = NewClient();
        bool loadedAtStart = mine.Loops.Get("Shout") is not null;

        await Beat(mine, 5);
        NewLoops().Save(NewLoop("Trigger"));
        await Settle(mine);

        Assert.Equal(loadedAtStart, mine.Loops.Get("Shout") is not null);
        Assert.NotNull(mine.Loops.Get("Trigger"));
    }

    [Fact]
    public async Task FolderThatCannotBeReached_ChangesNothing_WarnsOnce_AndNotesWhenItIsBack()
    {
        NewLoops().Save(NewLoop("Kept"));
        NewLairs().Save(NewSetup("Kept lair"));
        Client mine = NewClient();
        await Beat(mine);
        int loopEvents = mine.LoopEvents, lairEvents = mine.LairEvents;
        string away = Path.Combine(AppPaths.GameDataRoot, _setName + "-away");

        Directory.Move(Folder, away);
        await Beat(mine, 5);

        Assert.NotNull(mine.Loops.Get("Kept"));
        Assert.NotNull(mine.Lairs.Get("Kept lair"));
        Assert.Equal(1, mine.Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be read")));

        Directory.Move(away, Folder);
        await Beat(mine, 3);

        Assert.Contains(Messages(mine), m => m.Contains("can be read again"));
        Assert.NotNull(mine.Loops.Get("Kept"));
        Assert.Equal(loopEvents, mine.LoopEvents);
        Assert.Equal(lairEvents, mine.LairEvents);
    }

    // ----- the walk -------------------------------------------------

    [Fact]
    public async Task OneWalkServesBothCatalogues()
    {
        Client mine = NewClient();
        NewLoops().Save(NewLoop("Loop"));
        NewLairs().Save(NewSetup("Setup"));

        await Settle(mine);

        Assert.Equal(2, mine.Walks);
        Assert.NotNull(mine.Loops.Get("Loop"));
        Assert.NotNull(mine.Lairs.Get("Setup"));
    }

    [Fact]
    public async Task WalksEveryThirdBeat_AndOnTheNextBeatWhileAChangeIsPending()
    {
        Client mine = NewClient(pollEvery: 3);

        await Beat(mine, 2);
        Assert.Equal(0, mine.Walks);
        await Beat(mine);
        Assert.Equal(1, mine.Walks);

        NewLoops().Save(NewLoop("Late"));
        await Beat(mine, 2);
        Assert.Equal(1, mine.Walks);
        await Beat(mine);               // third beat: sees the change
        Assert.Equal(2, mine.Walks);
        Assert.Null(mine.Loops.Get("Late"));
        await Beat(mine);               // confirms on the very next beat
        Assert.Equal(3, mine.Walks);
        Assert.NotNull(mine.Loops.Get("Late"));
    }

    [Fact]
    public async Task NoWalkWhenNoSetIsActive()
    {
        Client mine = NewClient();
        mine.Loops.LoadAll(null);
        mine.Lairs.LoadAll(null);

        await Beat(mine, 5);

        Assert.Equal(0, mine.Walks);
    }

    [Fact]
    public async Task AWalkOvertakenByAnOwnWrite_IsDropped_WithNoSpuriousWarning()
    {
        NewLoops().Save(NewLoop("Mine"));
        Client mine = NewClient();
        LoopManager other = NewLoops();
        Loop theirs = other.Get("Mine")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);
        await Beat(mine);   // sees the edit

        // The next walk lists the edited file; before it is read, this client deletes it.
        mine.WhileWalking = () =>
        {
            mine.WhileWalking = null;
            mine.Loops.Delete("Mine");
        };
        await Beat(mine);
        await Beat(mine, 3);

        Assert.Null(mine.Loops.Get("Mine"));
        Assert.DoesNotContain(Messages(mine), m => m.Contains("can't be read"));
        Assert.DoesNotContain(Messages(mine), m => m.Contains("re-read after a change on disk"));
        Assert.Null(mine.Loops.LastOutsideReadAt);
    }

    // Another client removed the file first, so this client's delete writes nothing.
    // A walk already on its way must still be made stale by it, or its copy of the
    // loop would be put back.
    [Fact]
    public async Task ALoopDeletedHereAfterItsFileWasRemovedElsewhere_IsNotPutBackByAWalkInFlight()
    {
        NewLoops().Save(NewLoop("Gone"));
        NewLoops().Save(NewLoop("Other"));
        Client mine = NewClient();
        LoopManager other = NewLoops();
        Loop theirs = other.Get("Other")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);
        await Beat(mine);   // sees the edit

        mine.WhileWalking = () =>
        {
            mine.WhileWalking = null;
            File.Delete(LoopPath("Gone"));
            mine.Loops.Delete("Gone");
        };
        await Beat(mine);
        Assert.Null(mine.Loops.Get("Gone"));

        await Beat(mine, 3);
        Assert.Null(mine.Loops.Get("Gone"));
        Assert.DoesNotContain(Messages(mine), m => m.Contains("added: 'Gone'"));
    }

    // A sub-folder that can't be opened is not a folder whose loops were deleted.
    [Fact]
    public async Task SubFolderThatCannotBeOpened_KeepsItsLoops()
    {
        if (OperatingSystem.IsWindows()) return;   // no mode bits to take away
        NewLoops().Save(new Loop("Locked away", NewLoop("x").Waypoints) { Folder = "Vault" });
        Client mine = NewClient();
        int raised = mine.LoopEvents;
        string vault = Path.Combine(Folder, "Vault");
        UnixFileMode before = File.GetUnixFileMode(vault);

        File.SetUnixFileMode(vault, UnixFileMode.None);
        try
        {
            await Beat(mine, 5);

            Assert.NotNull(mine.Loops.Get("Locked away"));
            Assert.Equal(raised, mine.LoopEvents);
        }
        finally
        {
            File.SetUnixFileMode(vault, before);
        }
    }

    // The walk of the tree and the parse of the changed files must stay off the thread
    // the heartbeat runs on, which in the app is the one that draws the terminal. The
    // watcher here is the app's own: its walk and its read are the ones it ships with,
    // and only the hand-back to the "UI thread" is given, the app's being the Avalonia
    // dispatcher.
    [Fact]
    public void TheWalkAndTheParsingRunOffTheCallingThread()
    {
        OffThreadRun run = FollowOneChangeFromAThreadOfOurOwn(walk: null, parseOnTheHeartbeatThread: false);

        Assert.True(run.TakenIn);
        Assert.Equal(0, run.WalksOverBeforeTheHeartbeatReturned);
        Assert.Equal(new[] { false }, run.ParsedOnTheHeartbeatThread);
        Assert.True(run.SwappedOnTheHeartbeatThread);
        // The hold that makes the first of those certain saw the watcher's work start.
        Assert.True(run.WorkSeenStartingElsewhere > 0);
    }

    // The check above is only worth having if it can fail: a watcher whose walk stays
    // on the calling thread, and whose parse goes back to it, is caught by both.
    [Fact]
    public void AWalkOrAParseLeftOnTheCallingThread_IsCaught()
    {
        OffThreadRun run = FollowOneChangeFromAThreadOfOurOwn(
            walk: root => Task.FromResult(NavTreeScan.Walk(root, LoopManager.LoopFileSuffix, LairManager.LairFileSuffix)),
            parseOnTheHeartbeatThread: true);

        Assert.True(run.TakenIn);
        Assert.Equal(2, run.WalksOverBeforeTheHeartbeatReturned);   // one a beat
        Assert.Equal(new[] { true }, run.ParsedOnTheHeartbeatThread);
    }

    private sealed record OffThreadRun(int WalksOverBeforeTheHeartbeatReturned, int WorkSeenStartingElsewhere,
        bool[] ParsedOnTheHeartbeatThread, bool SwappedOnTheHeartbeatThread, bool TakenIn);

    // A loop file from before waypoints carried their commands. Reading one logs what
    // the upgrade dropped, from inside the parse, which is how the run below learns
    // which thread parsed it.
    private const string OlderLoopJson = """
        {
          "SchemaVersion": 2,
          "Name": "Far",
          "UserWaypoints": [ { "Map": 1, "Room": 1 }, { "Map": 1, "Room": 2 } ],
          "Steps": [ { "kind": "command", "Command": "dep 100", "DelayMs": 500 } ]
        }
        """;

    // Another client writes a loop; this client's watcher sees it on one beat and
    // reads it on the next. Every beat is sent from a thread made here, which also
    // runs whatever the watcher hands back to the "UI thread". No pool thread can be
    // that thread, so where work ran is never a coincidence of ids, and the test
    // thread only ever blocks: nothing here is compared across an await.
    //
    // walk null is the watcher's own. parseOnTheHeartbeatThread stands in for a
    // watcher that reads the changed files on the UI thread.
    private OffThreadRun FollowOneChangeFromAThreadOfOurOwn(Func<string, Task<NavTreeScan>>? walk, bool parseOnTheHeartbeatThread)
    {
        TimeSpan patience = TimeSpan.FromSeconds(30);
        LogService log = new();
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager loops = new(new BfsMapper(graph), graph, log);
        LairManager lairs = new(log);
        loops.LoadAll(_setName);
        lairs.LoadAll(_setName);
        NavFolderManager folders = new(loops, lairs, log);

        using BlockingCollection<Action> handedBack = new();
        ManualResetEventSlim heartbeatReturned = new(true);
        Thread heartbeatThread = null!;
        Exception? thrown = null;
        int walksOverEarly = 0, startedElsewhere = 0;

        // Raised on a pool thread as the heartbeat's execution context arrives on it,
        // which is before the work the heartbeat queued there starts. That work is held
        // until the heartbeat has returned. Without the hold, a walk on a pool thread
        // could be over before the calling thread reached its await, and would then
        // look exactly like a walk that never left that thread.
        AsyncLocal<object?> fromTheHeartbeat = new(change =>
        {
            if (!change.ThreadContextChanged || change.CurrentValue is null || Thread.CurrentThread == heartbeatThread) return;
            Interlocked.Increment(ref startedElsewhere);
            heartbeatReturned.Wait(patience);
        });

        heartbeatThread = new Thread(() =>
        {
            fromTheHeartbeat.Value = new object();
            foreach (Action action in handedBack.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { thrown ??= ex; }
            }
        })
        { IsBackground = true, Name = "test heartbeat thread" };

        // A walk that is over by the time the watcher asks for the UI thread, with the
        // heartbeat still on that thread's stack, ran on it.
        Action<Action> post = action =>
        {
            if (Thread.CurrentThread == heartbeatThread) Interlocked.Increment(ref walksOverEarly);
            handedBack.Add(action);
        };
        Func<Func<Action>, Task<Action>>? read = !parseOnTheHeartbeatThread ? null : job =>
        {
            TaskCompletionSource<Action> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handedBack.Add(() => done.SetResult(job()));
            return done.Task;
        };
        NavFolderWatcher watcher = new(loops, lairs, folders, log, 1, walk, read, post);

        List<bool> parsedOnHeartbeat = new();
        log.EntryAdded += entry =>
        {
            if (!entry.Message.Contains("migration of 'Far'")) return;
            lock (parsedOnHeartbeat) parsedOnHeartbeat.Add(Thread.CurrentThread == heartbeatThread);
        };
        bool swappedOnHeartbeat = false;
        loops.LoopsChanged += () => swappedOnHeartbeat = Thread.CurrentThread == heartbeatThread;

        heartbeatThread.Start();
        try
        {
            File.WriteAllText(LoopPath("Far"), OlderLoopJson);
            for (int beat = 0; beat < 2; beat++)
            {
                ManualResetEventSlim sent = new(false);
                handedBack.Add(() =>
                {
                    heartbeatReturned.Reset();
                    try { watcher.OnHeartbeat(); }
                    finally { heartbeatReturned.Set(); }
                    sent.Set();
                });
                Assert.True(sent.Wait(patience), "the heartbeat never returned");
                Assert.True(watcher.InFlight.Wait(patience), "the walk and the read never finished");
            }
        }
        finally
        {
            handedBack.CompleteAdding();
            heartbeatThread.Join(patience);
        }

        Assert.Null(thrown);
        lock (parsedOnHeartbeat)
            return new OffThreadRun(walksOverEarly, startedElsewhere, parsedOnHeartbeat.ToArray(), swappedOnHeartbeat,
                loops.Get("Far") is not null);
    }

    // ----- lair setups ----------------------------------------------

    [Fact]
    public async Task SetupWrittenByAnotherClient_AppearsOnTheSecondBeat_NotTheFirst()
    {
        Client mine = NewClient();
        int raised = mine.LairEvents;

        NewLairs().Save(NewSetup("Crypt"));
        await Beat(mine);
        Assert.Null(mine.Lairs.Get("Crypt"));
        await Beat(mine);

        Assert.NotNull(mine.Lairs.Get("Crypt"));
        Assert.Equal(raised + 1, mine.LairEvents);
        Assert.NotNull(mine.Lairs.LastOutsideReadAt);
        Assert.Null(mine.Loops.LastOutsideReadAt);
    }

    [Fact]
    public async Task SetupDeletedByAnotherClient_GoesAfterTwoBeats()
    {
        NewLairs().Save(NewSetup("Crypt"));
        Client mine = NewClient();

        NewLairs().Delete("Crypt");
        await Settle(mine);

        Assert.Null(mine.Lairs.Get("Crypt"));
    }

    [Fact]
    public async Task SetupMovedAndEditedByAnotherClient_Follows()
    {
        NewLairs().Save(NewSetup("Crypt"));
        Client mine = NewClient();
        LairSetup before = mine.Lairs.Get("Crypt")!;

        LairManager other = NewLairs();
        other.Move("Crypt", "Undead");
        await Settle(mine);
        Assert.Equal("Undead", mine.Lairs.Get("Crypt")!.Folder);

        LairSetup theirs = other.Get("Crypt")!;
        theirs.Markers.Add(new LairMarker(1, 11));
        other.Save(theirs);
        await Settle(mine);
        Assert.Equal(2, mine.Lairs.Get("Crypt")!.Markers.Count);
        Assert.Single(before.Markers);
    }

    [Fact]
    public async Task OwnSetupSaveMoveAndDelete_AreNotReadBackAsOutsideChanges()
    {
        Client mine = NewClient();
        mine.Lairs.Save(NewSetup("Mine"));
        mine.Lairs.Move("Mine", "Deep");
        mine.Lairs.Save(NewSetup("Gone"));
        mine.Lairs.Delete("Gone");
        int raised = mine.LairEvents;

        await Beat(mine, 5);

        Assert.Equal(raised, mine.LairEvents);
        Assert.Null(mine.Lairs.LastOutsideReadAt);
    }

    [Fact]
    public async Task UnreadableSetupFile_KeepsTheCopyInMemory_AndIsLoggedOnce()
    {
        NewLairs().Save(NewSetup("Fragile"));
        Client mine = NewClient();
        LairSetup held = mine.Lairs.Get("Fragile")!;

        File.WriteAllText(Path.Combine(Folder, "Fragile" + LairManager.LairFileSuffix), "{ this is not json");
        await Beat(mine, 6);

        Assert.Same(held, mine.Lairs.Get("Fragile"));
        Assert.Equal(1, mine.Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be read")));
    }

    [Fact]
    public async Task SetupReReadLog_NamesTheSetups()
    {
        Client mine = NewClient();
        NewLairs().Save(NewSetup("Crypt"));
        await Settle(mine);

        Assert.Contains(Messages(mine), m => m == "lair setups re-read after a change on disk: 1 added, 0 changed, 0 removed (added: 'Crypt')");
    }

    [Fact]
    public async Task DotNamedSetupAndFolder_StayInTheCatalogue()
    {
        LairManager first = NewLairs();
        first.Save(NewSetup(".hidden"));
        first.Save(new LairSetup("Archived", new[] { new LairMarker(1, 3) }) { Folder = ".archive" });
        Client mine = NewClient();
        int raised = mine.LairEvents;

        await Beat(mine, 5);
        Assert.Equal(raised, mine.LairEvents);

        NewLairs().Save(NewSetup("Trigger"));
        await Settle(mine);

        Assert.Equal(3, mine.Lairs.Setups.Count);
        Assert.NotNull(mine.Lairs.Get(".hidden"));
    }
}
