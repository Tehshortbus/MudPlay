using System.Globalization;
using FujinTerm.Game.GameData;

namespace FujinTerm.Game.Spells;

// Human-readable, level-scaled summary of a spell's magnitude and per-level
// growth — the Game Data Browser's spell-detail "LVL Cap" / "LVL Increases"
// block. Turns the raw per-level scaling columns (MinInc / MinIncLVLs / MaxInc /
// …) into a single readable formula ("Max: 24+(2*lvl)") plus the at-cap range
// ("18 to 68"), so the player reads what a spell does at its ceiling and how it
// gets there rather than a wall of bare scaling numbers.
//
// The magnitude range is the spell's per-cast intrinsic value
// (SpellCalculator.AffectMagnitude), NOT the per-round figure from
// SpellCalculator.MinDamage / MaxDamage: those fold in the 1000/EnergyCost
// multi-fire multiplier, which would inflate a "what one cast does" display for
// low-energy spells. (For the common 1000-energy spell the two coincide.) All
// scaling math routes through SpellCalculator so the numbers match the Spell
// Book and CastingDirector everywhere.
public static class SpellGrowthFormatter
{
    // Damage-bearing ability codes whose stored Min/MaxBase form the spell's
    // damage range — Damage (1), DrainLife (8), Damage(-MR) (17) — plus Heal
    // (18). Mirrors SpellEffectFormatter's damage/heal split; kept local so the
    // formatter is self-contained.
    private static readonly int[] _damageCodes = { 1, 8, 17 };
    private const int HealCode = 18;
    private const int NonMagicalSpellCode = 144;

    // The level the at-cap figures are evaluated at: the spell's cap when it has
    // one, otherwise its obtain level (floored to 1 so an unleveled spell still
    // yields its base value rather than scaling to 0).
    public static int DisplayLevel(in SpellFormulaInput spell)
        => spell.Cap > 0 ? spell.Cap : Math.Max(spell.ReqLevel, 1);

    // The spell's magnitude range at DisplayLevel — "18 to 68", or a single value
    // when min == max. null when the spell has no stored magnitude (a pure flag /
    // teleport / summon spell).
    public static string? MagnitudeRange(in SpellFormulaInput spell)
    {
        (long min, long max) = SpellCalculator.AffectMagnitude(spell, DisplayLevel(spell));
        if (min == 0 && max == 0) return null;
        return min == max
            ? min.ToString(CultureInfo.InvariantCulture)
            : $"{min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}";
    }

    // Label for the magnitude range — the spell's primary damage / heal ability
    // name ("Damage(-MR)", "Heal", "DrainLife"), with a NonMagicalSpell (144) slot
    // rewriting "Damage(-MR)" back to "Damage". Falls back to "Magnitude" for a
    // stat-affect spell whose range isn't a damage/heal figure.
    public static string MagnitudeLabel(in SpellFormulaInput spell)
    {
        int code = 0;
        bool nonMagical = false;
        foreach (SpellAbility a in spell.Abilities)
        {
            if (code == 0 && (a.Code == HealCode || Array.IndexOf(_damageCodes, a.Code) >= 0))
                code = a.Code;
            if (a.Code == NonMagicalSpellCode) nonMagical = true;
        }
        if (code == 0) return "Magnitude";

        string name = AbilityNames.GetName(code) ?? "Magnitude";
        if (nonMagical && name == "Damage(-MR)") name = "Damage";
        return name;
    }

    // At-cap effect duration in seconds (at DisplayLevel), or 0 when the spell has
    // no duration. Stored ticks are 3 seconds each.
    public static long DurationSeconds(in SpellFormulaInput spell)
        => SpellCalculator.Duration(spell, DisplayLevel(spell)) * SpellCalculator.SpellRoundSeconds;

    // The per-level growth formula: one clause per scaling component ("Min: …",
    // "Max: 24+(2*lvl)", "Dur: …"), joined with ", ". A component scales only when
    // it has both a non-zero increment and a non-zero per-level denominator —
    // matching SpellCalculator's incLvls == 0 no-scaling rule, so a spell whose
    // IncLVLs is 0 (flat) contributes no clause. null when nothing scales.
    public static string? GrowthFormula(in SpellFormulaInput spell)
    {
        List<string> parts = new();
        if (Clause("Min", spell.MinBase, spell.MinInc, spell.MinIncLVLs) is { } min) parts.Add(min);
        if (Clause("Max", spell.MaxBase, spell.MaxInc, spell.MaxIncLVLs) is { } max) parts.Add(max);
        if (Clause("Dur", spell.Dur, spell.DurInc, spell.DurIncLVLs) is { } dur) parts.Add(dur);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // One "Label: base±(term)" clause, or null when the component is flat.
    private static string? Clause(string label, int baseVal, int inc, int incLvls)
    {
        if (inc == 0 || incLvls <= 0) return null;
        string sign = inc < 0 ? "-" : "+";
        return $"{label}: {baseVal.ToString(CultureInfo.InvariantCulture)}{sign}({Term(inc, incLvls)})";
    }

    // The per-level term "2*lvl" / "lvl" / "lvl/2" / "3*lvl/2" for the slope
    // inc/incLvls, reduced to lowest terms (mathematically identical Fix() math,
    // just fewer digits). The leading sign is supplied by the caller, so the
    // magnitude here is always unsigned.
    private static string Term(int inc, int incLvls)
    {
        int magnitude = Math.Abs(inc);
        int g = Gcd(magnitude, incLvls);
        int n = magnitude / g;
        int d = incLvls / g;
        string lvl = d == 1 ? "lvl" : $"lvl/{d.ToString(CultureInfo.InvariantCulture)}";
        return n == 1 ? lvl : $"{n.ToString(CultureInfo.InvariantCulture)}*{lvl}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
