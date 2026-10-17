using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// In-memory index of Monsters.json for the active game-data set, answering
// two questions the monster-drop reroute needs: which monsters drop a given
// item, and where each of those monsters spawns. Backs the per-item
// source-from-drops affordance — when a walk-route crosses an (Item: N) /
// (Ticket: N) gate whose item we don't carry and no shop sells it, and the item
// is flagged AutoObtainForPath (item-edit dialog), MonsterDropRouter asks this
// index which monsters drop it and reroutes toward the nearest one's lair.
//
// A monster "drops" an item when the id appears in any of its ten
// DropItem-0..DropItem-9 slots; the paired DropItem%-N gives the drop chance
// (0 = unknown/always). Spawn sites come from the monster's Summoned By
// field, which lists the rooms the monster (including its lair group)
// populates — a single authoritative source that covers both fixed
// placements and lair spawns, so the room graph's Room.RawLairTag need not be
// parsed here. Every (map)/(room) token is taken (the field carries
// occasional malformed tokens — a truncated oup:, a map-less Group: 102 —
// which a digits/digits match skips naturally).
//
// Only monsters that drop at least one item have their spawn rooms indexed —
// the reroute never asks about a non-dropper, so parsing every monster's
// (often hundreds-long) Summoned By would be wasted work.
//
// Distinct from MonsterSpawnIndex (which maps the inverse — RoomKey →
// monsters summoned there — for room tooltips): this index is item-first and
// drop-scoped. Mirrors ShopStockIndex: subscribes to
// GameDataCache.ActiveSetChanged, reads the raw table once, builds the maps,
// and evicts the table.
public sealed class MonsterDropIndex
{
    // One monster that drops an item, with its drop chance.
    //   MonsterId   — the monster's Number.
    //   MonsterName — the monster's display name (for the reroute prompt).
    //   DropPercent — the paired DropItem%, or 0 when unset.
    public readonly record struct MonsterDrop(int MonsterId, string MonsterName, int DropPercent);

    private const int DropSlots = 10;

    // A monster's drop slots: the item in each, and the chance beside it.
    internal static readonly string[] DropItemFields = NumberedFields.Of("DropItem-", DropSlots);
    internal static readonly string[] DropPercentFields = NumberedFields.Of("DropItem%-", DropSlots);

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, List<MonsterDrop>> _droppersByItem = new();
    private readonly Dictionary<int, List<RoomKey>> _spawnRoomsByMonster = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    public MonsterDropIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Monsters that drop itemId, or an empty list when nothing in the active
    // set drops it. The returned list is a live view of the index — callers
    // read it, never mutate it.
    public IReadOnlyList<MonsterDrop> DroppersOf(int itemId)
        => _droppersByItem.TryGetValue(itemId, out List<MonsterDrop>? drops)
            ? drops
            : Array.Empty<MonsterDrop>();

    // Rooms where the monster with monsterId spawns (from its Summoned By
    // field), or an empty list when the monster isn't a known dropper. Live
    // view — read, don't mutate.
    public IReadOnlyList<RoomKey> SpawnRoomsOf(int monsterId)
        => _spawnRoomsByMonster.TryGetValue(monsterId, out List<RoomKey>? rooms)
            ? rooms
            : Array.Empty<RoomKey>();

    // Reload the index from setName's Monsters.json. Pass null to clear.
    // Wired by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _droppersByItem.Clear();
        _spawnRoomsByMonster.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("MonsterDropIndex", "No active set; cleared.");
            return;
        }

        GameTable? doc = _cache.GetTable("Monsters");
        if (doc is null)
        {
            _log?.Info("MonsterDropIndex", $"Active set '{setName}' has no Monsters.json; empty.");
            return;
        }

        foreach (GameRow row in doc.Rows)
        {
            if (!TryReadInt(row, "Number", out int monsterId) || monsterId <= 0) continue;

            string name = row.TryGetProperty("Name", out GameValue nameEl)
                          && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

            bool droppedAny = false;
            for (int slot = 0; slot < DropSlots; slot++)
            {
                if (!TryReadInt(row, DropItemFields[slot], out int itemId) || itemId <= 0)
                    continue;
                TryReadInt(row, DropPercentFields[slot], out int pct);
                if (!_droppersByItem.TryGetValue(itemId, out List<MonsterDrop>? drops))
                    _droppersByItem[itemId] = drops = new List<MonsterDrop>();
                drops.Add(new MonsterDrop(monsterId, name, pct));
                droppedAny = true;
            }

            // Only droppers ever get their spawn rooms indexed — the reroute
            // never asks where a non-dropper lives.
            if (droppedAny)
                IndexSpawnRooms(row, monsterId);
        }

        _cache.EvictTable("Monsters");

        _log?.Info("MonsterDropIndex",
            $"Indexed {_droppersByItem.Count} dropped item(s) across " +
            $"{_spawnRoomsByMonster.Count} dropper(s) from '{setName}'.");

    }

    private void IndexSpawnRooms(GameRow row, int monsterId)
    {
        if (!row.TryGetProperty("Summoned By", out GameValue summonEl)
            || summonEl.ValueKind != JsonValueKind.String)
            return;
        List<RoomKey>? rooms = ParseSummonedByRooms(summonEl.GetString());
        if (rooms is not null)
            _spawnRoomsByMonster[monsterId] = rooms;
    }

    // Extract the distinct (map)/(room) spawn sites from a monster's raw
    // "Summoned By" field, or null when it names none. The field carries
    // occasional malformed tokens (a truncated oup:, a map-less Group: 102);
    // a digits/digits match skips those naturally. Shared with ItemSourceIndex,
    // which resolves a giver monster's location off the same field.
    public static List<RoomKey>? ParseSummonedByRooms(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        List<RoomKey>? rooms = null;
        // A wandering monster can list thousands of rooms; checking the list itself
        // for each one made that quadratic.
        HashSet<RoomKey>? seen = null;
        int at = 0;
        while (RoomTokens.TryNext(text, ref at, out int map, out int room))
        {
            if (map <= 0 || room <= 0) continue;
            RoomKey key = new(map, room);
            rooms ??= new List<RoomKey>();
            seen ??= new HashSet<RoomKey>();
            if (seen.Add(key)) rooms.Add(key);
        }
        return rooms;
    }

    private static bool TryReadInt(GameRow row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out GameValue el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
