namespace FujinTerm.Game.Spells;

/// <summary>
/// One entry in <see cref="CasterMessageMatcher.Placeholders"/> — a template
/// token paired with what it captures, shown in the Game Data → Messages
/// editor so a user authoring or updating a spell-message line knows which
/// token pins which slot.
/// </summary>
/// <param name="Token">The literal placeholder, e.g. <c>{spellname}</c>.</param>
/// <param name="Meaning">Plain-language description of what the token captures.</param>
public readonly record struct MessagePlaceholder(string Token, string Meaning);
