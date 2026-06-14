namespace FujinTerm.Game.Calculators;

/// <summary>
/// Result of <see cref="CombatCalculator.CalculateHitChance"/> — the attacker's
/// hit chance, the defender's dodge chance, and the net hit chance after dodge,
/// plus the realm-specific caps that bounded them (for tooltip display).
/// </summary>
/// <param name="HitPercent">Hit chance after AC reduction and hit-floor/cap clamp.</param>
/// <param name="DodgePercent">Defender dodge chance against this accuracy.</param>
/// <param name="OverallHitPercent">
/// Net hit chance: <c>HitPercent - (HitPercent * DodgePercent / 100)</c>.
/// </param>
/// <param name="HitMinCap">Hit-chance floor applied (realm + armour-type dependent).</param>
/// <param name="HitMaxCap">Hit-chance ceiling applied.</param>
/// <param name="DodgeCap">Dodge ceiling applied.</param>
public readonly record struct HitCalcResult(
    int HitPercent,
    int DodgePercent,
    int OverallHitPercent,
    int HitMinCap,
    int HitMaxCap,
    int DodgeCap);
