using FujinTerm.Terminal;

namespace FujinTerm.Services;

// Contract every MessageRouter-registered pattern implements. A pattern decides
// whether a given LineExtractor.EmittedLine is a "yes for me" line and, when it
// is, surfaces any capture groups for the handler.
//
// Implementations:
//   Patterns.RegexPattern  — full PCRE-like match with capture groups.
//   Patterns.PrefixPattern — substring-startswith, no groups.
//   Patterns.ExactPattern  — whole-line equality, no groups.
// The user-defined Trigger engine implements this interface to plug into the
// same dispatch path.
public interface IMessagePattern
{
    // Stable identifier (e.g. "chat.gossip", "combat.user-hits") — appears on
    // the MatchResult for diagnostics and lets subsystems subscribe by name to
    // entries in the default registry.
    string Id { get; }

    // Execution-order hint among matching patterns. Higher fires first. Per the
    // fan-out semantics priority orders execution, not exclusivity — every
    // matching pattern still fires.
    int Priority { get; }

    // Try to match line. Returns true on hit and fills result; returns false
    // otherwise.
    bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result);
}
