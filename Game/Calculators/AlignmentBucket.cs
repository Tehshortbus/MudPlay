namespace FujinTerm.Game.Calculators;

/// <summary>
/// Three-way reduction of MajorMUD's fine-grained <c>who</c> alignment titles,
/// used by equip-time item filtering. Items carry alignment restrictions as
/// "GoodOnly / EvilOnly / NotGood / …" flags (see
/// <see cref="Game.Inventory.ItemEquipFilter"/>), which only resolve once the
/// character's alignment collapses to one of these three bands.
/// </summary>
/// <remarks>
/// The band membership follows the canonical ladder
/// <see cref="WhoListParser.AlignmentWords"/> exposes: <c>Saint / Lawful / Good</c>
/// are Good, the blank-column <c>Neutral</c> is Neutral, and
/// <c>Seedy / Outlaw / Criminal / Villain / Fiend</c> are Evil.
/// </remarks>
public enum AlignmentBucket
{
    /// <summary>Saint / Lawful / Good.</summary>
    Good,

    /// <summary>Neutral — the blank alignment column.</summary>
    Neutral,

    /// <summary>Seedy / Outlaw / Criminal / Villain / Fiend.</summary>
    Evil,
}
