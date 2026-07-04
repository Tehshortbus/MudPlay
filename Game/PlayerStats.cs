using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Snapshot of the in-game stat screen. Updated by StatParser; consumed by
// feature engines that need data the live statline prompt doesn't carry —
// most prominently RemoteCommandManager.LivesProvider (drives the @suicide
// hard-block) and the Character Workshop.
//
// Distinct from PlayerState: PlayerState tracks the LIVE values that flow
// through every prompt (HP / MA / status); PlayerStats is the periodic
// snapshot the user gets by typing stat. HP / Mana overlap between the two
// but with different freshness — PromptParser updates per-prompt, StatParser
// updates per-stat-screen.
//
// Single-writer invariant: every field is tagged with StatParser as the
// owner. Consumers subscribe to PropertyChanged or read directly; never write.
public sealed partial class PlayerStats : ObservableObject
{
    // ----- Identity ------------------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _name = string.Empty;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _race = string.Empty;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private string _class = string.Empty;

    // ----- Progression ---------------------------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _level;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _exp;
    // Lives remaining — half of the Lives/CP: N/M field.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _lives;
    // Character points available to spend — the other half of Lives/CP: N/M.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _cp;

    // Exp still needed to hit the next level — the first N in the exp
    // command's output line ("Exp needed for next level: N (M) [P%]").
    // Shrinks as XP comes in; clamped to 0 server-side when the character
    // already has enough exp to level (won't go negative even if they've
    // accumulated way past the threshold).
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _expToNext;
    // Total exp required to span the current level — the parenthesized (M)
    // in the exp line. NOT the cumulative threshold; the DELTA between the
    // current-level floor and the next-level floor. Constant for the current
    // level; the running progress is M - ExpToNext.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _levelExpSpan;
    // Progress through the current level as a percent (0–100) — the [P%] on
    // the exp line. Computed server-side as
    // (LevelExpSpan - ExpToNext) / LevelExpSpan × 100.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _levelPercent;

    // ----- Vitals (snapshot, not live) -----------------------------------
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _hits;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxHits;
    // Mana — present for caster classes. Mutually exclusive with Kai; both
    // stay 0 for Warriors.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _mana;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxMana;
    // Kai — present for Mystic / Monk-family classes. Mutually exclusive with Mana.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _kai;
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _maxKai;
    // Current armour class — left half of Armour Class: N/M (current is
    // reduced by damage to gear, etc.).
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
    // Spellcasting — present only on caster classes; 0 for warriors / thieves.
    [ObservableProperty] [field: Owner(typeof(StatParser))] private int _spellcasting;
}
