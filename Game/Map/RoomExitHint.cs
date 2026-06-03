namespace FujinTerm.Game.Map;

/// <summary>
/// Parenthetical exit-cell hint imported alongside the target
/// <see cref="RoomKey"/>. The MDB encodes these inline on the exit
/// string — e.g. <c>"1/1381 (Door)"</c> — and the importer round-trips
/// them through <c>Rooms.json</c>. Phase 7 needs Door (the walker
/// emits an explicit <c>open door {dir}</c> step before moving) and
/// Trap (the <c>RoomExit.TrapInfo</c> map overlay + the
/// <c>MovementCoordinator</c> ↔ <c>TrapDisarmManager</c> handoff). New
/// hint kinds get added here as we discover them on a per-realm basis;
/// unknown text falls through to <see cref="None"/> and the raw cell
/// is preserved on <see cref="RoomExit.RawHint"/> for diagnostics.
/// </summary>
public enum RoomExitHint
{
    None = 0,
    Door = 1,
    Trap = 2,
}
