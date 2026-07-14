using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FujinTerm.Services;

// ObservableCollection that can be refilled in one shot. A bulk (re)load done as
// Clear + N Add fires N+1 CollectionChanged events, and every index-rebuilding
// subscriber (MonsterDeathWatcher, ConditionTracker, SpellCoverageAuditor)
// rebuilds its whole index on each one — O(n²) over a ~1100-record catalogue at
// startup and on every game-data set switch. ReplaceAll swaps the backing list
// with no per-item notification and then raises a single Reset, so each
// subscriber rebuilds exactly once. Individual Add / Remove / indexer edits (the
// data editor's per-record upserts) keep their normal per-op notification, which
// downstream rebuilders and tests rely on for synchronous freshness.
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }

    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    // Replace the entire contents, raising a single Reset instead of Clear + N Add.
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (T item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
