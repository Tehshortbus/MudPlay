namespace FujinTerm.Game.Calculators;

/// <summary>
/// Result of <see cref="CombatCalculator.CalcSwings"/> — energy per swing plus
/// a 10-round simulation of swings landed and energy carried, since the round
/// remainder rolls into the next round and produces a repeating swing pattern.
/// </summary>
/// <param name="EnergyPerSwing">Energy each swing costs out of the 1000-per-round budget.</param>
/// <param name="RawSwings">Naive <c>1000 / EnergyPerSwing</c>, capped at <see cref="CombatCalculator.MAX_SWINGS"/>.</param>
/// <param name="EncumPercent">Encumbrance percentage used in the calculation.</param>
/// <param name="QnDCritBonus">Quick &amp; Deadly crit bonus from a fast weapon at low encumbrance.</param>
/// <param name="SwingsPerRound">Swings landed in each of 10 consecutive rounds (energy carried forward).</param>
/// <param name="EnergyRemaining">Energy carried into the next round after each of the 10 rounds.</param>
public readonly record struct SwingCalcResult(
    int EnergyPerSwing,
    double RawSwings,
    int EncumPercent,
    int QnDCritBonus,
    int[] SwingsPerRound,
    int[] EnergyRemaining);
