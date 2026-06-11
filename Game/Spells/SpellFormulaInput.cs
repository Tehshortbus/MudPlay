namespace FujinTerm.Game.Spells;

/// <summary>
/// The subset of <c>Spells</c>-table columns that
/// <see cref="SpellCalculator"/> needs to reproduce MMUD Explorer's
/// level-scaled spell math. The known-spell catalog (a later PR) builds
/// one of these from a raw game-data row; the calculator itself never
/// touches JSON.
/// </summary>
public readonly record struct SpellFormulaInput
{
    public SpellFormulaInput() { }

    /// <summary>Spell record number (<c>Number</c>). Identity used when a
    /// chained end-cast (<c>Abil == 151</c>) resolves a follow-up spell.</summary>
    public int Number { get; init; }

    /// <summary>Minimum damage/heal base value before level scaling.</summary>
    public int MinBase { get; init; }

    /// <summary>Minimum-value per-level numerator (<c>MinInc</c>).</summary>
    public int MinInc { get; init; }

    /// <summary>Minimum-value per-level denominator (<c>MinIncLVLs</c>).
    /// <c>0</c> means the base value is used unscaled.</summary>
    public int MinIncLVLs { get; init; }

    /// <summary>Maximum damage/heal base value before level scaling.</summary>
    public int MaxBase { get; init; }

    /// <summary>Maximum-value per-level numerator (<c>MaxInc</c>).</summary>
    public int MaxInc { get; init; }

    /// <summary>Maximum-value per-level denominator (<c>MaxIncLVLs</c>).
    /// <c>0</c> means the base value is used unscaled.</summary>
    public int MaxIncLVLs { get; init; }

    /// <summary>Duration base value before level scaling.</summary>
    public int Dur { get; init; }

    /// <summary>Duration per-level numerator (<c>DurInc</c>).</summary>
    public int DurInc { get; init; }

    /// <summary>Duration per-level denominator (<c>DurIncLVLs</c>).
    /// <c>0</c> means the base duration is used unscaled.</summary>
    public int DurIncLVLs { get; init; }

    /// <summary>Cast-level ceiling (<c>0</c> = uncapped) applied before scaling.</summary>
    public int Cap { get; init; }

    /// <summary>Cast-level floor applied before scaling.</summary>
    public int ReqLevel { get; init; }

    /// <summary>Energy spent per fire. Player energy is 1000 per round, so
    /// a spell fires roughly <c>Fix(1000 / EnergyCost)</c> times per round —
    /// the multiplier folded into the damage/mana results.</summary>
    public int EnergyCost { get; init; }

    /// <summary>Mana spent per individual fire (before the energy multiplier).</summary>
    public int ManaCost { get; init; }

    /// <summary>The ten <c>Abil-N</c> / <c>AbilVal-N</c> pairs in slot order.
    /// Empty when the row sets no ability slots.</summary>
    public IReadOnlyList<SpellAbility> Abilities { get; init; } = [];
}
