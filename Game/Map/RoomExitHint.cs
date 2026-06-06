namespace FujinTerm.Game.Map;

/// <summary>
/// Parenthetical exit-cell hint imported alongside the target
/// <see cref="RoomKey"/>. The MDB encodes these inline on the exit
/// string — e.g. <c>"1/1381 (Door)"</c>, <c>"1/2335 (Text: borrow skiff)"</c>,
/// <c>"1/1224 (Key: 172 [or 100 picklocks])"</c> — and the importer
/// round-trips them through <c>Rooms.json</c>. Unknown text falls
/// through to <see cref="None"/> and the raw cell is preserved on
/// <see cref="RoomExit.RawHint"/> for diagnostics.
/// </summary>
public enum RoomExitHint
{
    /// <summary>No modifier or unrecognised shape. Walker treats as a plain cardinal step.</summary>
    None = 0,

    /// <summary>
    /// <c>(Door)</c> with optional <c>[N picklocks/strength]</c> or
    /// <c>[N picklocks]</c> requirement. Walker opens it with
    /// <c>open &lt;dir&gt;</c>; bashes with <c>bash &lt;dir&gt;</c>
    /// (requires <see cref="RoomExit.CanBash"/> and stat ≥ <see cref="RoomExit.StatRequirement"/>);
    /// or picks with <c>pick &lt;dir&gt;</c> (picklock ≥
    /// <see cref="RoomExit.StatRequirement"/>).
    /// </summary>
    Door = 1,

    /// <summary>
    /// <c>(Trap, N damage)</c> or <c>(Spell Trap: N)</c>. Walker
    /// routes through <c>TrapDisarmManager</c> before stepping.
    /// </summary>
    Trap = 2,

    /// <summary>
    /// <c>(Key: ITEMID)</c> with optional <c>[or N picklocks]</c> /
    /// <c>[or N picklocks/strength]</c> alternative. Walker tries
    /// the stat alternative first (saves limited key charges) when
    /// the player meets it; falls back to single-shot
    /// <c>use &lt;keyName&gt; &lt;dir&gt;</c> + <c>open &lt;dir&gt;</c>.
    /// </summary>
    KeyLocked = 3,

    /// <summary>
    /// <c>(Text: cmd1, cmd2, …)</c>. Each comma-separated alternative
    /// is sufficient to traverse — walker sends the first one as the
    /// move command (no follow-up cardinal). Stored on
    /// <see cref="RoomExit.TextCommands"/>. Party-safe (followers
    /// follow normally).
    /// </summary>
    Text = 4,

    /// <summary>
    /// <c>(Item: ITEMID)</c> seen on an exit whose source room has
    /// <see cref="Room.Cmd"/> &gt; 0. The room's <c>CMD</c> indexes a
    /// <c>TBInfo</c> chain that resolves to the actual teleport
    /// command + destination. Party-breaking — leader broadcasts via
    /// <c>.@party &lt;command&gt;</c> before teleporting self.
    /// </summary>
    Teleport = 5,

    /// <summary>
    /// <c>(Hidden)</c>. Exit doesn't appear on "Obvious exits:"; the
    /// walker reveals it with <c>sea &lt;dir&gt;</c> before stepping.
    /// Capped by the Settings.Other attempt-search spinner.
    /// </summary>
    SearchableHidden = 6,

    /// <summary>
    /// <c>(Hidden, Needs N Actions, any/specific order)</c>. The
    /// walker visits each prerequisite room, fires the action, and
    /// returns to traverse. Action data is referenced separately
    /// from the exit cell; commit 6 wires the full expander.
    /// </summary>
    MultiActionHidden = 7,

    /// <summary>
    /// <c>(Ticket: ITEMID)</c>. Walker steps through normally; the
    /// server consumes the ticket. <see cref="RoomExit.KeyItemId"/>
    /// carries the ticket's item id for the path-planning gate.
    /// </summary>
    Ticket = 8,

    /// <summary>
    /// <c>(Item: ITEMID)</c> on an exit whose source room has
    /// <see cref="Room.Cmd"/> == 0. Inventory check — player must
    /// carry the item to traverse. Party-blocking for any follower
    /// without it (verified via <c>@have</c> if needed). For now the
    /// walker fails the path on a missing-item gate; future detour
    /// system (see GH issue) will route around.
    /// </summary>
    Item = 9,

    /// <summary>
    /// <c>(Toll: N)</c>. Walker steps through normally; the server
    /// deducts <see cref="RoomExit.TollGold"/> in gold. No
    /// path-time gate yet — follow-up PR adds the wallet check.
    /// </summary>
    Toll = 10,
}
