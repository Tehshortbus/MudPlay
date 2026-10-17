using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One selectable room in the boss-room picker: the room key, its display label
// ("1/2122 — Dragon's Lair — 6 steps" / "… — no route"), and the hop count used
// to order the list (null = unreachable under the current avoids).
public sealed record BossRoomOption(RoomKey Key, string Display, int? Distance);

// Builds the nearest→furthest room list the picker shows for a multi-room boss.
// Pure: the caller supplies the distance and name lookups. Reachable rooms come first (ordered by hop count), then any
// unreachable ones (ordered by map/room) so a boss whose rooms you can't currently
// path to still lists them rather than vanishing.
public static class BossGotoRooms
{
    public static IReadOnlyList<BossRoomOption> BuildOptions(
        IEnumerable<RoomKey> rooms,
        Func<RoomKey, int?> distance,
        Func<RoomKey, string?> roomName)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(distance);
        ArgumentNullException.ThrowIfNull(roomName);

        return rooms
            .Distinct()
            .Select(key =>
            {
                int? dist = distance(key);
                string? name = roomName(key);
                string label = key.ToString();
                if (!string.IsNullOrWhiteSpace(name)) label += $" — {name}";
                label += dist is { } d
                    ? $" — {(d == 1 ? "1 step" : $"{d} steps")}"
                    : " — no route";
                return new BossRoomOption(key, label, dist);
            })
            // Reachable first (Distance != null), then by hop count; unreachable
            // rooms fall to the bottom, ordered by map then room for stability.
            .OrderBy(o => o.Distance is null)
            .ThenBy(o => o.Distance ?? int.MaxValue)
            .ThenBy(o => o.Key.Map)
            .ThenBy(o => o.Key.Room)
            .ToList();
    }
}

// The picker's outcome: the room the user chose plus whether to start walking now
// (Run) or only arm the destination (Load, the GOTO arm-without-start path). The
// dialog returns null on Cancel.
public sealed record BossRoomPick(RoomKey Room, bool StartNow);
