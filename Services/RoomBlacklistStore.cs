using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-realm room-blacklist store. Loads the active realm's room_blacklist.json
// whenever the realm changes (profile load / BBS pin / realm assignment) and
// exposes a read-only set of RoomKeys for the
// consumers — BfsMapper (skip placement, keep stub edge), RoomSearchService
// (filter from room search), and the map's right-click "Add this room to
// Blacklist" command.
public sealed class RoomBlacklistStore
{
    private readonly LogService? _log;
    private string? _realmFolder;
    private readonly Dictionary<RoomKey, BlacklistedRoom> _entries = new();

    // Fires whenever the blacklist contents change — either a load against a new
    // active realm, or an in-session Add / Remove. Consumers refresh derived state
    // (BFS layout cache, search results, map render).
    public event Action? Changed;

    public RoomBlacklistStore(LogService? log = null)
    {
        _log = log;
    }

    // Read-only snapshot of currently-blacklisted keys.
    public IReadOnlySet<RoomKey> Blacklisted
        => _entries.Keys.ToHashSet();

    // Read-only ordered snapshot of currently-blacklisted rooms with the
    // display name captured at add-time. Order is insertion-stable so the Modify
    // dialog shows entries in the order the user added them.
    public IReadOnlyList<BlacklistedRoom> Entries
        => _entries.Values.ToList();

    // True when key is in the blacklist.
    public bool IsBlacklisted(RoomKey key) => _entries.ContainsKey(key);

    // True when key is blacklisted AND flagged CannotBeReached — a room the
    // RoomTracker must never resolve the player's position into (dev / orphan
    // rooms with no walkable inbound edge). Plain-blacklisted rooms return false
    // here; only the explicit flag counts. Bound to
    // RoomGraphManager.ConfigureUnreachable so candidate resolution skips them.
    public bool IsUnreachable(RoomKey key)
        => _entries.TryGetValue(key, out BlacklistedRoom? e) && e.CannotBeReached;

    // Add key with display name. Idempotent — re-adding an existing key updates
    // the stored name and persists. Fires Changed on insertion or name update;
    // no-ops when the existing entry is identical.
    public void Add(RoomKey key, string name)
    {
        TakeInOutsideChanges();
        if (_entries.TryGetValue(key, out BlacklistedRoom? existing))
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal)) return;
            existing.Name = name;
        }
        else
        {
            _entries[key] = new BlacklistedRoom(key.Map, key.Room, name);
        }
        Persist();
        Changed?.Invoke();
    }

    // Replace the entire blacklist with newEntries and persist — one commit of a
    // staged working copy, so an editor's Cancel can discard cleanly.
    public void ReplaceAll(IEnumerable<BlacklistedRoom> newEntries)
    {
        ArgumentNullException.ThrowIfNull(newEntries);
        _entries.Clear();
        foreach (BlacklistedRoom e in newEntries)
        {
            RoomKey key = new(e.Map, e.Room);
            _entries[key] = new BlacklistedRoom(e.Map, e.Room, e.Name, e.CannotBeReached);
        }
        Persist();
        Changed?.Invoke();
    }

    // Load the blacklist for the active realm. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the active realm's folder;
    // resets the in-memory store when there's none (null / blank).
    public void OnRealmChanged(string? realmFolder)
    {
        if (string.IsNullOrWhiteSpace(realmFolder))
        {
            if (_realmFolder is not null)
            {
                _realmFolder = null;
                _entries.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _realmFolder = realmFolder;
        _entries.Clear();
        if (TryRead(out List<BlacklistedRoom> loaded))
        {
            foreach (BlacklistedRoom e in loaded) _entries[new RoomKey(e.Map, e.Room)] = e;
            _log?.Log(LogSeverity.Info, "RoomBlacklist",
                $"Loaded {_entries.Count} entry(ies) for '{realmFolder}'.");
        }
        Changed?.Invoke();
    }

    private readonly SharedFileStamp _stamp = new();

    // Re-read the blacklist when another client on the realm has saved it: every
    // character on the realm shares it, and it decides what the map draws. Called
    // on the heartbeat and before a change. A file caught mid-write is left for the
    // next call, the list kept.
    public bool TakeInOutsideChanges()
    {
        if (_realmFolder is null) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmRoomBlacklistFile(_realmFolder))) return false;
        if (!TryRead(out List<BlacklistedRoom> loaded)) return false;
        _entries.Clear();
        foreach (BlacklistedRoom e in loaded) _entries[new RoomKey(e.Map, e.Room)] = e;
        _log?.Log(LogSeverity.Debug, "RoomBlacklist", "re-read: another client on this realm changed it");
        Changed?.Invoke();
        return true;
    }

    // Read the realm's file. False when it is there but can't be read or parsed.
    private bool TryRead(out List<BlacklistedRoom> loaded)
    {
        loaded = new List<BlacklistedRoom>();
        string path = AppPaths.RealmRoomBlacklistFile(_realmFolder!);
        if (!File.Exists(path)) { _stamp.Mark(path); return true; }
        try
        {
            List<BlacklistedRoom>? onFile = JsonSerializer.Deserialize<List<BlacklistedRoom>>(File.ReadAllText(path));
            if (onFile is not null) loaded = onFile.Where(e => e.Map > 0 && e.Room > 0).ToList();
            _stamp.Mark(path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log?.Log(LogSeverity.Warn, "RoomBlacklist", $"Failed to read '{path}': {ex.Message}");
            return false;
        }
    }

    private void Persist()
    {
        if (_realmFolder is null) return;
        string path = AppPaths.RealmRoomBlacklistFile(_realmFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        // Written beside the file and moved over it, so another client reading at
        // this moment sees the old list or the new, never half of one.
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values.ToList(), opts));
        File.Move(temp, path, overwrite: true);
        _stamp.Mark(path);
        _log?.Log(LogSeverity.Info, "RoomBlacklist",
            $"Persisted {_entries.Count} entry(ies) to '{path}'.");
    }
}
