using System.Collections.Generic;

namespace FujinTerm.Game.Map;

/// <summary>
/// Output of <see cref="BfsMapper.BuildLayout"/>. Maps room keys to
/// their planar (X, Y) coordinates relative to the origin (the origin
/// sits at (0, 0)).
/// </summary>
/// <remarks>
/// <para>
/// U/D exits are not represented in the 2D plane — they contribute
/// only to <see cref="VerticalHints"/>, which the Navigation map
/// surfaces via glyphs on the room cell (per the doc and the
/// <c>docs/skeleton/nav-map.jsx</c> reference).
/// </para>
/// <para>
/// Some rooms can't be placed on the 2D plane without colliding with
/// an earlier-visited neighbour (MajorMUD allows non-Euclidean room
/// layouts — N then S doesn't always return to the same coord). Rather
/// than drop those rooms, <see cref="BfsMapper.BuildLayout"/>'s final
/// pass force-seats each at the nearest free cell; the renderer draws a
/// dashed bridge (or, when too far, a stub) along the real connection.
/// They live in <see cref="Positions"/> / <see cref="CoordToRoom"/> like
/// any other room. <see cref="OffGrid"/> is retained for shape but stays
/// empty — no room is dropped to a separate lane any more.
/// </para>
/// </remarks>
public sealed record RoomLayout(
    RoomKey Origin,
    IReadOnlyDictionary<RoomKey, (int X, int Y)> Positions,
    IReadOnlyDictionary<RoomKey, VerticalHint> VerticalHints,
    IReadOnlyList<RoomKey> OffGrid,
    IReadOnlyDictionary<(int X, int Y), RoomKey> CoordToRoom,
    IReadOnlyDictionary<(int X, int Y), IReadOnlySet<Direction>> EdgesFromCoord,
    IReadOnlyDictionary<(int X, int Y), IReadOnlySet<Direction>> TrapEdgesFromCoord);

/// <summary>Whether a room exposes an up/down exit that the planar layout drops.</summary>
[Flags]
public enum VerticalHint
{
    None = 0,
    Up   = 1,
    Down = 2,
    Both = Up | Down,
}
