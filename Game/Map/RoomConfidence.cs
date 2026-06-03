namespace FujinTerm.Game.Map;

/// <summary>
/// How confident the room tracker is in its current room. Drives the
/// status-strip badge color on the Navigation window (green / orange /
/// yellow / red) and gates the walker / loop / auto-lair stacks that
/// require a known source room.
/// </summary>
public enum RoomConfidence
{
    /// <summary>
    /// No room observation has been recorded yet (fresh tracker, or
    /// post-disconnect before the first room display). Indistinguishable
    /// from <see cref="Lost"/> for walker purposes — both require a
    /// manual <c>SetLocated</c> or a fresh confirmed observation before
    /// any source-room-required operation can run.
    /// </summary>
    Unknown = 0,

    /// <summary>Current room is trusted. Walker / loop / auto-lair may use it as a source.</summary>
    Located = 1,

    /// <summary>
    /// A move was sent and we're waiting on confirmation. The
    /// <see cref="RoomState.CurrentRoom"/> still reflects the
    /// pre-move room until the next observation lands.
    /// </summary>
    Pending = 2,

    /// <summary>
    /// Latest observation didn't line up with what we expected. The
    /// tracker is searching for a single matching graph candidate;
    /// when ambiguous it stays here until the user disambiguates
    /// (Tier 3 manual pick or the deferred Tier 2 footprint match).
    /// </summary>
    Reconciling = 3,

    /// <summary>
    /// No candidate could be matched. Manual "I am here" override
    /// (Tier 3) is the only path back to <see cref="Located"/> until
    /// Tier 1 replay + Tier 2 footprint matching ship later in the
    /// phase.
    /// </summary>
    Lost = 4,
}
