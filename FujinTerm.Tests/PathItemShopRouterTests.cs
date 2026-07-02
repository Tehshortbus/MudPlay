using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PathItemShopRouterTests
{
    private static readonly RoomKey Cur = new(1, 100);
    private static readonly RoomKey Dest = new(1, 200);
    private static readonly RoomKey ShopA = new(1, 150);
    private static readonly RoomKey ShopB = new(1, 160);

    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    private static Need PathNeed(int id)
        => new(NeedKind.PathItem, id.ToString(), "test", DateTimeOffset.Now);

    private sealed class Harness
    {
        public readonly Dictionary<int, List<RoomKey>> ShopRooms = new();
        public readonly Dictionary<(RoomKey From, RoomKey To), int> Dist = new();
        public readonly HashSet<int> Carried = new();
        public readonly Dictionary<int, string> Names = new() { [42] = "boat" };
        public RoomKey? Current = Cur;
        public RoomKey? WalkDest = Dest;
        public bool Enabled = true;
        public bool EngineWalk;
        public readonly List<RoomKey> Walks = new();

        public PathItemShopRouter Build() => new(
            shopRoomsSellingItem: id => ShopRooms.TryGetValue(id, out List<RoomKey>? r)
                ? r
                : (IReadOnlyList<RoomKey>)Array.Empty<RoomKey>(),
            currentRoom: () => Current,
            walkDestination: () => WalkDest,
            distanceBetween: (a, b) => Dist.TryGetValue((a, b), out int d) ? d : null,
            isCarried: Carried.Contains,
            itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
            isEnabled: () => Enabled,
            engineWalkActive: () => EngineWalk,
            walkTo: Walks.Add,
            post: a => a(),                          // synchronous in tests
            log: null,
            buyTimeout: TimeSpan.FromHours(1));       // real timer never fires mid-test

        // One shop (ShopA) selling item 42, three steps out, four steps on to dest.
        public Harness WithSingleShop()
        {
            ShopRooms[42] = new List<RoomKey> { ShopA };
            Dist[(Cur, ShopA)] = 3;
            Dist[(ShopA, Dest)] = 4;
            return this;
        }
    }

    [Fact]
    public void OnNeedPosted_MissingItemShopExists_DetoursToShop()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.True(r.DetourActive);
        Assert.Equal(ShopA, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_FeatureOff_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Enabled = false;
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_EngineWalkActive_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.EngineWalk = true;             // a loop / auto-lair drives movement
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_ItemAlreadyCarried_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Carried.Add(42);
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoShopSellsItem_NoDetour()
    {
        var h = new Harness();           // no ShopRooms entry
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoCurrentRoom_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Current = null;
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoActiveWalkDestination_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.WalkDest = null;
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoItemName_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Names.Clear();                 // can't compose a `buy <name>`
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NonPathItemNeed_Ignored()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(new Need(NeedKind.LightSource, "illu>=1", "test", DateTimeOffset.Now));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_TwoShops_PicksFewestAddedSteps()
    {
        var h = new Harness();
        h.ShopRooms[42] = new List<RoomKey> { ShopA, ShopB };
        h.Dist[(Cur, ShopA)] = 2; h.Dist[(ShopA, Dest)] = 8;   // total 10
        h.Dist[(Cur, ShopB)] = 5; h.Dist[(ShopB, Dest)] = 3;   // total 8  ← min
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(ShopB, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_UnreachableShopSkipped()
    {
        var h = new Harness();
        h.ShopRooms[42] = new List<RoomKey> { ShopA, ShopB };
        h.Dist[(Cur, ShopA)] = 2; h.Dist[(ShopA, Dest)] = 2;   // A reachable
        // ShopB has no distances → unreachable, must be skipped.
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.Equal(ShopA, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnNeedPosted_AllShopsUnreachable_NoDetour()
    {
        var h = new Harness();
        h.ShopRooms[42] = new List<RoomKey> { ShopA };   // no distances at all
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnWalkEvent_ArriveAtShop_SendsBuy()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        Assert.Equal("buy boat", Decode(Assert.Single(r.LastSentForTests)));
        Assert.True(r.DetourActive);     // now Buying
    }

    [Fact]
    public void OnInventoryChanged_BuySucceeds_ResumesToDestination()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        h.Carried.Add(42);               // the buy landed
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnInventoryChanged_FoundBeforeArrival_AbortsDetourAndResumes()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));    // walking to shop

        h.Carried.Add(42);               // search revealed it en route
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
        Assert.Empty(r.LastSentForTests); // never reached the shop, never bought
    }

    [Fact]
    public void OnInventoryChanged_ItemStillMissing_KeepsDetour()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnInventoryChanged();          // unrelated inventory change

        Assert.True(r.DetourActive);
        Assert.Single(h.Walks);          // still only the shop walk
    }

    [Fact]
    public void OnBuyTimeout_BuyDidNotLand_ResumesToDestination()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        r.OnBuyTimeout();                // item never appeared

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_ShopUnreachable_ResumesToDestination()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", ShopA));

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_UserRedirects_AbandonsQuietly()
    {
        var h = new Harness().WithSingleShop();
        PathItemShopRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "user walk", null));

        Assert.Single(h.Walks);          // only the shop walk — no forced resume
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_SecondNeedWhileDetouring_Ignored()
    {
        var h = new Harness().WithSingleShop();
        h.ShopRooms[43] = new List<RoomKey> { ShopB };
        h.Dist[(Cur, ShopB)] = 1; h.Dist[(ShopB, Dest)] = 1;
        h.Names[43] = "rope";
        PathItemShopRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));    // detour armed for 42
        r.OnNeedPosted(PathNeed(43));    // busy — must be ignored (one item per walk)

        Assert.Single(h.Walks);
        Assert.Equal(ShopA, h.Walks[0]);
    }
}
