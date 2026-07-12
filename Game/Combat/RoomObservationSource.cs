namespace FujinTerm.Game.Combat;

// Which code path produced a RoomEntitiesObservation. Carried so consumers (and
// the LogPane) can tell a real room display apart from a synthetic re-fire.
// Critical for the "wasted re-attack mid-combat" diagnosis: a target-clearing
// empty observation can come from a genuine room change, a death-line removal,
// or an empty Also-Here parse — and the fix differs per source.
public enum RoomObservationSource
{
    // Parsed from an "Also here:" wire line (single- or multi-line stitched).
    AlsoHere,

    // A single entity appended on a "<name> walks in" arrival line
    // (RoomEntityClassifier.AppendArrivalEntity).
    Arrival,

    // One entity removed on a death line (RoomEntityClassifier.RemoveDeadEntity).
    // May leave the list empty when the last monster dies.
    Death,

    // One entity removed on a departure line — a mob (often dragged by a fleeing
    // player) walks out of the room (RoomEntityClassifier.RemoveDepartedEntity).
    // May leave the list empty when the last monster leaves, which clears the
    // Combat gate the departed mob was holding asserted.
    Departure,

    // Synthetic empty wipe on a confirmed room change
    // (RoomEntityClassifier.NoteRoomChanged).
    RoomChange,
}
