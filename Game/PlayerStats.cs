using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Snapshot of the in-game <c>stat</c> screen. Updated by
/// <see cref="StatParser"/>; consumed by feature engines that need
/// data the live statline prompt doesn't carry — most prominently
/// <see cref="Remote.RemoteCommandManager.LivesProvider"/> (drives
/// the <c>@suicide</c> hard-block) and the future Phase 9 Character
/// Workshop.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="PlayerState"/>: <see cref="PlayerState"/>
/// tracks the LIVE values that flow through every prompt
/// (HP / MA / status); <see cref="PlayerStats"/> is the periodic
/// snapshot the user gets by typing <c>stat</c>. HP / Mana overlap
/// between the two but with different freshness — PromptParser
/// updates per-prompt, StatParser updates per-stat-screen.
/// </para>
/// <para>
/// Single-writer invariant: every field is tagged
/// <see cref="OwnerAttribute"/> with <see cref="StatParser"/> as the
/// owner. Consumers subscribe to <see cref="ObservableObject.PropertyChanged"/>
/// or read directly; never write.
/// </para>
/// </remarks>
public sealed partial class PlayerStats : ObservableObject
{
    // ----- Identity ------------------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _name = string.Empty;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _race = string.Empty;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _class = string.Empty;

    // ----- Progression ---------------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _level;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _exp;
    /// <summary>Lives remaining — half of the <c>Lives/CP: N/M</c> field.</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _lives;
    /// <summary>Character points available to spend — the other half of <c>Lives/CP: N/M</c>.</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _cp;

    /// <summary>
    /// Exp still needed to hit the next level — the first <c>N</c>
    /// in the <c>exp</c> command's output line
    /// (<c>"Exp needed for next level: N (M) [P%]"</c>). Shrinks as
    /// XP comes in; <b>clamped to 0</b> server-side when the
    /// character already has enough exp to level (won't go
    /// negative even if they've accumulated way past the
    /// threshold).
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _expToNext;
    /// <summary>
    /// Total exp required to span the current level — the
    /// parenthesized <c>(M)</c> in the exp line. NOT the cumulative
    /// threshold; the DELTA between the current-level floor and
    /// the next-level floor. Constant for the current level; the
    /// running progress is <c>M - ExpToNext</c>.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _levelExpSpan;
    /// <summary>
    /// Progress through the current level as a percent (0–100) —
    /// the <c>[P%]</c> on the exp line. Computed server-side as
    /// <c>(LevelExpSpan - ExpToNext) / LevelExpSpan × 100</c>.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _levelPercent;

    // ----- Vitals (snapshot, not live) -----------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _hits;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxHits;
    /// <summary>Mana — present for caster classes. Mutually exclusive with <see cref="Kai"/>; both stay 0 for Warriors.</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _mana;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxMana;
    /// <summary>Kai — present for Mystic / Monk-family classes. Mutually exclusive with <see cref="Mana"/>.</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _kai;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxKai;
    /// <summary>Current armour class — left half of <c>Armour Class: N/M</c> (current is reduced by damage to gear, etc.).</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _armourClass;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxArmourClass;

    // ----- Primary stats -------------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _strength;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _intellect;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _willpower;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _agility;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _health;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _charm;

    // ----- Secondary skills ---------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _perception;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _stealth;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _thievery;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _traps;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _picklocks;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _tracking;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _martialArts;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _magicRes;
    /// <summary>Spellcasting — present only on caster classes; 0 for warriors / thieves.</summary>
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _spellcasting;
}
