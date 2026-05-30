namespace FujinTerm.Models.GameData;

/// <summary>
/// Non-spell condition pattern + the engine behaviour it should flip.
/// Covers blinded / poisoned / paralyzed / confused / diseased /
/// regenerating-self / regenerating-target / etc. Distinct from
/// <see cref="SpellMessage"/> which is always linked to a parent
/// <c>SpellId</c>; conditions are standalone.
/// </summary>
/// <param name="Name">
/// Stable display name shown in the Conditions tab list (e.g.
/// <c>"Poison applied"</c>). Treated as the primary key for conflict
/// resolution.
/// </param>
/// <param name="Pattern">
/// Substring / wildcard the runtime parser matches against the wire
/// line. Format is up to the importer's source; PR 5.9 stores it
/// verbatim.
/// </param>
/// <param name="EffectFlags">
/// Bitfield of engine behaviour overrides keyed against
/// <see cref="ConditionEffectFlag"/>.
/// </param>
/// <param name="Action">
/// The single high-level reaction the engine takes when the pattern
/// fires. See <see cref="ConditionAction"/>. The flags above modify
/// fine-grained behaviour; the action is the dominant verb.
/// </param>
public sealed record ConditionMessage(
    string Name,
    string Pattern,
    int EffectFlags,
    ConditionAction Action);

/// <summary>What the engine does when a condition fires.</summary>
public enum ConditionAction
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
/// Per-condition behaviour overrides. Flag combinations are OR'd into
/// <see cref="ConditionMessage.EffectFlags"/>. The Phase 13 automation
/// engines read these to decide which gates to flip while the
/// condition is active.
/// </summary>
[Flags]
public enum ConditionEffectFlag
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
