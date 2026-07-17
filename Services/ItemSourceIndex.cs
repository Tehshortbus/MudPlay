using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// One container that can yield an item — the reverse of ChestContentsReader:
// which chest an item drops from, and the per-open chance that a single `open`
// produces at least one of it.
public readonly record struct ItemSource(int ContainerItemId, string ContainerName, double Probability);

// Whether a textblock item-award roots at a monster (dialogue / turn-in) or a
// room (a room CMD like the dragon-statue "insert fang" → dragon key).
public enum ItemGiverKind { Monster, Room }

// One NPC or room that hands an item over through a TBInfo `giveitem` directive,
// attributed via the block's Called-From chain to its monster / room root. The
// requirement note ("turn in <item>", "purchase", "quest reward") is empty when
// the award line carries no gate.
public readonly record struct ItemGiver(
    ItemGiverKind Kind, int Number, int Map, int Room, string Name, string Requirement);

// Lazy per-set reverse index of the two item-acquisition paths the shop/drop
// indexes don't cover: containers (an item's chest sources — the inverse of
// ChestContentsReader) and textblock `giveitem` awards (quest turn-ins, room
// CMD rewards, and merchant gives, attributed to their monster / room via each
// TBInfo entry's Called-From chain). Backs the Game Data Browser's item detail
// pane only, so unlike the eager routing indexes (ShopStockIndex /
// MonsterDropIndex) it builds on first query and self-invalidates by comparing
// the cache's ActiveSet to the set it last built from — no ActiveSetChanged
// subscription, and it never evicts tables (the browser is actively reading
// them). `roomitem` awards are deliberately excluded: that verb scatter-places
// an item in a room rather than handing it to the player on a gated exchange.
public sealed class ItemSourceIndex
{
    private readonly GameDataCache _cache;
    private readonly TBInfoStore _tb;
    private readonly LogService? _log;

    private readonly Dictionary<int, List<ItemSource>> _containersByItem = new();
    private readonly Dictionary<int, List<ItemGiver>> _giversByItem = new();

    // Set the maps were last built from; compared against _cache.ActiveSet to
    // self-invalidate on a set swap. Null both means "never built".
    private string? _loadedSet;
    private bool _built;

    // Provenance-walk safety caps: a Called-From chain is a small acyclic tree in
    // practice, but the visited-set plus these bounds keep a malformed cycle or a
    // pathologically deep chain from spinning.
    private const int MaxWalkSteps = 512;

