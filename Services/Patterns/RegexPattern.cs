using System.Text.RegularExpressions;
using FujinTerm.Terminal;

namespace FujinTerm.Services.Patterns;

// Full Regex match against the line's text. The regex is compiled in the
// constructor; capture groups from index 1 onward are returned to the handler
// via MatchResult.Groups.
public sealed class RegexPattern : IMessagePattern
{
    private readonly Regex _regex;
    private static readonly string[] EmptyGroups = [];

    public string Id { get; }
    public int Priority { get; }

    // Construct with the pattern's stable id, the regex pattern string, and an
    // optional execution priority (higher fires first; default 0). options
    // supplies extra RegexOptions; Compiled and CultureInvariant are always
    // applied.
    public RegexPattern(string id, string pattern, int priority = 0, RegexOptions options = RegexOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        Id = id;
        Priority = priority;
        _regex = new Regex(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result)
    {
        Match m = _regex.Match(line.Text);
        if (!m.Success)
        {
            result = default;
            return false;
        }

        IReadOnlyList<string> groups = EmptyGroups;
        if (m.Groups.Count > 1)
        {
            string[] array = new string[m.Groups.Count - 1];
            for (int i = 1; i < m.Groups.Count; i++) array[i - 1] = m.Groups[i].Value;
            groups = array;
        }

        result = new MatchResult(Id, line, groups);
        return true;
    }
}
