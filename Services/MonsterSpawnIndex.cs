using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Reverse index of RoomKey → monster ids whose Monsters.json Summoned By
// field references that room. One immutable build per active set, published
// by a single reference assignment so a background warm-up can run safely —
// the same pattern ItemSourceIndex uses. Warmed on a worker thread when the
// active set changes (Services.AppServices); a query that beats the warm
// simply builds it itself.
//
// Bosses and other script-spawned monsters carry their spawn site on the
// monster record rather than the room's Room.RawLairTag. The room search
// reads this via RoomSearchService.RoomsByMonsterId; the room tooltip needs
// the inverse — given a room, which monsters does it host? Without the index
// a tooltip's Also Here line silently omits any boss whose presence lives
// only on the monster record (live repro: 1/1678 Darkwood Forest, Webbed
// Clearing had no giant spider in its tooltip even though Monster 52's
// Summoned By reads "Room 1/1678").
//
// The Summoned By field mixes THREE kinds of room reference, distinguished by
// the keyword before the map/room token (verified against the data + the room
// side: a monster's `Group(lair)` token always points at a room WITH a Lair
// tag, `Group:` at one WITHOUT):
//   - "Room m/r"          → the room's NPC fixture — a PLACED boss / unique.
//   - "Group(lair): m/r"  → a LAIR spawn (the room's Lair tag lists it too).
//   - "Group: m/r"        → an ASSIGNED roam / rare-random spawn (no lair).
// We index placed and assigned separately so the tooltip can show the
// distinction; lair members are read from the room's own Lair tag, so the
// (lair) tokens aren't split out here. `MonsterIdsSummonedAt` keeps returning
// EVERY token (the combat resolver's permissive candidate set relies on it).
//
// Cache rebuild costs O(monster references) once per active-set switch;
// per-tooltip lookups are O(1). The Lairs.json GroupIndex tokens use '-'
// between numbers, so a digits/digits regex catches only room references.
public sealed class MonsterSpawnIndex
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    // One immutable build, published by a single reference assignment. Readers
    // take the current snapshot and work from it, so a rebuild can run on a
    // background thread without ever handing a caller a half-filled map.
    //
    // Load is the load of the set it was built from, name and count: a set imported
    // again over itself keeps its name, and the name alone left the old build
    // answering until a real switch.
    private sealed class Snapshot
    {
        public required GameDataCache.Load Load { get; init; }
        public required RoomLists<int> SummonedAt { get; init; }
        public required RoomLists<int> PlacedAt { get; init; }
        public required RoomLists<int> AssignedAt { get; init; }
    }

    // Serialises builders so two threads racing the same set build once, not twice.
    private readonly object _buildGate = new();
    private volatile Snapshot? _snapshot;

    // Monsters on the Unobtainable list, which the room readouts leave out. Checked at
    // query time rather than folded into the build: the rule needs the room graph, which
    // a set switch rebuilds on the UI thread while this index warms on a worker.
    public Func<int, bool>? OutOfPlay { get; set; }

    public bool IsOutOfPlay(int id) => OutOfPlay?.Invoke(id) == true;

    public MonsterSpawnIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Monster ids whose Summoned By references the given room (ALL token kinds —
    // placed, assigned, and lair). Empty list when nothing spawns at the room (or
    // no Monsters table is loaded). This is the combat resolver's permissive
    // candidate set.
    public IReadOnlyList<int> MonsterIdsSummonedAt(RoomKey key) => EnsureBuilt().SummonedAt.ListOf(key);

    // Monster ids PLACED at the room — a "Room m/r" token (the room's NPC
    // fixture: bosses / uniques). Excludes lair and assigned/roam spawns.
    public IReadOnlyList<int> PlacedMonsterIdsAt(RoomKey key) => EnsureBuilt().PlacedAt.ListOf(key);

    // Monster ids ASSIGNED to roam / rare-randomly spawn at the room — a
    // non-lair "Group: m/r" token. Excludes placed fixtures and lair spawns.
    public IReadOnlyList<int> AssignedMonsterIdsAt(RoomKey key) => EnsureBuilt().AssignedAt.ListOf(key);

    // Build the index for the active set ahead of anyone asking for it. Safe to
    // call from a worker thread — the build touches only locals until it
    // publishes, and GameDataCache guards its own tables — so the build lands
    // while the set loads instead of inside the first room-tooltip hover that
    // needs it. A query racing the warm just builds it itself; the gate makes
    // one of them wait rather than doing the work twice.
    public void Warm()
    {
        try
        {
            // mayEvictRaw: false is a leftover with nothing left to guard; see EnsureBuilt.
            EnsureBuilt(mayEvictRaw: false);
        }
        catch (Exception ex)
        {
            // A warm-up is an optimisation; a failure here must not take down
            // the thread it runs on. The next query rebuilds and surfaces the fault.
            _log?.Log(LogSeverity.Warn, "MonsterSpawnIndex",
                $"Warm-up failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    // mayEvictRaw gates the post-build EvictTable("Monsters"). It no longer guards
    // anything: it kept the warm-up's worker thread from evicting when evicting
    // disposed a document other threads were still reading, and EvictTable now only
    // marks the table for the cache's idle sweep, which is safe from any thread. In
    // practice the build is the warm-up's (every set change starts one) and passes
    // false; a query that beats the warm-up to the build passes true. Either way
    // the table is handed back, since MonsterCatalog, RoomGraphManager,
    // SeeHiddenIndex and friends all evict "Monsters" too.
    private Snapshot EnsureBuilt(bool mayEvictRaw = true)
    {
        if (_snapshot is { } current && current.Load == _cache.ActiveLoad) return current;

        lock (_buildGate)
        {
            // Read once the gate is ours and before the build: another thread may
            // have published while we waited, and the set may have been loaded again.
            GameDataCache.Load load = _cache.ActiveLoad;
            if (_snapshot is { } published && published.Load == load) return published;
            Snapshot built = Build(load, mayEvictRaw);
            _snapshot = built;
            return built;
        }
    }

    private Snapshot Build(GameDataCache.Load load, bool mayEvictRaw)
    {
        string? active = load.Set;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var summonedAt = new Dictionary<RoomKey, List<int>>();
        var placedAt = new Dictionary<RoomKey, List<int>>();
        var assignedAt = new Dictionary<RoomKey, List<int>>();

        GameTable? doc = string.IsNullOrWhiteSpace(active) ? null : _cache.GetTable("Monsters");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                "Active set has no Monsters.json; index left empty.");
        }
        else
        {
            // A room's list must not name a monster twice. A room with many "Summoned
            // By" hits (a common "Group: m/r" roam token shared by thousands of
            // monster records) once checked that with List<int>.Contains, an O(n) scan
            // on every insert that made a single hot room's build cost O(n²): on
            // Paradigm's ~215k-token Monsters.json a real ~29 s UI-thread stall
            // (paradigm-20260911-231808). The rows come one monster at a time, so a
            // repeat within a row is the list's last entry, and only a Number the
            // table carries on two rows needs the scan.
            var numbersSeen = new HashSet<int>();

            int linked = 0;
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue numEl)
                    || numEl.ValueKind != JsonValueKind.Number
                    || !numEl.TryGetInt32(out int id)) continue;
                if (!row.TryGetProperty("Summoned By", out GameValue summonEl)
                    || summonEl.ValueKind != JsonValueKind.String) continue;
                string? text = summonEl.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                bool numberRepeats = !numbersSeen.Add(id);

                // Each comma-separated part carries at most one "map/room" token plus
                // the keyword that classifies it. Taken part by part so the keyword and
                // its room stay associated (a sweep of the whole field loses that pairing).
                ReadOnlySpan<char> rest = text;
                while (true)
                {
                    int comma = rest.IndexOf(',');
                    ReadOnlySpan<char> part = comma < 0 ? rest : rest[..comma];
                    int at = 0;
                    if (RoomTokens.TryNext(part, ref at, out int map, out int room) && map > 0 && room > 0)
                    {
                        RoomKey key = new(map, room);

                        AddTo(summonedAt, key, id, numberRepeats);   // permissive set — every token
                        linked++;

                        // Lair members come from the room's Lair tag.
                        if (!part.Contains("(lair)", StringComparison.OrdinalIgnoreCase))
                        {
                            if (part.Contains("Group", StringComparison.OrdinalIgnoreCase))
                                AddTo(assignedAt, key, id, numberRepeats);   // "Group:" (non-lair) → roam / random
                            else
                                AddTo(placedAt, key, id, numberRepeats);     // "Room m/r" (or an unkeyworded ref) → placed fixture
                        }
                    }
                    if (comma < 0) break;
                    rest = rest[(comma + 1)..];
                }
            }

            // Folded into the spawn maps — the cache may drop the Monsters table,
            // when this build is the one to say so.
            if (mayEvictRaw) _cache.EvictTable("Monsters");
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                $"Built spawn index — {summonedAt.Count} room(s) host {linked} monster reference(s) "
                + $"({placedAt.Count} placed, {assignedAt.Count} assigned), "
                + $"in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
        }

        return new Snapshot
        {
            Load = load,
            SummonedAt = RoomLists<int>.From(summonedAt),
            PlacedAt = RoomLists<int>.From(placedAt),
            AssignedAt = RoomLists<int>.From(assignedAt),
        };
    }

    // Append id to map[key] unless it is there, first-seen order. The row being read
    // is the only one adding id, so its earlier add to this list, if any, is the last
    // entry; numberRepeats says an earlier row carried the same Number, and then the
    // whole list is checked.
    private static void AddTo(Dictionary<RoomKey, List<int>> map, RoomKey key, int id, bool numberRepeats)
    {
        if (!map.TryGetValue(key, out List<int>? list))
            map[key] = list = new List<int>();
        else if (list[^1] == id || (numberRepeats && list.Contains(id)))
            return;
        list.Add(id);
    }
}
