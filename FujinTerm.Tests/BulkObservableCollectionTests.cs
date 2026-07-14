using System.Collections.Specialized;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins the invariant the O(n²) startup fix depends on: a bulk ReplaceAll raises
// exactly one Reset (so an index-rebuilding subscriber rebuilds once), while a
// per-item Add keeps its normal per-op notification (editor upserts stay
// synchronously fresh for downstream rebuilders).
public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesSingleReset_NotPerItem()
    {
        BulkObservableCollection<int> col = new(new[] { 1, 2 });
        List<NotifyCollectionChangedAction> actions = new();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll(new[] { 10, 20, 30, 40 });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
        Assert.Equal(new[] { 10, 20, 30, 40 }, col);
    }

    [Fact]
    public void ReplaceAll_WithEmpty_ClearsToOneReset()
    {
        BulkObservableCollection<int> col = new(new[] { 1, 2, 3 });
        List<NotifyCollectionChangedAction> actions = new();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll(Array.Empty<int>());

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
        Assert.Empty(col);
    }

    [Fact]
    public void Add_KeepsPerItemNotification()
    {
        BulkObservableCollection<int> col = new();
        List<NotifyCollectionChangedAction> actions = new();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.Add(1);
        col.Add(2);

        Assert.Equal(
            new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add },
            actions);
    }
}
