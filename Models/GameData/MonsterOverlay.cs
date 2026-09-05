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
// What IS overridable — per-monster automation behaviour: display name,
// relationship, target priority, per-monster attack preferences (the
// override-pre-attack and override-attack slots take priority over the
// global Combat-tab choices for this specific monster — the attack slot
// takes either a Spell.Number or a raw command verb), plus the
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

    // Per-room cast cap for OverridePreAttackSpellId; null/0 = unlimited.
    public int? OverridePreAttackCount { get; init; }

    // Minimum mana before the override pre-attack spell fires, interpreted per the
    // character's Combat-tab SpellManaThresholdMode (Percentage / Value) — the same
    // gate as CombatSpellSlot.MinManaPerCast. null/0 = no floor. Below it the override
    // holds and the normal combat flow takes the round.
    public int? OverridePreAttackMinMana { get; init; }

    // Override attack spell — Spell.Number to cast as the primary attack on
    // this monster, regardless of the global Combat-tab attack-spell
    // choice, routed through the mana-gated attack-spell rung. null = no
    // per-monster override (use the global setting).
    public int? OverrideAttackSpellId { get; init; }

    // Per-room cast cap for OverrideAttackSpellId; null/0 = unlimited.
    public int? OverrideAttackCount { get; init; }

    // Minimum mana before the override attack spell fires, same interpretation as
    // OverridePreAttackMinMana. null/0 = no floor. Ignored for a raw-command override
    // (OverrideAttackCommand), which never mana-gates.
    public int? OverrideAttackMinMana { get; init; }

    // Override attack COMMAND — a raw verb ("attack", "bash") sent verbatim as
    // this monster's attack, forced over the whole normal spell/weapon flow.
    // Unlike OverrideAttackSpellId it carries no cast-rung gating (no mana
    // floor, no per-room cap): it goes out like a weapon command and the
    // server auto-repeats it each round. The user hand-picked it, so it also
    // bypasses the "no effect" fallback — it's never second-guessed. null/blank
    // = no command override. The editor keeps these two mutually exclusive: a
    // numeric entry, or text that resolves to a known spell's cast-code, sets
    // OverrideAttackSpellId instead (so a spell typed by its code still gets
    // mana/cap gating); only text matching no spell sets this. See
    // MonsterEditDialogViewModel.ParseAttackOverride.
    public string? OverrideAttackCommand { get; init; }

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
