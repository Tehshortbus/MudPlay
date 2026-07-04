using FujinTerm.Terminal;

namespace FujinTerm.Services.Patterns;

// Matches when the line's text starts with a fixed prefix. Faster than
// RegexPattern for the common "line begins with X" classification cases; emits
// no capture groups.
public sealed class PrefixPattern : IMessagePattern
{
    private static readonly string[] EmptyGroups = [];

    public string Id { get; }
    public int Priority { get; }
    public string Prefix { get; }
    public StringComparison Comparison { get; }

    // Construct with the pattern's stable id, the prefix text to look for, an
    // optional priority, and an optional comparison mode (default ordinal —
    // case-sensitive).
    public PrefixPattern(
        string id,
        string prefix,
        int priority = 0,
        StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        Id = id;
        Prefix = prefix;
        Priority = priority;
        Comparison = comparison;
    }

    public bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result)
    {
        if (line.Text.StartsWith(Prefix, Comparison))
        {
            result = new MatchResult(Id, line, EmptyGroups);
            return true;
        }

        result = default;
        return false;
    }
}
