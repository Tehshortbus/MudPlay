namespace FujinTerm.Game.Calculators;

// Per-hit damage range for a Normal / Bash / Smash weapon attack (no defender
// mitigation applied): lower and upper damage bounds.
public readonly record struct MeleeDamageResult(int MinDamage, int MaxDamage)
{
    // Midpoint of the damage range.
    public double AvgDamage => (MinDamage + MaxDamage) / 2.0;
}
