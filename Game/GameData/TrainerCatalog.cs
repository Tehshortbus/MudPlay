using System;
using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

/// <summary>One training shop discovered in the active set's Shops table.</summary>
/// <param name="Number">Shops.Number.</param>
/// <param name="Name">Shop name (usually the town / guild label).</param>
/// <param name="Map">Host map, from <c>Assigned To</c> (0 when unresolved).</param>
/// <param name="Room">Host room, from <c>Assigned To</c> (0 when unresolved).</param>
/// <param name="MinLevel">Lowest level the trainer serves (<c>MinLVL</c>).</param>
/// <param name="MaxLevel">Exclusive upper level (<c>MaxLVL</c>).</param>
/// <param name="ClassRest">Class restriction (0 = universal, like Silvermere).</param>
public readonly record struct TrainerShop(
    int Number, string Name, int Map, int Room, int MinLevel, int MaxLevel, int ClassRest)
{
    /// <summary>True when a host room resolved from the shop's <c>Assigned To</c>.</summary>
    public bool HasRoom => Map > 0 && Room > 0;

    /// <summary>
    /// True when this trainer serves a character at <paramref name="level"/>,
    /// using MMUD's exact gate (<c>frmMain.frm</c>): served when
    /// <c>!(MinLVL &gt; level+1 || MaxLVL &lt;= level)</c>.
    /// </summary>
    public bool ServesLevel(int level) => !(MinLevel > level + 1 || MaxLevel <= level);
}

/// <summary>
/// Enumerates the training shops (<c>ShopType == 8</c>) in the active game-data
/// set, resolving each one's host room from <c>Assigned To</c>. Trainers whose
/// <c>MaxLVL</c> is the 999 sentinel (e.g. the unreachable "Sysop Trainer",
/// shop 39) are skipped — they're not real, player-reachable trainers.
/// </summary>
/// <remarks>
/// Drives the Settings → Auto-Trainer table (the discovered-trainers list) and,
/// once the navigation engine lands, the "which trainer for this level/class"
/// resolution. Level ranges + rooms come straight from the data, so the
/// in-game town progression (Newhaven → Silvermere → Aldreth → Aged Titan …)
/// falls out without any hardcoding.
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

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (GetInt(el, "ShopType") != TrainingShopType) continue;
            int maxLevel = GetInt(el, "MaxLVL");
            if (maxLevel == IgnoredMaxLevel) continue;

            ShopRoomParser.TryParseFirstRoom(GetString(el, "Assigned To"), out int map, out int room);
            trainers.Add(new TrainerShop(
                GetInt(el, "Number"),
                GetString(el, "Name"),
                map, room,
                GetInt(el, "MinLVL"),
                maxLevel,
                GetInt(el, "ClassRest")));
        }
        return trainers;
    }

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
