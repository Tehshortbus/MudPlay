namespace FujinTerm.Models.Profile;

/// <summary>
/// How a numeric threshold field is interpreted at engine time.
/// <see cref="Percentage"/> reads the value as a 0–100 percentage of
/// the live max (HP, MA, mana per cast). <see cref="Absolute"/> reads
/// it as a flat numeric value. Shared by
/// <see cref="HealthSettings"/> (HP / MA thresholds) and
/// <see cref="CombatSettings"/> (spell mana per cast).
/// </summary>
public enum ThresholdMode
{
    Percentage,
    Absolute,
}
