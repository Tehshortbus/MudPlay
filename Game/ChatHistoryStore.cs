using System.Collections.ObjectModel;
using System.Text;

namespace FujinTerm.Game;

/// <summary>
/// App-singleton chat / realm-event history. Subscribes to
/// <see cref="ChatRouter.EntryClassified"/> and appends every classified
/// entry into <see cref="Entries"/> for the Phase 2 ConversationWindow to
/// bind to. Wall-clock date rollovers insert a synthetic
/// <see cref="ChatChannel.DaySeparator"/> entry so multi-day sessions show
/// a visible break (the typical case — the app runs for hours, the user
/// keeps it open across midnight).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: app-scoped (not per-profile). Survives profile swap,
/// connect / disconnect, character switch. Cleared only on
/// <see cref="Clear"/> or app exit.
/// </para>
/// <para>
/// No disk persistence — the spec is explicit: in-memory only by default.
/// On-demand export via <see cref="ExportAsync"/> writes a plain-text file
/// with the same chronological order, optionally filtered to a channel
/// subset.
/// </para>
/// </remarks>
public sealed class ChatHistoryStore : IDisposable
{
    private readonly ChatRouter _router;
    private readonly ObservableCollection<ChatLogEntry> _entries = new();
    private DateOnly _lastDate;
    private bool _disposed;

    /// <summary>Read-only view for the Conversation window's binding.</summary>
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
    }

    /// <summary>
    /// Wipe every entry. The Phase 2 spec marks this as user-initiated only
    /// (no automatic clear on profile swap); intended for the Conversation
    /// window's right-click → Clear menu.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _lastDate = default;
    }

    /// <summary>
    /// Write the history to <paramref name="stream"/> as plain text. Each
    /// row becomes <c>[HH:mm:ss] {channel}{speaker?}: {message}</c>; day
    /// separators become <c>─── yyyy-MM-dd ───</c>. Optional
    /// <paramref name="channelFilter"/> trims to the listed channels; pass
    /// <c>null</c> to export everything.
    /// </summary>
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
