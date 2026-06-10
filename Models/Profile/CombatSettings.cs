namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Combat" settings — drives <see cref="Game.Combat.CombatManager"/>
/// (PR 9.A) target picking, weapon swap matrix, multi-attack spell gating, and
/// re-fire timing. Stored as the <c>"Combat"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// CombatManager swings; <see cref="Game.Combat.CombatStateTracker"/> (PR 9.0b)
/// asserts the <c>Combat</c> gate. The two read the same target list produced by
/// <see cref="Game.Combat.RoomEntityClassifier"/> + <c>Monsters.json</c>
/// <c>AttackPriority</c>. CombatSettings dictates HOW to dispatch the list;
/// AttackPriority dictates WHO is in the list and in what order. See
/// <c>docs/10-phase-9-automation-engines.md</c> § Cross-cut 2.
/// </para>
/// <para>
/// Defaults are conservative: <see cref="MasterAutoAttackEnabled"/> off,
/// <see cref="DoBackstab"/> off, <see cref="PoliteMode"/> off. New characters
/// don't auto-engage anything until the user opts in.
/// </para>
/// </remarks>
public sealed class CombatSettings
{
    // ----- Wire command ---------------------------------------------

    /// <summary>Wire command sent each round to swing — default <c>a</c>
    /// (the canonical MajorMUD attack alias). The master on/off for
    /// auto-attack lives on <c>GeneralSettings.AutoMode.AutoCombat</c>,
    /// shared with the Settings → General checkbox and the toolbar
    /// Toggle button.</summary>
    public string NormalAttackCommand { get; set; } = "a";

    /// <summary>Wire verb sent when we're swung the alternate weapon —
    /// some 2H alt weapons want <c>swing</c> while a 1H normal uses
    /// <c>a</c>. Default <c>a</c> so a single-weapon character doesn't
    /// have to configure both fields.</summary>
    public string AlternateAttackCommand { get; set; } = "a";

    // ----- Weapon slots ---------------------------------------------

    /// <summary>Primary weapon item ref (display name from game data).
    /// Null when not configured.</summary>
    public string? NormalWeapon { get; set; }

    /// <summary>Off-hand item ref paired with <see cref="NormalWeapon"/>.
    /// Null when not configured.</summary>
    public string? NormalOffHand { get; set; }

    /// <summary>Swap-target weapon item ref. Null when not configured.</summary>
    public string? AlternateWeapon { get; set; }

    /// <summary>Off-hand item ref paired with <see cref="AlternateWeapon"/>.
    /// Null when not configured.</summary>
    public string? AlternateOffHand { get; set; }

    /// <summary>Item ref equipped for the BS attempt round. Null when not
    /// configured.</summary>
    public string? BackstabWeapon { get; set; }

    /// <summary>Off-hand item ref paired with <see cref="BackstabWeapon"/>
    /// (often a shield). Null when not configured.</summary>
    public string? BackstabOffHand { get; set; }

    // ----- Backstab options -----------------------------------------

    /// <summary>Attempt backstab when entering a room with eligible targets.
    /// Default false — explicit opt-in even on stealth-capable classes (per
    /// the Phase 9 plan's safety rule).</summary>
    public bool DoBackstab { get; set; }

    /// <summary>Skip the BS attempt when the multi-attack room spell is
    /// firing this round. Default true.</summary>
    public bool SkipBackstabIfMultiAttack { get; set; } = true;

    /// <summary>Trigger flee behavior on a failed BS roll. Default false.</summary>
    public bool RunIfBackstabFails { get; set; }

    /// <summary>
    /// Combat-off override for stealth runners. When sprinting a walk-to
    /// route with combat OFF and AutoSneak ON (stealthing as much of the
    /// route as possible), a room holding a <c>SeeHidden</c> monster
    /// breaks sneak — running onward would drag and stack monsters across
    /// rooms, a lethal mess when solo. With this on, the engine force-clears
    /// every hostile in such a room (bypassing the Min/Max gate) so the
    /// route can resume sneaking. Default false — combat-off means
    /// combat-off unless the user opts in.
    /// </summary>
    public bool ClearHostilesWhenSeenHidden { get; set; }

    // ----- Targeting ------------------------------------------------

    /// <summary>Which monster in the priority-ranked list to swing at.</summary>
    public TargetOrder TargetOrder { get; set; } = TargetOrder.Normal;

    /// <summary>Re-fire mechanism for party / room coordination. See
    /// <see cref="AttackTiming"/> for the four modes.</summary>
    public AttackTiming AttackTiming { get; set; } = AttackTiming.Default;

    /// <summary>Player name to defer first swing to when
    /// <see cref="AttackTiming"/> is <see cref="AttackTiming.AttackAfter"/>.
    /// Null otherwise.</summary>
    public string? AttackAfterPlayerName { get; set; }

    /// <summary>Behavior when a non-party player is engaged with a monster we
    /// would otherwise target. Default <see cref="PoliteMode.Off"/> — attack
    /// regardless.</summary>
    public PoliteMode PoliteMode { get; set; } = PoliteMode.Off;

    // ----- Room-skip thresholds -------------------------------------

