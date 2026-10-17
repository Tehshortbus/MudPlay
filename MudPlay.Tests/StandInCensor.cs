using MudPlay.Services;

namespace MudPlay.Tests;

// A bug-report chat censor whose word list is made-up stand-in words, hashed the
// way the shipped table's words are, so no test spells out a real one.
internal static class StandInCensor
{
    public static BugReportChatCensor Of(params string[] words) => new(words.SelectMany(word => new[]
    {
        BugReportChatCensor.Hash(BugReportChatCensor.Normalise(word, collapseAll: false), collapsedAll: false),
        BugReportChatCensor.Hash(BugReportChatCensor.Normalise(word, collapseAll: true), collapsedAll: true),
    }));
}
