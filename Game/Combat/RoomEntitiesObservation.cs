namespace FujinTerm.Game.Combat;

// One emit of RoomEntityClassifier.EntitiesObserved — every time the classifier
// consumes a fresh "Also here:" line from the wire. Holds the parsed entity list
// plus the raw line so downstream consumers (CombatStateTracker, StealthManager,
// the unknown-entity fix dialog) don't have to re-parse.
//
// RawAlsoHereLine is the verbatim "Also here: ..." line from the wire, carried
// for the LogPane double-click-to-copy + Unknown-entity fix dialog flow
// (UnknownEntityFixDialogViewModel). Entities are the classified occupants, in
// the order they appeared in the line (left to right). At is the wall-clock
// timestamp of the observation. Source is which classifier path produced this
// emit — defaults to AlsoHere; the synthetic re-fire paths (arrival / death /
// room-change) stamp their own value so the empty-observation diagnosis can name
// the origin.
public readonly record struct RoomEntitiesObservation(
    string RawAlsoHereLine,
    IReadOnlyList<RoomEntity> Entities,
    DateTimeOffset At,
    RoomObservationSource Source = RoomObservationSource.AlsoHere);
