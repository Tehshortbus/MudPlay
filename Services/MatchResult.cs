using FujinTerm.Terminal;

namespace FujinTerm.Services;

/// <summary>
/// Payload handed to a <see cref="MessageRouter"/> subscriber when its
/// pattern matches a line. Carries the original emitted line, the pattern
/// id that fired (for fan-out disambiguation), and any capture groups the
/// pattern produced.
/// </summary>
/// <param name="PatternId">
/// <see cref="IMessagePattern.Id"/> of the pattern that matched. Lets a
/// handler tell which of several patterns it subscribed to actually fired.
/// </param>
/// <param name="Line">The originating <see cref="LineExtractor.EmittedLine"/> verbatim.</param>
/// <param name="Groups">
/// Ordered capture groups. <see cref="Patterns.RegexPattern"/> populates
/// these from regex group 1 onward (group 0, the full match, is not
/// included — it's already in <see cref="Line"/>); other pattern types
/// return an empty list.
/// </param>
public readonly record struct MatchResult(
    string PatternId,
    LineExtractor.EmittedLine Line,
    IReadOnlyList<string> Groups)
{
    /// <summary>Shorthand: the full matched line text.</summary>
    public string Text => Line.Text;
}
