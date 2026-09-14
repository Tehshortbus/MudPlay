namespace MudPlay.Game.Map;

// Rooms holding a level gate — a room you can walk into and stand in whose way
// onward is shut to anyone outside the gate's level window.
//
// This is a property of the MAP, not of the character: it marks where the gates
// are, not which ones happen to refuse you today. So it takes no level, needs no
// position, and reads the same whether you're level 5, level 99, or not
// connected at all. That also means it's correct while browsing game data
// offline, which is when "where are the gates" is most often the question.
//
// Level is the only thing consulted. Doors, locks, keys, stat requirements and
// class / alignment windows are deliberately out of scope: folding other gating
// in would make a level filter answer a question nobody asked it. A room shut
// by a door nobody can pick is not gated by level and is not marked here.
public static class LevelGatedRooms
{
    public static IReadOnlySet<RoomKey> Compute(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        HashSet<RoomKey> gated = [];

        foreach (Room room in graph.Rooms)
        {
            foreach (RoomExit exit in room.Exits.Values)
            {
                // Covers synthesised Teleport edges too — a CMD teleport's
                // `minlevel` lands on MinLevel at graph-build time, so a vortex
                // that won't take you until level 20 marks its room like any
                // other gated exit.
                if (!exit.HasLevelGate) continue;
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
            if (passage.MinLevel > 0) gated.Add(passage.DockRoom);
        }

        return gated;
    }
}
