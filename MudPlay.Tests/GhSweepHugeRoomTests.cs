using System.Diagnostics;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Conditions;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Roomba in a room with no item cap. A Paradigm vault holds hundreds of stacks and a
// search prints them as one unbroken line; reading that line locked the client for
// seconds per search (report paradigm-20261009-164508). The line goes through the
// real path here — emulator, line extractor, every router pattern, the condition
// tracker's templates, the floor reader, the sweep, the item-location log — on a
// floor several times the reported one.
public sealed class GhSweepHugeRoomTests : IDisposable
{
    // The sweep's settle, pacing and backoff timers, the clock its pacing reads,
    // and the game's prompts as it reads them.
    private readonly ManualTimers _sweepTimers = new();
    private DateTimeOffset _sweepNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly WirePromptScanner _sweepPrompt = new();

    // The pace between queued commands runs out.
    private void PaceOut()
    {
        _sweepNow += GhSweepManager.MinCommandInterval;
        _sweepTimers.RunDue(GhSweepManager.PromptWaitTimeout);
    }

    private static readonly RoomKey Weapons = new(1, 1);
    private static readonly RoomKey Armour = new(1, 2);
    private static readonly RoomKey Vault = new(1, 3);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-ghsweep-huge-" + Path.GetRandomFileName());
    private readonly string _scratchBbs = "ghsweep-huge-" + Path.GetRandomFileName();

    private readonly LogService _log = new();
    private readonly List<string> _sent = new();
    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly RoomTracker _tracker;
    private readonly GhRoomLabelStore _labels;
    private readonly GhSweepManager _sweep;
    private int _clock;
    // The clock the floor reads are timed on: each reading moves it 1 ms, so every
    // stage costs the same on any machine. A vault read (its list split, then its
    // merge) outranks an empty room's (a merge alone) however busy the machine is.
    private long _ticks;
    private long Timestamp() => _ticks += Stopwatch.Frequency / 1000;

