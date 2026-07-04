using System.Collections.ObjectModel;
using System.Text;

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
// No disk persistence — in-memory only by default. On-demand export via
// ExportAsync writes a plain-text file with the same chronological order,
// optionally filtered to a channel subset.
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

    // Write the history to stream as plain text. Each row becomes
    // `[HH:mm:ss] {channel}{speaker?}: {message}`; day separators become
    // `─── yyyy-MM-dd ───`. Optional channelFilter trims to the listed channels;
    // pass null to export everything.
    public async Task ExportAsync(Stream stream, IReadOnlySet<ChatChannel>? channelFilter = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        foreach (ChatLogEntry entry in _entries)
        {
            if (entry.Channel == ChatChannel.DaySeparator)
            {
                await writer.WriteLineAsync($"─── {entry.Message} ───").ConfigureAwait(false);
                continue;
            }

            if (channelFilter is not null && !channelFilter.Contains(entry.Channel)) continue;

            string speaker = entry.Speaker is null ? string.Empty : $" {entry.Speaker}";
            await writer.WriteLineAsync(
                $"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {entry.Channel}{speaker}: {entry.Message}")
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.EntryClassified -= OnEntryClassified;
    }
}
