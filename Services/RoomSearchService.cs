using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.Services;

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
//   3. Room-name token match — every whitespace/punctuation-delimited word of the
//      query must appear in Room.Name or Room.DisplayName (case-insensitive,
//      order-independent, so "titan aged" finds "aged titan"). Requires >= 2 chars
//      to avoid flooding on a single keystroke.
//   4. Monster-name token match (opt-out via Search's includeMonsters flag) —
//      lair-respawning mobs (RegenTime > 0) plus unique spawns (GameLimit == 1,
//      e.g. "aged titan"). One RoomSearchResult per (monster, room) pair; mobs
//      whose spawn rooms aren't recorded surface as informational rows. @goto and
//      the nav search box opt out — they resolve places only (monster / boss
//      destinations go through the GOTO list + boss table).
//   5. Saved GOTO favourites by label (opt-in via includeFavorites) — the nav
//      search box surfaces bookmarks whose label matches, jumping to their room.
//   6. Boss names from the boss table (opt-in via includeBosses) — one row per
//      listed room, tagged with the boss name.
//
// Caches: the regen-monster list + monster→rooms index live on the service and
// invalidate on GameDataCache.ActiveSetChanged + RoomGraphManager.GraphReloaded.
// Each call to Search consults BfsMapper for per-result step distances; those
// are BfsMapper's own concern to cache.
public sealed class RoomSearchService
{
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
    // Opt-in tiers (nav search box): saved GOTO favourites by label + boss names
    // from the boss table. Null when the caller didn't wire them — the
    // corresponding tier then no-ops.
    private readonly FavoritesStore? _favorites;
    private readonly BossStore? _bosses;

