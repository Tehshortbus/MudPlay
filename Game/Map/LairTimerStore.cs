using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Per-session lair-arrival tracker + per-room respawn-timer resolver. Two
// responsibilities, kept in one service because they share the game-data
// lookup pipeline:
//   Default respawn lookup — given a RoomKey, walk
//     Rooms.Lair → Lairs[GroupIndex].AvgDelay (or, pre-1.83, the slowest
//     Monsters[id].RegenTime across the listed monsters) and return the
//     canonical respawn time in seconds. Lookups are cached; the cache
//     invalidates on GameDataCache.ActiveSetChanged.
//   In-session arrivals — observes RoomTracker.StateChanged; whenever the
//     player lands Confirmed in a known lair room, the arrival timestamp
//     is stamped. The scheduler reads LastEntered + RespawnSeconds to
//     compute "next-ready-at". Arrivals are session-only — they vanish on
//     Stop and on profile swap; no persistence.
public sealed class LairTimerStore : IDisposable
{
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    private readonly Dictionary<RoomKey, int?> _respawnSecCache = new();
    private readonly Dictionary<string, int?> _groupDelaySecCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int?> _monsterRegenSecCache = new();
    private readonly Dictionary<RoomKey, DateTimeOffset> _lastEntered = new();
    private readonly object _arrivalLock = new();

    // Whole-set longest respawn, computed once per active set (a scan of every
    // lair room) then cached. The _computed flag distinguishes "no lair in set"
    // (null result) from "not yet scanned".
    private int? _maxRespawnSec;
    private bool _maxRespawnComputed;

    // Time unit conversion for the AvgDelay field. Stock MajorMUD exports
    // AvgDelay in minutes-per-respawn; Paradigm / GreaterMUD differs.
    // First-cut assumption: stock minutes.
    private const int AvgDelayUnitSeconds = 60;

    public LairTimerStore(
        GameDataCache cache,
        RoomGraphManager graph,
        RoomTracker tracker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tracker);
        _cache = cache;
        _graph = graph;
        _tracker = tracker;
        _log = log;

