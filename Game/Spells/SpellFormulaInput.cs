namespace FujinTerm.Game.Spells;

// The subset of Spells-table columns that SpellCalculator needs to reproduce
// MajorMUD's level-scaled spell math. The known-spell catalog builds one of
// these from a raw game-data row; the calculator itself never touches JSON.
public readonly record struct SpellFormulaInput
{
    public SpellFormulaInput() { }

    // Spell record number (Number). Identity used when a chained end-cast
    // (Abil == 151) resolves a follow-up spell.
    public int Number { get; init; }

    // Minimum damage/heal base value before level scaling.
    public int MinBase { get; init; }

    // Minimum-value per-level numerator (MinInc).
    public int MinInc { get; init; }

    // Minimum-value per-level denominator (MinIncLVLs). 0 means the base value
    // is used unscaled.
    public int MinIncLVLs { get; init; }

    // Maximum damage/heal base value before level scaling.
    public int MaxBase { get; init; }

    // Maximum-value per-level numerator (MaxInc).
    public int MaxInc { get; init; }

    // Maximum-value per-level denominator (MaxIncLVLs). 0 means the base value
    // is used unscaled.
    public int MaxIncLVLs { get; init; }

    // Duration base value before level scaling.
    public int Dur { get; init; }

    // Duration per-level numerator (DurInc).
    public int DurInc { get; init; }

    // Duration per-level denominator (DurIncLVLs). 0 means the base duration is
    // used unscaled.
    public int DurIncLVLs { get; init; }

    // Cast-level ceiling (0 = uncapped) applied before scaling.
    public int Cap { get; init; }

    // Cast-level floor applied before scaling.
    public int ReqLevel { get; init; }

    // Energy spent per fire. Player energy is 1000 per round, so a spell fires
    // roughly Fix(1000 / EnergyCost) times per round — the multiplier folded
    // into the damage/mana results.
    public int EnergyCost { get; init; }

    // Mana spent per individual fire (before the energy multiplier).
    public int ManaCost { get; init; }

    // The ten Abil-N / AbilVal-N pairs in slot order. Empty when the row sets no
    // ability slots.
    public IReadOnlyList<SpellAbility> Abilities { get; init; } = [];
}
