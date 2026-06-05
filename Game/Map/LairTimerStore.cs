using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Per-session lair-arrival tracker + per-room respawn-timer resolver.
/// Two responsibilities, kept in one service because they share the
/// game-data lookup pipeline:
/// </summary>
/// <list type="number">
///   <item><b>Default respawn lookup</b> — given a <see cref="RoomKey"/>,
///   walk <c>Rooms.Lair → Lairs[GroupIndex].AvgDelay</c> (or, pre-1.83,
///   the slowest <c>Monsters[id].RegenTime</c> across the listed monsters)
///   and return the canonical respawn time in seconds. Lookups are
///   cached; the cache invalidates on
///   <see cref="GameDataCache.ActiveSetChanged"/>.</item>
///   <item><b>In-session arrivals</b> — observes
///   <see cref="RoomTracker.StateChanged"/>; whenever the player lands
///   Confirmed in a known lair room, the arrival timestamp is stamped.
///   The scheduler reads <c>LastEntered + RespawnSeconds</c> to compute
///   "next-ready-at". Arrivals are session-only — they vanish on Stop
///   and on profile swap (per the Phase 7 plan; no persistence).</item>
/// </list>
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

    // Time unit conversion for the AvgDelay field. Stock MajorMUD
    // exports AvgDelay in minutes-per-respawn; Paradigm / GreaterMUD
    // differs. PR 7.19 will read Info.Legit to pick the right factor.
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

    /// <summary>
    /// Default respawn time for <paramref name="key"/> in seconds.
    /// Returns null when the room isn't a lair OR the lookup couldn't
    /// resolve a delay (room not in graph, no <c>Lairs</c> table, etc.).
    /// </summary>
    public int? DefaultRespawnSeconds(RoomKey key)
    {
        if (_respawnSecCache.TryGetValue(key, out int? cached)) return cached;

        int? computed = ComputeDefaultRespawnSeconds(key);
        _respawnSecCache[key] = computed;
        return computed;
    }

    /// <summary>
    /// Last time the player was observed entering <paramref name="key"/>
    /// in the current session. Null when the player hasn't yet arrived
    /// in that room since the store was created / profile swapped.
    /// </summary>
    public DateTimeOffset? LastEntered(RoomKey key)
    {
        lock (_arrivalLock)
            return _lastEntered.TryGetValue(key, out DateTimeOffset t) ? t : null;
    }

    /// <summary>
    /// Computed time when <paramref name="key"/>'s spawn will next be
    /// ready, given its respawn timer + last arrival. Convenience for
    /// the scheduler. Returns null when respawn time isn't known OR
    /// the player has never entered this room (in which case the
    /// scheduler should treat it as "ready now").
    /// </summary>
    public DateTimeOffset? NextReadyAt(RoomKey key, int? overrideRespawnSeconds = null)
    {
        int? respawn = overrideRespawnSeconds ?? DefaultRespawnSeconds(key);
        if (respawn is not int seconds) return null;
        DateTimeOffset? last = LastEntered(key);
        return last is null ? null : last.Value.AddSeconds(seconds);
    }

    /// <summary>Force-clear per-room arrival history; used by the scheduler on Start.</summary>
    public void ResetArrivals()
    {
        lock (_arrivalLock) _lastEntered.Clear();
        _log?.Debug("LairTimerStore", "arrival history cleared.");
    }

    // ----- internals -------------------------------------------------

    private void OnActiveSetChanged(string? _)
    {
        _respawnSecCache.Clear();
        _groupDelaySecCache.Clear();
        _monsterRegenSecCache.Clear();
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

        LairTagInfo? info = LairTagParser.TryParse(room.RawLairTag);
        if (info is null) return null;

        // NMR 1.83+ shape: prefer the GroupIndex → Lairs.AvgDelay path.
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
