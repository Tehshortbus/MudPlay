using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Services;

// Centralised room-search resolver. Consumed by the Navigation rail search box,
// the Loop / Lair editor "Add room" rows, the manual Center-on dialog, the
// @goto remote-command handler — anywhere the user types a free-form room
// reference and we have to find the matching RoomKey(s).
//
// Resolution tiers (results from each tier accumulate; first tier with a hit
// doesn't block later tiers from also surfacing matches):
//   1. Coordinate — 1/297, 1,297, 1 297; bare 297 across all maps.
//   2. Acronym (opt-in via Search's includeAcronyms flag) — "Frozen Cavern,
//      Cave Opening" → FCCO. First letter of each whitespace/punctuation-
//      delimited word, uppercased.
//   3. Room-name substring — case-insensitive match against Room.Name +
//      Room.DisplayName. Requires >= 2 chars to avoid flooding on a single
//      keystroke.
//   4. Monster-name substring — limited to monsters with RegenTime > 0 (i.e.
//      lair-respawning mobs). One RoomSearchResult per (monster, lair-room)
//      pair; mobs whose spawn rooms aren't recorded surface as informational
//      rows.
//
// Caches: the regen-monster list + monster→rooms index live on the service and
// invalidate on GameDataCache.ActiveSetChanged + RoomGraphManager.GraphReloaded.
// Each call to Search consults BfsMapper for per-result step distances; those
// are BfsMapper's own concern to cache.
public sealed class RoomSearchService
{
    private static readonly Regex SummonedRoomRegex
        = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    // "Summoned By: Spell #491" — the spell another NPC casts to summon this
    // monster (used to borrow the summoner's placement when the target isn't
    // itself placed in a room).
    private static readonly Regex SummonedSpellRegex
        = new(@"Spell\s+#(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly RoomGraphManager _graph;
    private readonly GameDataCache _gameData;
    private readonly BfsMapper _bfs;
    private readonly RoomBlacklistStore _blacklist;
    private readonly MovementFilter? _movement;
    private readonly LogService? _log;

    private List<(int Id, string Name, int RegenHours)>? _regenMonsterCache;
    private Dictionary<int, List<RoomKey>>? _roomsByMonsterIdCache;
    private Dictionary<int, IReadOnlyList<RoomKey>>? _questKillRoomsCache;
    // Single-source distance cache. Each Search call reuses it when
    // source is unchanged → one BFS per current-room change, O(1)
    // lookups per match. Matters for the rail search box's 50+
    // matches per keystroke.
    private RoomKey? _distanceCacheSource;
    private IReadOnlyDictionary<RoomKey, int>? _distanceCache;

    public RoomSearchService(
        RoomGraphManager graph,
        GameDataCache gameData,
        BfsMapper bfs,
        RoomBlacklistStore blacklist,
        MovementFilter? movement = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(blacklist);
        _graph = graph;
        _gameData = gameData;
        _bfs = bfs;
        _blacklist = blacklist;
        _movement = movement;
        _log = log;

        _gameData.ActiveSetChanged += _ => InvalidateCaches();
        _graph.GraphReloaded        += InvalidateCaches;
        // Avoided-rooms changes flush the distance cache: BFS hop
        // counts are filter-sensitive, so a freshly-avoided room could
        // re-route a previously short path.
        if (_movement is not null) _movement.AvoidedChanged += InvalidateDistanceCache;
    }

    // Resolve query against the active graph. Each tier accumulates into the
    // returned list, deduped on (Key, MonsterTag). Results are sorted by step
    // distance (closer first) then by primary line.
    //   source — player's current room for step distance; pass null to skip.
    //   cap — soft cap on total matches; tiers stop accumulating once reached.
    //   includeAcronyms — run the acronym tier (only @goto uses this).
    public IReadOnlyList<RoomSearchResult> Search(
        string query,
        RoomKey? source = null,
        int cap = 200,
        bool includeAcronyms = false)
    {
        List<RoomSearchResult> matches = new();
        string needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0) return matches;

        // ----- Tier 1: coordinate -----
        (int? mapPart, int? roomPart) = TryParseCoordinate(needle);
        if (mapPart is int m && roomPart is int r
            && _graph.GetRoom(new RoomKey(m, r)) is { } exact
            && !_blacklist.IsBlacklisted(exact.Key))
        {
            matches.Add(BuildRoomMatch(exact, source));
        }
        else if (mapPart is null && roomPart is int onlyRoom)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (matches.Count >= cap) break;
                if (room.Key.Room != onlyRoom) continue;
                if (_blacklist.IsBlacklisted(room.Key)) continue;
                matches.Add(BuildRoomMatch(room, source));
            }
        }

        // ----- Tier 2: acronym -----
        if (includeAcronyms && matches.Count < cap)
        {
            string normalized = needle.ToUpperInvariant();
            foreach (Room room in _graph.Rooms)
            {
                if (matches.Count >= cap) break;
                if (_blacklist.IsBlacklisted(room.Key)) continue;
                string acro = ExtractAcronym(room.DisplayName);
                if (acro.Length == 0) continue;
                if (!string.Equals(acro, normalized, StringComparison.Ordinal)) continue;
                if (matches.Any(x => x.MonsterTag is null && x.Key.Equals(room.Key))) continue;
                matches.Add(BuildRoomMatch(room, source));
            }
        }

        // ----- Tier 3: room-name substring (≥ 2 chars) -----
        if (needle.Length >= 2 && matches.Count < cap)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (matches.Count >= cap) break;
                if (_blacklist.IsBlacklisted(room.Key)) continue;
                if (!room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                 && !room.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (matches.Any(x => x.MonsterTag is null && x.Key.Equals(room.Key))) continue;
                matches.Add(BuildRoomMatch(room, source));
            }
        }

        // ----- Tier 4: monster-name substring (regen-bearing mobs only) -----
        if (needle.Length >= 2 && matches.Count < cap)
        {
            foreach ((int monsterId, string name, int regenHours)
                     in EnumerateRegenMonsters())
            {
                if (matches.Count >= cap) break;
                if (!name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                string monsterTag = $"{name} · regen {regenHours}h";

                if (!RoomsByMonsterId().TryGetValue(monsterId, out List<RoomKey>? lairs)
                    || lairs.Count == 0)
                {
                    matches.Add(new RoomSearchResult(
                        Key:               new RoomKey(0, 0),
                        Name:              string.Empty,
                        StepsFromCurrent:  null,
                        MonsterTag:        monsterTag));
                    continue;
                }

                foreach (RoomKey lk in lairs)
                {
                    if (matches.Count >= cap) break;
                    if (_blacklist.IsBlacklisted(lk)) continue;
                    if (_graph.GetRoom(lk) is not { } lroom) continue;
                    int? steps = source is { } src ? DistanceFrom(src, lroom.Key) : null;
                    matches.Add(new RoomSearchResult(lroom.Key, lroom.DisplayName, steps, monsterTag));
                }
            }
        }

        return matches
            .OrderBy(mm => mm.StepsFromCurrent ?? int.MaxValue)
            .ThenBy(mm => mm.PrimaryLine, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ----- Pure parsers (also re-exported as statics so handlers can
    //       reuse without instantiating a service) ---------------------

    // Parse a coordinate token. 1/297 / 1,297 / 1 297 → (1, 297); bare 297 →
    // (null, 297); non-numeric → (null, null).
    public static (int? Map, int? Room) TryParseCoordinate(string text)
    {
        string[] parts = (text ?? string.Empty).Split(new[] { '/', ',', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out int onlyRoom))
            return (null, onlyRoom);
        if (parts.Length == 2
            && int.TryParse(parts[0], out int map)
            && int.TryParse(parts[1], out int room))
            return (map, room);
        return (null, null);
    }

    // Parse a comma/semicolon-separated coordinate list — used by @loop / @lair
    // to consume "1/224, 1/218, 1/245". Returns null if any token fails so the
    // caller falls back to name match.
    public static List<RoomKey>? TryParseCoordList(string text)
    {
        string[] tokens = (text ?? string.Empty).Split(new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 1) return null;

        List<RoomKey> keys = new(tokens.Length);
        foreach (string tok in tokens)
        {
            (int? mapPart, int? roomPart) = TryParseCoordinate(tok);
            if (mapPart is not int m || roomPart is not int r) return null;
            keys.Add(new RoomKey(m, r));
        }
        return keys;
    }

    // First letter of each whitespace-or-punctuation-delimited word, uppercased.
    // "Frozen Cavern, Cave Opening" → "FCCO".
    public static string ExtractAcronym(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        StringBuilder sb = new();
        bool startOfWord = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                if (startOfWord) sb.Append(char.ToUpperInvariant(c));
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }
        return sb.ToString();
    }

    // ----- internals --------------------------------------------------

    private RoomSearchResult BuildRoomMatch(Room room, RoomKey? sourceKey)
    {
        int? steps = sourceKey is { } src ? DistanceFrom(src, room.Key) : null;
        return new RoomSearchResult(room.Key, room.DisplayName, steps);
    }

    private int? DistanceFrom(RoomKey source, RoomKey destination)
    {
        if (_distanceCacheSource is not { } cached || !cached.Equals(source))
        {
            _distanceCache = _bfs.ComputeDistancesFrom(source, _movement);
            _distanceCacheSource = source;
        }
        return _distanceCache!.TryGetValue(destination, out int hops) ? hops : null;
    }

    private void InvalidateCaches()
    {
        _regenMonsterCache = null;
        _roomsByMonsterIdCache = null;
        _questKillRoomsCache = null;
        InvalidateDistanceCache();
        _log?.Debug("RoomSearch", "caches invalidated (graph or game-data swap).");
    }

    private void InvalidateDistanceCache()
    {
        _distanceCache = null;
        _distanceCacheSource = null;
    }

    private IEnumerable<(int Id, string Name, int RegenHours)> EnumerateRegenMonsters()
    {
        if (_regenMonsterCache is not null) return _regenMonsterCache;

        List<(int, string, int)> list = new();
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is null) { _regenMonsterCache = list; return list; }

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("RegenTime", out JsonElement regenEl)) continue;
            if (regenEl.ValueKind != JsonValueKind.Number) continue;
            if (!regenEl.TryGetInt32(out int regen) || regen <= 0) continue;
            if (!row.TryGetProperty("Number", out JsonElement numEl)
                || numEl.ValueKind != JsonValueKind.Number
                || !numEl.TryGetInt32(out int id)) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)
                || nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            list.Add((id, name, regen));
        }
        _regenMonsterCache = list;
        return list;
    }

    private Dictionary<int, List<RoomKey>> RoomsByMonsterId()
    {
        if (_roomsByMonsterIdCache is not null) return _roomsByMonsterIdCache;
        Dictionary<int, List<RoomKey>> map = new();

        // Source 1: lair tag on each room (pre-1.83 monster-list or
        // NMR 1.83+ group reference parsed via RoomTooltipBuilder).
        foreach (Room room in _graph.Rooms)
        {
            if (string.IsNullOrEmpty(room.RawLairTag)) continue;
            RoomTooltipBuilder.ParseLairTag(room.RawLairTag, out _, out IReadOnlyList<int> ids);
            foreach (int id in ids) AddMonsterRoom(map, id, room.Key);
        }

        // Source 2: Monsters.json "Summoned By" — boss / script spawns
        // whose room placement lives on the monster record.
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)
                    || numEl.ValueKind != JsonValueKind.Number
                    || !numEl.TryGetInt32(out int id)) continue;
                if (!row.TryGetProperty("Summoned By", out JsonElement summonEl)
                    || summonEl.ValueKind != JsonValueKind.String) continue;
                string? text = summonEl.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match m in SummonedRoomRegex.Matches(text))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int mn) || mn <= 0) continue;
                    if (!int.TryParse(m.Groups[2].Value, out int rn) || rn <= 0) continue;
                    AddMonsterRoom(map, id, new RoomKey(mn, rn));
                }
            }
        }

        _roomsByMonsterIdCache = map;
        return map;
    }

    private static void AddMonsterRoom(Dictionary<int, List<RoomKey>> map, int monsterId, RoomKey key)
    {
        if (!map.TryGetValue(monsterId, out List<RoomKey>? rooms))
            map[monsterId] = rooms = new List<RoomKey>();
        if (!rooms.Contains(key)) rooms.Add(key);
    }

    // Where a quest guide's kill step sends you: the room(s) the quest places its
    // kill target in, keyed by monster number. A quest-kill monster is placed
    // statically — the room's NPC field names it — so that placement is primary.
    // When the target is summoned rather than placed, its Monsters "Summoned By"
    // record either names the spawn room directly, or names the spell another NPC
    // casts to summon it — in which case that summoner's own placement stands in
    // (a boss you fight where its summoner waits). Distinct from RoomsByMonsterId,
    // which surfaces roaming-lair spawns; this is the single placed spot a guide
    // walks you to. Built once and cached (guide rebuilds resolve every kill step
    // against it), invalidated on game-data / graph swap.
    public IReadOnlyDictionary<int, IReadOnlyList<RoomKey>> QuestKillRooms()
    {
        if (_questKillRoomsCache is not null) return _questKillRoomsCache;
        Dictionary<int, List<RoomKey>> placed = new();

        // Primary: the room's NPC field statically places the monster there.
        foreach (Room room in _graph.Rooms)
            if (room.Npc > 0) AddMonsterRoom(placed, room.Npc, room.Key);

        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is not null)
        {
            // Map each summon spell to the NPC(s) whose CreateSpell casts it, so a
            // "Summoned By: Spell #N" target can borrow its summoner's placement.
            Dictionary<int, List<int>> summonerBySpell = new();
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!TryInt(row, "Number", out int owner)) continue;
                if (!TryInt(row, "CreateSpell", out int spell) || spell <= 0) continue;
                if (!summonerBySpell.TryGetValue(spell, out List<int>? owners))
                    summonerBySpell[spell] = owners = new List<int>();
                owners.Add(owner);
            }

            // Fallback for monsters with no static placement: their own Summoned By
            // spawn room(s), else the placement of whoever summons them.
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!TryInt(row, "Number", out int id) || placed.ContainsKey(id)) continue;
                if (!row.TryGetProperty("Summoned By", out JsonElement sbEl)
                    || sbEl.ValueKind != JsonValueKind.String) continue;
                string text = sbEl.GetString() ?? string.Empty;
                if (text.Length == 0) continue;

                bool addedRoom = false;
                foreach (Match m in SummonedRoomRegex.Matches(text))
                {
                    if (int.TryParse(m.Groups[1].Value, out int mn) && mn > 0
                        && int.TryParse(m.Groups[2].Value, out int rn) && rn > 0)
                    {
                        AddMonsterRoom(placed, id, new RoomKey(mn, rn));
                        addedRoom = true;
                    }
                }
                if (addedRoom) continue;

                foreach (Match m in SummonedSpellRegex.Matches(text))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int sp)) continue;
                    if (!summonerBySpell.TryGetValue(sp, out List<int>? owners)) continue;
                    foreach (int owner in owners)
                        if (placed.TryGetValue(owner, out List<RoomKey>? ownerRooms))
                            foreach (RoomKey k in ownerRooms) AddMonsterRoom(placed, id, k);
                }
            }
        }

        _questKillRoomsCache = placed.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<RoomKey>)kv.Value);
        return _questKillRoomsCache;
    }

    private static bool TryInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
