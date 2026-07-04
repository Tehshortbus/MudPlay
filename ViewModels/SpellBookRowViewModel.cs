using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

// One immutable row in the SpellBookViewModel table: a single KnownSpell
// from the class's learnable list, paired with whether the character has
// obtained it and the level-scaled effect / mana figures (via
// SpellCalculator) for the book's current level.
//
// Rows are throwaway — the window rebuilds the whole collection whenever
// SpellbookState.Changed fires (class swap, level change, or obtained-set
// update), so there's no per-row change notification.
public sealed class SpellBookRowViewModel
{
    public SpellBookRowViewModel(
        KnownSpell spell,
        bool isObtained,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName = null,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null)
    {
        Short = spell.Short;
        Name = spell.Name;
        ReqLevel = spell.ReqLevel;
        IsObtained = isObtained;

        Mana = SpellCalculator.ManaCost(spell.Formula);
        ManaText = Mana.ToString();
        EffectText = SpellEffectFormatter.Format(
            spell.Formula, level, resolveChain, resolveSpellName, resolveTextblockCasts);
        FormulaText = BuildFormula(spell.Formula);
    }

    // The verbatim Spells.Short cast-code the player types.
    public string Short { get; }

    // The full Spells.Name.
    public string Name { get; }

    // Level the spell unlocks at (Spells.ReqLevel).
    public int ReqLevel { get; }

    // True when the character has learned this spell.
    public bool IsObtained { get; }

    // Checkmark glyph for the obtained column ("✓" or empty).
    public string ObtainedGlyph => IsObtained ? "✓" : string.Empty;

    // The unlock level as a bare number — the unlock-level cell.
    public string ReqLevelText => ReqLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Per-round mana cost — numeric, for column sorting.
    public long Mana { get; }

    // Per-round mana cost at the spell's energy multiplier.
    public string ManaText { get; }

    // Level-scaled effect at the book's current level: "Dmg 14–22", "Heal
    // 30–45", "Dur 8", plus any decoded stat-affect abilities the spell
    // grants ("AC +10", "Strength +3"), joined by " · ". "—" when the spell
    // produces no figure at all. See SpellEffectFormatter.
    public string EffectText { get; }

    // The raw scaling formula (base value + per-level slope) shown as a
    // tooltip so the player can see how the effect grows, independent of the
    // current level. Empty when the spell has no scaling magnitude.
    public string FormulaText { get; }

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
