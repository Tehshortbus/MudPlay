namespace FujinTerm.Game.Combat;

/// <summary>
/// One emit of <see cref="RoomEntityClassifier.EntitiesObserved"/> —
/// every time the classifier consumes a fresh <c>Also here:</c> line
/// from the wire. Holds the parsed entity list plus the raw line so
/// downstream consumers (CombatStateTracker, future StealthManager,
/// the Phase 9 sub-G fix dialog) don't have to re-parse.
/// </summary>
/// <param name="RawAlsoHereLine">
/// The verbatim <c>"Also here: ..."</c> line from the wire. Carried
/// for the LogPane double-click-to-copy + Unknown-entity fix dialog
/// flow (<see cref="ViewModels.UnknownEntityFixDialogViewModel"/>).
/// </param>
/// <param name="Entities">Classified occupants, in the order they
/// appeared in the line (left to right).</param>
/// <param name="At">Wall-clock timestamp of the observation.</param>
public readonly record struct RoomEntitiesObservation(
    string RawAlsoHereLine,
    IReadOnlyList<RoomEntity> Entities,
    DateTimeOffset At);