    private List<(int Id, string Name, string Tag)>? _searchableMonsterCache;
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
        LogService? log = null,
        FavoritesStore? favorites = null,
        BossStore? bosses = null)
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
        _favorites = favorites;
        _bosses = bosses;

        _gameData.ActiveSetChanged += _ => InvalidateCaches();
        _graph.GraphReloaded        += InvalidateCaches;
        // Avoided-rooms changes flush the distance cache: BFS hop
        // counts are filter-sensitive, so a freshly-avoided room could
        // re-route a previously short path.
        if (_movement is not null) _movement.AvoidedChanged += InvalidateDistanceCache;
    }

    // Resolve query against the active graph. Each tier accumulates into the
    // returned list, deduped on (Key, MonsterTag). Results are sorted by match
    // rank first (literal whole-word matches before buried-substring ones), then
    // step distance (closer first), then primary line.
    //   source — player's current room for step distance; pass null to skip.
    //   cap — soft cap on total matches; tiers stop accumulating once reached.
    //   includeAcronyms — run the acronym tier (only @goto uses this).
    //   includeMonsters — run the monster-name tier; pass false to resolve places
    //     only (@goto + the nav search box opt out — monster / boss destinations go
    //     through the GOTO list + boss table instead).
    //   includeFavorites — surface saved GOTO favourites matched by their label.
    //   includeBosses — surface boss names from the boss table (→ their rooms).
    public IReadOnlyList<RoomSearchResult> Search(
        string query,
        RoomKey? source = null,
        int cap = 200,
        bool includeAcronyms = false,
        bool includeMonsters = true,
        bool includeFavorites = false,
        bool includeBosses = false)
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

        // ----- Tier 3: room-name token match (≥ 2 chars) -----
        string[] tokens = TokenizeNeedle(needle);
        if (needle.Length >= 2 && matches.Count < cap)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (matches.Count >= cap) break;
                if (_blacklist.IsBlacklisted(room.Key)) continue;
                if (!AllTokensMatch(room.Name, tokens) && !AllTokensMatch(room.DisplayName, tokens))
                    continue;
                if (matches.Any(x => x.MonsterTag is null && x.Key.Equals(room.Key))) continue;
                int rank = Math.Min(MatchRank(room.Name, tokens), MatchRank(room.DisplayName, tokens));
                matches.Add(BuildRoomMatch(room, source) with { MatchRank = rank });
            }
        }

        // ----- Tier 4: monster-name token match (lair-respawning + unique mobs) -----
        if (includeMonsters && needle.Length >= 2 && matches.Count < cap)
        {
            foreach ((int monsterId, string name, string tag)
                     in EnumerateSearchableMonsters())
            {
                if (matches.Count >= cap) break;
                if (!AllTokensMatch(name, tokens)) continue;
                string monsterTag = $"{name} · {tag}";
                int rank = MatchRank(name, tokens);

                if (!RoomsByMonsterId().TryGetValue(monsterId, out List<RoomKey>? lairs)
                    || lairs.Count == 0)
                {
                    matches.Add(new RoomSearchResult(
                        Key:               new RoomKey(0, 0),
                        Name:              string.Empty,
                        StepsFromCurrent:  null,
                        MonsterTag:        monsterTag,
                        MatchRank:         rank,
                        Kind:              SearchResultKind.Monster));
                    continue;
                }

                foreach (RoomKey lk in lairs)
                {
                    if (matches.Count >= cap) break;
                    if (_blacklist.IsBlacklisted(lk)) continue;
                    if (_graph.GetRoom(lk) is not { } lroom) continue;
                    int? steps = source is { } src ? DistanceFrom(src, lroom.Key) : null;
                    matches.Add(new RoomSearchResult(
                        lroom.Key, lroom.DisplayName, steps, monsterTag, rank, SearchResultKind.Monster));
                }
            }
        }

        // ----- Tier 5: saved GOTO favourites by label (opt-in) -----
        // Match ANY saved GOTO bookmark's label (not only starred ones); the row
        // jumps to the favourite's room and carries a "<label> · goto" header tag so
        // it reads as a named GOTO (the label above, the underlying room below) — the
        // same tagged-row shape the boss / monster tiers use. Skips a room the
        // room-name tier already surfaced (no duplicate row).
        if (includeFavorites && _favorites is not null && needle.Length >= 2 && matches.Count < cap)
        {
            foreach (Models.Profile.FavoriteRoom fav in _favorites.All)
            {
                if (matches.Count >= cap) break;
                if (string.IsNullOrWhiteSpace(fav.Label)) continue;
                if (!AllTokensMatch(fav.Label, tokens)) continue;
                RoomKey key = new(fav.Map, fav.Room);
                if (_blacklist.IsBlacklisted(key)) continue;
                if (_graph.GetRoom(key) is not { } froom) continue;
                if (matches.Any(x => x.MonsterTag is null && x.Key.Equals(key))) continue;
                matches.Add(BuildRoomMatch(froom, source)
                    with { MonsterTag = $"{fav.Label} · goto", MatchRank = MatchRank(fav.Label!, tokens),
                           Kind = SearchResultKind.Favorite });
            }
        }

        // ----- Tier 6: boss names from the boss table (opt-in) -----
        // Mirrors the monster tier but sourced from the curated boss list — one row
        // per listed room, tagged with the boss name so it renders as a named target.
        if (includeBosses && _bosses is not null && needle.Length >= 2 && matches.Count < cap)
        {
            foreach (Models.Profile.BossDef boss in _bosses.Resolve())
            {
                if (matches.Count >= cap) break;
                if (!AllTokensMatch(boss.Name, tokens)) continue;
                string bossTag = $"{boss.Name} · boss";
                int rank = MatchRank(boss.Name, tokens);
                foreach (string roomRef in boss.Rooms)
                {
                    if (matches.Count >= cap) break;
                    (int? bm, int? br) = TryParseCoordinate(roomRef);
                    if (bm is null || br is null) continue;
                    RoomKey bk = new(bm.Value, br.Value);
                    if (_blacklist.IsBlacklisted(bk)) continue;
                    if (_graph.GetRoom(bk) is not { } broom) continue;
                    if (matches.Any(x => x.MonsterTag == bossTag && x.Key.Equals(bk))) continue;
                    int? steps = source is { } src ? DistanceFrom(src, broom.Key) : null;
                    matches.Add(new RoomSearchResult(
                        broom.Key, broom.DisplayName, steps, bossTag, rank, SearchResultKind.Boss));
                }
            }
        }

        // Group by source first — favourites, then bosses, then rooms, then bare
        // monster labels — so the dropdown reads by kind instead of interleaving
        // them by relevance. Within each group the original relevance / distance /
        // name order is preserved.
        return matches
            .OrderBy(mm => (int)mm.Kind)
            .ThenBy(mm => mm.MatchRank)
            .ThenBy(mm => mm.StepsFromCurrent ?? int.MaxValue)
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

    // Parse a comma/semicolon-separated coordinate list ("1/224, 1/218,
    // 1/245"). Returns null if any token fails so the caller falls back to name
    // match.
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

    // True when every whitespace/punctuation-delimited word of `needle` appears in
    // `name` (case-insensitive, order-independent) — the same token-subset rule the
    // room-name search tier uses (tier 3), re-exported so the @loop / @goto handlers
    // can single out a saved loop, a GOTO favourite label, or a boss name the same
    // way: "godfrey bank" matches "Bank of Godfrey Loop". A null/blank name or needle
    // never matches. It's the caller's job to require a 1-of-1 hit across candidates.
    public static bool NameMatchesTokens(string? name, string? needle)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(needle)) return false;
        return AllTokensMatch(name, TokenizeNeedle(needle.Trim()));
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
        _searchableMonsterCache = null;
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

    // Monsters the search can resolve to a room: lair-respawning mobs (RegenTime > 0)
    // AND unique / limited spawns (GameLimit == 1, e.g. "aged titan"), which the
    // regen-only gate used to drop when they don't respawn. The tag distinguishes them
    // in the result row ("regen 24h" vs "unique"); a unique that also respawns leads
    // with "unique". RoomsByMonsterId already resolves both via lair tags + Summoned By.
    private IEnumerable<(int Id, string Name, string Tag)> EnumerateSearchableMonsters()
    {
        if (_searchableMonsterCache is not null) return _searchableMonsterCache;

        List<(int, string, string)> list = new();
        GameTable? doc = _gameData.GetTable("Monsters");
        if (doc is null) { _searchableMonsterCache = list; return list; }

        foreach (GameRow row in doc.Rows)
        {
            TryInt(row, "RegenTime", out int regen);
            TryInt(row, "GameLimit", out int limit);
            bool isUnique = limit == 1;
            if (regen <= 0 && !isUnique) continue;
            if (!TryInt(row, "Number", out int id)) continue;
            if (!row.TryGetProperty("Name", out GameValue nameEl)
                || nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            string tag = isUnique
                ? (regen > 0 ? $"unique · regen {regen}h" : "unique")
                : $"regen {regen}h";
            list.Add((id, name, tag));
        }
        _searchableMonsterCache = list;
        return list;
    }

    // Split a search needle into word tokens on whitespace + punctuation, so extra
    // spaces / commas / apostrophes don't defeat a match and multi-word queries can
    // match in any order (see AllTokensMatch).
    private static string[] TokenizeNeedle(string needle)
        => needle.Split(s_tokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly char[] s_tokenSeparators =
        { ' ', '\t', ',', '/', '-', '\'', '.', ';', ':', '(', ')', '[', ']' };

    // A candidate name matches when every needle token is a substring of it
    // (case-insensitive, order-independent) — "titan aged" finds "aged titan". An
    // empty token set (a needle of only separators) never matches.
    private static bool AllTokensMatch(string candidate, string[] tokens)
    {
        if (tokens.Length == 0) return false;
        foreach (string tok in tokens)
            if (!candidate.Contains(tok, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    // Rank how literally a candidate matches the needle tokens, for result ordering:
    // 0 when every token lands on a whole word, 1 when every token at least starts a
    // word, 2 when any token only appears buried mid-word (e.g. "aged" in "Ravaged").
    // Lower sorts first, so literal word matches lead partial substring matches. The
    // worst-placed token sets the rank — one buried token demotes the whole candidate.
    internal static int MatchRank(string candidate, string[] tokens)
    {
        int worst = 0;
        foreach (string tok in tokens)
        {
            int q = TokenWordQuality(candidate, tok);
            if (q > worst) worst = q;
        }
        return worst;
    }

    // Best (lowest) placement quality of a single token within a candidate:
    //   0 = standalone word (non-alphanumeric boundary on both sides)
    //   1 = starts a word (boundary before, letters after — a word prefix)
    //   2 = only occurs buried mid-word
    //   3 = not present (shouldn't happen for an AllTokensMatch candidate)
    private static int TokenWordQuality(string candidate, string token)
    {
        if (token.Length == 0) return 3;
        int best = 3;
        int from = 0;
        while (true)
        {
            int i = candidate.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            bool boundaryBefore = i == 0 || !char.IsLetterOrDigit(candidate[i - 1]);
            int after = i + token.Length;
            bool boundaryAfter = after >= candidate.Length || !char.IsLetterOrDigit(candidate[after]);
            int q = boundaryBefore ? (boundaryAfter ? 0 : 1) : 2;
            if (q < best) best = q;
            if (best == 0) break;
            from = i + 1;
        }
        return best;
    }

    private Dictionary<int, List<RoomKey>> RoomsByMonsterId()
    {
        if (_roomsByMonsterIdCache is not null) return _roomsByMonsterIdCache;
        Dictionary<int, List<RoomKey>> map = new();
        Dictionary<int, HashSet<RoomKey>> seen = new();

        // Source 1: lair tag on each room (pre-1.83 monster-list or
        // NMR 1.83+ group reference parsed via RoomTooltipBuilder).
        foreach (Room room in _graph.Rooms)
        {
            if (string.IsNullOrEmpty(room.RawLairTag)) continue;
            RoomTooltipBuilder.ParseLairTag(room.RawLairTag, out _, out IReadOnlyList<int> ids);
            foreach (int id in ids) AddMonsterRoom(map, seen, id, room.Key);
        }

        // Source 2: Monsters.json "Summoned By" — boss / script spawns
        // whose room placement lives on the monster record.
        GameTable? doc = _gameData.GetTable("Monsters");
        if (doc is not null)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue numEl)
                    || numEl.ValueKind != JsonValueKind.Number
                    || !numEl.TryGetInt32(out int id)) continue;
                if (!row.TryGetProperty("Summoned By", out GameValue summonEl)
                    || summonEl.ValueKind != JsonValueKind.String) continue;
                string? text = summonEl.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                int at = 0;
                while (RoomTokens.TryNext(text, ref at, out int mn, out int rn))
                {
                    if (mn <= 0 || rn <= 0) continue;
                    AddMonsterRoom(map, seen, id, new RoomKey(mn, rn));
                }
            }
        }

        _roomsByMonsterIdCache = map;
        return map;
    }

    // seen dedupes each monster's rooms: a monster listed in thousands of them made
    // checking the list itself for every addition quadratic.
    private static void AddMonsterRoom(Dictionary<int, List<RoomKey>> map,
        Dictionary<int, HashSet<RoomKey>> seen, int monsterId, RoomKey key)
    {
        if (!map.TryGetValue(monsterId, out List<RoomKey>? rooms))
        {
            map[monsterId] = rooms = new List<RoomKey>();
            seen[monsterId] = new HashSet<RoomKey>();
        }
        if (seen[monsterId].Add(key)) rooms.Add(key);
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
        Dictionary<int, HashSet<RoomKey>> seen = new();

        // Primary: the room's NPC field statically places the monster there.
        foreach (Room room in _graph.Rooms)
            if (room.Npc > 0) AddMonsterRoom(placed, seen, room.Npc, room.Key);

        GameTable? doc = _gameData.GetTable("Monsters");
        if (doc is not null)
        {
            // Map each summon spell to the NPC(s) whose CreateSpell casts it, so a
            // "Summoned By: Spell #N" target can borrow its summoner's placement.
            Dictionary<int, List<int>> summonerBySpell = new();
            foreach (GameRow row in doc.Rows)
            {
                if (!TryInt(row, "Number", out int owner)) continue;
                if (!TryInt(row, "CreateSpell", out int spell) || spell <= 0) continue;
                if (!summonerBySpell.TryGetValue(spell, out List<int>? owners))
                    summonerBySpell[spell] = owners = new List<int>();
                owners.Add(owner);
            }

            // Fallback for monsters with no static placement: their own Summoned By
            // spawn room(s), else the placement of whoever summons them.
            foreach (GameRow row in doc.Rows)
            {
                if (!TryInt(row, "Number", out int id) || placed.ContainsKey(id)) continue;
                if (!row.TryGetProperty("Summoned By", out GameValue sbEl)
                    || sbEl.ValueKind != JsonValueKind.String) continue;
                string text = sbEl.GetString() ?? string.Empty;
                if (text.Length == 0) continue;

                bool addedRoom = false;
                int at = 0;
                while (RoomTokens.TryNext(text, ref at, out int mn, out int rn))
                {
                    if (mn > 0 && rn > 0)
                    {
                        AddMonsterRoom(placed, seen, id, new RoomKey(mn, rn));
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
                            foreach (RoomKey k in ownerRooms) AddMonsterRoom(placed, seen, id, k);
                }
            }
        }

        _questKillRoomsCache = placed.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<RoomKey>)kv.Value);
        return _questKillRoomsCache;
    }

    private static bool TryInt(GameRow row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out GameValue el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
