namespace FujinTerm.Game.Calculators;

// Energy per swing plus a 10-round simulation of swings landed and energy
// carried, since the round remainder rolls into the next round and produces a
// repeating swing pattern.
//   EnergyPerSwing  — energy each swing costs out of the 1000-per-round budget.
//   RawSwings       — naive 1000 / EnergyPerSwing, capped at MAX_SWINGS.
//   EncumPercent    — encumbrance percentage used in the calculation.
//   QnDCritBonus    — Quick & Deadly crit bonus from a fast weapon at low encum.
//   SwingsPerRound  — swings landed in each of 10 rounds (energy carried forward).
//   EnergyRemaining — energy carried into the next round after each of 10 rounds.
public readonly record struct SwingCalcResult(
    int EnergyPerSwing,
    double RawSwings,
    int EncumPercent,
    int QnDCritBonus,
    int[] SwingsPerRound,
    int[] EnergyRemaining);
