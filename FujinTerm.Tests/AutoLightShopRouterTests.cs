using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Light;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="AutoLightShopRouter"/> — the provisioning detour that turns the
/// provisioner's <see cref="AutoLightAction.Buy"/> verdict into a walk-to-shop /
/// <c>buy</c> run and resumes to the original destination. Exercises the master-
/// toggle gate, engine-walk suppression, fewest-added-steps shop pick, the buy
/// batch, resume-on-first-copy (one covers the dark route), and the failure
/// suppression that keeps a re-announced dark route from looping the detour.
/// </summary>
public sealed class AutoLightShopRouterTests
{
    private static readonly RoomKey Cur = new(1, 100);
    private static readonly RoomKey Dest = new(1, 200);
    private static readonly RoomKey Dest2 = new(1, 300);
    private static readonly RoomKey ShopA = new(1, 150);
    private static readonly RoomKey ShopB = new(1, 160);

    // lantern: MDB id 2, buyable in the harness shop.
    private const int LanternId = 2;

    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    private static AutoLightBuyRequest Buy(int count = 1)
        => new(LanternId, "lantern", count);

    private sealed class Harness
    {
        public readonly Dictionary<int, List<RoomKey>> ShopRooms = new();
        public readonly Dictionary<(RoomKey From, RoomKey To), int> Dist = new();
        public readonly Dictionary<int, int> Carried = new();
        public RoomKey? Current = Cur;
        public RoomKey? WalkDest = Dest;
        public bool Enabled = true;
        public bool EngineWalk;
        public readonly List<RoomKey> Walks = new();

        public void Carry(int id, int n = 1) => Carried[id] = n;

        public AutoLightShopRouter Build() => new(
            shopRoomsSellingItem: id => ShopRooms.TryGetValue(id, out List<RoomKey>? r)
                ? r
                : (IReadOnlyList<RoomKey>)Array.Empty<RoomKey>(),
            currentRoom: () => Current,
            walkDestination: () => WalkDest,
            distanceBetween: (a, b) => Dist.TryGetValue((a, b), out int d) ? d : null,
            carriedCount: id => Carried.TryGetValue(id, out int c) ? c : 0,
            isEnabled: () => Enabled,
            engineWalkActive: () => EngineWalk,
            walkTo: Walks.Add,
            post: a => a(),                          // synchronous in tests
            log: null,
            buyTimeout: TimeSpan.FromHours(1));       // real timer never fires mid-test

        // One shop (ShopA) selling the lantern, three steps out, four steps on.
        public Harness WithSingleShop()
        {
            ShopRooms[LanternId] = new List<RoomKey> { ShopA };
            Dist[(Cur, ShopA)] = 3;
            Dist[(ShopA, Dest)] = 4;
            return this;
        }
    }

