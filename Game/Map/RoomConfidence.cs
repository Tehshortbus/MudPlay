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

    /// <summary>
    /// Current room is trusted. Walker / loop / auto-lair may use it as
    /// a source. This is the "happy path" state; the tracker writes
    /// <see cref="Models.Profile.CharacterProfile.LastKnownRoom"/> on
    /// every entry so the next session opens at the same spot.
    /// </summary>
    Confirmed = 1,

    /// <summary>
    /// A move was sent and we're waiting on confirmation. The
    /// <see cref="RoomState.CurrentRoom"/> still reflects the
    /// pre-move room until the next observation lands.
    /// </summary>
    Pending = 2,

    /// <summary>
    /// Latest observation didn't line up with what we expected, but the
    /// previous <see cref="RoomState.CurrentRoom"/> is preserved as our
    /// best-guess anchor. A counter on <see cref="RoomState.SuspectStrikes"/>
    /// increments on each subsequent mismatch; at the configured strike
    /// limit the tracker tries replay-from-last-Confirmed recovery and
    /// falls through to <see cref="Lost"/> when replay fails. Suspect
    /// is internal-only — the badge stays green so the UI doesn't churn
    /// on transient observation glitches.
    /// </summary>
    Suspect = 3,

    /// <summary>
    /// Replay-from-last-Confirmed failed and no graph candidate matched.
    /// Room is null. Recovery paths: a future confirming observation
    /// resolves us back to <see cref="Confirmed"/>, or the user clicks
    /// "I am here" on the Navigation map.
    /// </summary>
    Lost = 4,
}