    public ItemSourceIndex(GameDataCache cache, TBInfoStore tb, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tb);
        _cache = cache;
        _tb = tb;
        _log = log;
    }

    // Containers that can yield itemId, highest drop chance first, or an empty
    // list when nothing in the active set drops it. Live view — read, don't mutate.
    public IReadOnlyList<ItemSource> ContainersOf(int itemId)
    {
        EnsureBuilt();
        return _containersByItem.TryGetValue(itemId, out List<ItemSource>? list)
            ? list
            : Array.Empty<ItemSource>();
    }

    // Monsters / rooms that hand itemId over via a textblock `giveitem`, or an
    // empty list when nothing gives it. Live view — read, don't mutate.
    public IReadOnlyList<ItemGiver> GiversOf(int itemId)
    {
        EnsureBuilt();
        return _giversByItem.TryGetValue(itemId, out List<ItemGiver>? list)
            ? list
            : Array.Empty<ItemGiver>();
    }

    private void EnsureBuilt()
    {
        string? active = _cache.ActiveSet;
        if (_built && _loadedSet == active) return;
        Rebuild(active);
    }

    private void Rebuild(string? active)
    {
        _containersByItem.Clear();
        _giversByItem.Clear();
        _loadedSet = active;
        _built = true;

        if (string.IsNullOrWhiteSpace(active))
        {
            _log?.Info("ItemSourceIndex", "No active set; cleared.");
            return;
        }

        BuildContainers();
        BuildGivers();

        _log?.Info("ItemSourceIndex",
            $"Indexed {_containersByItem.Count} container-sourced item(s) and " +
            $"{_giversByItem.Count} textblock-given item(s) from '{active}'.");
    }

    // Invert ChestContentsReader.ReadAll (container → drops) into item → containers.
    private void BuildContainers()
    {
        IReadOnlyDictionary<int, ChestContents> chests = ChestContentsReader.ReadAll(_cache);
        foreach ((int containerId, ChestContents contents) in chests)
        {
            string containerName = _cache.FindNameByNumber("Items", containerId) is { Length: > 0 } n
                ? n
                : $"#{containerId.ToString(CultureInfo.InvariantCulture)}";
            foreach (ChestDrop drop in contents.Drops)
            {
                if (!_containersByItem.TryGetValue(drop.ItemId, out List<ItemSource>? list))
                    _containersByItem[drop.ItemId] = list = new List<ItemSource>();
                list.Add(new ItemSource(containerId, containerName, drop.Probability));
            }
        }
        // Highest-chance source first so the detail pane leads with the most
        // likely chest to open.
        foreach (List<ItemSource> list in _containersByItem.Values)
            list.Sort(static (a, b) => b.Probability.CompareTo(a.Probability));
    }

    // Walk every TBInfo entry that hands out an item, attribute each award to its
    // monster / room root, and record the per-award requirement gate.
    private void BuildGivers()
    {
        Dictionary<int, string> itemNames = BuildNameMap("Items");
        Dictionary<int, string> monsterNames = BuildNameMap("Monsters");
        Dictionary<(int Map, int Room), string> roomNames = BuildRoomNameMap();

        var roots = new List<GiverRoot>();
        var giveItems = new List<int>();

        foreach (TBInfoEntry entry in _tb.Entries)
        {
            string? action = entry.Action;
            if (string.IsNullOrEmpty(action)
                || action.IndexOf("giveitem", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            roots.Clear();
            ResolveRoots(entry, roots);
            if (roots.Count == 0) continue;   // orphaned block — nothing to attribute to.

            foreach (string rawLine in action.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                giveItems.Clear();
                int takeId = 0;
                bool hasPrice = false, hasAbility = false;
                foreach (string rawTok in line.Split(':'))
                {
                    string tok = rawTok.Trim();
                    if (TryArg(tok, "giveitem", out int gi) && gi > 0) giveItems.Add(gi);
                    else if (takeId == 0 && TryArg(tok, "takeitem", out int ti) && ti > 0) takeId = ti;
                    else if (tok.StartsWith("price", StringComparison.OrdinalIgnoreCase)) hasPrice = true;
                    else if (tok.StartsWith("giveability", StringComparison.OrdinalIgnoreCase)) hasAbility = true;
                }
                if (giveItems.Count == 0) continue;

                // Turn-in names the required item (the most useful hint); a bare
                // price is a purchase; a lone giveability marks a quest reward.
                string requirement =
                    takeId > 0 ? "turn in " + ResolveName(itemNames, takeId, $"item #{takeId}")
                    : hasPrice ? "purchase"
                    : hasAbility ? "quest reward"
                    : string.Empty;

                foreach (int itemId in giveItems)
                {
                    foreach (GiverRoot root in roots)
                    {
                        string name = root.Kind == ItemGiverKind.Monster
                            ? ResolveName(monsterNames, root.Number, $"Monster #{root.Number}")
                            : (roomNames.TryGetValue((root.Map, root.Room), out string? rn) && rn.Length > 0
                                ? rn
                                : $"Room {root.Map}/{root.Room}");
                        AddGiver(itemId,
                            new ItemGiver(root.Kind, root.Number, root.Map, root.Room, name, requirement));
                    }
                }
            }
        }
    }

    // Walk the entry's Called-From graph upward, collecting the distinct monster /
    // room roots. Textblock refs recurse (a block called by another block); spell
    // refs stop the walk — an item cast out of a spell is either chest loot (the
    // ContainersOf path already covers it) or a self-cast, neither of which is a
    // walkable "go here to get it" source.
    private void ResolveRoots(TBInfoEntry entry, List<GiverRoot> roots)
    {
        var seenTb = new HashSet<int>();
        var pending = new Queue<string?>();
        pending.Enqueue(entry.CalledFrom);

        int steps = 0;
        while (pending.Count > 0 && steps++ < MaxWalkSteps)
        {
            foreach (CalledFromRef r in ParseCalledFrom(pending.Dequeue()))
            {
                switch (r.Kind)
                {
                    case CfKind.Monster:
                        AddRoot(roots, new GiverRoot(ItemGiverKind.Monster, r.Number, 0, 0));
                        break;
                    case CfKind.Room:
                        AddRoot(roots, new GiverRoot(ItemGiverKind.Room, 0, r.Map, r.Room));
                        break;
                    case CfKind.Textblock:
                        if (r.Number > 0 && seenTb.Add(r.Number)
                            && _tb.GetEntry(r.Number) is { } parent)
                            pending.Enqueue(parent.CalledFrom);
                        break;
                    case CfKind.Spell:
                        break;   // dead-ends the walk (see method note).
                }
            }
        }
    }

    private void AddGiver(int itemId, ItemGiver giver)
    {
        if (!_giversByItem.TryGetValue(itemId, out List<ItemGiver>? list))
            _giversByItem[itemId] = list = new List<ItemGiver>();

        // Dedup by source identity — the same monster / room reached through
        // several award lines is one row. Keep the first requirement seen, but
        // backfill it if the earlier hit had none.
        for (int i = 0; i < list.Count; i++)
        {
            ItemGiver e = list[i];
            if (e.Kind == giver.Kind && e.Number == giver.Number
                && e.Map == giver.Map && e.Room == giver.Room)
            {
                if (e.Requirement.Length == 0 && giver.Requirement.Length > 0)
                    list[i] = e with { Requirement = giver.Requirement };
                return;
            }
        }
        list.Add(giver);
    }

    private static void AddRoot(List<GiverRoot> roots, GiverRoot root)
    {
        if (!roots.Contains(root)) roots.Add(root);
    }

    // ----- Called-From parsing -----

    private enum CfKind { Monster, Room, Textblock, Spell }
    private readonly record struct CalledFromRef(CfKind Kind, int Number, int Map, int Room);
    private readonly record struct GiverRoot(ItemGiverKind Kind, int Number, int Map, int Room);

    // A Called-From string is a comma-separated list of provenance tokens:
    // "Monster #246", "Room 7/1008", "Textblock #349", "Textblock(rndm) #868",
    // "Spell #559". Unknown / malformed tokens are skipped.
    private static IEnumerable<CalledFromRef> ParseCalledFrom(string? calledFrom)
    {
        if (string.IsNullOrWhiteSpace(calledFrom)) yield break;
        foreach (string raw in calledFrom.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            if (part.StartsWith("Monster", StringComparison.OrdinalIgnoreCase))
            {
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Monster, n, 0, 0);
            }
            else if (part.StartsWith("Room", StringComparison.OrdinalIgnoreCase))
            {
                if (TryRoom(part, out int map, out int room))
                    yield return new CalledFromRef(CfKind.Room, 0, map, room);
            }
            else if (part.StartsWith("Textblock", StringComparison.OrdinalIgnoreCase))
            {
                // Covers both "Textblock #N" and "Textblock(rndm) #N".
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Textblock, n, 0, 0);
            }
            else if (part.StartsWith("Spell", StringComparison.OrdinalIgnoreCase))
            {
                yield return new CalledFromRef(CfKind.Spell, 0, 0, 0);
            }
        }
    }

    // Leading integer after the first '#', or 0.
    private static int IntAfterHash(string s)
    {
        int hash = s.IndexOf('#');
        if (hash < 0) return 0;
        return LeadingInt(s[(hash + 1)..]);
    }

    // Parse the "map/room" pair out of "Room 7/1008".
    private static bool TryRoom(string s, out int map, out int room)
    {
        map = 0; room = 0;
        int slash = s.IndexOf('/');
        if (slash < 0) return false;
        // Map number is the trailing int of the segment before '/'.
        map = TrailingInt(s[..slash]);
        room = LeadingInt(s[(slash + 1)..]);
        return map > 0 && room > 0;
    }

    private Dictionary<int, string> BuildNameMap(string table)
    {
        var map = new Dictionary<int, string>();
        JsonDocument? doc = _cache.GetRawTable(table);
        if (doc is null) return map;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("Number", out JsonElement n) || n.ValueKind != JsonValueKind.Number) continue;
            if (!n.TryGetInt32(out int num) || num <= 0) continue;
            if (row.TryGetProperty("Name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
                map[num] = nm.GetString() ?? string.Empty;
        }
        return map;
    }

    private Dictionary<(int Map, int Room), string> BuildRoomNameMap()
    {
        var map = new Dictionary<(int, int), string>();
        JsonDocument? doc = _cache.GetRawTable("Rooms");
        if (doc is null) return map;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(row, "Map Number", out int m) || !TryInt(row, "Room Number", out int r)) continue;
            if (row.TryGetProperty("Name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
                map[(m, r)] = nm.GetString() ?? string.Empty;
        }
        return map;
    }

    private static string ResolveName(Dictionary<int, string> names, int number, string fallback)
        => names.TryGetValue(number, out string? n) && n.Length > 0 ? n : fallback;

    // ----- Directive-token parsing (giveitem / takeitem grammar) -----
    //
    // A trimmed, deliberately small duplicate of ChestContentsReader's private
    // token helpers: that copy parses the chest loot subset (giveitem / random),
    // this one parses the award subset (giveitem / takeitem) — same shape, but
    // the two grammars live with the code that owns them rather than sharing an
    // exported parser.

    private static bool TryArg(string tok, string keyword, out int value)
    {
        value = 0;
        if (!tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        int i = keyword.Length;
        if (i >= tok.Length || tok[i] != ' ') return false;
        value = LeadingInt(tok[(i + 1)..]);
        return true;
    }

    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start
            && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static int TrailingInt(string s)
    {
        int i = s.Length;
        while (i > 0 && s[i - 1] == ' ') i--;
        int end = i;
        while (i > 0 && s[i - 1] is >= '0' and <= '9') i--;
        return end > i
            && int.TryParse(s.AsSpan(i, end - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static bool TryInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
