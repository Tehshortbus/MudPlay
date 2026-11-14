using MudPlay.Terminal;

namespace MudPlay.Services.Patterns;

// A router pattern whose wordings come from the active game-data set rather than a
// fixed regex: the set's own lines of one kind (RealmEventLines), read through a
// function set once the set is known. Until it is set, or for a set without such
// lines, it matches nothing. Groups are what the function returns for the line.
public sealed class RealmLinePattern : IMessagePattern
{
    private Func<string, IReadOnlyList<string>?>? _match;

    public RealmLinePattern(string id) => Id = id;

    public string Id { get; }

    public int Priority => 0;

    public void SetMatcher(Func<string, IReadOnlyList<string>?> match) => _match = match;

    public bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result)
    {
        result = default;
        if (line.IsPromptLine || _match?.Invoke(line.Text) is not { } groups) return false;
        result = new MatchResult(Id, line, groups);
        return true;
    }
}
