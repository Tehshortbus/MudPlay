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
/// case-insensitively against typed input.
/// </param>
/// <param name="Enabled">Per-alias on / off without deleting.</param>
/// <param name="Expansion">
/// What the alias expands to before being sent to the game.
/// Substitution syntax:
/// <list type="bullet">
///   <item><c>$0</c> — every token after the alias name as one string.</item>
///   <item><c>$1</c>, <c>$2</c>, … — positional whitespace-split tokens.</item>
///   <item><c>$name</c> — named variable from the shared <see cref="Services.TriggerEngine"/> store
///     (Triggers populate the store by capturing <c>{name}</c> placeholders in their patterns).</item>
/// </list>
/// </param>
public sealed record Alias(
    string Name,
    bool Enabled,
    string Expansion);
