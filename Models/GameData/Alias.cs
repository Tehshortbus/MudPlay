namespace FujinTerm.Models.GameData;

// User-defined outgoing typed-shortcut → command expansion. The
// outgoing-text mirror of Trigger: when the user hits Enter on the
// terminal canvas or the Conversation window's input field, the first
// whitespace-delimited word is checked against the alias table; matches
// are expanded and sent to the game in place of the typed text.
//
// Name: the literal first token of the user's input, matched
// case-insensitively. Rejected at edit time when it would collide with a
// MajorMUD chat-channel command (see AliasEngine.NameConflictReason).
//
// Expansion: what the alias expands to before being sent. Substitution
// uses positional placeholders only — the alias engine deliberately does
// NOT share the named-variable cache TriggerEngine populates from pattern
// captures; the two engines stay isolated.
//   {0} — every token after the alias name as one string (the "rest of the line").
//   {1}, {2}, … — positional whitespace-split tokens after the alias name.
//   Multi-step output via ^M or ; in the expansion (same convention as macros / triggers).
//   Trailing whitespace per split step is trimmed after substitution, so
//   an absent placeholder doesn't dangle a space onto the sent command.
public sealed record Alias(
    string Name,
    bool Enabled,
    string Expansion);
