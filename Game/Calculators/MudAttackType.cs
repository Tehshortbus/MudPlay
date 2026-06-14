namespace FujinTerm.Game.Calculators;

/// <summary>
/// Physical attack mode for accuracy/swing math. Values match MMUD Explorer's
/// <c>eAttackTypeMUD</c> so they can be compared against game-data attack-type
/// fields directly.
/// </summary>
public enum MudAttackType
{
    Punch = 1,
    Kick = 2,
    Jumpkick = 3,
    Normal = 5,
    Bash = 6,
    Smash = 7,
}
