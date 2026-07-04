using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Cash;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless Transaction history window VM — a pure projection over
/// <see cref="TransactionHistoryTracker"/>. Rebuilds <see cref="Rows"/> on the
/// tracker's <c>Changed</c> signal (marshalled to the dispatcher) in
/// newest-first order so the latest deposit / stash sits at the top. The
/// tracker owns all the state and the session-reset boundary, so this VM never
/// mutates anything.
/// </summary>
public sealed partial class TransactionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly TransactionHistoryTracker _tracker;
    private bool _disposed;

    /// <summary>The session's recorded transactions, newest first.</summary>
    public ObservableCollection<TransactionEntry> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    /// <summary>Drives the "no transactions yet" placeholder.</summary>
    public bool IsEmpty => Count == 0;

    public TransactionHistoryViewModel(TransactionHistoryTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        Rebuild();
        _tracker.Changed += OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) Rebuild();
    });

    private void Rebuild()
    {
        Rows.Clear();
        IReadOnlyList<TransactionEntry> snap = _tracker.Snapshot();
        for (int i = snap.Count - 1; i >= 0; i--) // newest first
            Rows.Add(snap[i]);
        Count = Rows.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.Changed -= OnChanged;
    }
}
