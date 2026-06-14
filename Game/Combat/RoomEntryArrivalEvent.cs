namespace FujinTerm.Game.Combat;

/// <summary>
/// Payload of <see cref="RoomEntryWatcher.ArrivalObserved"/>. The
/// classifier appends a <see cref="RoomEntity"/> to its observation
/// list and re-fires <see cref="RoomEntityClassifier.EntitiesObserved"/>;
/// this is the smaller-grained event that names the arrival itself,
/// for subsystems that care about the spawn-as-event (e.g. future
/// Phase 11 SessionStats or a per-mob spawn-counter).
/// </summary>
/// <param name="Name">Name post-article-strip — e.g. "fierce lashworm"
/// from the wire's "A fierce lashworm crawls into the room from
/// nowhere." (the leading "A "/"An "/"The " is dropped).</param>
/// <param name="Kind">Classification — Monster / Player / Unknown.
/// Color of the name segment on the wire (yellow vs red) was used as
/// a tiebreaker hint when the name didn't match a known game-data
/// record; consumers can trust this as authoritative.</param>
/// <param name="Direction">The direction word from the arrival line:
/// cardinal ("north"), non-cardinal ("northeast"), <c>"up"</c>,
/// <c>"down"</c>, or <c>"nowhere"</c> (script spawn).</param>
/// <param name="At">Wall-clock time the arrival line was observed.</param>
public readonly record struct RoomEntryArrivalEvent(
    string Name,
    EntityKind Kind,
    string Direction,
    DateTimeOffset At);
