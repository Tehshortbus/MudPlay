namespace MudPlay.Game.Map;

// Rooms a route may start or end in but never cross on its way somewhere else: the
// Island of Bones and the bone caverns behind it. They are reached through the
// library and left by the dark mage's portal back into Arlysia, past the isle's
// teleporting rooms and its deadliest spawns, a few rooms from where the trip began.
// A walk aimed at them (the midnight dragon) still goes, and one standing in them
// is still planned out; a shortcut through them never is (the user, 2026-11-02;
// GAME_MECHANICS "Island of Bones: reached through the library, never crossed").
//
// The one exit cast on the walker inside them, west from the stairs up from Crystal
// Lake, never lets anyone onto the isle (the user, 2026-11-02), though its spell
// reads as a teleport only to 49 and under: RoomGraphManager marks it as an exit
// that never lands where it leads.
//
// Told by the room names, which no other room on any set we hold bears.
public static class RoomsNoRouteCrosses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Island of Bones", "Bone Strewn Cavern", "Bone Room", "Bone Cavern", "Isle of Shadow",
    };

    public static bool Holds(Room room) => Names.Contains(room.Name);

    public static IReadOnlySet<RoomKey> Of(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Derived("rooms no route crosses", () =>
            (IReadOnlySet<RoomKey>)graph.Rooms.Where(Holds).Select(r => r.Key).ToHashSet());
    }
}
