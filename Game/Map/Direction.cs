namespace FujinTerm.Game.Map;

// Cardinal exit direction off a room. Values match the order of the
// matching property names on a MajorMUD Rooms row
// (N/S/E/W/NE/NW/SE/SW/U/D) so a single ExitMask bit-field can be built
// from a direction set with 1u << (int)dir.
//
// BFS layout in BfsMapper is planar — U and D are rendered as glyphs on
// the room cell instead of contributing to the 2D layout pass.
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
}
