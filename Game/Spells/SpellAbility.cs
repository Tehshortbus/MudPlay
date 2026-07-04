namespace FujinTerm.Game.Spells;

// One Abil-N / AbilVal-N pair off a Spells-table row. Code is the MajorMUD
// ability code (decoded by AbilityNames); Value is the paired magnitude. A spell
// row carries ten of these in slot order.
public readonly record struct SpellAbility(int Code, int Value);
