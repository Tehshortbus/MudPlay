namespace FujinTerm.Game.Map;

// One row from the imported TBInfo.json table — the MajorMUD "TextBlock Info"
// mechanism that backs room-level CMD chains (gambling / NPC services / teleport
// portals) and monster dialogue trees. Indexed by Number; loaded by
// TBInfoStore.
//
// Action is a multi-line string where each line is a colon-separated directive
// sequence keyed by a text keyword:
//
//     boat:7
//     passage:7
//     skiff:7
//     charge:7
//
// or with verbs and side-effects (gambling/teleport/etc.):
//
//     go hole:message 767:teleport 487 2:message 768
//     enter hole:message 767:teleport 487 2:message 768
//
// The teleport handler parses the directive chain; TBInfoStore only loads +
// indexes the raw rows.
public sealed record TBInfoEntry
{
    // Primary key — referenced by Room.Cmd.
    public required int Number { get; init; }

    // Optional chain pointer — when non-zero, the action sequence continues at
    // this number after the current entry's directives finish (typically via a
    // "random N" or final "text N" step).
    public int LinkTo { get; init; }

    // Verbatim multi-line action chain from the MDB. The teleport handler parses
    // keyword-prefixed directive lines; the store just retains the raw text. A
    // NUL-only sentinel means no actions.
    public string? Action { get; init; }

    // Provenance string from the MDB — e.g. "Room 1/2, Room 1/2337" or
    // "Monster #61". Diagnostic only.
    public string? CalledFrom { get; init; }
}
