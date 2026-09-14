namespace MudPlay.Game.Map;

// Rooms holding a level gate this character can't pass — the room you can walk
// into and stand in while the way onward is shut to you.
//
// This is the other half of LevelBlockedRooms. That one answers "what can't I
// reach", so a gate room never appears in it: you can reach a gate room by
// definition, it's the far side that's sealed. Asking where the gate IS is a
// separate question, and a cheaper one — no reachability, just a scan.
//
// Level is the only thing consulted. Doors, locks, keys, stat requirements and
// class / alignment windows are deliberately out of scope: this is the level
// filter, and folding other gating in would make it answer a question nobody
// asked it. A room shut by a 1000-picklock door is not level-blocked.
public static class LevelGatedRooms
{
    // Empty when the level is unknown — an unknown level never gates, matching
    // MovementFilter's rule of not refusing on what it can't evaluate.
    public static IReadOnlySet<RoomKey> Compute(RoomGraphManager graph, int level)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (level <= 0) return EmptySet;

        HashSet<RoomKey> gated = [];

        foreach (Room room in graph.Rooms)
        {
            foreach (RoomExit exit in room.Exits.Values)
            {
                if (!exit.ExcludesLevel(level)) continue;
                gated.Add(room.Key);
                break;
            }
        }

        // A dock's sailings carry the same kind of level floor an exit does, but
        // a BoatPassage is not a RoomExit — several sailings share one dock and
        // the single Teleport slot holds only one — so the scan above can't see
        // them and they need their own pass.
        foreach (BoatPassage passage in graph.AllBoatPassages)
        {
            if (passage.MinLevel > 0 && level < passage.MinLevel) gated.Add(passage.DockRoom);
        }

        return gated;
    }

    private static readonly IReadOnlySet<RoomKey> EmptySet = new HashSet<RoomKey>();
}
