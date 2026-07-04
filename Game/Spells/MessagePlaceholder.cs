namespace FujinTerm.Game.Spells;

// One entry in CasterMessageMatcher.Placeholders — a template token paired with
// what it captures, shown in the Game Data → Messages editor so a user
// authoring or updating a spell-message line knows which token pins which slot.
// Token is the literal placeholder, e.g. {spellname}; Meaning is a
// plain-language description of what the token captures.
public readonly record struct MessagePlaceholder(string Token, string Meaning);
