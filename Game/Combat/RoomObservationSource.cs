namespace FujinTerm.Game.Combat;

/// <summary>
/// Which code path produced a <see cref="RoomEntitiesObservation"/>.
/// Carried so consumers (and the LogPane) can tell a real room display
/// apart from a synthetic re-fire. Critical for the "wasted re-attack
/// mid-combat" diagnosis: a target-clearing empty observation can come
/// from a genuine room change, a death-line removal, or an empty
/// Also-Here parse — and the fix differs per source.
/// </summary>
public enum RoomObservationSource
{
    /// <summary>Parsed from an <c>Also here:</c> wire line (single- or
    /// multi-line stitched).</summary>
    AlsoHere,

    /// <summary>A single entity appended on a "&lt;name&gt; walks in"
    /// arrival line (<see cref="RoomEntityClassifier.AppendArrivalEntity"/>).</summary>
    Arrival,

    /// <summary>One entity removed on a death line
    /// (<see cref="RoomEntityClassifier.RemoveDeadEntity"/>). May leave
    /// the list empty when the last monster dies.</summary>
    Death,

    /// <summary>Synthetic empty wipe on a confirmed room change
    /// (<see cref="RoomEntityClassifier.NoteRoomChanged"/>).</summary>
    RoomChange,
}