        _cache.ActiveSetChanged += OnActiveSetChanged;
        _tracker.StateChanged   += OnRoomTransition;
    }

    public void Dispose()
    {
        _cache.ActiveSetChanged -= OnActiveSetChanged;
        _tracker.StateChanged   -= OnRoomTransition;
    }

    // Default respawn time for key in seconds. Returns null when the room
    // isn't a lair OR the lookup couldn't resolve a delay (room not in
    // graph, no Lairs table, etc.).
    public int? DefaultRespawnSeconds(RoomKey key)
    {
        if (_respawnSecCache.TryGetValue(key, out int? cached)) return cached;

        int? computed = ComputeDefaultRespawnSeconds(key);
        _respawnSecCache[key] = computed;
        return computed;
    }

    // Last time the player was observed entering key in the current
    // session. Null when the player hasn't yet arrived in that room since
    // the store was created / profile swapped.
    public DateTimeOffset? LastEntered(RoomKey key)
    {
        lock (_arrivalLock)
            return _lastEntered.TryGetValue(key, out DateTimeOffset t) ? t : null;
    }

    // Computed time when key's spawn will next be ready, given its respawn
    // timer + last arrival. Convenience for the scheduler. Returns null
    // when respawn time isn't known OR the player has never entered this
    // room (in which case the scheduler should treat it as "ready now").
    public DateTimeOffset? NextReadyAt(RoomKey key, int? overrideRespawnSeconds = null)
    {
        int? respawn = overrideRespawnSeconds ?? DefaultRespawnSeconds(key);
        if (respawn is not int seconds) return null;
        DateTimeOffset? last = LastEntered(key);
        return last is null ? null : last.Value.AddSeconds(seconds);
    }

    // Longest default respawn (seconds) across every lair room in the active
    // game-data set, or null when the set has no resolvable lair. Drives the
    // Navigation heat-map's coldest (black) endpoint so a lair's colour is
    // stable regardless of which rooms are on screen. Scans all rooms on first
    // call; cached until the active set changes.
    public int? MaxDefaultRespawnSeconds()
    {
        if (_maxRespawnComputed) return _maxRespawnSec;

        int max = 0;
        foreach (Room room in _graph.Rooms)
        {
            if (!room.HasLair) continue;
            if (DefaultRespawnSeconds(room.Key) is int s && s > max) max = s;
        }
        _maxRespawnSec = max > 0 ? max : null;
        _maxRespawnComputed = true;
        return _maxRespawnSec;
    }

    // Force-clear per-room arrival history; used by the scheduler on Start.
    public void ResetArrivals()
    {
        lock (_arrivalLock) _lastEntered.Clear();
        _log?.Debug("LairTimerStore", "arrival history cleared.");
    }

    // Drop the recorded arrival for the supplied subset of rooms, leaving
    // any others intact. Used by AutoLairManager.Start so a Run begins with
    // every marked room treated as "ready" regardless of whether the player
    // happened to walk through it earlier in the session — the in-game
    // spawn check still fires on the next entry, but from the scheduler's
    // POV the countdown starts on THAT entry, not on whatever stale
    // wall-clock anchor the store had.
    public void ResetArrivalsFor(IEnumerable<RoomKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_arrivalLock)
        {
            foreach (RoomKey k in keys) _lastEntered.Remove(k);
        }
    }

    // ----- internals -------------------------------------------------

    private void OnActiveSetChanged(string? _)
    {
        _respawnSecCache.Clear();
        _groupDelaySecCache.Clear();
        _monsterRegenSecCache.Clear();
        _maxRespawnSec = null;
        _maxRespawnComputed = false;
        _log?.Debug("LairTimerStore", "active set changed; respawn caches dropped.");
    }

    private void OnRoomTransition(RoomTransition t)
    {
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is not { } room) return;
        if (!room.HasLair) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_arrivalLock) _lastEntered[room.Key] = now;
        // Debug-only: arrivals are noisy in normal play. Scheduler
        // emits Info-level "lair entered" on its own surface.
        _log?.Debug("LairTimerStore", $"entered {room.Key} ('{room.Name}') at {now:HH:mm:ss.fff}.");
    }

    private int? ComputeDefaultRespawnSeconds(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return null;
        if (!room.HasLair) return null;

        // Primary: the per-room MDB Delay field. GreaterMUD formula
        // (D-1)m + 30s — same one RoomTooltipBuilder uses for its
        // "Max Regen: N @ Xm 30s" line, so the user sees a consistent
        // timer between the room tooltip + the CURRENT NAV row. Delay
        // = 0 means "no respawn delay" (or unset); fall through to the
        // lair-tag paths below.
        if (room.Delay > 0)
            return (room.Delay - 1) * 60 + 30;

        LairTagInfo? info = LairTagParser.TryParse(room.RawLairTag);
        if (info is null) return null;

        // NMR 1.83+ shape (rooms with no per-room Delay): try the
        // GroupIndex → Lairs.AvgDelay path. Stock exports populate
        // Lairs.AvgDelay in minutes; we convert to seconds.
        if (info.GroupIndex is { } gi && ResolveGroupDelaySeconds(gi) is int groupSeconds)
            return groupSeconds;

        // Pre-1.83 fallback: pick the slowest RegenTime across the
        // listed monsters. "Slowest" because the player has to wait
        // for the latest-respawning mob in the lair before the room
        // is fully populated again — that's the binding constraint.
        if (info.MonsterIds.Count > 0)
        {
            int slowest = 0;
            foreach (int id in info.MonsterIds)
            {
                int? r = ResolveMonsterRegenSeconds(id);
                if (r is int s && s > slowest) slowest = s;
            }
            return slowest > 0 ? slowest : null;
        }

        return null;
    }

    private int? ResolveGroupDelaySeconds(string groupIndex)
    {
        if (_groupDelaySecCache.TryGetValue(groupIndex, out int? cached)) return cached;

        int? seconds = null;
        JsonDocument? doc = _cache.GetRawTable("Lairs");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("GroupIndex", out JsonElement giEl)
                    || giEl.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(giEl.GetString(), groupIndex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!row.TryGetProperty("AvgDelay", out JsonElement delayEl)) break;
                int avgDelay = TryReadInt(delayEl);
                if (avgDelay > 0) seconds = avgDelay * AvgDelayUnitSeconds;
                break;
            }
        }
        _groupDelaySecCache[groupIndex] = seconds;
        return seconds;
    }

    private int? ResolveMonsterRegenSeconds(int monsterId)
    {
        if (_monsterRegenSecCache.TryGetValue(monsterId, out int? cached)) return cached;

        int? seconds = null;
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)
                    || numEl.ValueKind != JsonValueKind.Number
                    || !numEl.TryGetInt32(out int id)
                    || id != monsterId) continue;
                if (row.TryGetProperty("RegenTime", out JsonElement regenEl))
                {
                    int v = TryReadInt(regenEl);
                    if (v > 0) seconds = v * AvgDelayUnitSeconds;
                }
                break;
            }
        }
        _monsterRegenSecCache[monsterId] = seconds;
        return seconds;
    }

    private static int TryReadInt(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt32(out int n) ? n : 0;
            case JsonValueKind.String:
                return int.TryParse(el.GetString(), out int s) ? s : 0;
            default:
                return 0;
        }
    }
}
