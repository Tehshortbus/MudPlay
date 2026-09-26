using System.Text.Json.Serialization;

namespace MudPlay.Models.GameData;

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
// What IS overridable — where the monster lives (Landmass / Region / Area, the
// table's location columns — the shipped seed fills them for the realms we
// carry, the user fills the rest), plus per-monster automation behaviour: display
// name, relationship, target priority, the DontBackstab flag, and the whole
// SINGLE-TARGET combat chain for this specific monster, mirroring the
// Settings → Combat spell grid rung-for-rung. Four override slots take
// priority over the global Combat-tab choices for this species:
//   • Debuff (single target)  — OverridePreAttack* (Spell.Number)
//   • Normal attack spell      — OverrideAttack*    (Spell.Number)
//   • Alternate attack spell   — OverrideAltAttack* (Spell.Number)
//   • Physical attack          — OverridePhysicalCommand (raw verb)
// The three spell slots substitute into their matching rung and run the
// EXACT same gated cascade the configured slots do — observed "no effect"
// immunity plus the predictive level / element-resist blocks, each computed
// against the OVERRIDE spell, not the global slot. The physical slot swaps
// the weapon command the engine would send ONLY on a round the decision
// engine already chose physical; it does not force physical or suppress
// spells. Multi-attack (AoE) and AoE-debuff rungs are deliberately NOT
// overridable — they're room-scoped, not per-species. All fields nullable so
// a partial-tier override only carries the keys the user actually set — the
// resolver overlays them onto the next-lower tier's values, preserving
// lower-tier values for fields the user didn't touch.
//
// Uses init-only properties (rather than the positional-record syntax) so
// the resolver's new T() requirement is satisfied.
public sealed record MonsterOverlay
{
    // Display name override; null keeps the MDB value.
    public string? Name { get; init; }

    // Where this monster lives, as the Landmass → Region → Area hierarchy the Game Data
    // Browser's Monsters table shows. Labels only — nothing in the engines reads them.
    // null = not set (the table cell and the record's box stay blank); a blank box saves
    // back to null so a partial-tier override never masks a lower tier with an empty string.
    public string? Landmass { get; init; }
    public string? Region { get; init; }
    public string? Area { get; init; }

    // How automation should treat this monster on sight.
    public MonsterRelationship? Relationship { get; init; }

    // Target-selection priority within auto-combat.
    public MonsterAttackPriority? Priority { get; init; }

    // Override pre-attack spell — Spell.Number to cast on this monster
    // before melee opens, regardless of the global Combat-tab pre-attack
    // choice. null = no per-monster override (use the global setting).
    public int? OverridePreAttackSpellId { get; init; }

    // Per-room cast cap for OverridePreAttackSpellId; null/0 = unlimited.
    public int? OverridePreAttackCount { get; init; }

    // Minimum mana before the override pre-attack spell fires, interpreted per the
    // character's Combat-tab SpellManaThresholdMode (Percentage / Value) — the same
    // gate as CombatSpellSlot.MinManaPerCast. null/0 = no floor. Below it the override
    // holds and the normal combat flow takes the round.
    public int? OverridePreAttackMinMana { get; init; }

    // Override NORMAL attack spell — Spell.Number that substitutes for the
    // global Combat-tab Normal-attack spell on this monster, occupying the
    // normal-attack rung and running the same gated cascade. null = no
    // per-monster override (use the global setting). Spell-only: a raw attack
    // verb goes in OverridePhysicalCommand, not here.
    public int? OverrideAttackSpellId { get; init; }

    // Per-room cast cap for OverrideAttackSpellId; null/0 = unlimited.
    public int? OverrideAttackCount { get; init; }

    // Minimum mana before the override normal-attack spell fires, same
    // interpretation as OverridePreAttackMinMana. null/0 = no floor.
    public int? OverrideAttackMinMana { get; init; }

    // Override ALTERNATE attack spell — Spell.Number that substitutes for the
    // global Combat-tab Alternate-attack spell on this monster, occupying the
    // alternate rung of the same gated cascade (fired when the normal rung
    // can't). null = no override. Spell-only.
    public int? OverrideAltAttackSpellId { get; init; }

    // Per-room cast cap for OverrideAltAttackSpellId; null/0 = unlimited.
    public int? OverrideAltAttackCount { get; init; }

    // Minimum mana before the override alternate-attack spell fires, same
    // interpretation as OverridePreAttackMinMana. null/0 = no floor.
    public int? OverrideAltAttackMinMana { get; init; }

    // Override PHYSICAL attack command — a raw verb ("attack", "bash") that
    // replaces the weapon command the engine would otherwise send, but ONLY on
    // a round the decision engine already chose physical (ActionOrder/
    // alternation). It does NOT force physical or suppress the spell rungs, and
    // it carries no cast-rung gating (no mana floor, no per-room cap). null/blank
    // = no override. Legacy data written under the old "Override Attack" command
    // slot round-trips into this field unchanged: the JSON key is pinned to the
    // historical "OverrideAttackCommand" so no migration is needed. See
    // MonsterEditDialogViewModel.ResolveSpellOverride.
    [JsonPropertyName("OverrideAttackCommand")]
    public string? OverridePhysicalCommand { get; init; }

    // Suppress auto-BS attempts on this target.
    public bool? DontBackstab { get; init; }

    // Kill a NEUTRAL monster on sight. Neutrals never attack first (they only
    // retaliate once attacked), so they're normally left alone and a room of them
    // is safe to rest in. Checking this makes auto-combat engage THIS neutral like
    // an enemy — while other passive neutrals stay non-engageable, so once it's dead
    // you can rest among the rest. Only meaningful when Relationship is Neutral;
    // ignored otherwise (Enemy already engages, Friend/Flee/Hangup never do).
    public bool? KillOnSight { get; init; }
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
