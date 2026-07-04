namespace FujinTerm.Models.Profile;

// How a numeric threshold field is interpreted at engine time. Percentage reads
// the value as a 0–100 percentage of the live max (HP, MA, mana per cast).
// Absolute reads it as a flat numeric value. Shared by HealthSettings (HP / MA
// thresholds) and CombatSettings (spell mana per cast).
public enum ThresholdMode
{
    Percentage,
    Absolute,
}
