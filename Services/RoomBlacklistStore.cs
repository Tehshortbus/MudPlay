using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-BBS room-blacklist store. Loads Data/BBS/{bbs}/room_blacklist.json on
// every BBS pin (the usual ProfileService event the rest of the BBS-tier
// subsystems subscribe to) and exposes a read-only set of RoomKeys for the
// consumers — BfsMapper (skip placement, keep stub edge), NavigationViewModel
// (filter from room search), and the right-click "Add to blacklist" command on
// the map.
public sealed class RoomBlacklistStore
{
    private readonly LogService? _log;
    private string? _activeBbs;
    private readonly Dictionary<RoomKey, BlacklistedRoom> _entries = new();

    // Fires whenever the blacklist contents change — either a load against a new
    // active BBS, or an in-session Add / Remove. Consumers refresh derived state
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

    // Remove key. No-op when absent. Fires Changed on actual removal.
    public bool Remove(RoomKey key)
    {
        if (!_entries.Remove(key)) return false;
        Persist();
        Changed?.Invoke();
        return true;
    }

    // Replace the entire blacklist with newEntries and persist. Used by the
    // Modify-Blacklist dialog on OK — it stages changes locally and commits the
    // full working copy in one shot so Cancel can discard cleanly.
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

    // Load the blacklist for the active BBS. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the resolved active BBS
    // name; resets the in-memory store when the pin clears (bbs is null / blank).
    public void OnBbsPinApplied(string? bbs)
    {
        if (string.IsNullOrWhiteSpace(bbs))
        {
            if (_activeBbs is not null)
            {
                _activeBbs = null;
                _entries.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _activeBbs = bbs;
        _entries.Clear();

        string path = AppPaths.BbsRoomBlacklistFile(bbs);
        if (!File.Exists(path))
        {
            _log?.Log(LogSeverity.Info, "RoomBlacklist",
                $"No blacklist file at '{path}'; starting empty.");
            Changed?.Invoke();
            return;
        }

        try
        {
            string raw = File.ReadAllText(path);
            List<BlacklistedRoom>? loaded = JsonSerializer.Deserialize<List<BlacklistedRoom>>(raw);
            if (loaded is not null)
            {
                foreach (BlacklistedRoom e in loaded)
                {
                    if (e.Map <= 0 || e.Room <= 0) continue;
                    _entries[new RoomKey(e.Map, e.Room)] = e;
                }
            }
            _log?.Log(LogSeverity.Info, "RoomBlacklist",
                $"Loaded {_entries.Count} entry(ies) from '{path}'.");
        }
        catch (JsonException ex)
        {
            _log?.Log(LogSeverity.Warn, "RoomBlacklist",
                $"Failed to parse '{path}': {ex.Message}");
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        if (_activeBbs is null) return;
        string path = AppPaths.BbsRoomBlacklistFile(_activeBbs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(_entries.Values.ToList(), opts));
        _log?.Log(LogSeverity.Info, "RoomBlacklist",
            $"Persisted {_entries.Count} entry(ies) to '{path}'.");
    }
}
