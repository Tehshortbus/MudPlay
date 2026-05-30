namespace FujinTerm.Models.GameData;

/// <summary>
/// Per-character / per-BBS / global override layered on top of an
/// MDB monster row. Persisted under the chosen tier via
/// <see cref="Services.SettingsResolver.WriteGameDataAt{T}"/> with
/// table = <c>"Monsters"</c> and record-id = the WCC No string. The
/// Game Data Browser → Monsters tab merges overrides on top of the
/// MDB <c>Monsters.json</c> base; the editor surface mirrors
/// MegaMUD's Monster/NPC Details dialog (minus the
/// <c>Find first</c> / <c>Check if alive</c> flags, which don't map
/// onto our automation engines).
/// </summary>
/// <remarks>
/// All fields are nullable so a partial-tier override only carries
/// the keys the user actually set — the resolver overlays them onto
/// the next-lower tier's values, preserving the MDB row for fields
/// the user didn't touch. Uses init-only properties (rather than the
/// positional-record syntax) so the resolver's <c>new T()</c>
/// requirement is satisfied.
/// </remarks>
public sealed record MonsterOverlay
{
    /// <summary>User-facing display name override; <c>null</c> keeps the MDB value.</summary>
    public string? Name { get; init; }

    /// <summary>How automation should treat this monster on sight.</summary>
    public MonsterRelationship? Relationship { get; init; }

    /// <summary>Target-selection priority within auto-combat.</summary>
    public MonsterAttackPriority? Priority { get; init; }

    /// <summary>Replacement EXP value when the MDB number is stale; <c>null</c> keeps the MDB value.</summary>
    public int? ExperienceOverride { get; init; }

    /// <summary>Replacement MaxHP; <c>null</c> keeps the MDB value.</summary>
    public int? MaxHpOverride { get; init; }

    /// <summary>Companion ceiling for <see cref="MaxHpOverride"/>; per the MegaMUD UI, the "Max" twin field on the HP row.</summary>
    public int? MaxHpMax { get; init; }

    /// <summary>Spell to cast before melee opens; <c>null</c> = none.</summary>
    public int? PreAttackSpellId { get; init; }

    /// <summary>Cast count for <see cref="PreAttackSpellId"/>; <c>null</c> = 0.</summary>
    public int? PreAttackCount { get; init; }

    /// <summary>Spell to cast as the primary attack; <c>null</c> = none.</summary>
    public int? AttackSpellId { get; init; }

    /// <summary>Cast count for <see cref="AttackSpellId"/>; <c>null</c> = 0.</summary>
    public int? AttackCount { get; init; }

    /// <summary>Don't attack unless attacked first.</summary>
    public bool? NotHostile { get; init; }

    /// <summary>Suppress auto-BS attempts on this target.</summary>
    public bool? DontBackstab { get; init; }
}

/// <summary>
/// How the automation engines treat a monster on sight. Wire-format
/// names match MegaMUD's listing column ("Friend" column displays
/// these values: <c>Enemy</c> / <c>Friend</c> / <c>Neutral</c> /
/// <c>Avoid</c> / <c>Hangup</c>).
/// </summary>
public enum MonsterRelationship
{
    /// <summary>Don't attack unless attacked first.</summary>
    Neutral = 0,

    /// <summary>Kill on sight.</summary>
    Enemy   = 1,

    /// <summary>Never attack.</summary>
    Friend  = 2,

    /// <summary>Actively flee on sight.</summary>
    Avoid   = 3,

    /// <summary>Disconnect from the BBS on sight.</summary>
    Hangup  = 4,
}

/// <summary>
/// Target-selection priority within an auto-combat round. Mirrors
/// MegaMUD's Attack Priority radio group on the Monster/NPC Details
/// dialog. The combat engine sorts visible enemies by this enum;
/// First targets fire before Last targets within the same group.
/// </summary>
public enum MonsterAttackPriority
{
    First  = 0,
    High   = 1,
    Normal = 2,
    Low    = 3,
    Last   = 4,
}
