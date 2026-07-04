namespace FujinTerm.Game.Calculators;

// Physical attack mode for accuracy/swing math. Values match MajorMUD's own
// attack-type field values so they can be compared against game-data
// attack-type fields directly.
public enum MudAttackType
{
    Punch = 1,
    Kick = 2,
    Jumpkick = 3,
    Normal = 5,
    Bash = 6,
    Smash = 7,
}
