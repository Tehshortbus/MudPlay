namespace FujinTerm.Game.Map;

/// <summary>
/// Cardinal exit direction off a room. Values match the order of the
/// matching property names on a MajorMUD <c>Rooms</c> row
/// (<c>N</c>/<c>S</c>/<c>E</c>/<c>W</c>/<c>NE</c>/<c>NW</c>/<c>SE</c>/<c>SW</c>/<c>U</c>/<c>D</c>)
/// so a single <see cref="ExitMask"/> bit-field can be built from a
/// direction set with <c>1u &lt;&lt; (int)dir</c>.
/// </summary>
/// <remarks>
/// BFS layout in <see cref="BfsMapper"/> is planar — <see cref="U"/>
/// and <see cref="D"/> are rendered as glyphs on the room cell instead
/// of contributing to the 2D layout pass.
/// </remarks>
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
