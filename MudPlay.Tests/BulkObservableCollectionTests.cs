using System.Collections.Specialized;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the invariant the O(n²) startup fix depends on: a bulk ReplaceAll raises
// exactly one Reset, so an index-rebuilding subscriber rebuilds once.
public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesSingleReset_NotPerItem()
    {
        BulkObservableCollection<int> col = new();
        col.ReplaceAll(new[] { 1, 2 });
        List<NotifyCollectionChangedAction> actions = new();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll(new[] { 10, 20, 30, 40 });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
        Assert.Equal(new[] { 10, 20, 30, 40 }, col);
    }

    [Fact]
    public void ReplaceAll_WithEmpty_ClearsToOneReset()
    {
        BulkObservableCollection<int> col = new();
        col.ReplaceAll(new[] { 1, 2, 3 });
        List<NotifyCollectionChangedAction> actions = new();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll(Array.Empty<int>());

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
        Assert.Empty(col);
    }

}
