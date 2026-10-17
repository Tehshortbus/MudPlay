using System;
using System.Collections.Generic;
using System.Text;

namespace MudPlay.Services.Update;

// Pulls the entries an update brings out of CHANGELOG.md so the update window can show
// the actual "what's new" bullets instead of the hand-authored GitHub release body
// (which is publish boilerplate — the asset table + checksum instructions). Pure
// string work, no network fetch.
public static class ChangelogExtractor
{
    // The entries someone going from `current` to `release` gains: every `## <version>`
    // entry newer than current, up to and including release, newest first. Entries
    // newer than release — merged after it shipped — are left out, so a late updater
    // never reads about changes the download doesn't have. Each entry is headed by its
    // version when there's more than one; the `- bug reports addressed:` bookkeeping
    // tail is dropped. Falls back to the release's own entry (or the top one) when
    // `current` can't be compared. Null when there's nothing to show.
    public static string? EntriesSince(string? changelogMarkdown, string? current, string? release)
    {
        if (string.IsNullOrWhiteSpace(changelogMarkdown)) return null;
        List<(string Version, string Body)> entries = Parse(changelogMarkdown);
        if (entries.Count == 0) return null;

        List<(string Version, string Body)> gained = entries.FindAll(e =>
            ReleaseParser.IsNewer(current, e.Version)
            && (string.IsNullOrWhiteSpace(release) || !ReleaseParser.IsNewer(release, e.Version)));
        if (gained.Count == 0)
        {
            int own = string.IsNullOrWhiteSpace(release) ? -1 : entries.FindIndex(e => SameVersion(e.Version, release!));
            gained = [entries[own >= 0 ? own : 0]];
        }
        gained.RemoveAll(e => e.Body.Length == 0);
        if (gained.Count == 0) return null;
        if (gained.Count == 1) return gained[0].Body;

        var sb = new StringBuilder();
        foreach ((string version, string body) in gained)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("Version ").Append(version).Append('\n').Append(body);
        }
        return sb.ToString();
    }

    private static List<(string Version, string Body)> Parse(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<(string, string)> entries = new();
        string? version = null;
        var body = new StringBuilder();
        foreach (string line in lines)
        {
            if (IsEntryHeading(line))
            {
                if (version is not null) entries.Add((version, body.ToString().Trim()));
                version = line[3..].Trim();
                body.Clear();
                continue;
            }
            if (version is null || IsBugReportsLine(line)) continue;
            body.Append(line).Append('\n');
        }
        if (version is not null) entries.Add((version, body.ToString().Trim()));
        return entries;
    }

    // A CHANGELOG version heading: "## 3.79.0". The top-level "# Version history"
    // title is a single '#', so requiring "## " excludes it.
    private static bool IsEntryHeading(string line) =>
        line.StartsWith("## ", StringComparison.Ordinal);

    private static bool SameVersion(string a, string b) =>
        string.Equals(a.TrimStart('v', 'V'), b.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    private static bool IsBugReportsLine(string line) =>
        line.TrimStart().StartsWith("- bug reports addressed", StringComparison.OrdinalIgnoreCase);
}
