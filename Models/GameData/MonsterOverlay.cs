namespace FujinTerm.Models.GameData;

/// <summary>
/// Per-character / per-BBS / global override layered on top of an
/// MDB monster row. Persisted under the chosen tier via
/// <see cref="Services.SettingsResolver.WriteGameDataAt{T}"/> with
/// table = <c>"Monsters"</c> and record-id = the WCC No string. The
/// Game Data Browser → Monsters tab merges overrides on top of the
/// MDB <c>Monsters.json</c> base; the editor surface mirrors
/// MegaMUD's Monster/NPC Details dialog with deliberate omissions:
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not overridable</b> — every BBS supplies a
/// concrete MDB (stock or custom-realm), so the MDB is the
/// canonical source of truth for the monster's static stats. No
/// override layer for Experience, MaxHP, etc. — read those from
/// the MDB row.
/// </para>
/// <para>
/// <b>Deliberately not modelled</b> — MegaMUD's <c>Find first</c>
/// and <c>Check if alive</c> flags don't map onto our automation
/// engines (per user direction); not stored.
/// </para>
/// <para>
/// <b>What IS overridable</b> — per-monster automation behaviour:
/// display name, relationship, target priority, per-monster spell
/// preferences (the override-pre-attack and override-attack-spell
/// slots take priority over the global Combat-tab spell choices
/// for this specific monster), plus the NotHostile / DontBackstab
/// flags. All fields nullable so a partial-tier override only
/// carries the keys the user actually set — the resolver overlays
/// them onto the next-lower tier's values, preserving lower-tier
/// values for fields the user didn't touch.
/// </para>
/// <para>
/// Uses init-only properties (rather than the positional-record
/// syntax) so the resolver's <c>new T()</c> requirement is satisfied.
/// </para>
/// </remarks>
public sealed record MonsterOverlay
{
    /// <summary>User-facing display name override; <c>null</c> keeps the MDB value.</summary>
    public string? Name { get; init; }

    /// <summary>How automation should treat this monster on sight.</summary>
    public MonsterRelationship? Relationship { get; init; }

    /// <summary>Target-selection priority within auto-combat.</summary>
    public MonsterAttackPriority? Priority { get; init; }

    /// <summary>
    /// Override pre-attack spell — Spell.Number to cast on this
    /// monster before melee opens, regardless of the global
    /// Combat-tab pre-attack choice. <c>null</c> = no per-monster
    /// override (use the global setting).
    /// </summary>
    public int? OverridePreAttackSpellId { get; init; }

    /// <summary>Cast count for <see cref="OverridePreAttackSpellId"/>; <c>null</c> = 0.</summary>
    public int? OverridePreAttackCount { get; init; }

    /// <summary>
    /// Override attack spell — Spell.Number to cast as the primary
    /// attack on this monster, regardless of the global Combat-tab
    /// attack-spell choice. <c>null</c> = no per-monster override
    /// (use the global setting).
    /// </summary>
    public int? OverrideAttackSpellId { get; init; }

    /// <summary>Cast count for <see cref="OverrideAttackSpellId"/>; <c>null</c> = 0.</summary>
    public int? OverrideAttackCount { get; init; }

    /// <summary>Don't attack unless attacked first.</summary>
    public bool? NotHostile { get; init; }

    /// <summary>Suppress auto-BS attempts on this target.</summary>
    public bool? DontBackstab { get; init; }
}

/// <summary>
/// How the automation engines treat a monster on sight.
/// <list type="bullet">
///   <item><see cref="Enemy"/> — kill on sight.</item>
///   <item><see cref="Neutral"/> — don't attack unless attacked first (or unless "attack all monsters" is on in Combat settings).</item>
///   <item><see cref="Friend"/> — never attack.</item>
///   <item><see cref="Flee"/> — actively run from on sight.</item>
///   <item><see cref="Hangup"/> — disconnect from the BBS on sight.</item>
/// </list>
/// </summary>
public enum MonsterRelationship
{
    Neutral = 0,
    Enemy   = 1,
    Friend  = 2,
    Flee    = 3,
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
