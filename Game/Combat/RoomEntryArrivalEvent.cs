namespace FujinTerm.Game.Combat;

// Payload of RoomEntryWatcher.ArrivalObserved. The classifier appends a
// RoomEntity to its observation list and re-fires
// RoomEntityClassifier.EntitiesObserved; this is the smaller-grained event that
// names the arrival itself, for subsystems that care about the spawn-as-event
// (e.g. SessionStats or a per-mob spawn-counter).
//
// Name is the name post-article-strip — e.g. "fierce lashworm" from the wire's
// "A fierce lashworm crawls into the room from nowhere." (the leading "A "/"An
// "/"The " is dropped). Kind is the classification — Monster / Player / Unknown;
// the colour of the name segment on the wire (yellow vs red) was used as a
// tiebreaker hint when the name didn't match a known game-data record, and
// consumers can trust this as authoritative. Direction is the direction word
// from the arrival line: cardinal ("north"), non-cardinal ("northeast"), "up",
// "down", or "nowhere" (script spawn). At is the wall-clock time the arrival
// line was observed.
public readonly record struct RoomEntryArrivalEvent(
    string Name,
    EntityKind Kind,
    string Direction,
    DateTimeOffset At);
