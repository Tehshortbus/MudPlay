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
/// layouts — N then S doesn't always return to the same coord). Those
/// rooms land in <see cref="OffGrid"/>; the map UI renders them in a
/// secondary lane with a connector back to whichever already-placed
/// room they exit from. The off-grid set is empty for grid-clean
/// neighbourhoods.
/// </para>
/// </remarks>
public sealed record RoomLayout(
    RoomKey Origin,
    IReadOnlyDictionary<RoomKey, (int X, int Y)> Positions,
    IReadOnlyDictionary<RoomKey, VerticalHint> VerticalHints,
    IReadOnlyList<RoomKey> OffGrid,
    IReadOnlyDictionary<(int X, int Y), RoomKey> CoordToRoom,
    IReadOnlyDictionary<(int X, int Y), IReadOnlySet<Direction>> EdgesFromCoord,
    IReadOnlyDictionary<(int X, int Y), IReadOnlySet<Direction>> TrapEdgesFromCoord)
{
    /// <summary>
    /// The room BFS structurally expanded from to produce this layout —
    /// the "seed" that fully determines the drawn shape (two layouts with
    /// the same root render identically, just translated). Equal to
    /// <see cref="Origin"/> in the common case, but the score-and-retry
    /// pass can re-root the layout at a more central room when the
    /// requested origin produces a stub-heavy draw; the layout is then
    /// translated so <see cref="Origin"/> still sits at (0,0) while
    /// <see cref="LayoutRoot"/> records which room actually anchored the
    /// BFS. Surfaced in the Navigation map header so a sparse vs. fuller
    /// draw can be reported by seed.
    /// </summary>
    public RoomKey LayoutRoot { get; init; }

    /// <summary>
    /// Count of placed exit edges that don't reach their declared target
    /// at the expected planar offset (the non-Euclidean "stub" connectors).
    /// A coverage-quality signal: 0 means a perfectly flat layout; a high
    /// count means BFS bent connectors to fit a maze region. Surfaced
    /// alongside <see cref="LayoutRoot"/> in the map header.
    /// </summary>
    public int StubCount { get; init; }
}

/// <summary>Whether a room exposes an up/down exit that the planar layout drops.</summary>
[Flags]
public enum VerticalHint
{
    None = 0,
    Up   = 1,
    Down = 2,
    Both = Up | Down,
}
