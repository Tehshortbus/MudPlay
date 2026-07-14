namespace FujinTerm.Game.Cash;

// A per-session ledger of cash/item offloads for the Session Stats →
// Transaction history window. Records one TransactionEntry per bank `dep`osit
// (fed from AutoDepositManager.Deposited) and per stash-room `hide` (fed from
// StashRoomManager.StashExecuted), each with the wall-clock time, the store kind,
// and a rendered description of what was put away.
//
// Owns no source subscriptions — AppServices wires the bank / stash events to the
// Note* forwarders — matching SessionActivityTracker and keeping the tracker
// dependency-free behind an injectable clock for unit tests. Every write and
// Snapshot runs on the marshalled dispatch thread (the sources all fire there),
// so the list is lock-free. Reset on the same session boundary as the other
// session-stats trackers (connect / character switch, the window "Reset session"
// button, and @reset).
public sealed class TransactionHistoryTracker
{
    // Hard cap on retained entries; the oldest is evicted past this so a long
    // session can't grow the ledger unbounded.
    public const int MaxEntries = 500;

    private readonly Func<DateTimeOffset> _clock;
    private readonly List<TransactionEntry> _entries = new();

    // Raised after any input records or clears an entry, so the Transaction
    // history VM can rebuild. Fires on the dispatch thread.
    public event Action? Changed;

    // Raised with each newly recorded entry (not on Reset), so a persistent log
    // can append it. Kept separate from Changed because the bounded ledger
    // evicts its oldest row past MaxEntries — diffing snapshots would miss the
    // append, so the tracker hands the fresh entry over directly.
    public event Action<TransactionEntry>? EntryAdded;

    public TransactionHistoryTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    // Record a bank deposit of the given copper wealth value. Non-positive
    // amounts are ignored (nothing was deposited).
    public void NoteBankDeposit(long copper)
    {
        if (copper <= 0) return;
        Add(TransactionKind.Bank, $"Deposited {copper:N0} wealth");
    }

    // Record a stash-room hide of the given per-denomination coin amounts and
    // item names. A dispatch with neither coins nor items is ignored (the stash
    // event never fires empty, but the guard keeps the ledger clean).
    public void NoteStash(
        IReadOnlyList<(string Currency, long Amount)> currencies,
        IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(items);
        if (currencies.Count == 0 && items.Count == 0) return;
        Add(TransactionKind.Stash, FormatStash(currencies, items));
    }

    // Point-in-time copy of the ledger, oldest entry first.
    public IReadOnlyList<TransactionEntry> Snapshot() => _entries.ToArray();

    // Clear the ledger — called on the connect / character-switch boundary and by
    // the manual / remote session reset, matching the other session-stats
    // trackers.
    public void Reset()
    {
        _entries.Clear();
        Changed?.Invoke();
    }

    private void Add(TransactionKind kind, string detail)
    {
        TransactionEntry entry = new(_clock(), kind, detail);
        _entries.Add(entry);
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        EntryAdded?.Invoke(entry);
        Changed?.Invoke();
    }

    // "Hid a torch ×3, 400 gold, 40 platinum" — identical item tokens fold
    // into "name ×N" (MajorMUD lists each carried copy separately), then the
    // per-denomination coin amounts follow in their native denomination.
    private static string FormatStash(
        IReadOnlyList<(string Currency, long Amount)> currencies,
        IReadOnlyList<string> items)
    {
        List<string> parts = new();
        foreach (IGrouping<string, string> g in items.GroupBy(i => i, StringComparer.Ordinal))
        {
            int n = g.Count();
            parts.Add(n > 1 ? $"{g.Key} ×{n}" : g.Key);
        }
        foreach ((string currency, long amount) in currencies)
            parts.Add($"{amount:N0} {currency}");
        return parts.Count > 0 ? $"Hid {string.Join(", ", parts)}" : "Hid nothing";
    }
}
