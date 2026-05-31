namespace FujinTerm.Models.GameData;

/// <summary>
/// User-defined outgoing typed-shortcut → command expansion. The
/// outgoing-text mirror of <see cref="Trigger"/>: when the user hits
/// Enter on the terminal canvas or the Conversation window's input
/// field, the first whitespace-delimited word is checked against the
/// alias table; matches are expanded and sent to the game in place of
/// the typed text.
/// </summary>
/// <param name="Name">
/// Match word — the literal first token of the user's input. Treated
/// case-insensitively. Rejected at edit time when it would collide
/// with a MajorMUD chat-channel command (see
/// <see cref="Services.AliasEngine.NameConflictReason"/>).
/// </param>
/// <param name="Enabled">Per-alias on / off without deleting.</param>
/// <param name="Expansion">
/// What the alias expands to before being sent to the game.
/// Substitution syntax uses positional placeholders only — the alias
/// engine deliberately does NOT share the named-variable cache the
/// <see cref="Services.TriggerEngine"/> populates from pattern
/// captures; the two engines stay isolated.
/// <list type="bullet">
///   <item><c>{0}</c> — every token after the alias name as one string (the "rest of the line").</item>
///   <item><c>{1}</c>, <c>{2}</c>, … — positional whitespace-split tokens after the alias name.</item>
///   <item>Multi-step output via <c>^M</c> or <c>;</c> in the expansion
///     (same convention as macros / triggers).</item>
///   <item>Trailing whitespace per split step is trimmed after substitution,
///     so an absent placeholder doesn't dangle a space onto the sent command.</item>
/// </list>
/// </param>
public sealed record Alias(
    string Name,
    bool Enabled,
    string Expansion);
