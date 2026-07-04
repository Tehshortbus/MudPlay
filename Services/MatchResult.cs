using FujinTerm.Terminal;

namespace FujinTerm.Services;

// Payload handed to a MessageRouter subscriber when its pattern matches a
// line. Carries the original emitted line, the pattern id that fired (for
// fan-out disambiguation), and any capture groups the pattern produced.
//   PatternId — IMessagePattern.Id of the pattern that matched. Lets a
//     handler tell which of several patterns it subscribed to actually fired.
//   Line      — the originating LineExtractor.EmittedLine verbatim.
//   Groups    — ordered capture groups. RegexPattern populates these from
//     regex group 1 onward (group 0, the full match, is not included — it's
//     already in Line); other pattern types return an empty list.
public readonly record struct MatchResult(
    string PatternId,
    LineExtractor.EmittedLine Line,
    IReadOnlyList<string> Groups)
{
    // The full matched line text.
    public string Text => Line.Text;
}