    [Fact]
    public void OnBuyRequested_ShopExists_DetoursToShop()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.True(r.DetourActive);
        Assert.Equal(ShopA, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnBuyRequested_FeatureOff_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Enabled = false;
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnBuyRequested_EngineWalkActive_NoDetour()
    {
        // A loop / auto-lair run drives movement — don't hijack it to a shop.
        var h = new Harness().WithSingleShop();
        h.EngineWalk = true;
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnBuyRequested_InvalidRequest_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(new AutoLightBuyRequest(0, "", 1));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_NoShopSellsLight_NoDetour()
    {
        var h = new Harness();           // no ShopRooms entry
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_NoCurrentRoom_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.Current = null;
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_NoWalkDestination_NoDetour()
    {
        var h = new Harness().WithSingleShop();
        h.WalkDest = null;
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_TwoShops_PicksFewestAddedSteps()
    {
        var h = new Harness();
        h.ShopRooms[LanternId] = new List<RoomKey> { ShopA, ShopB };
        h.Dist[(Cur, ShopA)] = 2; h.Dist[(ShopA, Dest)] = 8;   // total 10
        h.Dist[(Cur, ShopB)] = 5; h.Dist[(ShopB, Dest)] = 3;   // total 8  ← min
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.Equal(ShopB, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnBuyRequested_UnreachableShopSkipped()
    {
        var h = new Harness();
        h.ShopRooms[LanternId] = new List<RoomKey> { ShopA, ShopB };
        h.Dist[(Cur, ShopA)] = 2; h.Dist[(ShopA, Dest)] = 2;   // A reachable
        // ShopB has no distances → unreachable, must be skipped.
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.Equal(ShopA, Assert.Single(h.Walks));
    }

    [Fact]
    public void OnBuyRequested_AllShopsUnreachable_NoDetour()
    {
        var h = new Harness();
        h.ShopRooms[LanternId] = new List<RoomKey> { ShopA };   // no distances at all
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnWalkEvent_ArriveAtShop_SendsBuyBatch()
    {
        // Provisioning a two-copy carry batch → two `buy lantern`s at the shop.
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy(count: 2));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        Assert.Equal(
            new[] { "buy lantern", "buy lantern" },
            r.LastSentForTests.Select(Decode).ToArray());
        Assert.True(r.DetourActive);     // still Buying until a copy lands
    }

    [Fact]
    public void OnWalkEvent_ArriveAtShop_BuysOnlyTheMissingDelta()
    {
        // Already hold one of the three-copy target → buy only the two short.
        var h = new Harness().WithSingleShop();
        h.Carry(LanternId, 1);
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy(count: 3));

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        Assert.Equal(2, r.LastSentForTests.Count);
    }

    [Fact]
    public void OnInventoryChanged_FirstCopyLands_ResumesEvenMidBatch()
    {
        // Buying three, but the first copy covers the dark route → resume now;
        // the remaining buys already fired and still land as we walk off.
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy(count: 3));
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        h.Carry(LanternId, 1);           // first copy in the pack
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnInventoryChanged_FoundBeforeArrival_AbortsDetourAndResumes()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());         // walking to shop

        h.Carry(LanternId, 1);           // picked one up en route
        r.OnInventoryChanged();

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
        Assert.Empty(r.LastSentForTests); // never reached the shop, never bought
    }

    [Fact]
    public void OnInventoryChanged_NoNewCopy_KeepsDetour()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());

        r.OnInventoryChanged();          // unrelated inventory change

        Assert.True(r.DetourActive);
        Assert.Single(h.Walks);          // still only the shop walk
    }

    [Fact]
    public void OnBuyTimeout_BuyDidNotLand_ResumesToDestination()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));

        r.OnBuyTimeout();                // no copy ever appeared

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_ShopUnreachable_ResumesToDestination()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", ShopA));

        Assert.Equal(2, h.Walks.Count);
        Assert.Equal(Dest, h.Walks[1]);
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnWalkEvent_UserRedirects_AbandonsQuietly()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());

        r.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "user walk", null));

        Assert.Single(h.Walks);          // only the shop walk — no forced resume
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_SecondRequestWhileDetouring_Ignored()
    {
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();

        r.OnBuyRequested(Buy());         // detour armed
        r.OnBuyRequested(Buy());         // busy — re-fired verdict must be ignored

        Assert.Single(h.Walks);
        Assert.Equal(ShopA, h.Walks[0]);
    }

    [Fact]
    public void OnBuyRequested_WalkToSupersedeStopped_DetourSurvivesAndBuys()
    {
        // AutoWalkManager.WalkTo pre-empts the in-progress walk with a
        // Stopped("superseded by new walk") event. Delivered back into
        // Phase.WalkingToShop it must NOT be read as a user takeover — otherwise
        // the router tears down the detour it just armed, the re-announced dark
        // route re-fires the Buy verdict, and it re-detours forever without ever
        // buying (the report's symptom).
        var shops = new List<RoomKey> { ShopA };
        var dist = new Dictionary<(RoomKey, RoomKey), int>
        {
            [(Cur, ShopA)] = 3,
            [(ShopA, Dest)] = 4,
        };
        AutoLightShopRouter? router = null;
        var walks = new List<RoomKey>();

        router = new AutoLightShopRouter(
            shopRoomsSellingItem: _ => shops,
            currentRoom: () => Cur,
            walkDestination: () => Dest,
            distanceBetween: (a, b) => dist.TryGetValue((a, b), out int d) ? d : (int?)null,
            carriedCount: _ => 0,
            isEnabled: () => true,
            engineWalkActive: () => false,
            walkTo: d =>
            {
                walks.Add(d);
                // Mimic WalkTo superseding the prior in-flight walk.
                router!.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "superseded by new walk", Dest));
            },
            post: a => a(),
            log: null,
            buyTimeout: TimeSpan.FromHours(1));

        router.OnBuyRequested(Buy());

        Assert.True(router.DetourActive);              // survived the supersede-Stopped
        Assert.Equal(ShopA, Assert.Single(walks));

        router.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));
        Assert.Equal("buy lantern", Decode(Assert.Single(router.LastSentForTests)));
    }

    // ----- Failure suppression: the re-announced dark route can't loop -------

    [Fact]
    public void OnBuyRequested_AfterFailedBuySameDest_Suppressed()
    {
        // Buy fails at the shop → resume to Dest. The resumed walk re-announces
        // the same dark route, re-firing the Buy verdict; it must NOT re-detour.
        var h = new Harness().WithSingleShop();
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));
        r.OnBuyTimeout();                // failed → resumed to Dest (Walks: shop, Dest)

        r.OnBuyRequested(Buy());         // same Dest still active — suppressed

        Assert.Equal(2, h.Walks.Count);  // no third walk
        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnBuyRequested_AfterFailureNewDestination_Retries()
    {
        // A failed buy only suppresses re-detour to THAT destination. Once the
        // player heads somewhere else, provisioning is fair game again.
        var h = new Harness().WithSingleShop();
        h.Dist[(Cur, ShopA)] = 3; h.Dist[(ShopA, Dest2)] = 1;   // shop reaches Dest2 too
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());
        r.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "reached", ShopA));
        r.OnBuyTimeout();                // failed → suppressed Dest

        h.WalkDest = Dest2;              // player walks elsewhere
        r.OnBuyRequested(Buy());

        Assert.True(r.DetourActive);
        Assert.Equal(ShopA, h.Walks[^1]);   // detoured again for the new trip
    }

    [Fact]
    public void OnBuyRequested_UnreachableThenReachableSameDest_Suppressed()
    {
        // No reachable shop on the first ask suppresses this destination too, so
        // the immediately re-announced route doesn't re-probe every hop.
        var h = new Harness();
        h.ShopRooms[LanternId] = new List<RoomKey> { ShopA };   // no distances → unreachable
        AutoLightShopRouter r = h.Build();
        r.OnBuyRequested(Buy());
        Assert.Empty(h.Walks);

        // Even if the graph now "reaches" it, the same destination stays suppressed.
        h.Dist[(Cur, ShopA)] = 2; h.Dist[(ShopA, Dest)] = 2;
        r.OnBuyRequested(Buy());

        Assert.Empty(h.Walks);
        Assert.False(r.DetourActive);
    }
}
