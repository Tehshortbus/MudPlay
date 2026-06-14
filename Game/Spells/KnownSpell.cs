namespace FujinTerm.Game.Spells;

/// <summary>
/// One spell a given class can learn/cast at a given level — the output
/// row of <see cref="KnownSpellCatalog"/>. Carries the display identity
/// (<see cref="Short"/> cast-code + full <see cref="Name"/>) plus the
/// <see cref="Formula"/> needed to compute level-scaled damage / heal /
/// duration via <see cref="SpellCalculator"/>.
/// </summary>
/// <remarks>
/// <see cref="Short"/> is the verbatim <c>Spells.Short</c> shortcode the
/// player types to cast (and that <c>spells</c> / <c>pow</c> rows print);
/// <see cref="Name"/> is the full <c>Spells.Name</c> the learn-scroll line
/// reports. The Spell Book matches catalog rows against the live
/// <c>spells</c> list by <see cref="Short"/> and against the learn-scroll
/// signal by <see cref="Name"/>.
/// <para>
/// <see cref="Targets"/> is the raw <c>Spells.Targets</c> scope code (see
/// <see cref="GameData.LookupEnums.FormatSpellTargets"/> for the label
/// table). It tells the party-buff caster whether a buff blankets the whole
/// party in one cast (Full / Divided Party Area) or must be cast per member.
/// </para>
/// </remarks>
public readonly record struct KnownSpell(
    int Number,
    string Short,
    string Name,
    int Magery,
    int MageryLvl,
    int ReqLevel,
    int Targets,
    SpellFormulaInput Formula);
