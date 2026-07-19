using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// In-memory index of Monsters.json for the active game-data set, answering
// two questions the monster-drop reroute needs: which monsters drop a given
// item, and where each of those monsters spawns. Backs the per-item
// source-from-drops affordance — when a walk-route crosses an (Item: N) /
// (Ticket: N) gate whose item we don't carry and no shop sells it, and the item
// is flagged SourceFromDropsForPath (item-edit dialog, under AutoObtainForPath),
// MonsterDropRouter asks this index which monsters drop it and reroutes
// toward the nearest one's lair.
//
// A monster "drops" an item when the id appears in any of its ten
// DropItem-0..DropItem-9 slots; the paired DropItem%-N gives the drop chance
// (0 = unknown/always). Spawn sites come from the monster's Summoned By
// field, which lists the rooms the monster (including its lair group)
// populates — a single authoritative source that covers both fixed
// placements and lair spawns, so the room graph's Room.RawLairTag need not be
// parsed here. All (map)/(room) tokens are regex-extracted (the field carries
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
// and evicts the JsonDocument.
public sealed class MonsterDropIndex
{
    // One monster that drops an item, with its drop chance.
    //   MonsterId   — the monster's Number.
    //   MonsterName — the monster's display name (for the reroute prompt).
    //   DropPercent — the paired DropItem%, or 0 when unset.
    public readonly record struct MonsterDrop(int MonsterId, string MonsterName, int DropPercent);

    private const int DropSlots = 10;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, List<MonsterDrop>> _droppersByItem = new();
    private readonly Dictionary<int, List<int>> _dropItemsByMonster = new();
    private readonly Dictionary<int, List<RoomKey>> _spawnRoomsByMonster = new();

    private static readonly Regex s_roomToken = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of distinct dropped item ids in the active set.
    public int ItemCount => _droppersByItem.Count;

    // Fires after every successful (re)load, including the transition to no-set-active.
    public event Action? StoreReloaded;

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

    // Item ids the monster with monsterId can drop (its non-empty DropItem-0..9
    // slots), or an empty list when it drops nothing. Forward view for the
    // post-kill re-look: on a monster's death we ask what it could have dropped
    // to decide whether to re-survey the room. Live view — read, don't mutate.
    public IReadOnlyList<int> DropItemsOf(int monsterId)
        => _dropItemsByMonster.TryGetValue(monsterId, out List<int>? items)
            ? items
            : Array.Empty<int>();

    // True when at least one monster in the active set drops the item.
    public bool AnyMonsterDrops(int itemId) => _droppersByItem.ContainsKey(itemId);

    // Reload the index from setName's Monsters.json. Pass null to clear.
    // Wired by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _droppersByItem.Clear();
        _dropItemsByMonster.Clear();
        _spawnRoomsByMonster.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("MonsterDropIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null)
        {
            _log?.Info("MonsterDropIndex", $"Active set '{setName}' has no Monsters.json; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int monsterId) || monsterId <= 0) continue;

            string name = row.TryGetProperty("Name", out JsonElement nameEl)
                          && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

            bool droppedAny = false;
            for (int slot = 0; slot < DropSlots; slot++)
            {
                if (!TryReadInt(row, $"DropItem-{slot}", out int itemId) || itemId <= 0)
                    continue;
                TryReadInt(row, $"DropItem%-{slot}", out int pct);
                if (!_droppersByItem.TryGetValue(itemId, out List<MonsterDrop>? drops))
                    _droppersByItem[itemId] = drops = new List<MonsterDrop>();
                drops.Add(new MonsterDrop(monsterId, name, pct));

                if (!_dropItemsByMonster.TryGetValue(monsterId, out List<int>? items))
                    _dropItemsByMonster[monsterId] = items = new List<int>();
                if (!items.Contains(itemId)) items.Add(itemId);

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

        StoreReloaded?.Invoke();
    }

    private void IndexSpawnRooms(JsonElement row, int monsterId)
    {
        if (!row.TryGetProperty("Summoned By", out JsonElement summonEl)
            || summonEl.ValueKind != JsonValueKind.String)
            return;
        string? text = summonEl.GetString();
        if (string.IsNullOrEmpty(text)) return;

        List<RoomKey>? rooms = null;
        foreach (Match m in s_roomToken.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int map) || map <= 0) continue;
            if (!int.TryParse(m.Groups[2].Value, out int room) || room <= 0) continue;
            RoomKey key = new(map, room);
            rooms ??= new List<RoomKey>();
            if (!rooms.Contains(key)) rooms.Add(key);
        }
        if (rooms is not null)
            _spawnRoomsByMonster[monsterId] = rooms;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
