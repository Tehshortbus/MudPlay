using System.Collections.ObjectModel;

namespace FujinTerm.Game;

// App-singleton chat / realm-event history. Subscribes to
// ChatRouter.EntryClassified and appends every classified entry into Entries
// for the ConversationWindow to bind to. Wall-clock date rollovers insert a
// synthetic DaySeparator entry so multi-day sessions show a visible break (the
// typical case — the app runs for hours, the user keeps it open across
// midnight).
//
// Lifetime: app-scoped (not per-profile). Survives profile swap, connect /
// disconnect, character switch. Cleared only on Clear or app exit.
//
// This store is the in-memory view the Conversation window binds to. Durable
// disk persistence lives in Services.SessionLogService, which subscribes to the
// same ChatRouter and rolls a per-character talk.log.
public sealed class ChatHistoryStore : IDisposable
{
    // Upper bound on retained entries. The store is in-memory and app-lifetime,
    // so an all-day session would otherwise grow the collection without limit;
    // past this many the oldest rows drop off the front. Generous enough that
    // the Conversation window's scrollback never feels truncated in practice.
    private const int MaxEntries = 5_000;

    private readonly ChatRouter _router;
    private readonly ObservableCollection<ChatLogEntry> _entries = new();
    private DateOnly _lastDate;
    private bool _disposed;

    // Read-only view for the Conversation window's binding.
    public ReadOnlyObservableCollection<ChatLogEntry> Entries { get; }

    public ChatHistoryStore(ChatRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        Entries = new ReadOnlyObservableCollection<ChatLogEntry>(_entries);
        _router.EntryClassified += OnEntryClassified;
    }

    private void OnEntryClassified(ChatLogEntry entry)
    {
        DateOnly entryDate = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);

        // First entry of the session anchors _lastDate without emitting a
        // separator — only rollovers within a live session deserve one.
        if (_lastDate == default)
        {
            _lastDate = entryDate;
        }
        else if (entryDate != _lastDate)
        {
            _entries.Add(new ChatLogEntry(
                entry.Timestamp,
                ChatChannel.DaySeparator,
                Speaker: null,
                Message: entryDate.ToString("yyyy-MM-dd"),
                RawText: string.Empty));
            _lastDate = entryDate;
        }

        _entries.Add(entry);

        // Bounded ring: shed the oldest rows once past the cap. Chat arrives
        // at human speech rates, so the O(n) front-removal is off any hot path.
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    // Wipe every entry. User-initiated only (no automatic clear on profile
    // swap); intended for the Conversation window's right-click → Clear menu.
    public void Clear()
    {
        _entries.Clear();
        _lastDate = default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.EntryClassified -= OnEntryClassified;
    }
}
