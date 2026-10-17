using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

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
        _root = Path.Combine(Path.GetTempPath(), "mudplay-autodeposit-tests-" + Path.GetRandomFileName());
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

    // Dark-route fixture for the light detour: origin 1/1 (lit) ─N─ 1/2 (very
    // dark, Light -200) ─N─ 1/3 (bank, lit); a light shop hangs off the origin at
    // 1/4 (1/1 ─E─ 1/4). The bank→origin return leg crosses the dark 1/2, so a
    // character carrying no covering light must detour through the shop.
    private const string DarkGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Origin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dark",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Bank",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Shop",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required ProfileService Profile { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required CashManager Cash { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required StashRoomManager Stash { get; init; }
        public required Game.Light.AutoLightProvisioner Provisioner { get; init; }
        public required Game.Light.AutoLightShopRouter LightShop { get; init; }
        public required AutoDepositManager AutoDeposit { get; init; }

        // The shared mutable source of truth: the manager closures read these
        // two members directly, so a test body just assigns them and fires
        // the inventory-changed re-check.
        public CashSettings Settings { get; } = new();
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;

        // Light-detour knobs read by the provisioner / shop-router closures. Off
        // by default so the coin-only tests never take a light detour; a light
        // test flips AutoLightEnabled and stocks the catalogue + shop.
        public bool AutoLightEnabled { get; set; }
        public int WornIllu { get; set; }
        public Models.Profile.AutoLightSettings LightSettings { get; } = new();
        public List<Game.Light.LightItem> Catalogue { get; } = new();
        public List<RoomKey> LightShopRooms { get; } = new();
        public List<byte[]> Bought { get; } = new();

        public IEnumerable<string> BuyLines() =>
            Bought.Where(b => Encoding.Latin1.GetString(b).TrimEnd('\r').StartsWith("buy "))
                  .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        // Count carried copies of an item id by matching the catalogue name in the
        // snapshot's carried tokens (stacks strip their leading count).
        public int CountCarried(int itemId)
        {
            string? name = Catalogue.FirstOrDefault(l => l.Number == itemId).Name;
            if (string.IsNullOrEmpty(name)) return 0;
            int count = 0;
            foreach (string token in Snapshot.CarriedItems)
            {
                string t = token.Trim();
                int i = 0;
                while (i < t.Length && char.IsDigit(t[i])) i++;
                string bare = i > 0 && i < t.Length && t[i] == ' ' ? t[(i + 1)..].TrimStart() : t;
                if (string.Equals(bare, name, StringComparison.OrdinalIgnoreCase)) count++;
            }
            return count;
        }
        // Rooms the deposit engine treats as real banks (Shops ShopType == 7).
        // The line-graph fixture ships no Shops.json, so a bank test registers
        // its destination here to stand in for the game-data lookup.
        public HashSet<RoomKey> Banks { get; } = new();
        public List<byte[]> Deposited { get; } = new();
        public List<byte[]> Stashed { get; } = new();

        // Every command on the deposit wire — the bank path now sends `i` (the
        // pre-deposit re-read) before its `dep`, so this carries both.
        public IEnumerable<string> SentLines() =>
            Deposited.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        // Just the `dep <value>` commands — filters out the `i` re-read the bank
        // path fires on arrival.
        public IEnumerable<string> DepositLines() =>
            SentLines().Where(s => s.StartsWith("dep ", StringComparison.Ordinal));

        public IEnumerable<string> StashLines() =>
            Stashed.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        /// <summary>Mutate holdings + fire the inventory-changed re-check
        /// that arms the auto-deposit gate.</summary>
        public void SetWealth(int copper, int silver, int gold, int platinum, int runic, long totalCopperValue)
        {
            SetSnapshotOnly(copper, silver, gold, platinum, runic, totalCopperValue);
            Cash.OnInventoryChanged();
        }

        /// <summary>Set holdings WITHOUT re-firing the Cash gate — models the
        /// fresh `i` dump that lands at the bank (e.g. a lower figure after an
        /// unobserved en-route toll).</summary>
        public void SetSnapshotOnly(int copper, int silver, int gold, int platinum, int runic, long totalCopperValue)
        {
            Snapshot = new InventorySnapshot(
                new CurrencyHoldings(copper, silver, gold, platinum, runic, totalCopperValue),
                EncumbranceReading.Empty,
                Array.Empty<EquippedItem>(),
                Array.Empty<string>(),
                DateTimeOffset.UtcNow);
        }

        // Copper values the manager pushed to the on-hand tracker at `dep`
        // dispatch. Models InventoryManager.NoteAutoDeposit: record the amount and
        // decrement the snapshot immediately (greedy re-decompose, same as
        // ApplyTransaction) so the manager's own getSnapshot reads post-deposit
        // wealth the instant it plans the return leg.
        public List<long> NotedDeposits { get; } = new();

        public void NoteAutoDeposit(long copper)
        {
            if (copper <= 0) return;
            NotedDeposits.Add(copper);
            long wealth = Math.Max(0, Snapshot.Currency.TotalCopperValue - copper);
            long total = wealth;
            int runic = (int)(wealth / 1_000_000L); wealth %= 1_000_000L;
            int plat = (int)(wealth / 10_000L); wealth %= 10_000L;
            int gold = (int)(wealth / 100L); wealth %= 100L;
            int silver = (int)(wealth / 10L); wealth %= 10L;
            int cop = (int)wealth;
            Snapshot = new InventorySnapshot(
                new CurrencyHoldings(cop, silver, gold, plat, runic, total),
                Snapshot.Encumbrance, Snapshot.EquippedItems, Snapshot.CarriedItems,
                DateTimeOffset.UtcNow);
        }

        public void Dispose()
        {
            AutoDeposit.Dispose();
            LightShop.Dispose();
            Stash.Dispose();
            Lair.Dispose();
            Timers.Dispose();
            Cash.Dispose();
        }
    }

    private Harness NewHarness(string? graphJson = null, bool nearestLoopRoom = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), graphJson ?? GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), LairsJson);

        ProfileService profile = new();
        profile.LoadDefaultProfile();

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
            collectAfterCombatFinished: () => h.Settings.CollectAfterCombatFinished,
            hasEngageableHostiles: () => false,
            getSnapshot: () => h.Snapshot);
        StashRoomManager stash = new(profile,
            readCash: () => h.Settings,
            getSnapshot: () => h.Snapshot,
            resolveAutoStashItem: _ => null,
            isEnabled: () => true);
        Game.Light.AutoLightProvisioner provisioner = new(
            isEnabled: () => h.AutoLightEnabled,
            snapshot: () => h.Snapshot,
            catalogue: () => h.Catalogue,
            resolveRoom: graph.GetRoom,
            wornIllu: () => h.WornIllu,
            roomLightSpell: () => null,
            castRoomLightSpell: _ => false,
            roomLightSpellUp: _ => false,
            settings: () => h.LightSettings);
        Game.Light.AutoLightShopRouter lightShop = new(
            shopRoomsSellingItem: _ => h.LightShopRooms,
            currentRoom: () => tracker.State.CurrentRoom?.Key,
            walkDestination: () => walker.Destination,
            distanceBetween: (a, b) => bfs.DistanceBetween(a, b),
            carriedCount: id => h.CountCarried(id),
            isEnabled: () => h.AutoLightEnabled,
            engineWalkActive: () => false,
            walkTo: k => walker.WalkTo(k),
            post: action => action());
        AutoDepositManager autoDeposit = new(cash,
            readCash: () => h.Settings,
            getSnapshot: () => h.Snapshot,
            noteAutoDeposit: v => h.NoteAutoDeposit(v),
            isBankRoom: k => h.Banks.Contains(k),
            profile: profile,
            tracker: tracker,
            walker: walker,
            loopRunner: loop,
            autoLair: lair,
            stash: stash,
            provisioner: provisioner,
            lightShop: lightShop,
            carriedCount: id => h.CountCarried(id),
            post: action => action(),
            nearestLoopRoom: !nearestLoopRoom ? null : (from, l) =>
            {
                IReadOnlyList<RoomKey> rooms = LoopExpander.ResolveCycleRoomKeys(l.Waypoints, bfs, graph);
                IReadOnlyDictionary<RoomKey, int> steps = bfs.ComputeDistancesTo(from, rooms);
                return rooms.Where(steps.ContainsKey).OrderBy(r => steps[r]).Cast<RoomKey?>().FirstOrDefault();
            });
        // Production drives OnRoomEntered from AppServices' early StateChanged
        // handler (registered ahead of LoopRunner) rather than a self-subscription;
        // mirror that here so Arrive() still triggers the pass-through stash.
        tracker.StateChanged += autoDeposit.OnRoomEntered;

        h = new Harness
        {
            Profile = profile,
            Graph = graph,
            Cash = cash,
            Tracker = tracker,
            Walker = walker,
            Loop = loop,
            Lair = lair,
            Timers = timers,
            Stash = stash,
            Provisioner = provisioner,
            LightShop = lightShop,
            AutoDeposit = autoDeposit,
        };
        // Split the manager's single wire by command so `dep` assertions stay
        // isolated from `buy`s the light detour issues on the same wire.
        autoDeposit.SetWireSender(b =>
        {
            if (Encoding.Latin1.GetString(b).StartsWith("buy ")) h.Bought.Add(b);
            else h.Deposited.Add(b);
        });
        stash.SetWireSender(b => h.Stashed.Add(b));
        return h;
    }

    // ----- helpers ---------------------------------------------------

    // Build the observation from the loaded graph so the same helper drives any
    // fixture (the coin graph and the dark-route graph below).
    private static void Arrive(Harness h, RoomKey room)
    {
        Room r = h.Graph.GetRoom(room) ?? throw new ArgumentOutOfRangeException(nameof(room));
        h.Tracker.NoteRoomObserved(new RoomObservation(r.Name, new HashSet<Direction>(r.Exits.Keys)));
    }

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
        if (RoomKey.TryParseWire(bankRoom, out RoomKey key)) h.Banks.Add(key);
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

    // A sale near the bank goes first: the crossing is offered to the sell detour, and
    // the bank run starts when the detour hands it back from the shop (user, 2026-10-05).
    [Fact]
    public void GateCross_SaleNearTheBankGoesFirst_BankRunStartsAtTheHandOver()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);
        (DetourResumeKind Kind, RoomKey Bank)? offered = null;
        h.AutoDeposit.SellFirst = (resume, bank) => { offered = (resume.Kind, bank); return true; };

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        Assert.Equal((DetourResumeKind.Lair, new RoomKey(1, 3)), offered);
        Assert.False(h.AutoDeposit.IsRerouting);
        Assert.True(h.Lair.IsActive);                    // the sell detour stops it, not the bank run

        h.Lair.Stop("sell detour");
        Assert.True(h.AutoDeposit.TakeOverFromDetour(new DetourResume(DetourResumeKind.Lair), new RoomKey(1, 1)));
        Assert.True(h.AutoDeposit.IsRerouting);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void GateCross_ToAStashRoom_IsNotOfferedToASale()
    {
        using Harness h = NewHarness();
        StartLair(h);
        h.Profile.Current!.StashRooms = new List<RoomRef> { new RoomRef(1, 2) };
        h.Settings.BankRoomKey = "1/2";
        h.Settings.AutoDepositIfWealthExceeds = 1000;
        bool offered = false;
        h.AutoDeposit.SellFirst = (_, _) => offered = true;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        Assert.False(offered);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
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

        // On arrival the manager re-reads holdings (`i`) and defers the `dep`
        // until the fresh dump lands — no deposit yet.
        Assert.Empty(h.DepositLines());
        DeliverBankInventory(h);

        // Now a single `dep <wealth - keep>`; keep floors are 0 here.
        Assert.Equal("dep 5000", Assert.Single(h.DepositLines()));

        // Now walking back to the origin (1/1).
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);

        // Walk back: 1/3 → 1/2 → 1/1.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));

        // Engine restarted.
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void Deposit_DecrementsOnHandTracker_BeforeReturnLeg()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        // The dep fired AND the on-hand tracker was decremented by the same value
        // the moment the command went out — so the return leg (already destined
        // for the origin) planned against post-deposit wealth, not the stale
        // pre-deposit purse that would have routed through an unaffordable toll.
        Assert.Equal("dep 5000", Assert.Single(h.DepositLines()));
        Assert.Equal(5000L, Assert.Single(h.NotedDeposits));
        Assert.Equal(0L, h.Snapshot.Currency.TotalCopperValue);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
    }

    [Fact]
    public void Deposit_NothingAboveKeep_DoesNotTouchTracker()
    {
        using Harness h = NewHarness();
        StartLair(h);
        h.Settings.BankRoomKey = "1/3";
        h.Banks.Add(new RoomKey(1, 3));
        h.Settings.AutoDepositIfCoinsExceed = 1;
        h.Settings.KeepOnHandWealth = 10_000; // keep 100 gold worth of copper

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        // Nothing deposited (5000 ≤ 10000 keep) → the tracker is left alone; no
        // spurious decrement can drop the return-leg wallet below a real toll.
        Assert.Empty(h.DepositLines());
        Assert.Empty(h.NotedDeposits);
        Assert.Equal(5000L, h.Snapshot.Currency.TotalCopperValue);
    }

    [Fact]
    public void BankArrival_ReReadsInventory_DepositsPostTollAmount()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);

        // Gate arms on the pre-toll figure the loop last saw.
        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        // A toll silently drains 500 copper en route — no pattern observes it,
        // so the manager's snapshot still reads 5000 when it reaches the bank.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));

        // On arrival it re-reads holdings and holds the deposit until the dump.
        Assert.Contains("i", h.SentLines());
        Assert.Empty(h.DepositLines());

        // The fresh `i` reflects the post-toll balance; deposit tracks it, not
        // the stale 5000 (a `dep 5000` would be rejected outright, banking nothing).
        h.SetSnapshotOnly(copper: 0, silver: 0, gold: 45, platinum: 0, runic: 0, totalCopperValue: 4500);
        DeliverBankInventory(h);
        Assert.Equal("dep 4500", Assert.Single(h.DepositLines()));
    }

    [Fact]
    public void Deposit_SubtractsKeepOnHandFloor()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);
        h.Settings.KeepOnHandWealth = 1_000; // keep 10 gold worth of copper

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        // 5000 wealth − 1000 keep = 4000 deposited.
        Assert.Equal("dep 4000", Assert.Single(h.DepositLines()));
    }

    [Fact]
    public void Deposit_NothingAboveKeep_SendsNoCommandButStillResumes()
    {
        using Harness h = NewHarness();
        StartLair(h);
        // Trip on the coin-count gate (≥1 coin) so the deposit math can still
        // come out ≤ keep at the bank.
        h.Settings.BankRoomKey = "1/3";
        h.Banks.Add(new RoomKey(1, 3));
        h.Settings.AutoDepositIfCoinsExceed = 1;
        h.Settings.KeepOnHandWealth = 10_000; // keep 100 gold worth of copper

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        // 5000 ≤ 10000 keep → nothing to deposit, but the round-trip still
        // walks back and resumes.
        Assert.Empty(h.DepositLines());
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
        Assert.Equal("hide 50 gold crown", Assert.Single(h.StashLines()));

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
    public void GateCross_OrphanedBankKey_DoesNotReroute()
    {
        using Harness h = NewHarness();
        StartLair(h);
        // A stale / orphaned BankRoomKey: a parseable room that is neither a
        // real bank (never registered in h.Banks) nor a marked stash (1/2 is
        // also not a marked lair room, so the lair engine would never Stop() for
        // it on its own). The Settings picker would show its placeholder, but
        // the raw key survives on disk. The engine must no-op — the lair keeps
        // running (never stopped for a detour), so no walk to a phantom bank and
        // crucially no toll @wealth probe en route.
        h.Settings.BankRoomKey = "1/2";
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);

        Assert.True(h.Lair.IsActive);
        Assert.Empty(h.Deposited);
    }

    // An aborted reroute (here an orphaned bank key that resolves to no valid
    // destination) must re-arm the CashManager gate so a later inventory change
    // retries — the regression was that the abort left the single-fire guard
    // latched and auto-deposit never fired again for the session. The retry is
    // held for a cooldown so a persistently-unreachable bank can't thrash the
    // engine; advancing the clock past it lets the retry through.
    [Fact]
    public void AbortedReroute_ReArmsGate_RetriesAfterCooldown()
    {
        using Harness h = NewHarness();
        StartLair(h);

        int fires = 0;
        h.Cash.AutoDepositRequested += _ => fires++;

        DateTime now = DateTime.UtcNow;
        h.Cash.AutoDepositClock = () => now;

        // Parseable key that is neither a bank nor a marked stash → aborts.
        h.Settings.BankRoomKey = "1/2";
        h.Settings.AutoDepositIfWealthExceeds = 1000;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(1, fires);
        Assert.True(h.Lair.IsActive);   // never stopped for the phantom bank

        // Within the cooldown — no re-fire on the next inventory line.
        h.SetWealth(copper: 0, silver: 0, gold: 51, platinum: 0, runic: 0, totalCopperValue: 5100);
        Assert.Equal(1, fires);

        // Past the cooldown — the gate retries.
        now = now.AddMinutes(2);
        h.SetWealth(copper: 0, silver: 0, gold: 52, platinum: 0, runic: 0, totalCopperValue: 5200);
        Assert.Equal(2, fires);
    }

    [Fact]
    public void RerouteCompletesWithoutDepositing_ReArmsGate_NotWedged()
    {
        // A reroute that reaches the bank but banks nothing (everything is under
        // the KeepOnHand floor) still leaves the gate above threshold. The
        // single-fire guard must re-arm on the return leg so a later crossing
        // retries — otherwise it wedges for the session and the character loops
        // forever, full and never depositing (report paradigm-20260730-125716).
        using Harness h = NewHarness();
        StartLair(h);
        h.Settings.BankRoomKey = "1/3";
        h.Banks.Add(new RoomKey(1, 3));
        h.Settings.AutoDepositIfCoinsExceed = 1;
        h.Settings.KeepOnHandWealth = 10_000;      // nothing lands above the floor

        DateTime now = DateTime.UtcNow;
        h.Cash.AutoDepositClock = () => now;
        int fires = 0;
        h.Cash.AutoDepositRequested += _ => fires++;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(1, fires);

        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);
        Assert.Empty(h.DepositLines());            // nothing above keep → no dep

        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));              // walked back
        Assert.True(h.Lair.IsActive);              // engine resumed
        Assert.False(h.AutoDeposit.IsRerouting);

        // Still over the gate. Past the retry cooldown the RE-ARMED guard fires
        // again rather than staying wedged from the first crossing.
        now = now.AddMinutes(2);
        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(2, fires);
    }

    // Report #5 / #1: a manual Stop mid-reroute must tear the reroute down so a
    // later walker Finished (from whatever the user starts next) can't fire
    // Resume() and resurrect the pre-detour engine — and it must re-arm the
    // CashManager gate so the single-fire guard isn't left wedged for the
    // session. The walker's Stopped event is the abort signal.
    [Fact]
    public void UserStopMidReroute_TearsDownReroute_AndReArmsGate()
    {
        using Harness h = NewHarness();
        StartLair(h);
        ArmBankGate(h);

        DateTime now = DateTime.UtcNow;
        h.Cash.AutoDepositClock = () => now;
        int fires = 0;
        h.Cash.AutoDepositRequested += _ => fires++;

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(1, fires);
        Assert.True(h.AutoDeposit.IsRerouting);
        Assert.False(h.Lair.IsActive);                 // lair stopped for the detour
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // User hits Stop mid-detour — the walker halts, raising Stopped.
        h.Walker.Stop("user stop from Navigation");
        Assert.False(h.AutoDeposit.IsRerouting);        // reroute torn down

        // A fresh walk the user starts must run unimpeded: its Finished no longer
        // triggers the dead reroute's Resume(), so the pre-detour lair stays down.
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        Assert.False(h.Lair.IsActive);

        // The stop re-armed the gate (not wedged): past the retry cooldown a
        // fresh crossing fires again instead of staying latched.
        now = now.AddMinutes(2);
        h.SetWealth(copper: 0, silver: 0, gold: 52, platinum: 0, runic: 0, totalCopperValue: 5200);
        Assert.Equal(2, fires);
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
        Assert.Equal("hide 50 gold crown", Assert.Single(h.StashLines()));
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
        Assert.Equal("hide 50 gold crown", Assert.Single(h.StashLines()));
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

    // Enable the light engine and stock a torch (Strength 100 covers the -200 dark
    // room from a 0 worn baseline; ~6 h burn so a single copy meets the carry
    // target). The shop that sells it sits at 1/4.
    private static void EnableLightDetour(Harness h)
    {
        h.AutoLightEnabled = true;
        h.WornIllu = 0;
        h.Catalogue.Add(new Game.Light.LightItem(100, "torch", 100, 7200));
        h.LightShopRooms.Add(new RoomKey(1, 4));
    }

    // Commit the deferred bank deposit: on arrival the manager sends `i` and waits
    // for inventory to re-sync before it fires `dep`. Stands in for
    // InventoryManager.Changed firing after the fresh dump lands.
    private static void DeliverBankInventory(Harness h) => h.AutoDeposit.OnInventoryChanged();

    // Simulate a bought copy landing in the pack and fire the inventory re-check
    // the manager listens on for its buy phase.
    private static void GiveTorch(Harness h)
    {
        h.Snapshot = new InventorySnapshot(
            h.Snapshot.Currency, EncumbranceReading.Empty,
            Array.Empty<EquippedItem>(), new[] { "torch" }, DateTimeOffset.UtcNow);
        h.AutoDeposit.OnInventoryChanged();
    }

    // A loop picks up at its room nearest the bank (1/2), not back where the trip
    // began (1/1) — the same as a sell detour (user, 2026-09-30).
    [Fact]
    public void LoopTrip_WalksBackToTheLoopsNearestRoom_AndResumes()
    {
        using Harness h = NewHarness(nearestLoopRoom: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 1), new RoomKey(1, 2) })));
        ArmBankGate(h);               // bank at 1/3, off the loop

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);
        Assert.Equal("dep 5000", h.DepositLines().Single());

        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 2));
        Assert.Equal(LoopState.Running, h.Loop.State);
    }

    [Fact]
    public void BankReturnLegRunsDark_DetoursToLightShopThenResumes()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);                 // loop [1/1, 1/3], resumable engine
        ArmBankGate(h);               // bank at 1/3
        EnableLightDetour(h);

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);

        // Walk to the bank (1/1 → 1/2 → 1/3) → re-read, then deposit fires.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);
        Assert.Equal("dep 5000", h.DepositLines().Single());

        // Return leg (1/3 → 1/2 → 1/1) runs dark and nothing is carried → detour
        // to the light shop at 1/4 instead of straight home.
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);

        // Walk to the shop (1/3 → 1/2 → 1/1 → 1/4) → buy fires on arrival.
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));
        Arrive(h, new RoomKey(1, 4));
        Assert.Equal("buy torch", h.BuyLines().Single());

        // The bought torch lands → resume the walk home (shop 1/4 → origin 1/1).
        GiveTorch(h);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);

        Arrive(h, new RoomKey(1, 1));
        Assert.Equal(LoopState.Running, h.Loop.State);   // engine resumed
    }

    // The dark-route fixture with a second light shop in the bank's town, two rooms
    // east of the bank (1/3 ─E─ 1/5 ─E─ 1/6). The shop by the origin (1/4) adds
    // fewer steps to the way home, but it lies past the dark room.
    private const string TownShopGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Origin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dark",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Bank",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Shop",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Street",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Town Shop",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A bank trip buys its light in town before the walk back sets out (user,
    // 2026-10-17): the shop nearest the bank, not the one by the origin that adds
    // fewer steps but is reached only through the dark.
    [Fact]
    public void BankReturnLegRunsDark_BuysInTheBanksTownBeforeTheWalkBack()
    {
        using Harness h = NewHarness(TownShopGraphJson);
        StartLoop(h);
        ArmBankGate(h);
        EnableLightDetour(h);
        h.LightShopRooms.Add(new RoomKey(1, 6));

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);
        Assert.Equal("dep 5000", h.DepositLines().Single());

        Assert.Equal(new RoomKey(1, 6), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 5));
        Arrive(h, new RoomKey(1, 6));
        Assert.Equal("buy torch", h.BuyLines().Single());

        GiveTorch(h);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
    }

    [Fact]
    public void BankReturnLegRunsDark_AutoLightOff_NoDetour()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        ArmBankGate(h);
        EnableLightDetour(h);
        h.AutoLightEnabled = false;   // master toggle off gates the whole detour

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);
        Assert.Equal("dep 5000", h.DepositLines().Single());

        // No light detour — straight back to origin, nothing bought.
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Assert.Empty(h.BuyLines());

        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));
        Assert.Equal(LoopState.Running, h.Loop.State);
    }

    [Fact]
    public void BankReturnLegRunsDark_NoStockingShop_ReturnsWithoutLight()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        ArmBankGate(h);
        EnableLightDetour(h);
        h.LightShopRooms.Clear();     // dark leg, but nothing sells the light

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        // Buy verdict but no reachable shop → head home without light, don't stall.
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Assert.Empty(h.BuyLines());
    }

    // The return leg is covered by the torch carried, but one torch (6 h) is short of
    // a 12 h Carry-hours target and the shop off the origin adds 2 steps to the way
    // back: the bank run stops there for one more torch.
    [Fact]
    public void BankReturnLegCovered_ShortOfCarryHours_StocksUpAtTheNearbyShop()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        ArmBankGate(h);
        EnableLightDetour(h);
        h.LightSettings.CarryHours = 12;
        h.LightSettings.PreferredLightName = "torch";

        h.SetWealth(copper: 0, silver: 0, gold: 50, platinum: 0, runic: 0, totalCopperValue: 5000);
        h.Snapshot = h.Snapshot with { CarriedItems = new[] { "torch" } };
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 3));
        DeliverBankInventory(h);

        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 2));
        Arrive(h, new RoomKey(1, 1));
        Arrive(h, new RoomKey(1, 4));
        Assert.Equal("buy torch", h.BuyLines().Single());

        h.Snapshot = h.Snapshot with { CarriedItems = new[] { "torch", "torch" }, LastUpdated = DateTimeOffset.UtcNow };
        h.AutoDeposit.OnInventoryChanged();
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 1));
        Assert.Equal(LoopState.Running, h.Loop.State);
    }

    // A loop's lit torch dwindles under Reorder-at (50 minutes left of a 60-minute
    // threshold): the reorder the `i` dump raises is a trip of its own off the loop —
    // stopped, to the shop, one torch bought, back, the loop resumed.
    [Fact]
    public void LoopReorder_RestockTrip_WalksToTheShopBuysAndResumesTheLoop()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        EnableLightDetour(h);
        h.Provisioner.SetLoopRestock(
            engineRunning: () => h.Loop.State != LoopState.Idle,
            restock: buy => h.AutoDeposit.StartLightRestock(buy, maxAddedSteps: null, "reorder"));

        h.Snapshot = h.Snapshot with { ReadiedLight = new ReadiedLight("torch", 100), LastUpdated = DateTimeOffset.UtcNow };
        h.Provisioner.OnInventoryChanged();

        Assert.True(h.AutoDeposit.IsLightTrip);
        Assert.Equal(LoopState.Idle, h.Loop.State);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
        Assert.Contains("light restock trip", h.AutoDeposit.RerouteStatus);

        Arrive(h, new RoomKey(1, 4));
        Assert.Equal("buy torch", h.BuyLines().Single());
        GiveTorch(h);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 1));
        Assert.Equal(LoopState.Running, h.Loop.State);
        Assert.False(h.AutoDeposit.IsRerouting);
    }

    // A loop whose circuit crosses the dark 1/2 with no light carried: Auto-Light
    // stops it for a light trip — to the shop at 1/4, a torch bought, back to 1/1,
    // the loop resumed (user, 2026-10-17) — and asks no second time on the resumed
    // loop once the torch is carried.
    [Fact]
    public void LoopCircuitRunsDark_NoLight_LightTripThenTheLoopResumes()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        EnableLightDetour(h);
        h.Provisioner.SetLoopRestock(
            engineRunning: () => h.Loop.State != LoopState.Idle,
            restock: buy => h.AutoDeposit.StartLightRestock(buy, maxAddedSteps: null, "light for the run"),
            engineRooms: h.Loop.ResolveLoopRoomKeys);

        h.Provisioner.OnEngineStarted(userStarted: false);

        Assert.True(h.AutoDeposit.IsLightTrip);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
        Arrive(h, new RoomKey(1, 4));
        Assert.Equal("buy torch", h.BuyLines().Single());
        GiveTorch(h);
        Arrive(h, new RoomKey(1, 1));
        Assert.Equal(LoopState.Running, h.Loop.State);

        h.Provisioner.OnEngineStarted(userStarted: false);
        Assert.False(h.AutoDeposit.IsRerouting);
        Assert.Single(h.BuyLines());
    }

    // The loop's Started event says whether the user pressed Start: a light trip's
    // own resume of the loop doesn't, so it can't wipe the trip's back-off.
    [Fact]
    public void LoopStarted_SaysUserStarted_OnlyForTheUsersStart_NotTheTripsResume()
    {
        using Harness h = NewHarness(DarkGraphJson);
        EnableLightDetour(h);
        List<bool> starts = new();
        h.Loop.Event += e => { if (e.Kind == LoopEventKind.Started) starts.Add(e.UserStarted); };
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 1), new RoomKey(1, 3) }), userStarted: true));

        Assert.Equal(LightRestockOutcome.Started, h.AutoDeposit.StartLightRestock(
            new Game.Light.AutoLightBuyRequest(100, "torch", 1), maxAddedSteps: null, "reorder"));
        Arrive(h, new RoomKey(1, 4));
        GiveTorch(h);
        Arrive(h, new RoomKey(1, 1));
        h.Loop.Stop("test");
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 1), new RoomKey(1, 3) })));   // an event's or a task's start

        Assert.Equal(new[] { true, false, false }, starts);
    }

    [Fact]
    public void LightRestockTrip_NoShopSellsTheLight_LoopKeepsRunning()
    {
        using Harness h = NewHarness(DarkGraphJson);
        StartLoop(h);
        EnableLightDetour(h);
        h.LightShopRooms.Clear();

        LightRestockOutcome outcome = h.AutoDeposit.StartLightRestock(
            new Game.Light.AutoLightBuyRequest(100, "torch", 1), maxAddedSteps: null, "reorder");

        Assert.Equal(LightRestockOutcome.NotPossible, outcome);
        Assert.Equal(LoopState.Running, h.Loop.State);
        Assert.False(h.AutoDeposit.IsRerouting);
    }

    [Fact]
    public void LightRestockTrip_NoEngineRunning_NotPossible()
    {
        using Harness h = NewHarness(DarkGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        EnableLightDetour(h);

        Assert.Equal(LightRestockOutcome.NotPossible, h.AutoDeposit.StartLightRestock(
            new Game.Light.AutoLightBuyRequest(100, "torch", 1), maxAddedSteps: null, "reorder"));
    }
}
