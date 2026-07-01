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
        public readonly HashSet<int> Carried = new();
        public bool InventoryLoaded = true;
        public bool Enabled = true;

        public PathItemDemandTracker Build() => new(
            Needs,
            isCarried: Carried.Contains,
            inventoryLoaded: () => InventoryLoaded,
            isEnabled: () => Enabled);

        public int OutstandingCount => Needs.Outstanding(NeedKind.PathItem).Count;
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
        h.Carried.Add(42);
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
        h.Carried.Add(2);           // carried — should be skipped
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

        h.Carried.Add(42);          // item turns up in the pack
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
}