    /// <summary>Skip the room if fewer than this many hostiles are present.
    /// Range 0–20. Default 0 (no minimum).</summary>
    public int MinMonstersInRoom { get; set; }

    /// <summary>Skip the room if more than this many hostiles are present.
    /// Range 1–20 (rooms cap at 20 NPCs). Default 20.</summary>
    public int MaxMonstersInRoom { get; set; } = 20;

    /// <summary>Rooms to flee before re-evaluating. Range 1–100. Default 3.</summary>
    public int RunDistance { get; set; } = 3;

    // ----- Failure tracking -----------------------------------------

    /// <summary>How many consecutive "no effect" lines move a target to the
    /// room-scoped unhittable set. Default 1.</summary>
    public int NoEffectFailureThreshold { get; set; } = 1;

    // ----- Spell combat ---------------------------------------------

    /// <summary>How <see cref="CombatSpellSlot.MinManaPerCast"/> values are
    /// read across all five spell slots below. Default
    /// <see cref="ThresholdMode.Percentage"/>.</summary>
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;

    /// <summary>Multi-target room spell (e.g. <c>cast star</c>).</summary>
    public CombatSpellSlot MultiAttackSpell { get; set; } = new();

    /// <summary>Area-effect debuff (e.g. blind-room, curse-room).</summary>
    public CombatSpellSlot AreaDebuffSpell { get; set; } = new();

    /// <summary>Single-target debuff (e.g. weakness, slow). Ignores
    /// <see cref="CombatSpellSlot.MinEnemies"/>.</summary>
    public CombatSpellSlot SingleTargetDebuffSpell { get; set; } = new();

    /// <summary>Primary single-target damage spell. Ignores
    /// <see cref="CombatSpellSlot.MinEnemies"/>.</summary>
    public CombatSpellSlot NormalAttackSpell { get; set; } = new();

    /// <summary>Fallback single-target damage spell — used when the normal
    /// pick can't fire. Ignores <see cref="CombatSpellSlot.MinEnemies"/>.</summary>
    public CombatSpellSlot AlternateAttackSpell { get; set; } = new();

    // ----- Display --------------------------------------------------

    /// <summary>Append the per-round damage roll-up to the terminal canvas
    /// after each round. Default false.</summary>
    public bool ShowCombatRoundTotals { get; set; }
}

/// <summary>
/// One spell-row entry in the Combat tab's Spell-combat section. Five of
/// these live on <see cref="CombatSettings"/> (multi-attack / AOE debuff /
/// single-target debuff / normal attack spell / alternate attack spell).
/// Single-target rows ignore <see cref="MinEnemies"/>; the engine documents
/// which rows honor it.
/// </summary>
public sealed class CombatSpellSlot
{
    /// <summary>Spell name as it appears in game data. Null = slot unused.</summary>
    public string? SpellName { get; set; }

    /// <summary>Only cast when at least this many hostiles are in the room.
    /// 0 = no minimum. Ignored for single-target rows.</summary>
    public int MinEnemies { get; set; }

    /// <summary>Cap on back-to-back fires within one room / engagement.
    /// 0 = unlimited.</summary>
    public int MaxCastsPerRoom { get; set; }

    /// <summary>Minimum mana required to cast — interpreted per
    /// <see cref="CombatSettings.SpellManaThresholdMode"/>.</summary>
    public int MinManaPerCast { get; set; }
}

/// <summary>
/// Which monster in the priority-ranked target list to swing at.
/// <see cref="Normal"/> picks the highest-priority entry; <see cref="Reverse"/>
/// picks the lowest. Independent of <see cref="AttackTiming"/>.
/// </summary>
public enum TargetOrder
{
    Normal,
    Reverse,
}

/// <summary>
/// Re-fire mechanism for party / room coordination. Wraps the standard
/// MudProxy <c>PartyAttackOrder</c> behavior plus a FujinTerm-original
/// <see cref="AttackLastRoom"/> mode that drops the party-membership filter.
/// </summary>
public enum AttackTiming
{
    /// <summary>Own cadence; no party coordination.</summary>
    Default,

    /// <summary>Re-fire the most recent attack command on every party member's
    /// "moves to attack X" announcement, ensuring your announce is the most
    /// recent vs the target. Non-party announcements ignored.</summary>
    AttackLastParty,

    /// <summary>Re-fire on every "moves to attack X" announcement regardless
    /// of party membership — guarantees your announce is last among
    /// everyone in the room, party or not.</summary>
    AttackLastRoom,

    /// <summary>Defer first swing until <see cref="CombatSettings.AttackAfterPlayerName"/>
    /// announces; mirror their target; re-fire on their subsequent
    /// announcements against the same target.</summary>
    AttackAfter,
}

/// <summary>
/// Behavior when a non-party player is engaged with a monster we would
/// otherwise target. FujinTerm-original — MudProxy has no equivalent.
/// </summary>
public enum PoliteMode
{
    /// <summary>Engage regardless of who else is fighting. Default.</summary>
    Off,

    /// <summary>Pause until the other player disengages, then engage.</summary>
    WaitForOthers,

    /// <summary>Skip the entire room — walker continues to next room.</summary>
    SkipRoom,

    /// <summary>Pick a different monster in the same room that no non-party
    /// player is fighting.</summary>
    AttackDifferent,
}
