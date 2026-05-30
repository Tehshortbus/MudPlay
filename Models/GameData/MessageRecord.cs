namespace FujinTerm.Models.GameData;

/// <summary>
/// One "Messages/Responses" entry — a wire-line pattern paired with the
/// engine behaviour it should flip. Covers conditions (blinded /
/// poisoned / paralyzed / confused / diseased / regenerating-self /
/// regenerating-target / etc.) and any other game message the user
/// wants the runtime to react to. Distinct from
/// <see cref="SpellMessage"/> which is always linked to a parent
/// <c>SpellId</c>; message records are standalone.
/// </summary>
/// <remarks>
/// Surfaced + edited via the Game Data Browser → Messages tab.
/// Initially imported from a MegaMUD <c>messages.md</c> file, then
/// persisted alongside the active game-data set under
/// <c>Data/Global/Messages/{set-name}.json</c> — paired with the
/// game-data folder so each realm carries its own message catalogue.
/// </remarks>
/// <param name="Name">
/// Stable display name shown in the Messages tab list (e.g.
/// <c>"Poison applied"</c>). Treated as the primary key for conflict
/// resolution.
/// </param>
/// <param name="Pattern">
/// Substring / wildcard the runtime parser matches against the wire
/// line. Format is up to the importer's source; the listing surface
/// stores it verbatim.
/// </param>
/// <param name="EffectFlags">
/// Bitfield of engine behaviour overrides keyed against
/// <see cref="MessageEffectFlag"/>.
/// </param>
/// <param name="Action">
/// The single high-level reaction the engine takes when the pattern
/// fires. See <see cref="MessageAction"/>. The flags above modify
/// fine-grained behaviour; the action is the dominant verb.
/// </param>
public sealed record MessageRecord(
    string Name,
    string Pattern,
    int EffectFlags,
    MessageAction Action);

/// <summary>What the engine does when a message pattern fires.</summary>
public enum MessageAction
{
    /// <summary>Note the match for logging; take no engine action.</summary>
    Ignore,

    /// <summary>Re-poll the current state (e.g. send <c>par</c> / <c>health</c>) before the next decision.</summary>
    Recheck,

    /// <summary>Pause the action loop until the condition clears.</summary>
    Wait,

    /// <summary>Rest until HP is full before continuing.</summary>
    RestHp,

    /// <summary>Rest / meditate until MA is full before continuing.</summary>
    RestMana,

    /// <summary>Skip auto-rest / auto-run logic while the condition is active.</summary>
    DontRestRun,

    /// <summary>Drop the connection.</summary>
    Hangup,
}

/// <summary>
/// Per-message behaviour overrides. Flag combinations are OR'd into
/// <see cref="MessageRecord.EffectFlags"/>. The Phase 13 automation
/// engines read these to decide which gates to flip while the
/// condition is active.
/// </summary>
[Flags]
public enum MessageEffectFlag
{
    None             = 0,
    BlocksMovement   = 1 << 0,
    BlocksCasting    = 1 << 1,
    BlocksAttacks    = 1 << 2,
    DrainsHp         = 1 << 3,
    DrainsMana       = 1 << 4,
    DegradesItems    = 1 << 5,
    InterruptsRest   = 1 << 6,
    SuppressesVision = 1 << 7,
}
