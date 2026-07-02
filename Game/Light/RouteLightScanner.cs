using System;
using System.Collections.Generic;
using FujinTerm.Game.Map;

namespace FujinTerm.Game.Light;

/// <summary>
/// Scans a planned route for its darkest room and the light strength needed to
/// see it. The route is any ordered sequence of <see cref="RoomKey"/> (a
/// walk-to path or a <see cref="LoopExpander.ResolveCycleRoomKeys"/> cycle);
/// resolution to concrete rooms is left to a caller-supplied delegate
/// (production passes <c>RoomGraphManager.GetRoom</c>) so the scanner stays
/// decoupled from where the route came from.
/// </summary>
public static class RouteLightScanner
{
    /// <summary>
    /// Find the darkest room on <paramref name="route"/> and the minimum
    /// light-source <c>Strength</c> to ready so it clears the see threshold.
    /// Keys that don't resolve to a room are skipped (a route may cross rooms
    /// outside the active set). Pass the player's worn <c>+illu</c> as
    /// <paramref name="charIllu"/> — the returned
    /// <see cref="RouteLightScan.NeededLightStrength"/> is then the strength a
    /// provisioning light must project on top of that worn baseline.
    /// </summary>
    public static RouteLightScan Scan(
        IEnumerable<RoomKey> route, Func<RoomKey, Room?> resolve, int charIllu)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(resolve);

        int count = 0;
        int darkest = int.MaxValue;
        RoomKey? where = null;
        foreach (RoomKey key in route)
        {
            if (resolve(key) is not { } room) continue;
            count++;
            if (room.Light < darkest)
            {
                darkest = room.Light;
                where = key;
            }
        }

        if (count == 0) return RouteLightScan.Empty;
        int needed = LightModel.IlluGapToSee(charIllu, darkest);
        return new RouteLightScan(count, darkest, where, needed);
    }
}
