using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

/// <summary>
/// In-memory index of <c>Monsters.json</c> for the active game-data set,
/// answering two questions the monster-drop reroute needs: which monsters
/// drop a given item, and where each of those monsters spawns. Backs the
/// Settings → Other "hunt item if needed" affordance — when a walk-route
/// crosses an <c>(Item: N)</c> / <c>(Ticket: N)</c> gate whose item we
/// don't carry and no shop sells it,
/// <see cref="Game.Map.MonsterDropRouter"/> asks this index which monsters
/// drop it and reroutes toward the nearest one's lair.
/// </summary>
/// <remarks>
/// <para>
/// A monster "drops" an item when the id appears in any of its ten
/// <c>DropItem-0</c>..<c>DropItem-9</c> slots; the paired
/// <c>DropItem%-N</c> gives the drop chance (0 = unknown/always). Spawn
/// sites come from the monster's <c>Summoned By</c> field, which lists the
/// rooms the monster (including its lair group) populates — a single
/// authoritative source that covers both fixed placements and lair spawns,
/// so the room graph's <see cref="Room.RawLairTag"/> need not be parsed
/// here. All <c>(map)/(room)</c> tokens are regex-extracted (the field
/// carries occasional malformed tokens — a truncated <c>oup:</c>, a
/// map-less <c>Group: 102</c> — which a digits/digits match skips
/// naturally).
/// </para>
/// <para>
/// Only monsters that drop at least one item have their spawn rooms
/// indexed — the reroute never asks about a non-dropper, so parsing every
/// monster's (often hundreds-long) <c>Summoned By</c> would be wasted work.
/// </para>
/// <para>
/// Distinct from <see cref="MonsterSpawnIndex"/> (which maps the inverse —
/// <c>RoomKey → monsters summoned there</c> — for room tooltips): this
/// index is item-first and drop-scoped. Mirrors <see cref="ShopStockIndex"/>:
/// subscribes to <see cref="GameDataCache.ActiveSetChanged"/>, reads the
/// raw table once, builds the maps, and evicts the <see cref="JsonDocument"/>.
/// </para>
/// </remarks>
public sealed class MonsterDropIndex
{
    /// <summary>One monster that drops an item, with its drop chance.</summary>
    /// <param name="MonsterId">The monster's <c>Number</c>.</param>
    /// <param name="MonsterName">The monster's display name (for the reroute prompt).</param>
    /// <param name="DropPercent">The paired <c>DropItem%</c>, or 0 when unset.</param>
    public readonly record struct MonsterDrop(int MonsterId, string MonsterName, int DropPercent);

    private const int DropSlots = 10;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, List<MonsterDrop>> _droppersByItem = new();
    private readonly Dictionary<int, List<RoomKey>> _spawnRoomsByMonster = new();

    private static readonly Regex s_roomToken = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    /// <summary>Set the index was last built from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>Number of distinct dropped item ids in the active set.</summary>
    public int ItemCount => _droppersByItem.Count;

    /// <summary>Fires after every successful (re)load, including the transition to no-set-active.</summary>
    public event Action? StoreReloaded;

    public MonsterDropIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Monsters that drop <paramref name="itemId"/>, or an empty list when
    /// nothing in the active set drops it. The returned list is a live view
    /// of the index — callers read it, never mutate it.
    /// </summary>
    public IReadOnlyList<MonsterDrop> DroppersOf(int itemId)
        => _droppersByItem.TryGetValue(itemId, out List<MonsterDrop>? drops)
            ? drops
            : Array.Empty<MonsterDrop>();

    /// <summary>
    /// Rooms where the monster with <paramref name="monsterId"/> spawns
    /// (from its <c>Summoned By</c> field), or an empty list when the
    /// monster isn't a known dropper. Live view — read, don't mutate.
    /// </summary>
    public IReadOnlyList<RoomKey> SpawnRoomsOf(int monsterId)
        => _spawnRoomsByMonster.TryGetValue(monsterId, out List<RoomKey>? rooms)
            ? rooms
            : Array.Empty<RoomKey>();

    /// <summary>True when at least one monster in the active set drops the item.</summary>
    public bool AnyMonsterDrops(int itemId) => _droppersByItem.ContainsKey(itemId);

    /// <summary>
    /// Reload the index from <paramref name="setName"/>'s
    /// <c>Monsters.json</c>. Pass <c>null</c> to clear. Wired by
    /// <see cref="AppServices"/> to <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        _droppersByItem.Clear();
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
