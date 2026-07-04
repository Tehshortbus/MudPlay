using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.E follow-up — <see cref="AutoDepositManager"/> reroute. A wealth /
/// coin gate crossing (signalled by <see cref="CashManager.AutoDepositRequested"/>)
/// while a Loop or Auto-Lair is running detours to the configured bank /
/// stash room, offloads the excess coin (<c>dep</c> for a bank;
/// per-currency <c>hide</c> via <see cref="StashRoomManager.ExecuteStash"/>
/// for a stash), walks back to where it left off, and restarts the captured
/// engine. A bare walk-to (or an idle stack) never reroutes. A stash room
/// that sits on the active route (a resolved loop circuit room, or a marked
/// Auto-Lair room) spends no detour — the manager stashes it on pass-through
/// as the engine walks through; only an off-route stash room detours. A
/// manual walk-through with no engine running never stashes.
///
/// Headless: the gate is driven by mutating the snapshot + settings and
/// calling <see cref="CashManager.OnInventoryChanged"/>; the walker is
/// advanced room-by-room via <see cref="RoomTracker.NoteRoomObserved"/>
/// (the same idiom AutoWalkManagerTests uses).
/// </summary>
public sealed class AutoDepositManagerTests : IDisposable
{
    private readonly string _root;

    public AutoDepositManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-autodeposit-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ──N── 1/2 ──N── 1/3 ; 1/3 carries a lair tag so the timer
    // store resolves a respawn against it. 1/1 + 1/3 are the two Auto-Lair
    // markers; 1/1 doubles as the start position (and loop / walk origin).
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
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [ { "GroupIndex": "1-1-1", "AvgDelay": 5 } ]
        """;

    private sealed class Harness : IDisposable
    {
        public required ProfileService Profile { get; init; }
        public required CashManager Cash { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required StashRoomManager Stash { get; init; }
        public required AutoDepositManager AutoDeposit { get; init; }

        // The shared mutable source of truth: the manager closures read these
        // two members directly, so a test body just assigns them and fires
        // the inventory-changed re-check.
        public CashSettings Settings { get; } = new();
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        public List<byte[]> Deposited { get; } = new();
        public List<byte[]> Stashed { get; } = new();
        public List<long> DepositedValues { get; } = new();

        public IEnumerable<string> DepositLines() =>
            Deposited.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public IEnumerable<string> StashLines() =>
            Stashed.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        /// <summary>Mutate holdings + fire the inventory-changed re-check
        /// that arms the auto-deposit gate.</summary>
        public void SetWealth(int copper, int silver, int gold, int platinum, int runic, long totalCopperValue)
        {
            Snapshot = new InventorySnapshot(
                new CurrencyHoldings(copper, silver, gold, platinum, runic, totalCopperValue),
                EncumbranceReading.Empty,
                Array.Empty<EquippedItem>(),
                Array.Empty<string>(),
                DateTimeOffset.UtcNow);
            Cash.OnInventoryChanged();
        }

        public void Dispose()
        {
            AutoDeposit.Dispose();
            Stash.Dispose();
            Lair.Dispose();
            Timers.Dispose();
            Cash.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), LairsJson);

        ProfileService profile = new();
        profile.LoadBlank();

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LoopRunner loop = new(tracker, coord, graph: graph, bfs: bfs, walker: walker);
        loop.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        // The Cash + AutoDeposit closures read the harness's exposed Settings
        // instance and Snapshot field directly. Capture the harness by
        // reference now; it's assigned just below, before any closure runs.
        Harness h = null!;
        CashManager cash = new(router,
            readSettings: () => h.Settings,
            isEnabled: () => true,
            getSnapshot: () => h.Snapshot);
        StashRoomManager stash = new(profile,
            readCash: () => h.Settings,
            getSnapshot: () => h.Snapshot,
            resolveAutoStashItem: _ => null,
            isEnabled: () => true);
        AutoDepositManager autoDeposit = new(cash,
            readCash: () => h.Settings,
            getSnapshot: () => h.Snapshot,
            profile: profile,
            tracker: tracker,
            walker: walker,
            loopRunner: loop,
            autoLair: lair,
            stash: stash);

        h = new Harness
        {
            Profile = profile,
            Cash = cash,
            Tracker = tracker,
            Walker = walker,
            Loop = loop,
            Lair = lair,
            Timers = timers,
            Stash = stash,
            AutoDeposit = autoDeposit,
        };
        autoDeposit.SetWireSender(b => h.Deposited.Add(b));
        autoDeposit.Deposited += v => h.DepositedValues.Add(v);
        stash.SetWireSender(b => h.Stashed.Add(b));
        return h;
    }

    // ----- helpers ---------------------------------------------------

    private static RoomObservation Obs(RoomKey room) => room switch
    {
        { Map: 1, Room: 1 } => new RoomObservation("A", new HashSet<Direction> { Direction.N }),
        { Map: 1, Room: 2 } => new RoomObservation("B", new HashSet<Direction> { Direction.N, Direction.S }),
        { Map: 1, Room: 3 } => new RoomObservation("C", new HashSet<Direction> { Direction.S }),
        _ => throw new ArgumentOutOfRangeException(nameof(room)),
    };

    private static void Arrive(Harness h, RoomKey room) => h.Tracker.NoteRoomObserved(Obs(room));

    /// <summary>Mark 1/1 + 1/3, locate at 1/1, start Auto-Lair — the
    /// resumable "running engine" for these tests.</summary>
    private static void StartLair(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        Assert.True(h.Lair.IsActive);
    }

    /// <summary>Locate at 1/1, start a [1/1, 1/3] loop — the line graph
    /// expands to the circuit 1/1 → 1/2 → 1/3 → 1/2 → 1/1, so every room
    /// (1/1, 1/2, 1/3) is a guaranteed per-lap visit.</summary>
    private static void StartLoop(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 1), new RoomKey(1, 3) })));
        Assert.Equal(LoopState.Running, h.Loop.State);
    }

    // Drive a wealth-gate crossing: a bank at 1/3, wealth above the
    // configured ceiling. Returns the harness already at 1/1 with Auto-Lair
    // running so a test can assert the reroute.
    private static void ArmBankGate(Harness h, string bankRoom = "1/3", long wealthCeiling = 1000)
    {
        h.Settings.BankRoomKey = bankRoom;
        h.Settings.AutoDepositIfWealthExceeds = wealthCeiling;
    }

    [Fact]
    public void GateCross_WhileLairing_StopsEngineAndWalksToBank()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // Engine captured + stopped; walker now en route to the bank (1/3).
        Assert.False(h.Lair.IsActive);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void GateCross_WhileIdle_DoesNotReroute()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        ArmBankGate(h);

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // No loop / lair to resume — nothing reroutes.
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void GateCross_WhilePlainWalkTo_DoesNotReroute()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        ArmBankGate(h);

        // A bare walk-to is not a resume target — it has its own single
        // destination and no resume semantics.
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        RoomKey? destBefore = h.Walker.Destination;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // Walker untouched — still heading to its own destination, no detour.
        Assert.Equal(destBefore, h.Walker.Destination);
        Assert.Empty(h.Deposited);
    }

    [Fact]
    public void BankRoundTrip_DepositsExcessAndResumesEngine()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);

        // Walk to the bank: 1/1 → 1/2 → 1/3.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));

        // Arrived → a single `dep <wealth - keep>`; keep floors are 0 here.
        Assert.Single(h.Deposited);
        Assert.Equal("dep 5000", h.DepositLines().Single());
        // ...and the Deposited event carries the same copper value for the
        // Session Stats stashed/deposited tally.
        Assert.Equal(5000L, Assert.Single(h.DepositedValues));

        // Now walking back to the origin (1/1).
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);

        // Walk back: 1/3 → 1/2 → 1/1.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));

        // Engine restarted.
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void Deposit_SubtractsKeepOnHandFloors()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);
        h.Settings.KeepGoldOnHand = 10; // keep 10 gold = 1000 copper on hand

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));

        // 5000 wealth − 1000 keep = 4000 deposited.
        Assert.Single(h.Deposited);
        Assert.Equal("dep 4000", h.DepositLines().Single());
    }

    [Fact]
    public void Deposit_NothingAboveKeep_SendsNoCommandButStillResumes()
    {
        using Harness h = NewHarness();
        StartLair(h);
        // Trip on the coin-count gate (≥1 coin) so the deposit math can still
        // come out ≤ keep at the bank.
        h.Settings.BankRoomKey = "1/3";
        h.Settings.AutoDepositIfCoinsExceed = 1;
        h.Settings.KeepGoldOnHand = 100; // keep 100 gold = 10000 copper

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));

        // 5000 ≤ 10000 keep → nothing to deposit, but the round-trip still
        // walks back and resumes.
        Assert.Empty(h.Deposited);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void StashOffLairRoute_DetoursStashesAndResumes()
    {
        using Harness h = NewHarness();
        StartLair(h);   // marks 1/1 + 1/3 (NOT 1/2)
        // 1/2 is off the marked lair route, so the gate spends a dedicated
        // detour: stop the lair, walk to the stash room, fire one `hide` per
        // held currency above its keep floor, walk back, and resume. No `dep`
        // goes out on the bank channel.
        h.Profile.Current!.StashRooms = new List<RoomRef> { new RoomRef(1, 2) };
        h.Settings.BankRoomKey = "1/2";
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.False(h.Lair.IsActive);                       // detour stopped it
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);

        // Walk to the stash room (1/1 → 1/2) → hide fires on arrival.
        Arrive(h, new RoomKey(1, 2));
        Assert.Empty(h.Deposited);
        Assert.Equal("hide 50 gold", Assert.Single(h.StashLines()));

        // Walk back to origin (1/2 → 1/1) → lair resumes.
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 1));
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void GateCross_NoBankConfigured_DoesNotReroute()
    {
        using Harness h = NewHarness();
        StartLair(h);
        // Gate threshold set but no bank/stash location → nothing fires.
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        Assert.True(h.Lair.IsActive);
        Assert.Empty(h.Deposited);
    }

    [Fact]
    public void StashOnLoopCircuit_SkipsDetour_StashesOnPassThrough()
    {
        using Harness h = NewHarness();
        StartLoop(h);
        // Stash 1/2 sits on the loop circuit (1/1 → 1/2 → 1/3 → 1/2 → 1/1),
        // so the gate crossing spends no detour: the loop keeps running and
        // the stash fires when the character walks through 1/2 on its own.
        h.Profile.Current!.StashRooms = new List<RoomRef> { new RoomRef(1, 2) };
        h.Settings.BankRoomKey = "1/2";
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // No detour — the loop was not stopped and nothing stashed yet.
        Assert.Equal(LoopState.Running, h.Loop.State);
        Assert.Empty(h.StashLines());

        // Character laps through the stash room → stash in passing.
        Arrive(h, new RoomKey(1, 2));
        Assert.Equal("hide 50 gold", Assert.Single(h.StashLines()));
        Assert.Equal(LoopState.Running, h.Loop.State);
    }

    [Fact]
    public void StashOnLairMarkedRoom_SkipsDetour_StashesOnPassThrough()
    {
        using Harness h = NewHarness();
        StartLair(h);   // marks 1/1 + 1/3
        // Stash 1/3 is a MARKED Auto-Lair room → on-route, no detour. The
        // lair roams through its marked room and the stash fires in passing.
        h.Profile.Current!.StashRooms = new List<RoomRef> { new RoomRef(1, 3) };
        h.Settings.BankRoomKey = "1/3";
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // No detour — the lair is still active and nothing stashed yet.
        Assert.True(h.Lair.IsActive);
        Assert.Empty(h.StashLines());

        // Lair walks the character through its marked room → stash in passing.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        Assert.Equal("hide 50 gold", Assert.Single(h.StashLines()));
    }

    [Fact]
    public void PassThrough_WhenIdle_DoesNotStash()
    {
        using Harness h = NewHarness();
        // No loop / lair running — a purely manual walk through a stash room
        // must never stash (per user direction).
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Profile.Current!.StashRooms = new List<RoomRef> { new RoomRef(1, 2) };
        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        Arrive(h, new RoomKey(1, 2));

        Assert.Empty(h.StashLines());
    }
}
