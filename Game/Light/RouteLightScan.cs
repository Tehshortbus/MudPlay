using FujinTerm.Game.Map;

namespace FujinTerm.Game.Light;

/// <summary>
/// The darkness profile of a planned route (a walk-to path or a farming loop):
/// the darkest room on it and the minimum light-source <c>Strength</c> the
/// player must ready to see the whole way at a given baseline illumination.
/// Produced by <see cref="RouteLightScanner.Scan"/>.
/// </summary>
/// <param name="RoomCount">Rooms on the route that resolved to a graph room.</param>
/// <param name="DarkestRoomLight">The most-negative <see cref="Room.Light"/> on
/// the route (<c>0</c> when nothing is dark, or the route is empty).</param>
/// <param name="DarkestRoom">Where <see cref="DarkestRoomLight"/> occurs, or
/// <c>null</c> for an empty route.</param>
/// <param name="NeededLightStrength">Minimum light-source <c>Strength</c> to
/// ready so the darkest room clears the see threshold at the scan's baseline
/// illumination; <c>0</c> when the route is already visible.</param>
public readonly record struct RouteLightScan(
    int RoomCount,
    int DarkestRoomLight,
    RoomKey? DarkestRoom,
    int NeededLightStrength)
{
    /// <summary>An empty scan — no rooms, no darkness, no light needed.</summary>
    public static RouteLightScan Empty => new(0, 0, null, 0);

    /// <summary><c>true</c> when the route has a room the player can't see at the
    /// scan's baseline illumination, so a light must be readied to cover it.</summary>
    public bool NeedsLight => NeededLightStrength > 0;
}
