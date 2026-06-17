using System;
using System.Collections.Generic;
using System.Globalization;

namespace FujinTerm.Game.GameData;

/// <summary>
/// Parses a Shops row's <c>Assigned To</c> free-text room list (e.g.
/// <c>"Room 1/297, Room 6/1334"</c>) into <c>(map, room)</c> pairs. Mirrors
/// MMUD Explorer's <c>GetShopRoomNames</c> shape: split on <c>','</c> and read
/// each <c>"Room M/R"</c> token. Shared by the Game Data Browser's Shops tab and
/// the auto-train trainer catalogue so the parse lives in one place.
/// </summary>
public static class ShopRoomParser
{
    /// <summary>
    /// Parse EVERY <c>Room map/room</c> token of <paramref name="assignedTo"/>,
    /// in order. Returns an empty list when the field is empty, unassigned
    /// (<c>\x00</c>), or holds no parseable <c>"Room M/R"</c> tokens. A shop can
    /// be assigned to more than one room (e.g. the universal Training Room sits in
    /// both Silvermere and Newhaven), so each token becomes its own entry.
    /// </summary>
    public static IReadOnlyList<(int Map, int Room)> ParseRooms(string? assignedTo)
    {
        var rooms = new List<(int Map, int Room)>();
        if (string.IsNullOrEmpty(assignedTo)) return rooms;

        foreach (string raw in assignedTo.Split(','))
        {
            string token = raw.Trim();
            if (!token.StartsWith("Room ", StringComparison.Ordinal)) continue;

            string remainder = token[5..].Trim();
            int slash = remainder.IndexOf('/');
            if (slash <= 0) continue;
            if (int.TryParse(remainder[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
                && int.TryParse(remainder[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
            {
                rooms.Add((m, r));
            }
        }
        return rooms;
    }

    /// <summary>
    /// Parse the FIRST <c>Room map/room</c> token of <paramref name="assignedTo"/>.
    /// Returns false (with map/room 0) when the field is empty, unassigned
    /// (<c>\x00</c>), or doesn't start with <c>"Room "</c>.
    /// </summary>
    public static bool TryParseFirstRoom(string? assignedTo, out int map, out int room)
    {
        IReadOnlyList<(int Map, int Room)> rooms = ParseRooms(assignedTo);
        if (rooms.Count == 0)
        {
            map = 0;
            room = 0;
            return false;
        }
        (map, room) = rooms[0];
        return true;
    }
}
