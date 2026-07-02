using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MonsterDropRouterTests
{
    private static readonly RoomKey Cur = new(1, 100);
    private static readonly RoomKey Dest = new(1, 200);
    private static readonly RoomKey Spawn = new(1, 150);
    private static readonly RoomKey Spawn2 = new(1, 160);

    private static Need PathNeed(int id)
        => new(NeedKind.PathItem, id.ToString(), "test", DateTimeOffset.Now);

    private sealed class Harness
    {
        public readonly Dictionary<int, List<MonsterDropSpawn>> DropSpawns = new();
        public readonly HashSet<int> ShopItems = new();
        public readonly Dictionary<RoomKey, int> Distances = new();
        public readonly HashSet<int> Carried = new();
        public readonly Dictionary<int, string> Names = new() { [42] = "rune" };
        public RoomKey? Current = Cur;
        public RoomKey? WalkDest = Dest;
        public bool Enabled = true;
        public bool EngineWalk;
        public readonly List<RoomKey> Walks = new();

        // Confirm-prompt control. ConfirmGate, when set, defers the answer so a
        // test can drive an inventory change / user walk while the prompt is
        // "open"; otherwise the answer is ConfirmAnswer, resolved synchronously.
        public bool ConfirmAnswer = true;
        public int ConfirmCalls;
        public string? LastConfirmTitle;
        public string? LastConfirmBody;
        public TaskCompletionSource<bool>? ConfirmGate;

        public MonsterDropRouter Build() => new(
            dropSpawnsForItem: id => DropSpawns.TryGetValue(id, out List<MonsterDropSpawn>? l)
                ? l
                : (IReadOnlyList<MonsterDropSpawn>)Array.Empty<MonsterDropSpawn>(),
            anyShopSells: ShopItems.Contains,
            currentRoom: () => Current,
            walkDestination: () => WalkDest,
            distancesFrom: _ => Distances,
            isCarried: Carried.Contains,
            itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
            isEnabled: () => Enabled,
            engineWalkActive: () => EngineWalk,
            confirm: (title, body) =>
            {
                ConfirmCalls++;
                LastConfirmTitle = title;
                LastConfirmBody = body;
                return ConfirmGate?.Task ?? Task.FromResult(ConfirmAnswer);
            },
            walkTo: Walks.Add,
            post: a => a(),                          // synchronous in tests
            log: null);

        // One monster (gremlin) dropping item 42 at Spawn, three steps out.
        public Harness WithSingleSpawn()
        {
            DropSpawns[42] = new List<MonsterDropSpawn> { new(Spawn, 7, "gremlin", 25) };
            Distances[Spawn] = 3;
            return this;
        }
    }

    [Fact]
    public void OnNeedPosted_UnsoldItemMonsterDrops_PromptsAndReroutes()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(1, h.ConfirmCalls);
        Assert.True(r.DetourActive);
        Assert.Equal(Spawn, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_Declined_NoReroute()
    {
        var h = new Harness().WithSingleSpawn();
        h.ConfirmAnswer = false;
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(1, h.ConfirmCalls);
        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_ShopSellsItem_Ignored()
    {
        var h = new Harness().WithSingleSpawn();
        h.ShopItems.Add(42);             // PathItemShopRouter's job, not ours
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_FeatureOff_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.Enabled = false;
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_EngineWalkActive_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.EngineWalk = true;             // a loop / auto-lair drives movement
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_ItemAlreadyCarried_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.Carried.Add(42);
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoMonsterDropsItem_NoPrompt()
    {
        var h = new Harness();           // no DropSpawns entry
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoCurrentRoom_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.Current = null;
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoActiveWalkDestination_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.WalkDest = null;
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoItemName_NoPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.Names.Clear();
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NonPathItemNeed_Ignored()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(new Need(NeedKind.LightSource, "illu>=1", "test", DateTimeOffset.Now));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_TwoSpawns_PicksNearest()
    {
        var h = new Harness();
        h.DropSpawns[42] = new List<MonsterDropSpawn>
        {
            new(Spawn,  7, "gremlin", 25),
            new(Spawn2, 8, "kobold",  25),
        };
        h.Distances[Spawn]  = 6;
        h.Distances[Spawn2] = 2;   // nearer
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(Spawn2, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_TieDistance_PicksHigherDropPercent()
    {
        var h = new Harness();
        h.DropSpawns[42] = new List<MonsterDropSpawn>
        {
            new(Spawn,  7, "gremlin", 10),
            new(Spawn2, 8, "kobold",  80),   // same distance, better odds
        };
        h.Distances[Spawn]  = 4;
        h.Distances[Spawn2] = 4;
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(Spawn2, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_AllSpawnsUnreachable_NoPrompt()
    {
        var h = new Harness();
        h.DropSpawns[42] = new List<MonsterDropSpawn> { new(Spawn, 7, "gremlin", 25) };
        // No distance entry for Spawn → unreachable, nothing to select.
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(0, h.ConfirmCalls);
        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnWalkEvent_ArriveAtSpawn_EntersHunting()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", Spawn));

        Assert.True(r.DetourActive);     // now Hunting
        Assert.Single(h.Walks);          // no command, no extra walk — just waiting
    }

    [Fact]
    public void OnInventoryChanged_DropLands_ResumesToDestination()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", Spawn));

        h.Carried.Add(42);               // the monster dropped it
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnInventoryChanged_FoundBeforeArrival_AbortsAndResumes()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));    // walking to spawn

        h.Carried.Add(42);               // search revealed it en route
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnInventoryChanged_FoundDuringConfirm_AbortsPendingPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.ConfirmGate = new TaskCompletionSource<bool>();
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));    // prompt open, awaiting the user
        Assert.Equal(1, h.ConfirmCalls);
        Assert.Empty(h.Walks);           // not walking yet

        h.Carried.Add(42);               // item turns up while the prompt sits
        r.OnInventoryChanged();

        Assert.Equal(Dest, Assert.Single(h.Walks));   // resumed straight to dest
        Assert.False(r.DetourActive);

        h.ConfirmGate.SetResult(true);   // stale "yes" arrives — must be a no-op
        Assert.Equal(Dest, Assert.Single(h.Walks));   // no reroute to spawn
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnInventoryChanged_ItemStillMissing_KeepsDetour()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnInventoryChanged();          // unrelated inventory change

        Assert.True(r.DetourActive);
        Assert.Single(h.Walks);          // still only the spawn walk
    }

    [Fact]
    public void OnWalkEvent_SpawnUnreachable_ResumesToDestination()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", Spawn));

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_UserRedirects_AbandonsQuietly()
    {
        var h = new Harness().WithSingleSpawn();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "user walk", null));

        Assert.Single(h.Walks);          // only the spawn walk — no forced resume
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_UserWalksDuringConfirm_AbandonsPrompt()
    {
        var h = new Harness().WithSingleSpawn();
        h.ConfirmGate = new TaskCompletionSource<bool>();
        MonsterDropRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));    // prompt open

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Started, "user walk", Spawn2));
        Assert.False(r.DetourActive);    // abandoned while prompt sits

        h.ConfirmGate.SetResult(true);   // stale "yes" — no reroute
        Assert.Empty(h.Walks);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_SecondNeedWhileDetouring_Ignored()
    {
        var h = new Harness().WithSingleSpawn();
        h.DropSpawns[43] = new List<MonsterDropSpawn> { new(Spawn2, 8, "kobold", 50) };
        h.Distances[Spawn2] = 1;
        h.Names[43] = "amulet";
        MonsterDropRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));    // reroute armed for 42
        r.OnNeedPosted(PathNeed(43));    // busy — must be ignored (one item per walk)

        Assert.Single(h.Walks);
        Assert.Equal(Spawn, h.Walks[0]);
    }
}
