namespace FujinTerm.Game.Calculators;

/// <summary>
/// Backstab damage range from <see cref="CombatCalculator.CalcBSDamage"/>.
/// </summary>
/// <param name="MinDamage">Lower backstab damage bound.</param>
/// <param name="MaxDamage">Upper backstab damage bound.</param>
public readonly record struct BSDamageResult(int MinDamage, int MaxDamage)
{
    /// <summary>Midpoint of the damage range.</summary>
    public double AvgDamage => (MinDamage + MaxDamage) / 2.0;
}