    public GhSweepHugeRoomTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), HugeFloor.ItemsJson(HugeFloor.MaxDistinct));
        string seeds = Path.Combine(_root, "seeds");
        AppPaths.ExtractEmbeddedSeeds(seeds);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        _labels = new GhRoomLabelStore(profile);
        _labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        _labels.SetLabel(Weapons, new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        _labels.SetLabel(Armour, new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        _labels.SetSearchesPerRoom(2);
        _labels.SetSearchForHidden(true);

        GhItemLocationStore locations = new(names);
        locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null,
            isRoomName: line => line == "Bronze House Vault", timestamp: Timestamp);

        // The condition tracker reads every line against the realm's message
        // catalogue; its cast-line templates are where the seconds went.
        MessageStore messages = new();
        messages.Messages.ReplaceAll(
            JsonStore.Load<List<MessageRecord>>(Path.Combine(seeds, "Messages.paradigm.seed.json"))
            ?? new List<MessageRecord>());
        ConditionTracker conditions = new(messages);

        LineExtractor lines = new(_emulator);
        lines.LineEmitted += router.Dispatch;
        conditions.AttachLineExtractor(lines);
        ground.AttachLineExtractor(lines);

        _tracker = new RoomTracker(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        _tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(_tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        _sweep = sweep = new GhSweepManager(_labels, runner, _tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false, log: _log,
            isParadigm: () => true, itemLocations: locations, timestamp: Timestamp, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);
        _sweep.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        _tracker.SetLocated(Weapons);
    }

    public void Dispose()
    {
        _sweep.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best effort */ }
    }

    // The game's own bytes: the list unbroken, then the line's end.
    private void Wire(params string[] rows)
    {
        foreach (string row in rows) _emulator.Feed(Encoding.Latin1.GetBytes(row + "\r\n"));
    }

    private void Arrive(string roomName, params Direction[] exits) =>
        _tracker.NoteRoomObserved(new RoomObservation(roomName, new HashSet<Direction>(exits)),
            DateTimeOffset.UtcNow.AddSeconds(++_clock));

    private void SearchSettles() => _sweepTimers.RunDue(GhSweepManager.ReconSearchSettle);

    private List<string> SweepLog() => _log.Snapshot()
        .Where(e => e.Source == GhSweepManager.LogCategory)
        .Select(e => e.Message)
        .ToList();

    // The sort queue as (item, count, from, to, hidden).
    private List<(string Item, int Count, RoomKey From, RoomKey To, bool Hidden)> Queue() =>
        _sweep.PendingMoves.Select(m => (m.ItemName, m.Count, m.From, m.To, m.Hidden)).ToList();

    [Fact]
    public void AVaultOfSixHundredStacks_IsQueuedStackForStack_AndLoggedInOneBoundedLine()
    {
        const int visible = 400, all = 600;
        Assert.True(_sweep.Start());

        // The vault's own display shows 400 stacks; each search finds the 200
        // hidden ones, and answers after its echo with those alone.
        Wire("[HP=611/MA=720]:n", "Bronze House Vault");
        Wire(HugeFloor.Line(visible));
        Arrive("C", Direction.N, Direction.S);
        Assert.Equal("sea", _sent[^1]);
        Wire("[HP=611/MA=720]:sea");
        Wire(HugeFloor.Line(all - visible, first: visible));
        SearchSettles();
        Assert.Equal("sea", _sent[^1]);
        Wire("[HP=611/MA=720]:sea");
        Wire(HugeFloor.Line(all - visible, first: visible));
        SearchSettles();
        Assert.Equal("n", _sent[^1]);

        // The rest of the lap: B, back through the vault (now read from the room
        // display alone), and home.
        Arrive("B", Direction.S);
        SearchSettles();
        SearchSettles();
        Wire("[HP=611/MA=720]:s", "Bronze House Vault");
        Wire(HugeFloor.Line(visible));
        Arrive("C", Direction.N, Direction.S);
        SearchSettles();
        SearchSettles();
        Arrive("A", Direction.N);
        SearchSettles();
        SearchSettles();
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, _sweep.Phase);

        // The plan: every stack once, with its count, bound for the room its type
        // is labelled for, and tagged hidden exactly when only a search showed it.
        List<(string Item, int Count, RoomKey From, RoomKey To, bool Hidden)> expected = new();
        for (int i = 0; i < all; i++)
            expected.Add((HugeFloor.Name(i), HugeFloor.CountOf(i), Vault,
                i % 2 == 0 ? Weapons : Armour, i >= visible));
        Assert.Equal(
            expected.OrderBy(m => m.Item, StringComparer.Ordinal),
            Queue().OrderBy(m => m.Item, StringComparer.Ordinal));

        // One line per visit to the vault, with counts in place of the full list.
        List<string> log = SweepLog();
        Assert.DoesNotContain(log, m => m.Contains("revealed hidden", StringComparison.Ordinal));
        List<string> vaultLines = log.Where(m => m.StartsWith("recon at 1/3:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, vaultLines.Count);
        int shown = Enumerable.Range(0, visible).Sum(HugeFloor.CountOf);
        int hidden = Enumerable.Range(visible, all - visible).Sum(HugeFloor.CountOf);
        Assert.StartsWith(
            $"recon at 1/3: {shown + hidden} item(s) of {all} kind(s) in the room; "
            + $"on display {shown} in {visible} stack(s); searched 2 time(s); "
            + $"hidden {hidden} in {all - visible} stack(s): ", vaultLines[0]);
        Assert.Contains($"(+{all - visible - 20} more)", vaultLines[0]);
        Assert.Contains("; 3 floor read(s), slowest: read ", vaultLines[0]);
        Assert.All(vaultLines, line => Assert.True(line.Length < 800, $"summary line ran to {line.Length} characters"));

        // Whichever of the vault's reads was slowest: the display's 400 stacks or a
        // search's 600.
        GhSurveyTiming slowest = Assert.IsType<GhSurveyTiming>(_sweep.SlowestSurvey);
        Assert.Equal(Vault, slowest.Room);
        Assert.InRange(slowest.Stacks, visible, all);
        Assert.True(slowest.Items > slowest.Stacks);
    }

    // A reply four times as long is four times the work when it is read straight
    // through. What it allocates is counted, not how long it takes: bytes are the
    // same on a busy machine. The bound is one the old row-by-row join fails. That
    // join built a new string and attribute array of everything gathered so far for
    // each wrapped row, about 1,760 bytes per row already held: some 37 MB over the
    // 206 rows of 1,000 stacks and 620 MB over the 841 rows of 4,000, sixteen times
    // as much and far more than the rest of the path allocates.
    [Fact]
    public void ASearchReplyFourTimesAsLong_AllocatesNoMoreThanItsShare()
    {
        Assert.True(_sweep.Start());
        Arrive("C", Direction.N, Direction.S);
        Assert.Equal("sea", _sent[^1]);

        long thousand = AllocatedReading(1000);
        long fourThousand = AllocatedReading(4000);

        Assert.Equal(4000, _sweep.ObservedItemsAt(Vault).Count);
        Assert.True(fourThousand < thousand * 6,
            $"reading 1,000 stacks allocated {thousand:N0} bytes, 4,000 stacks {fourThousand:N0}");
    }

    // The bytes one reply of this many stacks costs once the room already holds
    // them: the first read is left out, since it also grows the room's records.
    private long AllocatedReading(int stacks)
    {
        string reply = HugeFloor.Line(stacks);
        Wire("[HP=611/MA=720]:sea", reply);
        Wire("[HP=611/MA=720]:sea");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Wire(reply);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
