using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

// One training-shop room discovered in the active set's Shops table. Number is
// Shops.Number; Name is the shop name (usually the guild / role label, e.g.
// "Training Room"); Map / Room come from Assigned To (0 when unresolved);
// RoomName is the host room's display name from Rooms (empty when unresolved);
// MinLevel / MaxLevel are MinLVL / MaxLVL; ClassRest is the class restriction
// (0 = universal, like the Training Room); Markup is the Shops.Markup% that
// scales the per-level training fee.
public readonly record struct TrainerShop(
    int Number, string Name, int Map, int Room, string RoomName, int MinLevel, int MaxLevel, int ClassRest, int Markup)
{
    // True when a host room resolved from the shop's Assigned To.
    public bool HasRoom => Map > 0 && Room > 0;

    // Stable per-row identity (shop + host room) for the allow/disallow set. A
    // multi-room shop yields one row per room, so the disabled set keys on the
    // physical trainer location, not just the shop number — enabling Newhaven's
    // Training Room while disabling Silvermere's works because they're distinct.
    public string RowKey => string.Create(CultureInfo.InvariantCulture, $"{Number}/{Map}/{Room}");

    // True when this trainer serves a character at level, using MajorMUD's exact
    // gate: served when !(MinLVL > level+1 || MaxLVL <= level).
    public bool ServesLevel(int level) => !(MinLevel > level + 1 || MaxLevel <= level);

    // True when this trainer serves classNumber: the universal Training Room
    // (ClassRest == 0) serves every class; a guild trainer serves only its own
    // class number.
    public bool ServesClass(int classNumber) => ClassRest == 0 || ClassRest == classNumber;
}

// Enumerates the training shops (ShopType == 8) in the active game-data set,
// resolving each one's host room(s) from Assigned To. A shop assigned to several
// rooms yields one TrainerShop per room (so the universal Training Room appears
// once for Silvermere and once for Newhaven). Trainers whose MaxLVL is the 999
// sentinel (e.g. the unreachable "Sysop Trainer", shop 39) are skipped, as are
// shops with no parseable room (nothing to route to).
//
// Drives the Settings → Auto-Trainer table (the discovered-trainers list) and the
// navigation engine's "which trainer for this level/class" resolution. Level
// ranges + rooms come straight from the data, so the in-game town progression
// (Newhaven → Silvermere → Aldreth → Aged Titan …) falls out without any
// hardcoding.
public static class TrainerCatalog
{
    // Shops.ShopType value for a training shop.
    public const int TrainingShopType = 8;

    // MaxLVL sentinel marking a non-reachable / placeholder trainer to ignore.
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
            int markup = GetInt(el, "Markup%");

            foreach ((int map, int room) in ShopRoomParser.ParseRooms(GetString(el, "Assigned To")))
            {
                string roomName = roomNames.TryGetValue((map, room), out string? rn) ? rn : string.Empty;
                trainers.Add(new TrainerShop(number, name, map, room, roomName, minLevel, maxLevel, classRest, markup));
            }
        }
        return trainers;
    }

    // Pick the nearest trainer that can serve the character: serves level, is
    // universal or matches classNumber, isn't in disabled (keyed by
    // TrainerShop.RowKey), has a resolvable room, and is reachable per distance
    // (which returns the path length to a trainer's room, or null when
    // unreachable). Returns null when nothing qualifies — e.g. a quest-gated level
    // with no matching trainer. Pure: the caller supplies the distance metric (BFS).
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

    // Cheapest markup among trainers that can teach a character of classNumber up
    // to targetLevel (MinLVL <= targetLevel <= MaxLVL, class-ok). Training cost
    // rises monotonically with markup for a fixed level, so the lowest-markup
    // trainer is always the cheapest — the caller feeds this into
    // ShopPriceCalculator.TrainCopper. Ignores room reachability (cost is
    // location-independent) and the auto-trainer disabled set (this is a price
    // projection, not a walk target). Null when no trainer serves that
    // level/class — e.g. a quest-gated level or a class with no super trainer.
    public static int? CheapestMarkup(IReadOnlyList<TrainerShop> trainers, int targetLevel, int classNumber)
    {
        ArgumentNullException.ThrowIfNull(trainers);

        int? cheapest = null;
        foreach (TrainerShop t in trainers)
        {
            if (!t.ServesLevel(targetLevel - 1)) continue;
            if (!t.ServesClass(classNumber)) continue;
            if (cheapest is null || t.Markup < cheapest) cheapest = t.Markup;
        }
        return cheapest;
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
