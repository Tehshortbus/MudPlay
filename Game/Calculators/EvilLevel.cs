namespace FujinTerm.Game.Calculators;

// MajorMUD alignment / evil-points band. Feeds the vile-ward adjustment in
// CombatCalculator — higher evil scales a defender's vile ward down before it
// counts toward defense.
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
