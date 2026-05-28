using FujinTerm.Terminal;

namespace FujinTerm.Services;

/// <summary>
/// Contract every <see cref="MessageRouter"/>-registered pattern implements.
/// A pattern decides whether a given <see cref="LineExtractor.EmittedLine"/>
/// is a "yes for me" line and, when it is, surfaces any capture groups for
/// the handler.
/// </summary>
/// <remarks>
/// Implementations:
/// <list type="bullet">
///   <item><description><see cref="Patterns.RegexPattern"/> — full PCRE-like match with capture groups.</description></item>
///   <item><description><see cref="Patterns.PrefixPattern"/> — substring-startswith, no groups.</description></item>
///   <item><description><see cref="Patterns.ExactPattern"/> — whole-line equality, no groups.</description></item>
/// </list>
/// Custom patterns from later phases (the Phase 5 user-defined Trigger
/// engine) implement this interface to plug into the same dispatch path.
/// </remarks>
public interface IMessagePattern
{
    /// <summary>
    /// Stable identifier (e.g. <c>"chat.gossip"</c>, <c>"combat.user-hits"</c>)
    /// — appears on the <see cref="MatchResult"/> for diagnostics and lets
    /// later subsystems subscribe by name to entries in the default registry.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Execution-order hint among matching patterns. Higher fires first.
    /// Per the fan-out semantics priority orders <i>execution</i>, not
    /// exclusivity — every matching pattern still fires.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Try to match <paramref name="line"/>. Returns <c>true</c> on hit and
    /// fills <paramref name="result"/>; returns <c>false</c> otherwise.
    /// </summary>
    bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result);
}
