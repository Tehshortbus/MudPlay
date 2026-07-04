namespace FujinTerm.Game.Map;

// Parenthetical exit-cell hint imported alongside the target RoomKey. The MDB
// encodes these inline on the exit string — e.g. "1/1381 (Door)",
// "1/2335 (Text: borrow skiff)", "1/1224 (Key: 172 [or 100 picklocks])" — and
// the importer round-trips them through Rooms.json. Unknown text falls through
// to None and the raw cell is preserved on RoomExit.RawHint for diagnostics.
public enum RoomExitHint
{
    // No modifier or unrecognised shape. Walker treats as a plain cardinal step.
    None = 0,

    // (Door) with optional [N picklocks/strength] or [N picklocks] requirement.
    // Walker opens it with `open <dir>`; bashes with `bash <dir>` (requires
    // CanBash and stat ≥ StatRequirement); or picks with `pick <dir>` (picklock
    // ≥ StatRequirement).
    Door = 1,

    // (Trap, N damage) or (Spell Trap: N). Walker routes through
    // TrapDisarmManager before stepping.
    Trap = 2,

    // (Key: ITEMID) with optional [or N picklocks] / [or N picklocks/strength]
    // alternative. Walker tries the stat alternative first (saves limited key
    // charges) when the player meets it; falls back to single-shot
    // `use <keyName> <dir>` + `open <dir>`.
    KeyLocked = 3,

    // (Text: cmd1, cmd2, …). Each comma-separated alternative is sufficient to
    // traverse — walker sends the first one as the move command (no follow-up
    // cardinal). Stored on RoomExit.TextCommands. Party-safe (followers follow
    // normally).
    Text = 4,

    // (Item: ITEMID) seen on an exit whose source room has Room.Cmd > 0. The
    // room's CMD indexes a TBInfo chain that resolves to the actual teleport
    // command + destination. Party-breaking — leader broadcasts via
    // `.@party <command>` before teleporting self.
    Teleport = 5,

    // (Hidden). Exit doesn't appear on "Obvious exits:"; the walker reveals it
    // with `sea <dir>` before stepping. Capped by the Settings.Other
    // attempt-search spinner.
    SearchableHidden = 6,

    // (Hidden, Needs N Actions, any/specific order). The walker visits each
    // prerequisite room, fires the action, and returns to traverse. Action data
    // is referenced separately from the exit cell.
    MultiActionHidden = 7,

    // (Ticket: ITEMID). Walker steps through normally; the server consumes the
    // ticket. RoomExit.KeyItemId carries the ticket's item id for the
    // path-planning gate.
    Ticket = 8,

    // (Item: ITEMID) on an exit whose source room has Room.Cmd == 0. Inventory
    // check — player must carry the item to traverse. Party-blocking for any
    // follower without it (verified via @have if needed). The walker fails the
    // path on a missing-item gate rather than routing around it.
    Item = 9,

    // (Toll: N). Walker steps through normally; the server deducts
    // RoomExit.TollGold in gold. No path-time wallet check yet.
    Toll = 10,
}
