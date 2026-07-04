using System.Collections.Generic;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PathItemDemandTrackerTests
{
    private sealed class Harness
    {
        public readonly NeedsRegistry Needs = new();
        public readonly Dictionary<int, int> Carried = new();
        public bool InventoryLoaded = true;
        public bool Enabled = true;

        public PathItemDemandTracker Build() => new(
            Needs,
            carriedCount: id => Carried.TryGetValue(id, out int n) ? n : 0,
            inventoryLoaded: () => InventoryLoaded,
            isEnabled: () => Enabled);

        public void Carry(int id, int n = 1) => Carried[id] = n;

        public int OutstandingCount => Needs.Outstanding(NeedKind.PathItem).Count;

        public int? QuantityOf(int id)
        {
            foreach (Need n in Needs.Outstanding(NeedKind.PathItem))
                if (n.Descriptor == id.ToString()) return n.Quantity;
            return null;
        }
    }

    [Fact]
    public void OnPathItemsRequired_MissingItem_PostsNeedAndArmsDemand()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 });

        Assert.Equal(1, h.OutstandingCount);
        Assert.True(t.SearchDemandActive);
    }

    [Fact]
    public void OnPathItemsRequired_CarriedItem_PostsNothing()
    {
        var h = new Harness();
        h.Carry(42);
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 });

        Assert.Equal(0, h.OutstandingCount);
        Assert.False(t.SearchDemandActive);
    }

    [Fact]
    public void OnPathItemsRequired_FeatureOff_PostsNothing()
    {
        var h = new Harness { Enabled = false };
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 });

        Assert.Equal(0, h.OutstandingCount);
    }

    [Fact]
    public void OnPathItemsRequired_InventoryNotLoaded_PostsNothing()
    {
        var h = new Harness { InventoryLoaded = false };
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 });

        Assert.Equal(0, h.OutstandingCount);
    }

    [Fact]
    public void OnPathItemsRequired_DedupesRepeatedAndSkipsNonPositive()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 0, -1, 7, 7, 7 });

        Assert.Equal(1, h.OutstandingCount);
    }

    [Fact]
    public void OnPathItemsRequired_MultipleMissingItems_PostsEach()
    {
        var h = new Harness();
        h.Carry(2);                 // carried — should be skipped
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 1, 2, 3 });

        Assert.Equal(2, h.OutstandingCount);
    }

    [Fact]
    public void OnPathItemsRequired_ReAnnounce_DedupesViaRegistry()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 });
        t.OnPathItemsRequired(new[] { 42 });   // superseded walk, same gate

        Assert.Equal(1, h.OutstandingCount);
    }

    [Fact]
    public void OnInventoryChanged_ItemAcquired_ResolvesNeedAndDropsDemand()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();
        t.OnPathItemsRequired(new[] { 42 });
        Assert.True(t.SearchDemandActive);

        h.Carry(42);                // item turns up in the pack
        t.OnInventoryChanged();

        Assert.Equal(0, h.OutstandingCount);
        Assert.False(t.SearchDemandActive);
    }

    [Fact]
    public void OnInventoryChanged_ItemStillMissing_LeavesNeedOutstanding()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();
        t.OnPathItemsRequired(new[] { 42 });

        t.OnInventoryChanged();     // unrelated inventory change

        Assert.Equal(1, h.OutstandingCount);
        Assert.True(t.SearchDemandActive);
    }

    [Fact]
    public void SearchDemandActive_FalseWhenFeatureOffDespiteOutstandingNeed()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();
        t.OnPathItemsRequired(new[] { 42 });
        Assert.True(t.SearchDemandActive);

        h.Enabled = false;

        Assert.False(t.SearchDemandActive);
        Assert.Equal(1, h.OutstandingCount);   // need itself persists
    }

    [Fact]
    public void OnPathItemsRequired_EmptyRoute_NoNeeds()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(System.Array.Empty<int>());

        Assert.Equal(0, h.OutstandingCount);
        Assert.False(t.SearchDemandActive);
    }

    // ----- Quantity (party shortfall) --------------------------------------

    [Fact]
    public void OnPathItemsRequired_Quantity_PostsNeedWithCount()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 }, quantity: 4);   // leader provisions a party of four

        Assert.Equal(4, h.QuantityOf(42));
    }

    [Fact]
    public void OnPathItemsRequired_CarrySomeButNotAll_StillPosts()
    {
        var h = new Harness();
        h.Carry(42, 1);             // hold one, but the party needs three
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 }, quantity: 3);

        Assert.Equal(1, h.OutstandingCount);
        Assert.Equal(3, h.QuantityOf(42));
    }

    [Fact]
    public void OnPathItemsRequired_CarryFullCount_PostsNothing()
    {
        var h = new Harness();
        h.Carry(42, 3);             // already hold the whole shortfall
        PathItemDemandTracker t = h.Build();

        t.OnPathItemsRequired(new[] { 42 }, quantity: 3);

        Assert.Equal(0, h.OutstandingCount);
    }

    [Fact]
    public void OnInventoryChanged_FirstCopyOfMany_LeavesNeedOutstanding()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();
        t.OnPathItemsRequired(new[] { 42 }, quantity: 3);

        h.Carry(42, 1);             // one copy in — still two short
        t.OnInventoryChanged();

        Assert.Equal(1, h.OutstandingCount);
        Assert.True(t.SearchDemandActive);
    }

    [Fact]
    public void OnInventoryChanged_FullCountReached_ResolvesNeed()
    {
        var h = new Harness();
        PathItemDemandTracker t = h.Build();
        t.OnPathItemsRequired(new[] { 42 }, quantity: 3);

        h.Carry(42, 3);             // the whole shortfall is now carried
        t.OnInventoryChanged();

        Assert.Equal(0, h.OutstandingCount);
        Assert.False(t.SearchDemandActive);
    }
}
