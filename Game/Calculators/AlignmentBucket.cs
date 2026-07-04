namespace FujinTerm.Game.Calculators;

// Three-way reduction of MajorMUD's fine-grained who alignment titles, used by
// equip-time item filtering. Items carry alignment restrictions as
// GoodOnly / EvilOnly / NotGood / … flags (ItemEquipFilter), which only resolve
// once the character's alignment collapses to one of these three bands.
// Band membership follows the canonical WhoListParser.AlignmentWords ladder:
// Saint / Lawful / Good are Good, the blank-column Neutral is Neutral, and
// Seedy / Outlaw / Criminal / Villain / Fiend are Evil.
public enum AlignmentBucket
{
    // Saint / Lawful / Good.
    Good,

    // Neutral — the blank alignment column.
    Neutral,

    // Seedy / Outlaw / Criminal / Villain / Fiend.
    Evil,
}
