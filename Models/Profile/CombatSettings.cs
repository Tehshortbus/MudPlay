using MudPlay.Services;

namespace MudPlay.Models.Profile;

// Per-character "Combat" settings — drives Game.Combat.CombatManager target
// picking, weapon swap matrix, multi-attack spell gating, and re-fire timing.
// Stored as the "Combat" entry in CharacterProfile.Settings.
//
// CombatManager swings; Game.Combat.CombatStateTracker asserts the Combat gate.
// The two read the same target list produced by Game.Combat.RoomEntityClassifier
// + Monsters.json AttackPriority. CombatSettings dictates HOW to dispatch the
// list; AttackPriority dictates WHO is in the list and in what order.
//
// Defaults are conservative: DoBackstab off, PoliteMode off.
public sealed class CombatSettings
{
    // ----- Wire command ---------------------------------------------

    // Wire command sent each round to swing — default a (the canonical MajorMUD
    // attack alias). The master on/off for auto-attack lives on
    // GeneralSettings.AutoMode.AutoCombat, shared with the Settings → General
    // checkbox and the toolbar Toggle button.
    public string NormalAttackCommand { get; set; } = "a";

    // Wire verb sent when we're swung the alternate weapon — some 2H alt weapons
    // want swing while a 1H normal uses a. Default a so a single-weapon
    // character doesn't have to configure both fields.
    public string AlternateAttackCommand { get; set; } = "a";

    // ----- Combat action order --------------------------------------

    // Which action the engine prefers as the round's one combat action.
    // SpellsFirst casts the attack-spell cascade (multi → normal → alternate)
    // when one applies and swings only when none can fire this round;
    // PhysicalFirst swings the weapon and reverts to that cascade only when the
    // weapon path is proven ineffective against the target (the normal weapon
    // can't damage it and there's no working alternate).
    // Game.Combat.CombatSpellChooser reads it. Two things sit OUTSIDE this
    // choice: the backstab opener always fires first when enabled + eligible
    // (see DoBackstab), and debuffs are in-between casts scheduled by
    // CastingDirector against buffs / heals via the Spells tab's priority list —
    // so they land alongside the round's action regardless of this setting.
    // Default SpellsFirst (the previously hard-coded spell-before-swing order).
    public CombatActionOrder ActionOrder { get; set; } = CombatActionOrder.SpellsFirst;

    // ----- Weapon slots ---------------------------------------------

    // Primary weapon item ref (display name from game data). Null when not
    // configured.
    public string? NormalWeapon { get; set; }

    // Off-hand item ref paired with NormalWeapon. Null when not configured.
    public string? NormalOffHand { get; set; }

    // Swap-target weapon item ref. Null when not configured.
    public string? AlternateWeapon { get; set; }

    // Off-hand item ref paired with AlternateWeapon. Null when not configured.
    public string? AlternateOffHand { get; set; }

    // Item ref equipped for the BS attempt round. Null when not configured.
    public string? BackstabWeapon { get; set; }

    // Off-hand item ref paired with BackstabWeapon (often a shield). Null when
    // not configured.
    public string? BackstabOffHand { get; set; }

    // ----- Backstab options -----------------------------------------

    // Attempt backstab when entering a room with eligible targets. Default false
    // — explicit opt-in even on stealth-capable classes (a safety rule).
    public bool DoBackstab { get; set; }

    // Skip the BS attempt when the multi-attack room spell is firing this round.
    // Default true.
    public bool SkipBackstabIfMultiAttack { get; set; } = true;

    // Trigger flee behavior on a failed BS roll. Default false.
    public bool RunIfBackstabFails { get; set; }

    // Hit-and-run backstabbing: when the room held two or more hostiles as we snuck
    // in, run once the backstab lands, so the loop / walk re-sneaks and comes back
    // for another one instead of slugging it out with the pack. Default false.
    public bool HitAndRunTactics { get; set; }

    // How many runs one hit-and-run backstab may take, the first included: a monster
    // that chases us into the retreat room is run from again until this is spent, then
    // we stand and fight instead of hunting for an empty room to re-sneak in.
    [SettingRange(1, 20)]
    public int HitAndRunMaxRuns { get; set; } = 3;

    // Combat-off override for stealth runners. When sprinting a walk-to route
    // with combat OFF and AutoSneak ON (stealthing as much of the route as
    // possible), a room holding a SeeHidden monster breaks sneak — running
    // onward would drag and stack monsters across rooms, a lethal mess when
    // solo. With this on, the engine force-clears every hostile in such a room
    // (bypassing the Min/Max gate) so the route can resume sneaking. Default
    // false — combat-off means combat-off unless the user opts in.
    public bool ClearHostilesWhenSeenHidden { get; set; }

