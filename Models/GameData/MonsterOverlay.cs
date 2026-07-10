namespace FujinTerm.Models.GameData;

// Per-character / per-BBS / global override layered on top of an MDB
// monster row. Persisted under the chosen tier via
// SettingsResolver.WriteGameDataAt with table = "Monsters" and record-id =
// the WCC No string. The Game Data Browser → Monsters tab merges overrides
// on top of the MDB Monsters.json base; the editor surface mirrors
// MegaMUD's Monster/NPC Details dialog with deliberate omissions:
//
// Deliberately not overridable — every BBS supplies a concrete MDB (stock
// or custom-realm), so the MDB is the canonical source of truth for the
// monster's static stats. No override layer for Experience, MaxHP, etc. —
// read those from the MDB row.
//
// Deliberately not modelled — MegaMUD's Find first and Check if alive
// flags don't map onto our automation engines (per user direction); not
// stored.
//
// What IS overridable — per-monster automation behaviour: display name,
// relationship, target priority, per-monster spell preferences (the
// override-pre-attack and override-attack-spell slots take priority over
// the global Combat-tab spell choices for this specific monster), plus the
// DontBackstab flag. All fields nullable so a partial-tier
// override only carries the keys the user actually set — the resolver
// overlays them onto the next-lower tier's values, preserving lower-tier
// values for fields the user didn't touch.
//
// Uses init-only properties (rather than the positional-record syntax) so
// the resolver's new T() requirement is satisfied.
public sealed record MonsterOverlay
{
    // Display name override; null keeps the MDB value.
    public string? Name { get; init; }

    // How automation should treat this monster on sight.
    public MonsterRelationship? Relationship { get; init; }

    // Target-selection priority within auto-combat.
    public MonsterAttackPriority? Priority { get; init; }

    // Override pre-attack spell — Spell.Number to cast on this monster
    // before melee opens, regardless of the global Combat-tab pre-attack
    // choice. null = no per-monster override (use the global setting).
    public int? OverridePreAttackSpellId { get; init; }

    // Cast count for OverridePreAttackSpellId; null = 0.
    public int? OverridePreAttackCount { get; init; }

    // Override attack spell — Spell.Number to cast as the primary attack on
    // this monster, regardless of the global Combat-tab attack-spell
    // choice. null = no per-monster override (use the global setting).
    public int? OverrideAttackSpellId { get; init; }

    // Cast count for OverrideAttackSpellId; null = 0.
    public int? OverrideAttackCount { get; init; }

    // Suppress auto-BS attempts on this target.
    public bool? DontBackstab { get; init; }
}

// How the automation engines treat a monster on sight.
//   Enemy — kill on sight.
//   Neutral — don't attack unless attacked first (or unless "attack all
//     monsters" is on in Combat settings).
//   Friend — never attack.
//   Flee — actively run from on sight.
//   Hangup — disconnect from the BBS on sight.
public enum MonsterRelationship
{
    Neutral = 0,
    Enemy   = 1,
    Friend  = 2,
    Flee    = 3,
    Hangup  = 4,
}

// Target-selection priority within an auto-combat round. Mirrors MegaMUD's
// Attack Priority radio group on the Monster/NPC Details dialog. The combat
// engine sorts visible enemies by this enum; First targets fire before Last
// targets within the same group.
public enum MonsterAttackPriority
{
    First  = 0,
    High   = 1,
    Normal = 2,
    Low    = 3,
    Last   = 4,
}
