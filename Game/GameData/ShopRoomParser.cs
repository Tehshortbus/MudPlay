using System;
using System.Globalization;

namespace FujinTerm.Game.GameData;

/// <summary>
/// Parses a Shops row's <c>Assigned To</c> free-text room list (e.g.
/// <c>"Room 1/297, Room 6/1334"</c>) into a <c>(map, room)</c> pair. Mirrors
/// MMUD Explorer's <c>GetShopRoomNames</c> shape: split on <c>','</c> and take
/// the first <c>"Room M/R"</c> token. Shared by the Game Data Browser's Shops
/// tab and the auto-train trainer catalogue so the parse lives in one place.
/// </summary>
public static class ShopRoomParser
{
    /// <summary>
    /// Parse the FIRST <c>Room map/room</c> token of <paramref name="assignedTo"/>.
    /// Returns false (with map/room 0) when the field is empty, unassigned
    /// (<c>\x00</c>), or doesn't start with <c>"Room "</c>.
    /// </summary>
    public static bool TryParseFirstRoom(string? assignedTo, out int map, out int room)
    {
        map = 0;
        room = 0;
        if (string.IsNullOrEmpty(assignedTo)) return false;

        string firstToken = assignedTo.Split(',', 2)[0].Trim();
        if (!firstToken.StartsWith("Room ", StringComparison.Ordinal)) return false;

        string remainder = firstToken[5..].Trim();
        int slash = remainder.IndexOf('/');
        if (slash <= 0) return false;
        if (int.TryParse(remainder[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
            && int.TryParse(remainder[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
        {
            map = m;
            room = r;
            return true;
        }
        return false;   // partial parse — leave map/room at 0
    }
}
