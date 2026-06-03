namespace FujinTerm.Game.Map;

/// <summary>
/// One row from the imported <c>TBInfo.json</c> table — the MajorMUD
/// "TextBlock Info" mechanism that backs room-level CMD chains
/// (gambling / NPC services / teleport portals) and monster dialogue
/// trees. Indexed by <see cref="Number"/>; loaded by
/// <see cref="Services.TBInfoStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Action"/> is a multi-line string where each line is a
/// colon-separated directive sequence keyed by a text keyword:
/// </para>
/// <code>
/// boat:7
/// passage:7
/// skiff:7
/// charge:7
/// </code>
/// <para>or with verbs and side-effects (gambling/teleport/etc.):</para>
/// <code>
/// go hole:message 767:teleport 487 2:message 768
/// enter hole:message 767:teleport 487 2:message 768
/// </code>
/// <para>
/// Commit 5 (teleport handler) parses the directive chain;
/// <see cref="Services.TBInfoStore"/> only loads + indexes the raw
/// rows for commit 1.
/// </para>
/// </remarks>
public sealed record TBInfoEntry
{
    /// <summary>Primary key — referenced by <see cref="Room.Cmd"/>.</summary>
    public required int Number { get; init; }

    /// <summary>
    /// Optional chain pointer — when non-zero, the action sequence
    /// continues at this number after the current entry's directives
    /// finish (typically via a <c>random N</c> or final <c>text N</c>
    /// step).
    /// </summary>
    public int LinkTo { get; init; }

    /// <summary>
    /// Verbatim multi-line action chain from the MDB. Commit 5 parses
    /// keyword-prefixed directive lines; commit 1 just retains the
    /// raw text. <c>"\0"</c> sentinel (NUL-only) means no actions.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Provenance string from the MDB — e.g. <c>"Room 1/2, Room 1/2337"</c>
    /// or <c>"Monster #61"</c>. Diagnostic only.
    /// </summary>
    public string? CalledFrom { get; init; }
}
