using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhSweepManagerIntegrationTests : IDisposable
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

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-ghsweep-manager-" + Path.GetRandomFileName());
    // GhRoomLabelStore is realm-tier — each test points it at this scratch realm
    // folder so its labels/settings persist somewhere real but isolated (cleaned
    // up in Dispose alongside _root).
    private readonly string _realm = Path.Combine(Path.GetTempPath(), "ghsweep-test-" + Path.GetRandomFileName());

    public GhSweepManagerIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try
        {
            if (Directory.Exists(_realm)) Directory.Delete(_realm, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void Sorting_SkipsSearchForVisibleItem_BatchesHiddenPickup_ThenDeliversBothInOneTrip()
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 2, "Name": "chain shirt", "ItemType": 0, "Encum": 1 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);   // this run exercises the hidden-item search path

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());
        Assert.Equal("n", sent[0]);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba sorting starting - ", StringComparison.Ordinal));

        // C is an unlabeled transit room. Its visible list arrives while
        // CurrentRoom still points at A; it must be staged against C and C must
        // still be reconned even though labels are destinations, not sources.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle();
        Assert.Equal("n", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal("sea", sent[^1]);
        // This item only appears as the response to recon's own search, so its
        // eventual PendingMove is explicitly tagged hidden.
        FeedRouter(router, "You notice a mace here.");
        FireSearchSettle();
        Assert.Equal("s", sent[^1]);

        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle();
        Assert.Equal("s", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle(); // completes recon lap; sorting starts northbound
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);
        Assert.Equal("n", sent[^1]);
        int reconSearchCount = sent.Count(command => command == "sea");

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        // war hammer was visible on entry during recon: no Sorting `sea` at all.
        Assert.Equal("get war hammer", sent[^1]);
        Assert.Equal(reconSearchCount, sent.Count(command => command == "sea"));

        // Full-ledger verification: "You took X" is trusted as ground truth and the
        // carry weight is tracked from the ledger, so NO per-transaction `i` goes out.
        // The reroute happens straight off the confirm — and it FILLS the pack before
        // delivering: the hidden mace at B is also a weapon (bound for A), so rather
        // than deliver the war hammer immediately, the planner routes NORTH to batch
        // the mace first, cutting two delivery trips down to one.
        FeedRouter(router, "You took war hammer.");
        Assert.Equal("n", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("sea", sent[^1]); // mace was tagged hidden during recon
        Assert.Equal(reconSearchCount + 1, sent.Count(command => command == "sea"));
        FireSearchSettle();
        Assert.Equal("get mace", sent[^1]); // picked up while still carrying the war hammer

        // Pack now holds everything bound for A and no source remains — deliver:
        // route from B toward A (two hops south through transit room C).
        FeedRouter(router, "You took mace.");
        Assert.Equal("s", sent[^1]);
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(7));
        Assert.Equal("s", sent[^1]); // C is pure transit on the delivery route

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(8));
        // Both carried weapons are dropped in the single visit to A — but ONE
        // COMMAND AT A TIME, each released by the game's prompt. A burst here is
        // what tripped the command-rate limiter and got a whole batch (plus the
        // loop's next move) silently dropped.
        Assert.Equal("drop war hammer", sent[^1]);
        FeedRouter(router, "You dropped war hammer.");

        PaceOut();
        Assert.Equal("drop mace", sent[^1]);
        FeedRouter(router, "You dropped mace.");
        // Everything is delivered — sorting is done and a final recon pass begins to
        // refresh each room's inventory before the sweep finishes.
        Assert.Equal(GhSweepManager.SweepPhase.FinalRecon, sweep.Phase);
        Assert.Equal(0, sweep.PendingMoveCount);

        // The whole sort completed on the tracked ledger — never a single `i`.
        Assert.DoesNotContain("i", sent);

        // Walk the final recon lap (same A-C-B-C-A circuit) to close it out and
        // reach genuine completion — the point the "sorting complete" announce fires.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(10));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(11));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(12));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(13));

        Assert.Equal(GhSweepManager.SweepPhase.Idle, sweep.Phase);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba sorting complete - sorted 2 item(s), inventoried ", StringComparison.Ordinal)
            && s.Contains(" item(s). started ", StringComparison.Ordinal)
            && s.Contains(", finished ", StringComparison.Ordinal));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // Search-for-hidden OFF (the default): recon walks the circuit and observes
    // the visible floor but never sends `sea`, so nothing hidden is revealed or
    // sorted. Recon still completes normally — its lap count is driven by the
    // loop's RepeatStarted, not by the (now-absent) search settles.
    [Fact]
    public void SearchForHiddenOff_ReconNeverSearches_SortsVisibleOnly()
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        // SearchForHidden left at its default (off) — the point of this test.

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Walk a full recon lap. A war hammer is plainly visible at transit room C;
        // a mace sits hidden at B and is only revealed by `sea`, which never goes out.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        // The whole recon lap ran without a single search, and the sort is under
        // way with only the visible war hammer to move: the hidden mace was never
        // revealed.
        Assert.DoesNotContain("sea", sent);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);
        Assert.Equal(1, sweep.PendingMoveCount);
        Assert.DoesNotContain("get mace", sent);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // A `get` that comes back "You don't see X here." (the item vanished between
    // recon and sort) is dropped from the queue — recorded under LeftInPlace — and
    // NOT retried, so a gone item can't pin the sweep in an endless `get` loop.
    [Fact]
    public void FailedGet_ItemNotHere_StrandsToLeftInPlace_AndDoesNotRetry()
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 } ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Recon a full lap: a war hammer is visible at transit room C and belongs
        // in weapon-room A. (Nothing else is on any floor.)
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FireSearchSettle();
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        FireSearchSettle();
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        FireSearchSettle();
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        FireSearchSettle();   // completes recon; sorting begins
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Sorting reaches C and dispatches the pickup.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        int getsSent = sent.Count(c => c == "get war hammer");

        // But it's gone — the game says so. It must be stranded, not retried.
        FeedRouter(router, "You don't see war hammer here.");

        Assert.Contains(sweep.LeftInPlace, f => f.ItemName == "war hammer");
        Assert.Equal(0, sweep.PendingMoveCount);                     // no longer queued
        Assert.Equal(getsSent, sent.Count(c => c == "get war hammer")); // never re-sent

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // A `get` refused with "You cannot carry that much!" is NOT a gone item. The
    // engine normally trusts its tracked ledger (no per-transaction `i`), but this
    // one signal proves the ledger drifted, so it requests a single resync `i`. When
    // the fresh dump shows the working budget is now smaller than the item's weight,
    // the item is stranded (too heavy) instead of being retried forever.
    [Fact]
    public void CapacityRefusal_ResyncsFromInventory_WithoutWritingOffWhatStillFitsWhenEmpty()
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 60 } ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        // Recon baseline: nearly empty (1/100), so a 60-weight hammer looks movable.
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FireSearchSettle();
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        FireSearchSettle();
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        FireSearchSettle();
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        FireSearchSettle();
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Sorting reaches C and dispatches the pickup — the ledger thinks it fits.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        int getsSent = sent.Count(c => c == "get war hammer");

        // Capacity refusal — the ONE signal that triggers a single resync `i`.
        FeedRouter(router, "You cannot carry that much!");
        Assert.Equal("i", sent[^1]);

        // The fresh dump shows only 40 of 100 free — the pack is holding 59 more
        // than when sorting opened. The hammer doesn't fit RIGHT NOW, and the
        // refusal correctly stops us re-sending the get into a full pack.
        FeedInventory(inventoryFeed, "nothing", currentWeight: 60);

        Assert.Equal(40, sweep.WorkingWeightBudget);
        Assert.Equal(getsSent, sent.Count(c => c == "get war hammer")); // never re-sent

        // But it is NOT written off. Base is everything in the pack the ledger
        // doesn't own, so it rises whenever the player picks something up
        // themselves — and "too heavy to EVER carry" measured against that dip
        // permanently discards items that fit fine once the pack clears. Judged at
        // our emptiest (99 free) the hammer is perfectly carriable, so it stays
        // queued for a later pass.
        Assert.DoesNotContain(sweep.LeftInPlace,
            f => f.ItemName == "war hammer" && f.Reason == GhLeftReason.TooHeavy);
        Assert.Equal(1, sweep.PendingMoveCount);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void Dispatch_IsPacedOneCommandPerPrompt_AndResendsAClobberedOne()
    {
        // Reproduces the flood that killed a 120-room sweep: a room's whole batch
        // went out at once, stock's rate limiter answered with a wall of "Why
        // don't you slow down for a few seconds?", every command was dropped, and
        // so was the loop's next move — leaving the tracker Pending on a move the
        // server never processed.
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 1 },
              { "Number": 4, "Name": "club", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Three weapons sitting in transit room C, all bound for A.
        FeedRouter(router, "You notice a war hammer, a mace, a club here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer, a mace, a club here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Arrive at the pickup room: exactly ONE get goes out, not three.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(1, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));

        // The game says we're going too fast and that the command was dropped —
        // it must be re-sent, not lost.
        string clobbered = sent[^1];
        FeedRouter(router, "You are typing too quickly - command ignored");

        // Prompts are ignored while we're deliberately backing off — pushing
        // again the instant the game complained is exactly what got us here.
        int sentWhileBackingOff = sent.Count;
        _sweepPrompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        Assert.Equal(sentWhileBackingOff, sent.Count);

        // Backoff over: the same command goes out again.
        _sweepNow += GhSweepManager.RateLimitBackoff;
        _sweepTimers.RunDue(GhSweepManager.RateLimitBackoff);
        Assert.Equal(clobbered, sent[^1]);
        FeedRouter(router, $"You took {clobbered["get ".Length..]}.");

        // A prompt arriving inside the minimum interval must NOT release the next
        // command. Every rate-limit line the game sends carries a prompt, so
        // releasing on prompt alone speeds up under exactly the condition that
        // should slow us down — observed live as bursts of five nudges in 200ms.
        int sentBeforeEarlyPrompt = sent.Count;
        _sweepPrompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        Assert.Equal(sentBeforeEarlyPrompt, sent.Count);

        // Each remaining command waits for its own prompt.
        PaceOut();
        Assert.Equal(3, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));

        PaceOut();
        Assert.Equal(4, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));

        // That was the last of them.
        PaceOut();
        Assert.Equal(4, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void DropSyntaxRefusal_ForAnItemWeDoNotHave_RemovesTheMove()
    {
        // "Syntax: DROP {Amount} {Currency}" names no item and means the game
        // didn't recognise the name as something we hold. The ledger thought a
        // pickup landed when it hadn't, so the drop can never succeed — retrying
        // it just replays the same syntax error every lap.
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Collect it — the ledger now believes it's carried.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        Assert.Equal(1, sweep.CarriedPendingCount);

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        // The game doesn't recognise it. That triggers a real inventory read
        // rather than a guess.
        FeedRouter(router, "Syntax: DROP {Amount} {Currency}");
        Assert.Equal("i", sent[^1]);

        // Inventory confirms we never had it → the move is dropped, not retried.
        FeedInventory(inventoryFeed, "nothing");

        Assert.Equal(0, sweep.CarriedPendingCount);
        Assert.Equal(0, sweep.PendingMoveCount);
        Assert.Contains(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);
        Assert.Equal(1, sent.Count(c => c.StartsWith("drop ", StringComparison.Ordinal)));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void ItemBinnedByAnotherEngineMidSweep_DropsTheMoveInsteadOfCarryingABelief()
    {
        // The live failure: auto-discard binned bloodstones Roomba had collected.
        // Roomba kept believing it held them, and its eventual "drop bloodstone"
        // was partial-matched by the game onto a bloodstone ORB it really was
        // holding. That one bounced (undroppable) — a droppable collision would
        // have thrown away the wrong item silently.
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        Assert.Equal(1, sweep.CarriedPendingCount);

        // Another engine bins it while we walk. No dispatch of ours is in flight.
        int before = sent.Count;
        FeedRouter(router, "You dropped war hammer.");

        // The belief is dropped, so no phantom "drop war hammer" is ever queued.
        Assert.Equal(0, sweep.CarriedPendingCount);
        Assert.Contains(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);
        Assert.Equal(before, sent.Count);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void DropSyntaxRefusal_ForAnItemWeDoHave_KeepsTheMoveQueued()
    {
        // The mirror case: if the fresh `i` DOES show the item, the syntax error
        // was a name misparse rather than a phantom pickup. Removing the move
        // there would silently abandon something we're really carrying.
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        FeedRouter(router, "You took war hammer.");
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        FeedRouter(router, "Syntax: DROP {Amount} {Currency}");
        FeedInventory(inventoryFeed, "a war hammer");

        // Still ours to move — retried on a later visit, not abandoned.
        Assert.Equal(1, sweep.CarriedPendingCount);
        Assert.DoesNotContain(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void AbortedSweep_ResumesFromItsQueueWithoutRescanning()
    {
        // Re-walking a 120-room circuit to rediscover a survey we already have is
        // most of the cost of a sweep, and an abort doesn't make what it found
        // wrong. Resume picks the queue back up and goes straight to sorting.
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(1, h.Sweep.PendingMoveCount);

        // Something kills the sweep mid-sort.
        h.Sweep.Stop("test abort");
        Assert.Equal(GhSweepManager.SweepPhase.Idle, h.Sweep.Phase);
        Assert.True(h.Sweep.CanResume);
        Assert.Equal(1, h.Sweep.ResumableMoveCount);

        int sentBeforeResume = h.Sent.Count;
        Assert.True(h.Sweep.Resume());

        // Straight into sorting with the queue intact — no recon lap.
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(1, h.Sweep.PendingMoveCount);
        Assert.False(h.Sweep.CanResume);
        Assert.DoesNotContain(h.Sent.Skip(sentBeforeResume), c => c == "sea");

        h.Dispose();
    }

    [Fact]
    public void ResumeSurvivesARestart_FromThePersistedQueue()
    {
        // The case that matters most: a client restart after a bail is exactly
        // when re-walking a 120-room circuit hurts, and it's the one an
        // in-memory-only queue can't survive. A brand-new manager (no memory of
        // the sweep at all) must still be able to resume from the profile.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[]
        {
            new GhSuspendedMove("1/3", "1/1", "war hammer", 1, carried: false, hidden: false),
            new GhSuspendedMove("1/3", "1/1", "mace", 2, carried: true, hidden: false),
        });

        // A live 'i' read still shows the carried mace — Resume verifies carried
        // entries against the pack, so it must be present to be re-adopted.
        SweepHarness h = NewSweepHarness(_root, _realm, store, carriedInventory: "mace, mace");

        Assert.True(h.Sweep.CanResume);
        Assert.Equal(2, h.Sweep.ResumableMoveCount);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Resume());

        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(2, h.Sweep.PendingMoveCount);
        Assert.Equal(1, h.Sweep.CarriedPendingCount);   // the carried flag round-tripped
        Assert.DoesNotContain(h.Sent, c => c == "sea");
        // Consumed: a later Start mustn't adopt the same carried item again.
        Assert.False(store.Any);

        h.Dispose();
    }

    [Fact]
    public void Resume_DropsCarriedItemNoLongerInThePack()
    {
        // Resume verifies the carried half against a live 'i' read, exactly as a fresh
        // Start does: an item sold or dropped since the sweep bailed is dropped from the
        // plan rather than blindly re-dropped (which the game partial-matches onto a
        // different item still held).
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[]
        {
            new GhSuspendedMove("1/3", "1/1", "war hammer", 1, carried: false, hidden: false),
            new GhSuspendedMove("1/3", "1/1", "mace", 1, carried: true, hidden: false),
        });

        // The pack no longer holds the mace.
        SweepHarness h = NewSweepHarness(_root, _realm, store, carriedInventory: "nothing");

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Resume());

        Assert.Equal(1, h.Sweep.PendingMoveCount);      // only the uncarried war hammer survives
        Assert.Equal(0, h.Sweep.CarriedPendingCount);   // the gone mace is dropped from the manifest
        h.Dispose();
    }

    [Fact]
    public void LootPickedUpMidSweep_NarrowsHeadroomButStrandsNothing()
    {
        // Live numbers from a real sweep. Base is everything in the pack the sort
        // ledger doesn't own, so auto-get loot inflates it — 3513 climbing to 4508
        // over four resyncs, collapsing the budget 1767 -> 772. Judged against
        // that dip, 800-weight hauberks were permanently written off as "too heavy
        // to ever carry" despite fitting comfortably at the sweep's own opening
        // budget. 56 items went that way in one run.
        SweepHarness h = NewSweepHarness(_root, _realm, currentWeight: 3513, maxWeight: 5280);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice an adamantite hauberk here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice an adamantite hauberk here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);

        // Fits the opening budget (5280 - 3513 = 1767) and is queued.
        Assert.Equal(1767, h.Sweep.WorkingWeightBudget);
        Assert.Equal(1, h.Sweep.PendingMoveCount);

        // Now the player loots ~1000 of weight Roomba never collected, so the get
        // at C is refused and the `i` it asks for shows the heavier pack.
        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get adamantite hauberk", h.Sent[^1]);
        h.Feed("You cannot carry that much!");
        Assert.Equal("i", h.Sent[^1]);
        FeedInventory(h.InventoryFeed, "nothing", currentWeight: 4508, maxWeight: 5280);

        // Headroom really has narrowed — that part must stay honest, or we
        // over-fill and earn a capacity refusal.
        Assert.Equal(772, h.Sweep.WorkingWeightBudget);
        // But nothing is written off: "ever" is judged at our emptiest.
        Assert.DoesNotContain(h.Sweep.LeftInPlace, l => l.Reason == GhLeftReason.TooHeavy);
        Assert.Equal(1, h.Sweep.PendingMoveCount);

        h.Dispose();
    }

    [Fact]
    public void GenuinelyUnmovableItemIsStillStranded()
    {
        // The guard must not become a rubber stamp: something heavier than the
        // pack could ever hold is still dropped, or the sweep retries it forever.
        SweepHarness h = NewSweepHarness(_root, _realm, currentWeight: 4900, maxWeight: 5280);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice an adamantite hauberk here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice an adamantite hauberk here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);

        // Best case is 380, the hauberk is 800 — unmovable however empty we get.
        Assert.Equal(380, h.Sweep.WorkingWeightBudget);
        Assert.Equal(0, h.Sweep.PendingMoveCount);
        Assert.Contains(h.Sweep.LeftInPlace, l => l.Reason == GhLeftReason.TooHeavy);

        h.Dispose();
    }

    [Fact]
    public void StalledSort_FinishesAfterAVerificationLap_InsteadOfLoopingForever()
    {
        // The 70-lap bug: a queued move that can't complete (here it's never
        // confirmed) kept the sweep circling the house indefinitely. Now the first
        // fruitless lap is only a warning — a verification pass — and a second
        // fruitless lap finishes: the move is surfaced as CouldNotComplete and the
        // sweep leaves Sorting rather than retrying forever.
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(1, h.Sweep.PendingMoveCount);

        // First fruitless lap is a grace pass: still sorting, move still queued.
        SortLapWithNothingAnswered(h);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(1, h.Sweep.PendingMoveCount);

        // Second fruitless lap confirms nothing more can move: the move is stranded
        // and the sweep leaves Sorting (onto its final verification lap) — it does
        // not keep circling.
        SortLapWithNothingAnswered(h);
        Assert.NotEqual(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);
        Assert.Equal(0, h.Sweep.PendingMoveCount);
        Assert.Contains(h.Sweep.LeftInPlace, l => l.Reason == GhLeftReason.CouldNotComplete);

        h.Dispose();
    }

    [Fact]
    public void GetRefusedWithTheCurrencySyntax_IsStrandedNotRetriedForever()
    {
        // The live realm emits BRACES — "Syntax: GET {Amount} {Currency}" — while
        // the pattern had been written for the bracket form recorded in
        // GAME_MECHANICS. It therefore never matched, so an item the game won't
        // take by that name failed silently and was requeued every lap: one
        // capture shows `get piece of amber` sent 42 times, with the sweep pinned
        // ping-ponging between the two rooms holding those items.
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);

        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get war hammer", h.Sent[^1]);
        int getsSent = h.Sent.Count(c => c == "get war hammer");

        h.Feed("Syntax: GET {Amount} {Currency}");

        Assert.Equal(0, h.Sweep.PendingMoveCount);
        Assert.Contains(h.Sweep.LeftInPlace, l => l.ItemName == "war hammer");
        Assert.Equal(getsSent, h.Sent.Count(c => c == "get war hammer"));

        h.Dispose();
    }

    // "A strange force stops you from getting this item." is the game refusing the
    // item itself. It refuses it every time, so the item is left where it lies
    // under a reason of its own and the get is not sent again.
    [Fact]
    public void GetRefusedByAStrangeForce_IsLeftInPlace_NotRetried()
    {
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);

        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get war hammer", h.Sent[^1]);
        int getsSent = h.Sent.Count(c => c == "get war hammer");

        h.Feed("A strange force stops you from getting this item.");

        Assert.Equal(0, h.Sweep.PendingMoveCount);
        Assert.Contains(h.Sweep.LeftInPlace,
            l => l.ItemName == "war hammer" && l.Reason == GhLeftReason.RefusedByGame);
        Assert.Equal(getsSent, h.Sent.Count(c => c == "get war hammer"));

        h.Dispose();
    }

    [Fact]
    public void GetCurrencySyntax_BracketFormIsStillRecognised()
    {
        // Keep the documented bracket form working — the realms may differ, and
        // the cost of missing either is an item retried until someone notices.
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get war hammer", h.Sent[^1]);

        h.Feed("Syntax: GET [Amount] [Currency]");

        Assert.Equal(0, h.Sweep.PendingMoveCount);

        h.Dispose();
    }

    [Fact]
    public void MultiUnitMove_NeedsEveryUnitConfirmed_AndSpareRepliesArentForeignDrops()
    {
        // Stock has no bulk verb, so a 2x move goes out as two commands and
        // answers with two lines. Completing on the first orphaned the second,
        // and the orphan was then misread as somebody else dropping the item —
        // which deleted an unrelated carried move. One live run left the player
        // holding ~20 items the sweep had collected and forgotten, six of them
        // copies of the same gloves.
        // Stock: no bulk verb, so a 2x move is sent as two separate commands.
        SweepHarness h = NewSweepHarness(_root, _realm, paradigm: false);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice 2 war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice 2 war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);

        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get war hammer", h.Sent[^1]);

        // First unit only — the move is NOT collected yet.
        h.Feed("You took war hammer.");
        Assert.Equal(0, h.Sweep.CarriedPendingCount);

        // Second unit completes the collection.
        h.Feed("You took war hammer.");
        Assert.Equal(1, h.Sweep.CarriedPendingCount);

        // Same rule on the way out: two units, two replies.
        h.Observe("A", Direction.N);
        Assert.Equal("drop war hammer", h.Sent[^1]);

        h.Feed("You dropped war hammer.");
        Assert.Equal(1, h.Sweep.CarriedPendingCount);   // still owed a unit
        Assert.Empty(h.Sweep.MovedSoFar);
        // The spare reply must not be mistaken for somebody else's drop — that
        // path deletes a carried move and loses a real item.
        Assert.DoesNotContain(h.Sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);

        h.Feed("You dropped war hammer.");
        Assert.Equal(0, h.Sweep.CarriedPendingCount);
        Assert.Equal(2, Assert.Single(h.Sweep.MovedSoFar).Count);

        h.Dispose();
    }

    [Fact]
    public void MultiUnitMove_Paradigm_ConfirmsFromOneBulkReply()
    {
        // Paradigm HAS a bulk verb: a 2x move goes out as one `get 2 war hammer` and
        // the game answers with a SINGLE count-prefixed line. Counting replies (a raw
        // ++) left that move stuck at 1/2 forever — the sweep never finished; the fix
        // adds the line's own count.
        SweepHarness h = NewSweepHarness(_root, _realm, paradigm: true);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());

        h.Feed("You notice 2 war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice 2 war hammer here.");   // listed again on the way back
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, h.Sweep.Phase);

        h.Observe("C", Direction.N, Direction.S);
        Assert.Equal("get 2 war hammer", h.Sent[^1]);   // one bulk command, not two

        // One reply carries BOTH units.
        h.Feed("You took 2 war hammer.");
        Assert.Equal(1, h.Sweep.CarriedPendingCount);    // collected from the single line

        h.Observe("A", Direction.N);
        Assert.Equal("drop 2 war hammer", h.Sent[^1]);
        h.Feed("You dropped 2 war hammer.");
        Assert.Equal(0, h.Sweep.CarriedPendingCount);
        Assert.Equal(2, Assert.Single(h.Sweep.MovedSoFar).Count);

        h.Dispose();
    }

    [Fact]
    public void Resume_DoesNotCountItsOwnRestoredLoadAsThePlayersGear()
    {
        // CaptureCarryBaseline assumes an empty sort-load ("sorting opens with an
        // empty sort-load"), which is true for Start and false for Resume — Resume
        // re-adopts carried items BEFORE capturing. Counting those as the player's
        // gear inflates base by exactly their weight, which collapses the budget,
        // trips the too-tight-to-collect guard, and makes a resumed sweep deliver
        // its load and stop.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[]
        {
            new GhSuspendedMove("1/3", "1/1", "adamantite hauberk", 1, carried: true, hidden: false),
        });

        // Pack reads 3000 of 5280, and 800 of that is the hauberk we just re-adopted
        // — which a live 'i' read must show for Resume to re-adopt it.
        SweepHarness h = NewSweepHarness(_root, _realm, store,
            currentWeight: 3000, maxWeight: 5280, carriedInventory: "adamantite hauberk");

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Resume());
        Assert.Equal(1, h.Sweep.CarriedPendingCount);

        // Base is the player's 2200, not the 3000 that includes our own load.
        Assert.Equal(3080, h.Sweep.WorkingWeightBudget);

        h.Dispose();
    }

    [Fact]
    public void CleanlyFinishedSweep_HasNothingToResume()
    {
        SweepHarness h = NewSweepHarness(_root, _realm);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);

        // Nothing on any floor, so the sweep finishes with an empty queue.
        Assert.False(h.Sweep.CanResume);
        Assert.Equal(0, h.Sweep.ResumableMoveCount);

        h.Dispose();
    }

    // The terminal, the Roomba tab's banner and its room list read the report: one
    // cut short says it stopped, and why, never that it is complete.
    [Fact]
    public void TheReport_TellsAFinishedSweepFromOneStoppedEarly()
    {
        SweepHarness h = NewSweepHarness(_root, _realm);
        List<GhSweepReport> reports = new();
        h.Sweep.SweepCompleted += reports.Add;

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.True(Assert.Single(reports).Finished);

        Assert.True(h.Sweep.Start());
        h.Sweep.Stop("user stop from toolbar");

        Assert.Equal(2, reports.Count);
        Assert.False(reports[1].Finished);
        Assert.Equal("user stop from toolbar", reports[1].StoppedBecause);

        h.Dispose();
    }

    // The unfinished queue's rooms are numbers of the game data it was saved on.
    [Fact]
    public void Resume_IsRefused_UnderAnotherGameDataSet()
    {
        string set = "alpha";
        SweepHarness h = NewSweepHarness(_root, _realm, activeGameData: () => set);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());
        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Feed("You notice a war hammer here.");
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        h.Sweep.Stop("test abort");
        Assert.True(h.Sweep.CanResume);

        set = "beta";
        Assert.False(h.Sweep.Resume());
        Assert.Equal("The unfinished sweep was saved on game data 'alpha', and 'beta' is active now: "
            + "its rooms are that game data's.", h.Sweep.LastStartError);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, h.Sweep.Phase);

        set = "alpha";
        Assert.True(h.Sweep.Resume());

        h.Dispose();
    }

    // A fresh sweep under another set leaves that set's record alone: switched back,
    // what the pack held for it is still there to deliver.
    [Fact]
    public void AFreshSweepUnderAnotherSet_KeepsThatSetsRecord()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[] { new GhSuspendedMove("1/3", "1/1", "mace", 1, carried: true, hidden: false) }, "alpha");
        SweepHarness h = NewSweepHarness(_root, _realm, store, activeGameData: () => "beta");

        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Sweep.Start());
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("B", Direction.S);
        h.Observe("C", Direction.N, Direction.S);
        h.Observe("A", Direction.N);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, h.Sweep.Phase);

        Assert.Equal("alpha", store.GameData);
        Assert.Equal("mace", Assert.Single(store.Load()).Item);

        h.Dispose();
    }

    [Fact]
    public void Resume_FromThePersistedQueue_IsRefused_UnderAnotherGameDataSet()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[] { new GhSuspendedMove("1/3", "1/1", "war hammer", 1, carried: false, hidden: false) }, "alpha");
        SweepHarness h = NewSweepHarness(_root, _realm, store, activeGameData: () => "beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(h.Sweep.Resume());
        Assert.StartsWith("The unfinished sweep was saved on game data 'alpha'", h.Sweep.LastStartError);
        Assert.True(store.Any);

        h.Dispose();
    }

    [Fact]
    public void FullDestination_RerootsTheWholeBatchToTheBackupRoom()
    {
        // Reproduces the sweep that spent nine minutes re-sending the same refused
        // drops every lap: the destination was at item capacity, nothing confirmed,
        // and the queue was requeued into the identical wall forever. One refusal
        // line now marks the room and moves everything bound for it to the next
        // room labeled for the same category.
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        // Two rooms labeled for the SAME category: A is the primary (first
        // labeled), B is its backup purely by also matching. No other config.
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Recon the A→C→B→C→A circuit; a war hammer is sitting in transit room C.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Collect it at C, then deliver to A — the first room labeled for weapons.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        // A is full. Before this, that produced no confirmation, a settle timeout,
        // and the same drop again next lap, forever.
        FeedRouter(router, "There is no room to drop war hammer here.");

        // Re-targeted onto B and still carried, so the sweep walks north to deliver
        // rather than retrying A. The item is NOT abandoned.
        Assert.Equal(1, sweep.CarriedPendingCount);
        Assert.Equal("n", sent[^1]);
        Assert.DoesNotContain(GhLeftReason.AllDestinationsFull, sweep.LeftInPlace.Select(l => l.Reason));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(7));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.Equal("drop war hammer", sent[^1]);

        FeedRouter(router, "You dropped war hammer.");
        Assert.Equal(0, sweep.PendingMoveCount);
        // Never retried the full room.
        Assert.Equal(2, sent.Count(c => c == "drop war hammer"));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // Minimal A/B/C-circuit rig for the newer tests. The older tests in this file
    // each build this inline; this exists so new ones don't add another copy.
    private sealed class SweepHarness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required GhSweepManager Sweep { get; init; }
        public required GroundItemTracker Ground { get; init; }
        public required InventoryManager Inventory { get; init; }
        public required MessageRouter Router { get; init; }
        public required List<string> Sent { get; init; }
        // The `i` dump lines, as the inventory reads them.
        public required ServerFeed InventoryFeed { get; init; }

        private int _clock;

        public void Feed(string line) => FeedRouter(Router, line);

        public void Observe(string name, params Direction[] exits) =>
            Tracker.NoteRoomObserved(new RoomObservation(name, new HashSet<Direction>(exits)),
                DateTimeOffset.UtcNow.AddSeconds(++_clock));

        public void Dispose()
        {
            Sweep.Dispose();
            Ground.Dispose();
            Inventory.Dispose();
        }
    }

    private SweepHarness NewSweepHarness(string root, string realm,
        GhSuspendedSweepStore? suspended = null, int currentWeight = 1, int maxWeight = 100,
        bool paradigm = true, string carriedInventory = "nothing", Func<string?>? activeGameData = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 },
              { "Number": 2, "Name": "adamantite hauberk", "ItemType": 1, "Encum": 800 }
            ]
            """);

        GameDataCache cache = new(root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(realm);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, carriedInventory, currentWeight, maxWeight);

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => paradigm, inventory: inventory, suspendedStore: suspended,
            activeGameData: activeGameData, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        List<string> sent = new();
        sweep.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        runner.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));

        return new SweepHarness
        {
            Tracker = tracker, Sweep = sweep, Ground = ground,
            Inventory = inventory, Router = router, Sent = sent, InventoryFeed = inventoryFeed,
        };
    }

    private void FireSearchSettle() => _sweepTimers.RunDue(GhSweepManager.ReconSearchSettle);

    // The sort shuttle goes round once, north to B and back down to A, with nothing
    // it sends on the way answered.
    private void SortLapWithNothingAnswered(SweepHarness h)
    {
        h.Observe("C", Direction.N, Direction.S);
        NothingAnswers();
        h.Observe("B", Direction.S);
        NothingAnswers();
        h.Observe("C", Direction.N, Direction.S);
        NothingAnswers();
        h.Observe("A", Direction.N);
        NothingAnswers();
    }

    private void NothingAnswers()
    {
        _sweepNow += TimeSpan.FromSeconds(5);
        _sweepTimers.Advance(TimeSpan.FromSeconds(5));
    }

    private static void FeedRouter(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    private static void FeedInventory(ServerFeed feed, string carried, int currentWeight = 1,
                                      int maxWeight = 100)
    {
        feed.Line($"You are carrying {carried}.");
        feed.Line("You have no keys.");
        feed.Line("Wealth:    0 copper farthings");
        feed.Line($"Encumbrance:    {currentWeight}/{maxWeight}  -  None  "
                        + $"[{currentWeight * 100 / maxWeight}%]");
    }

    // InventoryOnly walks the same recon circuit and feeds the item-location
    // log exactly like Sort does, but must never dispatch a single get/drop
    // and must finish the moment its one recon lap completes — no Sorting
    // phase, no final-recon lap.
    [Fact]
    public void InventoryOnlyMode_ObservesAndLogs_ButNeverMovesAnything()
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
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 2, "Name": "chain shirt", "ItemType": 0, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        // Both rooms labeled as sortable destinations — proves InventoryOnly
        // still refuses to act on them, not just that it has nowhere to sort to.
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);

        GhItemLocationStore locations = new(names);
        locations.OnRealmChanged(_realm);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        ServerFeed inventoryFeed = new();
        inventory.AttachLineExtractor(inventoryFeed.Lines);
        FeedInventory(inventoryFeed, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, itemLocations: locations, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        int completions = 0;
        sweep.SweepCompleted += _ => completions++;

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start(GhSweepManager.SweepMode.InventoryOnly));
        Assert.Equal(GhSweepManager.SweepMode.InventoryOnly, sweep.Mode);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba inventory mode starting - ", StringComparison.Ordinal));

        // Walk the full recon lap — a war hammer visible at A, a chain shirt at B.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FeedRouter(router, "You notice a chain shirt here.");
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        // The game lists a room's floor on every entry, the way back included.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));   // completes the lap

        // Finished on its own — no Sorting, no final recon — the moment recon closed.
        Assert.Equal(GhSweepManager.SweepPhase.Idle, sweep.Phase);
        Assert.Equal(1, completions);
        Assert.Empty(sweep.MovedSoFar);
        Assert.Empty(sweep.LeftInPlace);
        Assert.Empty(sweep.Stranded);
        Assert.DoesNotContain(sent, s => s.StartsWith("get ", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, s => s.StartsWith("drop ", StringComparison.Ordinal));
        // war hammer + chain shirt, one apiece — no leading stack count on either.
        Assert.Contains(sent, s => s.StartsWith("bg Roomba inventory complete - inventoried 2 item(s). started ", StringComparison.Ordinal)
            && s.Contains(", finished ", StringComparison.Ordinal));

        // But the item-location log IS fed, same as a Sort-mode recon would.
        // The staged arrival survey attaches to the room being entered when the
        // transition confirms — "war hammer" was fed before the C transition, so
        // it lands on C (room 3); "chain shirt" before B, so it lands on B (room 2).
        GhItemSighting hammer = Assert.Single(locations.FindSightings("war hammer"));
        Assert.Equal(3, hammer.Room);
        GhItemSighting shirt = Assert.Single(locations.FindSightings("chain shirt"));
        Assert.Equal(2, shirt.Room);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // The gang-house scoping fix: only rooms set to Actively Manage count toward
    // the sweep. Two labeled rooms with one turned off (or adopted via sync, which
    // arrives off) leaves just one manageable → Start refuses rather than walking a
    // one-room "circuit" or reaching toward the disabled room's (possibly foreign)
    // gang house. This path returns before any route plotting, so the setup is minimal.
    [Fact]
    public void Start_FewerThanTwoActivelyManagedRooms_Refuses()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), "[]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(_realm);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, postToUi: action => action());
        // The per-character managed set (stand-in for GhManagedRoomStore) — mutable so
        // we can drop one room between the two Start attempts.
        HashSet<RoomKey> managed = new() { new RoomKey(1, 1), new RoomKey(1, 2) };
        GhSweepManager sweep = new(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory,
            isRoomActivelyManaged: managed.Contains);
        sweep.SetWireSender(_ => { });
        runner.SetWireSender(_ => { });
        tracker.SetLocated(new RoomKey(1, 1));

        // Both managed → starts fine.
        Assert.True(sweep.Start());
        sweep.Stop("test reset");

        // Un-manage one (mirrors a synced / other-character room) → only one
        // manageable → refused.
        managed.Remove(new RoomKey(1, 2));
        Assert.False(sweep.Start());
        Assert.Contains("Actively Manage", sweep.LastStartError);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }
}
