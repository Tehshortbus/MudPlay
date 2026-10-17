using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of @roomba sync: scrapes the `@roombadata <blob>` reply lines
// off ChatRouter (ignored by RemoteCommandManager — see AppServices
// RegisterIgnored(RoombaQueryHandler.SyncResponseToken)), and merges each line's
// sightings straight into GhItemLocationStore as it arrives.
//
// Every line is SELF-CONTAINED (see GhItemSyncCodec): it decodes and merges on
// its own, with no cross-line reassembly. So a line the game's telepath
// flood-control dropped costs only the rooms it carried — the rest still land —
// instead of the old "one missing chunk discards the whole sync". There's also
// no meaningful "conflict" for a room-contents sighting (newest wins silently —
// see GhItemLocationStore.MergeSyncRecords), so there's no merge-review window.
//
// A reply is adopted only inside a REQUEST WINDOW opened by our OWN outbound
// `@roomba sync` (NoteSyncRequested, called from the outbound-chat watcher). The
// permission gate lives entirely on the RESPONDER: another client answers our
// request only if they've granted us "Query Roomba", so a reply arriving at all
// already proves we're authorized — the receiver just has to confirm we asked,
// not re-check a (reverse-direction) grant we'd otherwise need against them.
public sealed class RoombaSyncReceiver : IDisposable
{
    // How long after we send `@roomba sync` incoming replies are adopted. A big
    // gang house paces ~20 telepaths ~800ms apart once the responder starts, so
    // the window is generous enough for the whole reply (and a slow responder) to
    // land; it then goes stale so a much-later stray line isn't adopted.
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(2);

    private readonly ChatRouter _chat;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly LogService? _log;
    private DateTimeOffset _acceptUntil = DateTimeOffset.MinValue;
    private bool _disposed;

    public RoombaSyncReceiver(ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _chat = chat;
        _locations = locations;
        _labels = labels;
        _log = log;
        _chat.EntryClassified += Ingest;
    }

    // Called when WE send `@roomba sync` — opens the window during which incoming
    // replies are adopted. (They reply only because they've granted us, so the
    // reply itself is the authorization; we gate merely on having asked.)
    public void NoteSyncRequested() => _acceptUntil = DateTimeOffset.Now + RequestWindow;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
    }

    private void Ingest(ChatLogEntry e)
    {
        // By telepath or gangpath only (TransferChannels): the gang house's log is
        // not said aloud in a room.
        if (!TransferChannels.Allows(Transfer.RoombaSync, e.Channel)) return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out string blob)) return;   // cheap: an @roombadata line at all?
        // Only adopt inside a window we opened by sending `@roomba sync`. Logged at
        // Debug (not silent) so a "nothing synced" report shows we hadn't asked.
        if (DateTimeOffset.Now > _acceptUntil)
        {
            _log?.Debug("RoombaSync",
                $"ignoring @roomba sync line from {sender} — no @roomba sync requested recently");
            return;
        }

        // End-of-sync sentinel (a plain marker, not a blob) — note it and stop, so
        // it isn't mistaken for a malformed payload.
        if (string.Equals(blob, RoombaQueryHandler.SyncCompleteMarker, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info("RoombaSync", $"{sender}'s @roomba sync complete");
            return;
        }

        try
        {
            // The same @roombadata stream carries both room-label lines and item
            // sighting lines; route on the leading byte the codec exposes.
            if (GhItemSyncCodec.IsLabelLine(blob))
            {
                IReadOnlyList<Models.Profile.GhRoomLabel> labels = GhItemSyncCodec.DecodeLabelLine(blob);
                int adopted = _labels.MergeSyncLabels(labels);
                if (adopted > 0)
                    _log?.Info("RoombaSync",
                        $"adopted {adopted} gang-house room label(s) from {sender}'s @roomba sync");
            }
            else
            {
                IReadOnlyList<GhItemSyncRecord> records = GhItemSyncCodec.DecodeLine(blob);
                int applied = _locations.MergeSyncRecords(records);
                if (applied > 0)
                    _log?.Info("RoombaSync",
                        $"merged {applied}/{records.Count} item sighting(s) from {sender}'s @roomba sync");
            }
        }
        catch (FormatException ex)
        {
            _log?.Warn("RoombaSync", $"discarding malformed roomba-sync line from {sender}: {ex.Message}");
        }
    }

    // Parse "@roombadata <blob>", tolerating the {} the remote reply path wraps a
    // reply in. The blob is a single spaceless base64url token, so a 2-way split
    // keeps it whole. Each line is decoded on its own — there is no i/n fraction.
    private static bool TryParse(string message, out string blob)
    {
        blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!parts[0].Equals(RoombaQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;
        blob = parts[1].Trim();
        return blob.Length > 0;
    }
}
