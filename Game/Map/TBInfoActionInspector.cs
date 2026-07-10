using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Walks a TBInfo textblock and its LinkTo chain and surfaces the player-typed
// command keywords each Action line responds to — the set MME shows when you
// expand a monster's greet (or a room CMD) textblock. We surface only the
// keyword candidates (the first colon-token of each keyword line), not the full
// directive tree MME decodes: the popup answers "what can I type here?", which
// is the verified, load-bearing part of the block.
//
// Keyword cleaning mirrors MME's command decoder: a '*' (hidden-command marker)
// is stripped and '|' (alternative-keyword separator) becomes " OR " so a
// multi-keyword line reads naturally. Bare directive lines (no colon) are chain
// steps, not player keywords, so they're skipped — same test the teleport /
// remoteaction resolvers use.
public static class TBInfoActionInspector
{
    // Defensive cap so a malformed self-referential LinkTo can't spin. The
    // visited-set already breaks true cycles; this bounds a pathological long
    // chain too. MME uses the same style of nest cap.
    private const int MaxChainDepth = 32;

    // Collect the distinct command keywords the textblock (and everything it
    // links to) responds to, in first-seen order. Empty when the block has no
    // keyword lines or the number is unknown.
    public static IReadOnlyList<string> CollectActionKeywords(TBInfoStore store, int number)
    {
        ArgumentNullException.ThrowIfNull(store);
        List<string> ordered = new();
        if (number <= 0) return ordered;

        HashSet<int> visited = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        int current = number;
        int depth = 0;
        while (current > 0 && depth < MaxChainDepth && visited.Add(current))
        {
            depth++;
            TBInfoEntry? entry = store.GetEntry(current);
            if (entry is null) break;

            if (!string.IsNullOrWhiteSpace(entry.Action))
            {
                foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;

                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue; // bare directive line — no player keyword

                    string keyword = CleanKeyword(line[..colon]);
                    if (keyword.Length == 0) continue;
                    if (seen.Add(keyword)) ordered.Add(keyword);
                }
            }

            current = entry.LinkTo;
        }

        return ordered;
    }

    private static string CleanKeyword(string raw)
        => raw.Replace("*", string.Empty)
              .Replace("|", " OR ")
              .Trim();
}
