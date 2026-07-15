namespace FujinTerm.Game.Map;

// Cardinal exit direction off a room. Values match the order of the
// matching property names on a MajorMUD Rooms row
// (N/S/E/W/NE/NW/SE/SW/U/D) so a single ExitMask bit-field can be built
// from a direction set with 1u << (int)dir.
//
// BFS layout in BfsMapper is planar — U and D are rendered as glyphs on
// the room cell instead of contributing to the 2D layout pass.
//
// Teleport is a synthesized pseudo-direction, not a real cardinal off the
// Rooms row: RoomGraphManager mints Teleport exits for single-destination
// CMD teleports (a `go hole` / cast-teleport hop) so BFS can route through
// them. It's non-planar like U/D — it contributes no 2D offset, so a walk
// that crosses it leaves a natural gap on the map instead of drawing a line
// across the hop. Its value sits past D so it never joins the cardinal
// ExitMask fingerprint (built only from N..D).
public enum Direction
{
    N  = 0,
    S  = 1,
    E  = 2,
    W  = 3,
    NE = 4,
    NW = 5,
    SE = 6,
    SW = 7,
    U  = 8,
    D  = 9,
    Teleport = 10,
}
