namespace FujinTerm.Game.Spells;

/// <summary>
/// One <c>Abil-N</c> / <c>AbilVal-N</c> pair off a <c>Spells</c>-table
/// row. <see cref="Code"/> is the MajorMUD ability code (decoded by
/// <see cref="GameData.AbilityNames"/>); <see cref="Value"/> is the
/// paired magnitude. A spell row carries ten of these in slot order.
/// </summary>
public readonly record struct SpellAbility(int Code, int Value);
