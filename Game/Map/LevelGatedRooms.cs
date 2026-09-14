namespace MudPlay.Game.Map;

// Rooms holding a level gate this character can't pass — the room you can walk
// into and stand in while the way onward is shut to you.
//
// A scan, not a reachability sweep: it asks each room whether any way onward is
// shut to us, which is a question about the room itself rather than about how we
// got there. So the answer depends on the level and nothing else — the same
// wherever the character is standing, and computable with no position at all.
//
// Level is the only thing consulted. Doors, locks, keys, stat requirements and
// class / alignment windows are deliberately out of scope: folding other gating
// in would make a level filter answer a question nobody asked it. A room shut
// by a door nobody can pick is not gated by level and is not marked here.
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
                if (!ExcludesLevel(in exit, level)) continue;
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

    // True when this exit's level window refuses us. A zero bound means no bound
    // on that side — the MDB's 0 and 999 no-cap sentinels are normalised away at
    // parse time, so a gate only ever tests the bounds it actually has. Compute
    // has already rejected an unknown (zero) level before this runs.
    private static bool ExcludesLevel(in RoomExit exit, int level)
    {
        if (!exit.HasLevelGate) return false;
        if (exit.MinLevel > 0 && level < exit.MinLevel) return true;
        return exit.MaxLevel > 0 && level > exit.MaxLevel;
    }

    private static readonly IReadOnlySet<RoomKey> EmptySet = new HashSet<RoomKey>();
}
