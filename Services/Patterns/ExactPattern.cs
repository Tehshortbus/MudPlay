using FujinTerm.Terminal;

namespace FujinTerm.Services.Patterns;

// Matches when the line's text equals a fixed string. Useful for well-known
// one-shot lines (e.g. "Welcome to MajorMUD!") where neither prefix nor regex is
// needed.
public sealed class ExactPattern : IMessagePattern
{
    private static readonly string[] EmptyGroups = [];

    public string Id { get; }
    public int Priority { get; }
    public string Text { get; }
    public StringComparison Comparison { get; }

    public ExactPattern(
        string id,
        string text,
        int priority = 0,
        StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);

        Id = id;
        Text = text;
        Priority = priority;
        Comparison = comparison;
    }

    public bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result)
    {
        if (string.Equals(line.Text, Text, Comparison))
        {
            result = new MatchResult(Id, line, EmptyGroups);
            return true;
        }

        result = default;
        return false;
    }
}
