using System;
using System.Collections.Generic;
using FujinTerm.Game.Map;

namespace FujinTerm.Game.Light;

// Scans a planned route for its darkest room and the light strength needed to see
// it. The route is any ordered sequence of RoomKey (a walk-to path or a loop
// cycle); resolution to concrete rooms is left to a caller-supplied delegate
// (production passes RoomGraphManager.GetRoom) so the scanner stays decoupled from
// where the route came from.
public static class RouteLightScanner
{
    // Find the darkest room on the route and the minimum light-source Strength to
    // ready so it clears the see threshold. Keys that don't resolve to a room are
    // skipped (a route may cross rooms outside the active set). Pass the player's
    // worn +illu as charIllu — the returned NeededLightStrength is then the
    // strength a provisioning light must project on top of that worn baseline.
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
