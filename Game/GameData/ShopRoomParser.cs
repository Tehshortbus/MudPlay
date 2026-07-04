using System;
using System.Collections.Generic;
using System.Globalization;

namespace FujinTerm.Game.GameData;

// Parses a Shops row's `Assigned To` free-text room list (e.g. "Room 1/297, Room
// 6/1334") into (map, room) pairs: split on ',' and read each "Room M/R" token.
// Shared by the Game Data Browser's Shops tab and the auto-train trainer catalogue
// so the parse lives in one place.
public static class ShopRoomParser
{
    // Parse EVERY `Room map/room` token of assignedTo, in order. Returns an empty
    // list when the field is empty, unassigned (\x00), or holds no parseable "Room
    // M/R" tokens. A shop can be assigned to more than one room (e.g. the universal
    // Training Room sits in both Silvermere and Newhaven), so each token becomes its
    // own entry.
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

    // Parse the FIRST `Room map/room` token of assignedTo. Returns false (with
    // map/room 0) when the field is empty, unassigned (\x00), or doesn't start with
    // "Room ".
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
