using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// Reverse index of RoomKey → monster ids whose Monsters.json Summoned By
// field references that room. Builds once on the active game-data set and
// rebuilds on GameDataCache.ActiveSetChanged.
//
// Bosses and other script-spawned monsters carry their spawn site on the
// monster record rather than the room's Room.RawLairTag. The room search
// reads this via NavigationViewModel.RoomsByMonsterId; the room tooltip needs
// the inverse — given a room, which monsters does it host? Without the index
// a tooltip's Also Here line silently omits any boss whose presence lives
// only on the monster record (live repro: 1/1678 Darkwood Forest, Webbed
// Clearing had no giant spider in its tooltip even though Monster 52's
// Summoned By reads "Room 1/1678").
//
// Cache rebuild costs O(monsters) once per active-set switch; per-tooltip
// lookups are O(1). All (map)/(room) tokens in the field are matched (the
// search-side parser uses the same regex — Lairs.json GroupIndex tokens use
// '-' between numbers so a digits/digits regex catches only room references).
public sealed class MonsterSpawnIndex
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, List<int>> _summonedAt = new();
    private bool _built;

    private static readonly Regex s_roomTokenRegex
        = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    public MonsterSpawnIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
        _cache.ActiveSetChanged += _ => Invalidate();
    }

    // Monster ids whose Summoned By references the given room. Empty list
    // when nothing spawns at the room (or no Monsters table is loaded). Builds
    // the index lazily on first call after a set change.
    public IReadOnlyList<int> MonsterIdsSummonedAt(RoomKey key)
    {
        EnsureBuilt();
        return _summonedAt.TryGetValue(key, out List<int>? list)
            ? list
            : Array.Empty<int>();
    }

    // Drops the cached index — the next lookup re-scans Monsters.json.
    public void Invalidate()
    {
        _summonedAt.Clear();
        _built = false;
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                "Active set has no Monsters.json; index left empty.");
            return;
        }

        int linked = 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl)
                || numEl.ValueKind != JsonValueKind.Number
                || !numEl.TryGetInt32(out int id)) continue;
            if (!row.TryGetProperty("Summoned By", out JsonElement summonEl)
                || summonEl.ValueKind != JsonValueKind.String) continue;
            string? text = summonEl.GetString();
            if (string.IsNullOrEmpty(text)) continue;

            foreach (Match m in s_roomTokenRegex.Matches(text))
            {
                if (!int.TryParse(m.Groups[1].Value, out int map) || map <= 0) continue;
                if (!int.TryParse(m.Groups[2].Value, out int room) || room <= 0) continue;
                RoomKey key = new(map, room);
                if (!_summonedAt.TryGetValue(key, out List<int>? list))
                    _summonedAt[key] = list = new List<int>();
                if (!list.Contains(id)) list.Add(id);
                linked++;
            }
        }

        // Folded into the spawn map — release the pinned raw Monsters JsonDocument.
        _cache.EvictTable("Monsters");
        _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
            $"Built spawn index — {_summonedAt.Count} room(s) host {linked} monster reference(s).");
    }
}
