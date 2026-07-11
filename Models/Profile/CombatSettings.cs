namespace FujinTerm.Models.Profile;

// Per-character "Combat" settings — drives Game.Combat.CombatManager target
// picking, weapon swap matrix, multi-attack spell gating, and re-fire timing.
// Stored as the "Combat" entry in CharacterProfile.Settings.
//
// CombatManager swings; Game.Combat.CombatStateTracker asserts the Combat gate.
// The two read the same target list produced by Game.Combat.RoomEntityClassifier
// + Monsters.json AttackPriority. CombatSettings dictates HOW to dispatch the
// list; AttackPriority dictates WHO is in the list and in what order.
//
// Defaults are conservative: DoBackstab off, PoliteMode off. New characters
// don't auto-engage anything until the user opts in.
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

    // Combat-off override for stealth runners. When sprinting a walk-to route
    // with combat OFF and AutoSneak ON (stealthing as much of the route as
    // possible), a room holding a SeeHidden monster breaks sneak — running
    // onward would drag and stack monsters across rooms, a lethal mess when
    // solo. With this on, the engine force-clears every hostile in such a room
    // (bypassing the Min/Max gate) so the route can resume sneaking. Default
    // false — combat-off means combat-off unless the user opts in.
    public bool ClearHostilesWhenSeenHidden { get; set; }

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
    public int MinMonstersInRoom { get; set; }

    // Skip the room if more than this many hostiles are present. Range 1–20
    // (rooms cap at 20 NPCs). Default 20.
    public int MaxMonstersInRoom { get; set; } = 20;

    // Rooms to flee before re-evaluating. Range 1–100. Default 2.
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

    // How CombatSpellSlot.MinManaPerCast values are read across all five spell
    // slots below. Default Percentage.
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;

    // Multi-target room spell (e.g. cast star).
    public CombatSpellSlot MultiAttackSpell { get; set; } = new();

    // Area-effect debuff (e.g. blind-room, curse-room).
    public CombatSpellSlot AreaDebuffSpell { get; set; } = new();

    // Single-target debuff (e.g. weakness, slow). Ignores MinEnemies.
    public CombatSpellSlot SingleTargetDebuffSpell { get; set; } = new();

    // Primary single-target damage spell. Ignores MinEnemies.
    public CombatSpellSlot NormalAttackSpell { get; set; } = new();

    // Fallback single-target damage spell — used when the normal pick can't
    // fire. Ignores MinEnemies.
    public CombatSpellSlot AlternateAttackSpell { get; set; } = new();

    // ----- Display --------------------------------------------------

    // Append the per-round damage roll-up to the terminal canvas after each
    // round. Default false.
    public bool ShowCombatRoundTotals { get; set; }
}

// One spell-row entry in the Combat tab's Spell-combat section. Five of these
// live on CombatSettings (multi-attack / AOE debuff / single-target debuff /
// normal attack spell / alternate attack spell). Single-target rows ignore
// MinEnemies; the engine documents which rows honor it.
public sealed class CombatSpellSlot
{
    // Spell name as it appears in game data. Null = slot unused.
    public string? SpellName { get; set; }

    // Only cast when at least this many hostiles are in the room. 0 = no
    // minimum. Ignored for single-target rows.
    public int MinEnemies { get; set; }

    // Cap on back-to-back fires within one room / engagement. null (blank in the
    // editor) = no limit — cast as often as the slot's other gates allow; 0 =
    // never cast (an explicit off switch that keeps the spell name configured);
    // N > 0 = at most N casts per room.
    public int? MaxCastsPerRoom { get; set; }

    // Minimum mana required to cast — interpreted per
    // CombatSettings.SpellManaThresholdMode.
    public int MinManaPerCast { get; set; }
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
