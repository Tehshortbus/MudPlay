namespace FujinTerm.Game.Map;

// How confident the room tracker is in its current room. Drives the
// status-strip badge color on the Navigation window (green / orange / yellow /
// red) and gates the walker / loop / auto-lair stacks that require a known
// source room.
public enum RoomConfidence
{
    // No room observation has been recorded yet (fresh tracker, or
    // post-disconnect before the first room display). Indistinguishable from
    // Lost for walker purposes — both require a manual SetLocated or a fresh
    // confirmed observation before any source-room-required operation can run.
    Unknown = 0,

    // Current room is trusted. Walker / loop / auto-lair may use it as a source.
    // This is the "happy path" state; the tracker writes
    // CharacterProfile.LastKnownRoom on every entry so the next session opens at
    // the same spot.
    Confirmed = 1,

    // A move was sent and we're waiting on confirmation. RoomState.CurrentRoom
    // still reflects the pre-move room until the next observation lands.
    Pending = 2,

    // Latest observation didn't line up with what we expected, but the previous
    // RoomState.CurrentRoom is preserved as our best-guess anchor. A counter on
    // RoomState.SuspectStrikes increments on each subsequent mismatch; at the
    // configured strike limit the tracker tries replay-from-last-Confirmed
    // recovery and falls through to Lost when replay fails. Suspect is
    // internal-only — the badge stays green so the UI doesn't churn on transient
    // observation glitches.
    Suspect = 3,

    // Replay-from-last-Confirmed failed and no graph candidate matched. Room is
    // null. Recovery paths: a future confirming observation resolves us back to
    // Confirmed, or the user clicks "I am here" on the Navigation map.
    Lost = 4,

    // The character died — the death message just fired. The server will respawn
    // them at the realm's death-respawn room; the next room observation arrives
    // there. The tracker drains its pending queue / steps, records the death on
    // the profile, and treats the next observation as authoritative
    // source-of-truth (same as the Unknown path: 1-of-1 candidate → Confirmed,
    // else ambiguous → Suspect, else Lost).
    PendingRespawn = 5,
}
