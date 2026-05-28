using FujinTerm.Terminal;

namespace FujinTerm.Services.Patterns;

/// <summary>
/// Matches when the line's text starts with a fixed prefix. Faster than
/// <see cref="RegexPattern"/> for the common "line begins with X"
/// classification cases; emits no capture groups.
/// </summary>
public sealed class PrefixPattern : IMessagePattern
{
    private static readonly string[] EmptyGroups = [];

    public string Id { get; }
    public int Priority { get; }
    public string Prefix { get; }
    public StringComparison Comparison { get; }

    /// <summary>
    /// Construct with the pattern's stable <paramref name="id"/>, the
    /// <paramref name="prefix"/> text to look for, an optional
    /// <paramref name="priority"/>, and an optional
    /// <paramref name="comparison"/> mode (default ordinal — case-sensitive).
    /// </summary>
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
