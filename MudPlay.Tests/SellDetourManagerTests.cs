using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// SellDetourManager: a carried item flagged for sell detours, above its detour count,
// turns a walk-to / loop aside to a shop that trades it, lets Auto-sell sell there,
// then carries on (the walk to its destination, the loop back to where it left off).
public sealed class SellDetourManagerTests : IDisposable
{
    private readonly string _root;

    public SellDetourManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-selldetour-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ─N─ 1/2 ─N─ 1/3, with the shop at 1/4 east of 1/1.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Shop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Shop = new(1, 4);
    private const int ShopNumber = 7;

    private sealed class Harness : IDisposable
    {
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required MessageRouter Router { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoSellManager Sell { get; init; }
        public required SellDetourManager Detour { get; init; }
        public List<string> Carried { get; } = new();
        public int? DetourAbove { get; set; } = 0;
        // Stands in for the map's own step count when set (a shop far from the bank).
        public Func<RoomKey, RoomKey, int?>? Distance { get; set; }
        public int NearBankSteps { get; set; } = 25;
        public bool Blocked { get; set; }
        public bool ShopTrades { get; set; } = true;
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // Hold what the detour posts to the UI thread until RunPosted, as the real
        // dispatcher does until the current line's dispatch is over.
        public bool Defer { get; set; }
        public List<Action> Posted { get; } = new();

        public void RunPosted()
        {
            Action[] due = Posted.ToArray();
            Posted.Clear();
            foreach (Action a in due) a();
        }

        public void Arrive(RoomKey room)
        {
            Room r = Graph.GetRoom(room) ?? throw new ArgumentOutOfRangeException(nameof(room));
            // AppServices drives Auto-sell off the room change ahead of the walker.
            Sell.OnRoomEntered(room.Equals(Shop) ? ShopNumber : 0);
            Tracker.NoteRoomObserved(new RoomObservation(r.Name, new HashSet<Direction>(r.Exits.Keys)));
        }

        public void Sold()
        {
            Carried.Remove("dagger");
            Router.Dispatch(new LineExtractor.EmittedLine(
                "You sold dagger for 5 gold crowns.", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose()
        {
            Detour.Dispose();
            Sell.Dispose();
            Lair.Dispose();
            Timers.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
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

        Harness h = null!;
        AutoSellManager sell = new(router,
            carriedItems: () => h.Carried,
            resolve: e => e.Trim() == "dagger" ? new AutoSellManager.ResolvedSell(1, "dagger", true, 0) : null,
            shopTradesItem: (shop, item) => h.ShopTrades && shop == ShopNumber && item == 1,
            isEnabled: () => true);
        sell.SetWireSender(_ => { });
        SellDetourManager detour = new(
            candidates: () => new[]
            {
                new SellDetourManager.Candidate(1, "dagger", h.Carried.Count, 0, h.DetourAbove, new[] { Shop }),
            },
            distance: (a, b) => h.Distance is { } d ? d(a, b) : bfs.DistanceBetween(a, b),
            tracker: tracker, walker: walker, loops: loop, lair: lair, sell: sell, coordinator: coord,
            isEnabled: () => true,
            blocked: () => h.Blocked,
            nearestLoopRoom: (from, loop) =>
            {
                IReadOnlyList<RoomKey> rooms = LoopExpander.ResolveCycleRoomKeys(loop.Waypoints, bfs, graph);
                IReadOnlyDictionary<RoomKey, int> steps = bfs.ComputeDistancesTo(from, rooms);
                return rooms.Where(steps.ContainsKey).OrderBy(r => steps[r]).Cast<RoomKey?>().FirstOrDefault();
            },
            nearBankSteps: () => h.NearBankSteps,
            clock: () => h.Now,
            post: a => { if (h.Defer) h.Posted.Add(a); else a(); });
        h = new Harness
        {
            Graph = graph, Tracker = tracker, Walker = walker, Loop = loop, Lair = lair,
            Timers = timers, Router = router, Coordinator = coord, Sell = sell, Detour = detour,
        };
        return h;
    }

    [Fact]
    public void WalkTo_DueItem_DetoursToShop_SellsThenResumesTheWalk()
    {
        using Harness h = NewHarness();
        h.Carried.AddRange(new[] { "dagger", "dagger" });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        h.Detour.Evaluate();                          // mid-step: hold at the next room
        Assert.False(h.Detour.IsDetouring);
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();                          // stopped there — detour
        Assert.True(h.Detour.IsDetouring);
        Assert.Equal(Shop, h.Walker.Destination);

        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        Assert.True(h.Sell.IsSelling);
        h.Sold();
        h.Sold();

        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);   // back on the original walk
    }

    [Fact]
    public void AtOrBelowTheDetourCount_NoDetour()
    {
        using Harness h = NewHarness();
        h.DetourAbove = 2;
        h.Carried.AddRange(new[] { "dagger", "dagger" });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Detour.Evaluate();                          // mid-step, where a due item would hold
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);
        Assert.DoesNotContain(MovementCoordinator.SellDetourGate, h.Coordinator.AssertedGates);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Contains("last decline: (none)", h.Detour.Status);   // nothing due, nothing weighed
    }

    [Fact]
    public void WalkEndingAtTheShop_NoDetour()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Walker.WalkTo(Shop);
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 1));
        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);   // Auto-sell sells when the walk gets there
        Assert.DoesNotContain(MovementCoordinator.SellDetourGate, h.Coordinator.AssertedGates);
        Assert.Contains("last decline: dagger: the Walk reaches 1/4 itself", h.Detour.Status);
    }

    // The walk back ends at the loop's room nearest the shop (1/2), not where the
    // detour began (1/3) (report paradigm-20260930-182854).
    [Fact]
    public void Loop_DetoursThenWalksBackToItsNearestRoomAndResumes()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 2), new RoomKey(1, 3) })));

        h.Detour.Evaluate();                      // the loop's step to 1/3 is out — hold there
        h.Arrive(new RoomKey(1, 3));
        h.Detour.Evaluate();
        Assert.Equal(LoopState.Idle, h.Loop.State);
        Assert.Equal(Shop, h.Walker.Destination);

        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        h.Sold();
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);

        h.Arrive(new RoomKey(1, 1));
        h.Arrive(new RoomKey(1, 2));
        Assert.False(h.Detour.IsDetouring);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // Mid-lap, with the shop on the part of the cycle not walked yet: the loop gets
    // there itself, so no detour. The cycle's moves start at 1/3 (S, S, E, then back
    // W, N, N); replayed from the room the player stands in they'd walk off the map
    // before ever reaching the shop.
    [Fact]
    public void Loop_MidLap_ShopAheadOnTheCycle_NoDetour()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 3));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 3), Shop })));

        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 1));
        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
        Assert.Contains($"the Loop reaches {Shop} itself", h.Detour.Status);
    }

    [Fact]
    public void Blocked_NoDetour()
    {
        using Harness h = NewHarness();
        h.Blocked = true;
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);
        Assert.DoesNotContain(MovementCoordinator.SellDetourGate, h.Coordinator.AssertedGates);
    }

    // Walk to 1/3, detour to the shop and arrive there.
    private static void DetourAndArrive(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Detour.Evaluate();                          // mid-step: hold at the next room
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();
        Assert.True(h.Detour.IsDetouring);
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
    }

    // A shop that just didn't sell the item isn't written off for the session: it's
    // tried again once the retry wait is over (report paradigm-20260929-060520).
    [Fact]
    public void ShopThatSoldNothing_IsRetriedLater_NotWrittenOff()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.ShopTrades = false;                        // Auto-sell has nothing to queue
        DetourAndArrive(h);
        Assert.False(h.Sell.IsSelling);
        Assert.False(h.Detour.IsDetouring);          // carried on

        h.ShopTrades = true;
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);   // the walk resumed
        h.Arrive(new RoomKey(1, 1));
        h.Detour.Evaluate();
        Assert.False(h.Detour.IsDetouring);          // still waiting
        Assert.Contains("sold none lately", h.Detour.Status);

        h.Now = h.Now.AddMinutes(11);
        h.Detour.Evaluate();                          // due again: hold at the next room
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();
        Assert.True(h.Detour.IsDetouring);
    }

    [Fact]
    public void ShopThatRefusesTheItem_IsNotTriedAgain()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        DetourAndArrive(h);
        Assert.True(h.Sell.IsSelling);
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You cannot sell dagger here.", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        Assert.False(h.Detour.IsDetouring);

        h.Now = h.Now.AddHours(1);
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();
        Assert.False(h.Detour.IsDetouring);
        Assert.Contains("refused it or can't be reached", h.Detour.Status);
    }

    // As wired, Auto-sell's Selling gate holds the walker in the shop, so the walk's
    // Finished fires inside the `You sold` line's dispatch — before the inventory has
    // taken the line in. That sale must not read as nothing sold, which would put the
    // shop on a ten-minute wait (report paradigm-20260930-182949).
    [Fact]
    public void SaleTheInventoryHasNotTakenInYet_IsNotReadAsNothingSold()
    {
        using Harness h = NewHarness();
        h.Sell.SetMovementGate(h.Coordinator);
        h.Defer = true;
        h.Carried.Add("dagger");
        DetourAndArrive(h);
        Assert.True(h.Sell.IsSelling);

        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You sold dagger for 5 gold crowns.", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        h.Carried.Remove("dagger");                   // the inventory takes the line in after
        h.RunPosted();

        Assert.False(h.Detour.IsDetouring);
        Assert.Contains("sold nothing lately: none", h.Detour.Status);
    }

    // A sale that leaves a deposit due goes to the bank from the shop: the bank run
    // takes the way back, so the detour doesn't walk back itself (user, 2026-09-30).
    [Fact]
    public void ADepositDueAfterSelling_HandsTheWayBackToTheBankRun()
    {
        using Harness h = NewHarness();
        (DetourResumeKind Kind, RoomKey Origin)? handedOff = null;
        h.Detour.HandOffToBank = (resume, origin) => { handedOff = (resume.Kind, origin); return true; };
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 2), new RoomKey(1, 3) })));
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 3));
        h.Detour.Evaluate();
        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        h.Sold();

        Assert.Equal((DetourResumeKind.Loop, new RoomKey(1, 3)), handedOff);
        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(LoopState.Idle, h.Loop.State);      // the bank run resumes it, not us
    }

    private static readonly RoomKey Bank = new(1, 1);

    // A loop on 1/2-1/3 with a bank run just come due, the bank one step from the shop.
    private static bool OfferBankRun(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 2), new RoomKey(1, 3) })));
        h.Arrive(new RoomKey(1, 3));
        return h.Detour.SellAheadOfBankRun(
            DetourResume.Snapshot(h.Walker, h.Loop, h.Lair, includeWalk: false), Bank);
    }

    // A bank run coming due sells what's carried at a shop near the bank first, though
    // the item is under its detour count or makes no detours at all, and the bank run
    // takes over from the shop (user, 2026-10-05; report paradigm-20261005-214728).
    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public void BankRunDue_SellsNearTheBankFirst_WhateverTheDetourCount(int? detourAbove)
    {
        using Harness h = NewHarness();
        (DetourResumeKind Kind, RoomKey Origin)? handedOff = null;
        int notTaken = 0;
        h.Detour.HandOffToBank = (resume, origin) => { handedOff = (resume.Kind, origin); return true; };
        h.Detour.BankRunNotTaken = () => notTaken++;
        h.DetourAbove = detourAbove;
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 2), new RoomKey(1, 3) })));
        h.Arrive(new RoomKey(1, 3));
        h.Detour.Evaluate();
        Assert.False(h.Detour.IsDetouring);              // nothing is due on its own

        Assert.True(h.Detour.SellAheadOfBankRun(
            DetourResume.Snapshot(h.Walker, h.Loop, h.Lair, includeWalk: false), Bank));
        Assert.True(h.Detour.IsDetouring);
        Assert.Equal(LoopState.Idle, h.Loop.State);
        Assert.Equal(Shop, h.Walker.Destination);
        Assert.Contains($"ahead of the bank run to {Bank}", h.Detour.Status);

        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        h.Sold();

        Assert.Equal(DetourResumeKind.Loop, handedOff?.Kind);
        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(0, notTaken);
    }

    [Fact]
    public void BankRunDue_ShopFarFromTheBank_TheBankRunGoesAlone()
    {
        using Harness h = NewHarness();
        h.Detour.HandOffToBank = (_, _) => true;
        h.Distance = (_, _) => h.NearBankSteps + 1;
        h.Carried.Add("dagger");

        Assert.False(OfferBankRun(h));

        Assert.False(h.Detour.IsDetouring);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    [Fact]
    public void BankRunDue_StepCountZero_TheBankRunGoesAlone()
    {
        using Harness h = NewHarness();
        h.Detour.HandOffToBank = (_, _) => true;
        h.NearBankSteps = 0;
        h.Carried.Add("dagger");

        Assert.False(OfferBankRun(h));
    }

    [Fact]
    public void BankRunDue_NothingToSell_TheBankRunGoesAlone()
    {
        using Harness h = NewHarness();
        h.Detour.HandOffToBank = (_, _) => true;

        Assert.False(OfferBankRun(h));

        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // The deposit gate fired once for this crossing and waits on the hand-over, so a
    // detour that never gets there has to say so or the gate stays latched.
    [Fact]
    public void AheadOfABankRun_StoppedOnTheWay_TellsTheDepositGate()
    {
        using Harness h = NewHarness();
        int notTaken = 0;
        h.Detour.HandOffToBank = (_, _) => true;
        h.Detour.BankRunNotTaken = () => notTaken++;
        h.Carried.Add("dagger");
        Assert.True(OfferBankRun(h));

        h.Walker.Stop("user");

        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(1, notTaken);
    }

    [Fact]
    public void AheadOfABankRun_TheBankRunDeclinesAtTheShop_TellsTheDepositGateAndWalksBack()
    {
        using Harness h = NewHarness();
        int notTaken = 0;
        h.Detour.HandOffToBank = (_, _) => false;
        h.Detour.BankRunNotTaken = () => notTaken++;
        h.Carried.Add("dagger");
        Assert.True(OfferBankRun(h));
        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        h.Sold();

        Assert.Equal(1, notTaken);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);   // back to the loop
    }
}
