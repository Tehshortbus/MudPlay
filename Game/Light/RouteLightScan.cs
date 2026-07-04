using FujinTerm.Game.Map;

namespace FujinTerm.Game.Light;

// The darkness profile of a planned route (a walk-to path or a farming loop): the
// darkest room on it and the minimum light-source Strength the player must ready to
// see the whole way at a given baseline illumination. Produced by
// RouteLightScanner.Scan.
//
// RoomCount is the rooms on the route that resolved to a graph room.
// DarkestRoomLight is the most-negative Room.Light on the route (0 when nothing is
// dark, or the route is empty). DarkestRoom is where that occurs, or null for an
// empty route. NeededLightStrength is the minimum light-source Strength to ready so
// the darkest room clears the see threshold at the scan's baseline illumination; 0
// when the route is already visible.
public readonly record struct RouteLightScan(
    int RoomCount,
    int DarkestRoomLight,
    RoomKey? DarkestRoom,
    int NeededLightStrength)
{
    // An empty scan — no rooms, no darkness, no light needed.
    public static RouteLightScan Empty => new(0, 0, null, 0);

    // True when the route has a room the player can't see at the scan's baseline
    // illumination, so a light must be readied to cover it.
    public bool NeedsLight => NeededLightStrength > 0;
}