    // When the see-hidden clear above applies: alone, in a party, or both. A lone
    // runner has to deal with what a see-hidden monster woke; a party can afford to
    // keep running and may not want the stop. Both default on, which is the
    // behaviour before the two were split (user, 2026-10-07).
    public bool SeenHiddenClearWhileSolo { get; set; } = true;
    public bool SeenHiddenClearWhileInParty { get; set; } = true;

    // The same stealth run, when a sneaked move fails (the loud entry line, or a room
    // shown without "Sneaking..."): a room inside the Min/Max monster window is
    // cleared of every engageable hostile before the route re-sneaks and walks on;
    // one outside it is walked through unsneaked as usual. Default false.
    public bool ClearHostilesWhenSneakFails { get; set; }

    // ----- Targeting ------------------------------------------------

    // Which monster in our own priority-ranked list to swing at when
    // TargetPriority is Default.
    public TargetOrder TargetOrder { get; set; } = TargetOrder.Normal;

    // WHO to target in a party — the coordination half of combat (paired with
    // AttackTiming, which owns WHEN). Default follows our own game data
    // (TargetOrder + per-monster AttackPriority); the follow modes mirror the
    // party leader / a named member's announced monster.
    public TargetPriority TargetPriority { get; set; } = TargetPriority.Default;

    // Party member whose target we mirror when TargetPriority is FollowMember.
    // Null otherwise. Separate from AttackAfterPlayerName so the "who" and
    // "when" knobs can name different players.
    public string? TargetPriorityMemberName { get; set; }

    // Attack Order — WHEN to (re-)announce our swing relative to others, to
    // control initiative order. Pure timing: it re-fires our own current target
    // and never switches the monster (that's TargetPriority's job). See
    // AttackTiming for the four modes.
    public AttackTiming AttackTiming { get; set; } = AttackTiming.Default;

    // Player name whose attack announce triggers our re-fire when AttackTiming
    // is AttackAfter. Null otherwise.
    public string? AttackAfterPlayerName { get; set; }

    // Behavior when a non-party player is engaged with a monster we would
    // otherwise target. Default Off — attack regardless.
    public PoliteMode PoliteMode { get; set; } = PoliteMode.Off;

    // ----- Room-skip thresholds -------------------------------------

    // Skip the room if fewer than this many hostiles are present. Range 0–20.
    // Default 0 (no minimum).
    [SettingRange(0, 20)]
    public int MinMonstersInRoom { get; set; }

    // Skip the room if more than this many hostiles are present. Range 1–20
    // (rooms cap at 20 NPCs). Default 20.
    [SettingRange(1, 20)]
    public int MaxMonstersInRoom { get; set; } = 20;

    // When a room was engaged via a room spell (multi-attack AoE), finish it to
    // empty instead of moving on once the live hostile count drops below
    // MinMonstersInRoom — so an AoE that leaves tanky survivors (mixed HP pools)
    // gets cleaned up rather than dragging the leftovers. Bypasses only the MIN
    // floor, and only while we've actually room-spelled this room; the MAX cap
    // and every other gate still apply. Default false = move on from leftovers.
    public bool KillAllEngaged { get; set; }

    // Rooms to flee before re-evaluating. Range 1–100. Default 2.
    [SettingRange(1, 100)]
    public int RunDistance { get; set; } = 2;

    // Direction strategy when fleeing. Forward continues along the active walker
    // path (away from where we entered); Backward retraces the steps we just
    // came from. Default Backward — safer because we know what rooms we passed
    // through. Consumed by Game.Health.HealthManager's flee path alongside
    // RunDistance.
    public RunDirection RunDirection { get; set; } = RunDirection.Backward;

    // When true (default), HealthManager sends break before the first flee move
    // so the auto-attack disengages and the move can land cleanly. When false
    // the flee starts mid-fight and the server may reject the first move because
    // we're still engaged — fast option for users who'd rather take the chance
    // than waste a round on break.
    public bool BreakBeforeFleeing { get; set; } = true;

    // ----- Spell combat ---------------------------------------------

    // How CombatSpellSlot.MinManaPerCast values are read across all seven spell
    // slots below. Default Percentage.
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;

    // ----- Round-cycle action order (ActionOrder = CustomRoundCycle) -----

