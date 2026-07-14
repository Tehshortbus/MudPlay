using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Cash;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Modeless Transaction history window VM — a projection over
// TransactionHistoryTracker. Rebuilds Rows on the tracker's Changed signal
// (marshalled to the dispatcher) in newest-first order so the latest deposit
// / stash sits at the top. The ledger is user-owned: nothing in the automation
// path clears it (loop starts and party @reset no longer touch it), so the
// window's Clear button — this VM's Clear command — is the sole in-app wipe
// besides the connect / character-switch boundary.
public sealed partial class TransactionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly TransactionHistoryTracker _tracker;
    private bool _disposed;

    // The session's recorded transactions, newest first.
    public ObservableCollection<TransactionEntry> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    // Drives the "no transactions yet" placeholder.
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

    // The only user-driven clear of the ledger. Resetting the tracker raises
    // Changed, which rebuilds Rows empty via OnChanged. Also truncates the
    // persisted transactions.log so the on-disk copy matches — this is the
    // explicit user wipe, distinct from the session-boundary Reset the tracker
    // takes on connect / character switch (which must leave the log intact).
    [RelayCommand]
    private void Clear()
    {
        _tracker.Reset();
        AppServices.Current.SessionLog.TruncateTransactions();
    }

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
