namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 12 — a per-session ledger of cash/item offloads for the Session
/// Stats → Transaction history window. Records one <see cref="TransactionEntry"/>
/// per bank <c>dep</c>osit (fed from <see cref="AutoDepositManager.Deposited"/>)
/// and per stash-room <c>hide</c> (fed from
/// <see cref="StashRoomManager.StashExecuted"/>), each with the wall-clock
/// time, the store kind, and a rendered description of what was put away.
/// </summary>
/// <remarks>
/// Owns no source subscriptions — <see cref="Services.AppServices"/> wires the
/// bank / stash events to the <c>Note*</c> forwarders — mirroring
/// <see cref="Combat.SessionActivityTracker"/> and keeping the tracker
/// dependency-free behind an injectable clock for unit tests. Every write and
/// <see cref="Snapshot"/> runs on the marshalled dispatch thread (the sources
/// all fire there), so the list is lock-free. Reset on the same session
/// boundary as the other session-stats trackers (connect / character switch,
/// the window "Reset session" button, and <c>@reset</c>).
/// </remarks>
public sealed class TransactionHistoryTracker
{
    /// <summary>Hard cap on retained entries; the oldest is evicted past this
    /// so a long session can't grow the ledger unbounded.</summary>
    public const int MaxEntries = 500;

    private readonly Func<DateTimeOffset> _clock;
    private readonly List<TransactionEntry> _entries = new();

    /// <summary>Raised after any input records or clears an entry, so the
    /// Transaction history VM can rebuild. Fires on the dispatch thread.</summary>
    public event Action? Changed;

    public TransactionHistoryTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    /// <summary>Record a bank deposit of the given copper wealth value.
    /// Non-positive amounts are ignored (nothing was deposited).</summary>
    public void NoteBankDeposit(long copper)
    {
        if (copper <= 0) return;
        Add(TransactionKind.Bank, $"Deposited {copper:N0} wealth");
    }

    /// <summary>Record a stash-room hide of the given per-denomination coin
    /// amounts and item names. A dispatch with neither coins nor items is
    /// ignored (the stash event never fires empty, but the guard keeps the
    /// ledger clean).</summary>
    public void NoteStash(
        IReadOnlyList<(string Currency, long Amount)> currencies,
        IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(items);
        if (currencies.Count == 0 && items.Count == 0) return;
        Add(TransactionKind.Stash, FormatStash(currencies, items));
    }

    /// <summary>Point-in-time copy of the ledger, oldest entry first.</summary>
    public IReadOnlyList<TransactionEntry> Snapshot() => _entries.ToArray();

    /// <summary>Clear the ledger — called on the connect / character-switch
    /// boundary and by the manual / remote session reset, matching the other
    /// session-stats trackers.</summary>
    public void Reset()
    {
        _entries.Clear();
        Changed?.Invoke();
    }

    private void Add(TransactionKind kind, string detail)
    {
        _entries.Add(new TransactionEntry(_clock(), kind, detail));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
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
