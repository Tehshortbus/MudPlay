using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

/// <summary>
/// One immutable row in the <see cref="SpellBookViewModel"/> table: a single
/// <see cref="KnownSpell"/> from the class's learnable list, paired with
/// whether the character has obtained it and the level-scaled effect /
/// mana figures (via <see cref="SpellCalculator"/>) for the book's current
/// <paramref name="level"/>.
/// </summary>
/// <remarks>
/// Rows are throwaway — the window rebuilds the whole collection whenever
/// <see cref="SpellbookState.Changed"/> fires (class swap, level change, or
/// obtained-set update), so there's no per-row change notification.
/// </remarks>
public sealed class SpellBookRowViewModel
{
    public SpellBookRowViewModel(KnownSpell spell, bool isObtained, int level, Func<int, SpellFormulaInput?> resolveChain)
    {
        Short = spell.Short;
        Name = spell.Name;
        ReqLevel = spell.ReqLevel;
        IsObtained = isObtained;

        ManaText = SpellCalculator.ManaCost(spell.Formula).ToString();
        EffectText = BuildEffect(spell.Formula, level, resolveChain);
        FormulaText = BuildFormula(spell.Formula);
    }

    /// <summary>The verbatim <c>Spells.Short</c> cast-code the player types.</summary>
    public string Short { get; }

    /// <summary>The full <c>Spells.Name</c>.</summary>
    public string Name { get; }

    /// <summary>Level the spell unlocks at (<c>Spells.ReqLevel</c>).</summary>
    public int ReqLevel { get; }

    /// <summary>True when the character has learned this spell.</summary>
    public bool IsObtained { get; }

    /// <summary>Checkmark glyph for the obtained column ("✓" or empty).</summary>
    public string ObtainedGlyph => IsObtained ? "✓" : string.Empty;

    /// <summary>"Lv {ReqLevel}" — the unlock-level cell.</summary>
    public string ReqLevelText => $"Lv {ReqLevel}";

    /// <summary>Per-round mana cost at the spell's energy multiplier.</summary>
    public string ManaText { get; }

    /// <summary>
    /// Level-scaled effect at the book's current level: "Dmg 14–22",
    /// "Heal 30–45", "Dur 8", or a combination, joined by " · ". "—" when
    /// the spell produces no damage / heal / duration figure.
    /// </summary>
    public string EffectText { get; }

    /// <summary>
    /// The raw scaling formula (base value + per-level slope) shown as a
    /// tooltip so the player can see how the effect grows, independent of
    /// the current level. Empty when the spell has no scaling magnitude.
    /// </summary>
    public string FormulaText { get; }

    private static string BuildEffect(in SpellFormulaInput formula, int level, Func<int, SpellFormulaInput?> resolveChain)
    {
        List<string> parts = new();

        long minDmg = SpellCalculator.MinDamage(formula, level, resolveChain);
        long maxDmg = SpellCalculator.MaxDamage(formula, level, resolveChain);
        if (maxDmg > 0) parts.Add($"Dmg {Range(minDmg, maxDmg)}");

        long minHeal = SpellCalculator.MinHeal(formula, level, resolveChain);
        long maxHeal = SpellCalculator.MaxHeal(formula, level, resolveChain);
        if (maxHeal > 0) parts.Add($"Heal {Range(minHeal, maxHeal)}");

        long dur = SpellCalculator.Duration(formula, level);
        if (dur > 0) parts.Add($"Dur {dur}");

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private static string Range(long min, long max)
        => min == max ? max.ToString() : $"{min}–{max}";

    private static string BuildFormula(in SpellFormulaInput formula)
    {
        List<string> parts = new();
        if (formula.MinBase != 0 || formula.MaxBase != 0)
            parts.Add($"base {formula.MinBase}–{formula.MaxBase}");
        if (formula.MinIncLVLs > 0 && formula.MinInc != 0)
            parts.Add($"min +{formula.MinInc}/{formula.MinIncLVLs}lv");
        if (formula.MaxIncLVLs > 0 && formula.MaxInc != 0)
            parts.Add($"max +{formula.MaxInc}/{formula.MaxIncLVLs}lv");
        if (formula.Dur != 0 || (formula.DurIncLVLs > 0 && formula.DurInc != 0))
        {
            string slope = formula.DurIncLVLs > 0 && formula.DurInc != 0
                ? $" +{formula.DurInc}/{formula.DurIncLVLs}lv"
                : string.Empty;
            parts.Add($"dur {formula.Dur}{slope}");
        }
        return string.Join(", ", parts);
    }
}
