using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

/// <summary>One training-shop room discovered in the active set's Shops table.</summary>
/// <param name="Number">Shops.Number.</param>
/// <param name="Name">Shop name (usually the guild / role label, e.g. "Training Room").</param>
/// <param name="Map">Host map, from <c>Assigned To</c> (0 when unresolved).</param>
/// <param name="Room">Host room, from <c>Assigned To</c> (0 when unresolved).</param>
/// <param name="RoomName">Host room's display name (from Rooms), empty when unresolved.</param>
/// <param name="MinLevel">Lowest level the trainer serves (<c>MinLVL</c>).</param>
/// <param name="MaxLevel">Exclusive upper level (<c>MaxLVL</c>).</param>
/// <param name="ClassRest">Class restriction (0 = universal, like the Training Room).</param>
public readonly record struct TrainerShop(
    int Number, string Name, int Map, int Room, string RoomName, int MinLevel, int MaxLevel, int ClassRest)
{
    /// <summary>True when a host room resolved from the shop's <c>Assigned To</c>.</summary>
    public bool HasRoom => Map > 0 && Room > 0;

    /// <summary>
    /// Stable per-row identity (shop + host room) for the allow/disallow set. A
    /// multi-room shop yields one row per room, so the disabled set keys on the
    /// physical trainer location, not just the shop number — enabling Newhaven's
    /// Training Room while disabling Silvermere's works because they're distinct.
    /// </summary>
    public string RowKey => string.Create(CultureInfo.InvariantCulture, $"{Number}/{Map}/{Room}");

    /// <summary>
    /// True when this trainer serves a character at <paramref name="level"/>,
    /// using MMUD's exact gate (<c>frmMain.frm</c>): served when
    /// <c>!(MinLVL &gt; level+1 || MaxLVL &lt;= level)</c>.
    /// </summary>
    public bool ServesLevel(int level) => !(MinLevel > level + 1 || MaxLevel <= level);

    /// <summary>
    /// True when this trainer serves <paramref name="classNumber"/>: the universal
    /// Training Room (<c>ClassRest == 0</c>) serves every class; a guild trainer
    /// serves only its own class number.
    /// </summary>
    public bool ServesClass(int classNumber) => ClassRest == 0 || ClassRest == classNumber;
}

/// <summary>
/// Enumerates the training shops (<c>ShopType == 8</c>) in the active game-data
/// set, resolving each one's host room(s) from <c>Assigned To</c>. A shop
/// assigned to several rooms yields one <see cref="TrainerShop"/> per room (so the
/// universal Training Room appears once for Silvermere and once for Newhaven).
/// Trainers whose <c>MaxLVL</c> is the 999 sentinel (e.g. the unreachable "Sysop
/// Trainer", shop 39) are skipped, as are shops with no parseable room (nothing to
/// route to).
/// </summary>
/// <remarks>
/// Drives the Settings → Auto-Trainer table (the discovered-trainers list) and the
/// navigation engine's "which trainer for this level/class" resolution. Level
/// ranges + rooms come straight from the data, so the in-game town progression
/// (Newhaven → Silvermere → Aldreth → Aged Titan …) falls out without any
/// hardcoding.
/// </remarks>
public static class TrainerCatalog
{
    /// <summary><c>Shops.ShopType</c> value for a training shop.</summary>
    public const int TrainingShopType = 8;

    /// <summary><c>MaxLVL</c> sentinel marking a non-reachable / placeholder trainer to ignore.</summary>
    public const int IgnoredMaxLevel = 999;

    public static IReadOnlyList<TrainerShop> Enumerate(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        var trainers = new List<TrainerShop>();

        JsonDocument? doc = gameData.GetRawTable("Shops");
        if (doc is null) return trainers;

        Dictionary<(int, int), string> roomNames = BuildRoomNameIndex(gameData);

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (GetInt(el, "ShopType") != TrainingShopType) continue;
            int maxLevel = GetInt(el, "MaxLVL");
            if (maxLevel == IgnoredMaxLevel) continue;

            int number = GetInt(el, "Number");
            string name = GetString(el, "Name");
            int minLevel = GetInt(el, "MinLVL");
            int classRest = GetInt(el, "ClassRest");

            foreach ((int map, int room) in ShopRoomParser.ParseRooms(GetString(el, "Assigned To")))
            {
                string roomName = roomNames.TryGetValue((map, room), out string? rn) ? rn : string.Empty;
                trainers.Add(new TrainerShop(number, name, map, room, roomName, minLevel, maxLevel, classRest));
            }
        }
        return trainers;
    }

    /// <summary>
    /// Pick the nearest trainer that can serve the character: serves
    /// <paramref name="level"/>, is universal or matches
    /// <paramref name="classNumber"/>, isn't in <paramref name="disabled"/> (keyed
    /// by <see cref="TrainerShop.RowKey"/>), has a resolvable room, and is
    /// reachable per <paramref name="distance"/> (which returns the path length to a
    /// trainer's room, or null when unreachable). Returns null when nothing
    /// qualifies — e.g. a quest-gated level with no matching trainer. Pure: the
    /// caller supplies the distance metric (BFS).
    /// </summary>
    public static TrainerShop? SelectNearest(
        IReadOnlyList<TrainerShop> trainers, int level, int classNumber,
        IReadOnlyCollection<string> disabled, Func<TrainerShop, int?> distance)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);

        TrainerShop? best = null;
        int bestDist = int.MaxValue;
        foreach (TrainerShop t in trainers)
        {
            if (!t.HasRoom) continue;
            if (!t.ServesLevel(level)) continue;
            if (!t.ServesClass(classNumber)) continue;
            if (disabled.Contains(t.RowKey)) continue;
            if (distance(t) is { } dist && dist < bestDist)
            {
                best = t;
                bestDist = dist;
            }
        }
        return best;
    }

    // Build a (map, room) → room-name index from the active set's Rooms table.
    // Used only for the display label; an absent/odd Rooms table just yields
    // empty names (the row still lists by shop name + coords).
    private static Dictionary<(int, int), string> BuildRoomNameIndex(GameDataCache gameData)
    {
        var index = new Dictionary<(int, int), string>();
        JsonDocument? rooms = gameData.GetRawTable("Rooms");
        if (rooms is null) return index;

        foreach (JsonElement el in rooms.RootElement.EnumerateArray())
        {
            int map = GetInt(el, "Map Number");
            int room = GetInt(el, "Room Number");
            if (map <= 0 || room <= 0) continue;
            index[(map, room)] = GetString(el, "Name");
        }
        return index;
    }

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