    // Rounds to spend in the cycle's PHYSICAL phase before switching to the
    // spell phase. 0 = physical forever (the physical phase never ends, so the
    // cycle never reaches the spell phase — degenerates to PhysicalFirst-style
    // behavior). Only consulted when ActionOrder is CustomRoundCycle. Default 1.
    [SettingRange(0, 999)]
    public int CycleRoundsPhysical { get; set; } = 1;

    // Rounds to spend in the cycle's SPELL phase before switching back to
    // physical. 0 = spells for the rest of the fight once reached — "spells
    // till death" (the phase never ends, so the cycle never returns to
    // physical). Only consulted when ActionOrder is CustomRoundCycle. Default 1.
    [SettingRange(0, 999)]
    public int CycleRoundsSpell { get; set; } = 1;

    // Which phase the cycle opens on for a fresh target. False (default) opens
    // physical (e.g. "physical 2 rounds, then spells"); true opens spell (e.g.
    // "spell 1 round, then physical till death"). Only consulted when
    // ActionOrder is CustomRoundCycle.
    public bool CycleStartOnSpell { get; set; }

    // Multi-target room spell (e.g. cast star).
    public CombatSpellSlot MultiAttackSpell { get; set; } = new();

    // Second room spell, used as MultiAttackSpell's successor rather than a rival:
    // once the first slot's per-room cast cap is spent or mana has fallen under its
    // floor, this one carries the room. Lets a big opener soften the pack once or
    // twice and a cheap follow-up finish it. Off by default; the room's enemy-count
    // trigger is MultiAttackSpell.MinEnemies for both, so this slot's own MinEnemies
    // is never read.
    public bool MultiAttack2Enabled { get; set; }
    public CombatSpellSlot MultiAttack2Spell { get; set; } = new();

    // Area-effect debuff (e.g. blind-room, curse-room).
    public CombatSpellSlot AreaDebuffSpell { get; set; } = new();

    // When true, the AoE debuff is only attempted on room ENTRY — before any combat
    // round has been seen in the room — and is abandoned for the room once the fight
    // is underway. A debuff that lands on the first round softens the whole pack; one
    // that lands on round 2+ (because a higher-priority buff/heal took the between-round
    // slot on entry) is mostly wasted mana on a half-dead room. Default false: keep
    // retrying each open between-round slot until it lands once per room.
    public bool AreaDebuffFirstRoundOnly { get; set; }

    // Single-target debuff (e.g. weakness, slow). Ignores MinEnemies.
    public CombatSpellSlot SingleTargetDebuffSpell { get; set; } = new();

    // Primary single-target damage spell. Ignores MinEnemies.
    public CombatSpellSlot NormalAttackSpell { get; set; } = new();

    // Fallback single-target damage spell — used when the normal pick can't
    // fire. Ignores MinEnemies.
    public CombatSpellSlot AlternateAttackSpell { get; set; } = new();

    // ----- Drain (life-steal) spell ---------------------------------

    // Single-target life-drain spell (e.g. vamp / dtch / nebo) — the damage it
    // deals also heals the caster, so it's used like an emergency heal that also
    // attacks. It takes the place of the round's attack (auto-repeats like any
    // attack spell), gated by CombatSpellSlot.MaxCastsPerRoom (per target) +
    // MinManaPerCast, and only fires when HP is at/under DrainHpTrigger and the
    // target is drain-eligible (living AND not undead — a drain can't affect
    // NonLiving / Undead). Ignores MinEnemies.
    public CombatSpellSlot DrainSpell { get; set; } = new();

    // HP percentage at/under which DrainSpell overrides the round's normal action
    // (0–100). At or below this %, the drain is cast instead of the attack;
    // above it, the engine reverts to the normal pick. 0 = never (drain off even
    // when a spell is configured).
    [SettingRange(0, 100)]
    public int DrainHpTrigger { get; set; } = 50;

    // When true, the drain overrides the room AoE (multi-attack) too. Default
    // false — with enough enemies to trigger the AoE it's usually more dangerous
    // to drop rooming for a single-target drain, so by default the AoE wins and
    // the drain still overrides only the single-target / physical action.
    public bool DrainsOverrideAoe { get; set; }

    // ----- Undead attack spell -----------------------------------------

    // Single-target damage spell cast at an undead target (game data's Undead
    // column) in place of the normal and alternate attack spells, with its own
    // MaxCastsPerRoom (per target), MinManaPerCast and FireRegardlessOfMana. A round
    // it can't fire in (mana, casts spent, a "no effect") goes to the normal and
    // alternate spells as usual. Ignores MinEnemies.
    public CombatSpellSlot UndeadAttackSpell { get; set; } = new();

