namespace FujinTerm.Models.GameData;

/// <summary>
/// One "Messages/Responses" entry — a wire-line pattern paired with
/// the engine behaviour it should flip. Covers conditions (blinded /
/// poisoned / paralyzed / confused / diseased / regenerating-self /
/// regenerating-target / etc.) and any other game message the user
/// wants the runtime to react to. Distinct from
/// <see cref="SpellMessage"/> which is always linked to a parent
/// <c>SpellId</c>; message records are standalone.
/// </summary>
/// <remarks>
/// <para>
/// Surfaced + edited via the Game Data Browser → Messages tab.
/// Initially imported from a MegaMUD <c>messages.md</c> file, then
/// persisted alongside the active game-data set under
/// <c>Data/Global/Messages/{set-name}.json</c> — paired with the
/// game-data folder so each realm carries its own message catalogue.
/// </para>
/// <para>
/// Field shape matches MegaMUD's <c>messages.md</c> wire format so
/// records round-trip cleanly between import and export. See
/// <see cref="MessageAction"/> / <see cref="MessageFlags"/> for the
/// encoding tables.
/// </para>
/// </remarks>
/// <param name="Id">
/// Stable content hash <c>SHA1(Name | Message | EndsWith)</c> truncated
/// to 16 lowercase hex chars. Lets the same record dedupe across
/// imports / merges without relying on the user-editable
/// <see cref="Name"/>.
/// </param>
/// <param name="Name">
/// Stable display name shown in the Messages tab list (e.g.
/// <c>"Poison applied"</c>). User-editable; the immutable identity is
/// <see cref="Id"/>.
/// </param>
/// <param name="Message">
/// Pattern that fires when the effect begins / is present. Required.
/// </param>
/// <param name="EndsWith">
/// Optional pattern that fires when the effect expires. Empty string
/// when the record has no end-pattern.
/// </param>
/// <param name="Action">
/// The single high-level reaction the engine takes when the pattern
/// fires. See <see cref="MessageAction"/>.
/// </param>
/// <param name="Flags">Typed view of the known flag bits.</param>
/// <param name="RawFlagsHex">
/// Full 16-bit flag word as stored in the legacy <c>messages.md</c>
/// file. Preserves reserved-but-unknown bits (notably <c>0x0800</c>)
/// so the record round-trips back to the file losslessly.
/// </param>
/// <param name="ResponseCommands">
/// Commands the engine sends when the pattern fires. Legacy
/// <c>messages.md</c> encodes multiple commands separated by literal
/// <c>^M</c> or raw CR; we parse them out and store as a list.
/// </param>
public sealed record MessageRecord(
    string         Id,
    string         Name,
    string         Message,
    string         EndsWith,
    MessageAction  Action,
    MessageFlags   Flags,
    ushort         RawFlagsHex,
    IReadOnlyList<string> ResponseCommands);

/// <summary>
/// What the engine does when a message pattern fires. Values match the
/// legacy MegaMUD <c>messages.md</c> action code (single decimal digit)
/// so records round-trip without translation.
/// </summary>
public enum MessageAction
{
    /// <summary>Note the match for logging; take no engine action.</summary>
    Ignore      = 0,

    /// <summary>Re-poll the current room state (e.g. <c>look</c> / <c>par</c>) before the next decision.</summary>
    RecheckRoom = 1,

    /// <summary>Pause the action loop until the condition expires.</summary>
    WaitForEnd  = 2,

    /// <summary>Rest until HP is full before continuing.</summary>
    RestHp      = 3,

    /// <summary>Rest / meditate until MA is full before continuing.</summary>
    RestMana    = 4,

    /// <summary>Skip auto-rest and switch to auto-run while the condition is active.</summary>
    Run         = 5,

    /// <summary>Drop the connection.</summary>
    Hangup      = 6,
}

/// <summary>
/// Typed view of the message flag bitfield. Values match the legacy
/// MegaMUD <c>messages.md</c> 16-bit hex encoding so records round-trip
/// without translation. Bit <c>0x0800</c> is reserved / undocumented in
/// the legacy format; preserve it verbatim via
/// <see cref="MessageRecord.RawFlagsHex"/>.
/// </summary>
[Flags]
public enum MessageFlags : ushort
{
    None                = 0,
    Blinded             = 0x0001,
    Confused            = 0x0002,
    Poisoned            = 0x0004,
    LosingHp            = 0x0008,
    MovementPrevented   = 0x0010,
    AttackPrevented     = 0x0020,
    Diseased            = 0x0040,
    HpRegenerating      = 0x0080,
    FindInConversations = 0x0100,
    ManaRegenerating    = 0x0200,
    FindInText          = 0x0400,
    // 0x0800 reserved — preserve via RawFlagsHex
    EndsCombat          = 0x1000,
    LastActionFailed    = 0x2000,
    UseWhenChasing      = 0x4000,
    Disabled            = 0x8000,
}
