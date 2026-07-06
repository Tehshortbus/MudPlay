using System;
using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

// One bank room discovered in the active set's Shops table (ShopType == 7).
// Number is Shops.Number; Name is the bank / shop name; Map / Room come from
// Assigned To; RoomName is the host room's display name from Rooms (empty when
// unresolved). A bank assigned to several rooms (e.g. one bank branch in two
// towns) yields one BankShop per room.
public readonly record struct BankShop(int Number, string Name, int Map, int Room, string RoomName)
{
    public RoomKey Key => new(Map, Room);
}

// Enumerates the bank shops (ShopType == 7) in the active game-data set,
// resolving each one's host room(s) from Assigned To. Mirrors TrainerCatalog
// (ShopType == 8): the ShopType filter + Assigned-To room parse lives in one
// place, shared by the Settings → Cash bank picker (the drop-down source) and the
// auto-deposit destination-validity check. A persisted CashSettings.BankRoomKey
// can go stale — the game-data set changed, or the bank moved between exports —
// leaving a key that no longer names a bank; the deposit engine consults
// IsBankRoom to avoid detouring to a phantom destination.
public static class BankCatalog
{
    // Shops.ShopType value for a bank.
    public const int BankShopType = 7;

    public static IReadOnlyList<BankShop> Enumerate(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        var banks = new List<BankShop>();

        Dictionary<(int, int), string> roomNames = BuildRoomNameIndex(gameData);
        foreach ((int number, string name, int map, int room) in EnumerateRaw(gameData))
        {
            string roomName = roomNames.TryGetValue((map, room), out string? rn) ? rn : string.Empty;
            banks.Add(new BankShop(number, name, map, room, roomName));
        }
        return banks;
    }

    // True when key hosts a bank (ShopType == 7) in the active set. Skips the
    // Rooms name index the display path builds — only the (map, room) match
    // matters here.
    public static bool IsBankRoom(GameDataCache gameData, RoomKey key)
    {
        foreach ((_, _, int map, int room) in EnumerateRaw(gameData))
            if (map == key.Map && room == key.Room) return true;
        return false;
    }

    private static IEnumerable<(int Number, string Name, int Map, int Room)> EnumerateRaw(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        JsonDocument? doc = gameData.GetRawTable("Shops");
        if (doc is null) yield break;

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (GetInt(el, "ShopType") != BankShopType) continue;

            int number = GetInt(el, "Number");
            string name = GetString(el, "Name");
            foreach ((int map, int room) in ShopRoomParser.ParseRooms(GetString(el, "Assigned To")))
                yield return (number, name, map, room);
        }
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