    // ----- Display --------------------------------------------------

    // Append the per-round damage roll-up to the terminal canvas after each
    // round. Default false.
    public bool ShowCombatRoundTotals { get; set; }

    // With ShowCombatRoundTotals, which rows the table prints: ours, our party's, other
    // players', the monsters'. With none ticked no table prints. Read through
    // ShowsRoundTotalsRow.
    public bool? ShowCombatRoundTotalsSelf { get; set; }
    public bool? ShowCombatRoundTotalsParty { get; set; }
    public bool? ShowCombatRoundTotalsPlayers { get; set; }
    public bool? ShowCombatRoundTotalsMonsters { get; set; }

    // How same-named monsters show in the round table — either / or: stacked on one row
    // labelled with how many there were ("muckworm x3", the default), or a row each
    // ("muckworm #1", "#2"…).
    public bool ShowCombatRoundTotalsEachMonster { get; set; }

    // Cap a monster's damage taken (and its dealer's damage dealt) at the HP it had left,
    // so a killing blow counts only what killed it. Off (the default), every line counts
    // the number the game printed.
    public bool CapRoundTotalsAtMonsterHp { get; set; }

    // A box missing from settings saved before the boxes existed follows
    // ShowCombatRoundTotals: a table that was on keeps every row, one that was off
    // stays off (user, 2026-09-29).
    public bool ShowsRoundTotalsRow(bool? box) => box ?? ShowCombatRoundTotals;
}

// One spell-row entry in the Combat tab's Spell-combat section (multi-attack 1 and
// 2 / AOE debuff / single-target debuff / normal attack spell / alternate attack
// spell / drain). Single-target rows ignore MinEnemies; the engine documents which
// rows honor it.
public sealed class CombatSpellSlot
{
    // Spell name as it appears in game data. Null = slot unused.
    public string? SpellName { get; set; }

    // Only cast when at least this many hostiles are in the room. 0 = no
    // minimum. Ignored for single-target rows.
    [SettingRange(0, 20)]
    public int MinEnemies { get; set; }

    // Cap on back-to-back fires within one room / engagement. null (blank in the
    // editor) = no limit — cast as often as the slot's other gates allow; 0 =
    // never cast (an explicit off switch that keeps the spell name configured);
    // N > 0 = at most N casts per room.
    [SettingRange(0, 100)]
    public int? MaxCastsPerRoom { get; set; }

    // Minimum mana required to cast — interpreted per
    // CombatSettings.SpellManaThresholdMode.
    [SettingRange(0, 100_000)]
    public int MinManaPerCast { get; set; }

    // "Fire regardless of current mana": while the character is in a party this
    // attack spell is cast whenever its turn comes, whatever the mana (neither
    // MinManaPerCast nor its cost is asked), so a monster only it can hurt is
    // engaged (a party kill pays exp only to those engaged) instead of walked past
    // for a mana stall. Solo it does nothing.
    // Offered on the four attack-spell rows only; the debuff and drain rows never
    // read it.
    public bool FireRegardlessOfMana { get; set; }

    // Deep copy — combat spell profiles snapshot these slots and must never share
    // a reference with the live CombatSettings (an edit to one would leak to the
    // other).
    public CombatSpellSlot Clone() => new()
    {
        SpellName = SpellName,
        MinEnemies = MinEnemies,
        MaxCastsPerRoom = MaxCastsPerRoom,
        MinManaPerCast = MinManaPerCast,
        FireRegardlessOfMana = FireRegardlessOfMana,
    };
}

// Which action the auto-attack engine prefers as the round's one combat action.
// The backstab opener and debuffs sit outside this choice — the opener always
// fires first when enabled, and debuffs are in-between casts — so it governs
// only the main action of the round.
public enum CombatActionOrder
{
    // Cast the attack-spell cascade (multi → normal → alternate) when one
    // applies; swing the weapon only when no attack spell can fire. Default.
    SpellsFirst,

    // Swing the weapon; revert to the attack-spell cascade only when the weapon
    // path is proven ineffective against the target (normal can't damage it and
    // there's no working alternate).
    PhysicalFirst,

    // Alternate the round's action every round, starting on the spell: cast an
    // attack spell one round, swing the next, cast, swing… The two alternating
    // modes each behave like the fixed order matching THIS round's phase — a
    // spell-phase round is SpellsFirst (falls to the swing when no spell can
    // fire), a physical-phase round is PhysicalFirst (falls to the cascade only
    // when the weapon is proven ineffective) — so a phase that can't act still
    // does something rather than stalling the round. Unlike the fixed orders the
    // engine cannot lean on the server's auto-repeat here: the desired action
    // flips each round, so it re-issues a command every round.
    AlternateSpellPhysical,

