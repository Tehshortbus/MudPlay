namespace FujinTerm.Game.Calculators;

/// <summary>
/// Alignment / evil-points band, matching MMUD Explorer's <c>eEvilPoints</c>.
/// Feeds the vile-ward adjustment in <see cref="CombatCalculator"/> — higher
/// evil scales a defender's vile ward down before it counts toward defense.
/// </summary>
public enum EvilLevel
{
    Saint = 0,
    Good = 1,
    Neutral = 2,
    Seedy = 3,
    Outlaw = 4,
    Criminal = 5,
    Villain = 6,
    Fiend = 7,
}
