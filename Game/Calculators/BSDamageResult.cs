namespace FujinTerm.Game.Calculators;

// Backstab damage range: lower and upper damage bounds.
public readonly record struct BSDamageResult(int MinDamage, int MaxDamage)
{
    // Midpoint of the damage range.
    public double AvgDamage => (MinDamage + MaxDamage) / 2.0;
}