    // Same per-round alternation, starting on the swing: physical, spell,
    // physical…
    AlternatePhysicalSpell,

    // Round-scheduled phases of configurable length instead of a fixed 1-round
    // flip: spend CombatSettings.CycleRoundsPhysical rounds in the physical
    // phase, then CombatSettings.CycleRoundsSpell rounds in the spell phase
    // (CycleStartOnSpell picks which phase opens), repeating for as long as
    // both lengths are positive. A 0 length makes ITS phase permanent once
    // reached — e.g. CycleRoundsSpell = 0 is "physical for N rounds, then
    // spells for the rest of the fight" ("spells till death"). Each phase
    // behaves like the matching fixed order while it holds (a spell-phase
    // round falls back to the weapon when nothing can fire; WeaponIneffective
    // still overrides into the spell cascade regardless of phase, the same
    // safety valve PhysicalFirst gets). Unlike the two fixed Alternate* modes
    // above, a same-phase round does NOT re-issue a command — it leans on the
    // server's own auto-repeat / the existing spell-heartbeat dedup exactly
    // like SpellsFirst / PhysicalFirst do; only a genuine phase boundary forces
    // a fresh dispatch.
    CustomRoundCycle,
}

// Direction strategy for the auto-flee path.
public enum RunDirection
{
    // Retrace the path we just walked in on. Default — safer because we know
    // what rooms we passed through.
    Backward,

    // Continue along the active walker path away from where we came in. Faster
    // but moves into unscouted rooms.
    Forward,
}

// Which monster in our own priority-ranked target list to swing at when
// CombatSettings.TargetPriority is Default. Normal picks the highest-priority
// entry; Reverse picks the lowest. Independent of AttackTiming.
public enum TargetOrder
{
    Normal,
    Reverse,
}

// WHO to target in a party — the coordination half of combat. Pairs with
// AttackTiming (which owns WHEN to announce): together they decide the who and
// when of every round. The follow modes are reactive — they learn the leader /
// member's target from their "moves to attack X" announce. If our configured
// weapons + attack spells can't hit the followed target (proven un-actionable
// by game data), we fall back to our own next actionable target via the
// standard combat-fail path.
public enum TargetPriority
{
    // Pick our own target from game data (CombatSettings.TargetOrder +
    // per-monster AttackPriority). No party mirroring.
    Default,

    // Mirror the party leader's announced target (Game.PartyState.LeaderName).
    FollowLeader,

    // Mirror the announced target of the member named in
    // CombatSettings.TargetPriorityMemberName.
    FollowMember,
}

// Attack Order — WHEN to (re-)announce our swing to control initiative order.
// Pure timing: re-fires our own current target on someone else's announce so
// our announce lands last; it never switches the monster (TargetPriority owns
// the "who"). Covers the usual party attack-order timing modes plus an
// AttackLastRoom mode that drops the party-membership filter.
public enum AttackTiming
{
    // Own cadence; no re-fire coordination.
    Default,

    // Re-fire our current target on every party member's "moves to attack X"
    // announcement, keeping our announce most recent. Non-party announcements
    // ignored.
    AttackLastParty,

    // Re-fire our current target on every "moves to attack X" announcement
    // regardless of party membership — our announce stays last among everyone
    // in the room, party or not.
    AttackLastRoom,

    // Re-fire our current target only when
    // CombatSettings.AttackAfterPlayerName announces, keeping our announce
    // immediately after theirs.
    AttackAfter,

    // The inverse of attack-last: hold our own pick on room entry, then commit
    // ONCE — right after the FIRST party member's announce we see — and do NOT
    // re-fire when anyone else announces after us. So we slot in behind the first
    // mover instead of chasing the last slot. Only functional in a party of 3+;
    // in a party of 2 or fewer it behaves exactly like Default (immediate own
    // cadence, no hold, no re-fire).
    AttackNotLast,
}

// Behavior when a non-party player is engaged with a monster we would otherwise
// target.
public enum PoliteMode
{
    // Engage regardless of who else is fighting. Default.
    Off,

    // Pause until the other player disengages, then engage.
    WaitForOthers,

    // Skip the entire room — walker continues to next room.
    SkipRoom,

    // Pick a different monster in the same room that no non-party player is
    // fighting.
    AttackDifferent,
}
