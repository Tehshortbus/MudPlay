namespace FujinTerm.Game.Calculators;

/// <summary>
/// Per-hit damage range from <see cref="CombatCalculator.CalcMeleeDamage"/>
/// for a Normal / Bash / Smash weapon attack (no defender mitigation applied).
/// </summary>
/// <param name="MinDamage">Lower damage bound.</param>
/// <param name="MaxDamage">Upper damage bound.</param>
public readonly record struct MeleeDamageResult(int MinDamage, int MaxDamage)
{
    /// <summary>Midpoint of the damage range.</summary>
    public double AvgDamage => (MinDamage + MaxDamage) / 2.0;
}
