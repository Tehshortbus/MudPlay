using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

/// <summary>
/// Drafts an ordered, followable step checklist for a single quest flag by
/// walking the active set's <c>TBInfo</c> chains — the auto baseline behind
/// <c>QuestDefinition.Steps</c> that a user then refines. Stateless and
/// recomputed per call, mirroring <see cref="QuestCrawler"/>'s scan.
/// <para>
/// Every chain that advances the flag (its last quest-flag <c>giveability</c>
/// names <paramref name="flag"/>) becomes one <see cref="QuestStep"/>, ordered by
/// the chain's give-step — the quest's own progress counter, which the same-flag
/// <c>checkability</c>/<c>testability</c> gates follow. The crawl surfaces the
/// player command, location, and the requirement / item directives the spec
/// calls out; identical chains (the same step echoed from multiple rooms) collapse
/// to one entry.
/// </para>
/// </summary>
public static class QuestStepGraph
{
    /// <summary>
    /// Build the ordered step draft for <paramref name="flag"/> in the active set.
    /// Empty when no set is active, <c>TBInfo</c> is missing, or the flag has no
    /// chains. Steps are de-duplicated and ordered by progress, then location.
    /// </summary>
    public static IReadOnlyList<QuestStep> Build(GameDataCache cache, int flag)
    {
        ArgumentNullException.ThrowIfNull(cache);

        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (tbinfo is null) return Array.Empty<QuestStep>();

        var steps = new List<QuestStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement block in tbinfo.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("Action", out JsonElement actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            string? location = ReadLocation(block);

            foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                QuestStep? step = BuildStep(raw, flag, location);
                if (step is null) continue;
                if (seen.Add(CanonicalKey(step))) steps.Add(step);
            }
        }

        return steps
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Parse one chain into a step when it terminally advances `flag`; else null.
    private static QuestStep? BuildStep(string raw, int flag, string? location)
    {
        string[] segments = raw.Split(':');
        if (segments.Length == 0) return null;

        int? terminalFlag = null;
        int terminalStep = 0;
        int requiredLevel = 0;
        QuestAlignment alignment = QuestAlignment.None;
        var classes = new List<int>();
        var prereqs = new List<(int Flag, int Step)>();
        var requiredItems = new List<int>();
        var turnInItems = new List<int>();
        var grantedItems = new List<int>();

        for (int i = 0; i < segments.Length; i++)
        {
            string[] p = segments[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            string kw = p[0].ToLowerInvariant();

            switch (kw)
            {
                case "giveability" when p.Length >= 3
                    && int.TryParse(p[1], out int gf) && int.TryParse(p[2], out int gs):
                    // Track the terminal quest-flag grant; the chain only belongs to
                    // `flag` if that's the last one, matching the crawler's identity rule.
                    if (QuestCrawler.IsQuestFlag(gf)) { terminalFlag = gf; terminalStep = gs; }
                    break;
                case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                    requiredLevel = ml;
                    break;
                case "goodaligned":
                    alignment = QuestAlignment.Good;
                    break;
                case "evilaligned":
                    alignment = QuestAlignment.Evil;
                    break;
                case "neutralaligned":
                    alignment = QuestAlignment.Neutral;
                    break;
                case "class" when p.Length >= 2 && int.TryParse(p[1], out int cid):
                    if (!classes.Contains(cid)) classes.Add(cid);
                    break;
                case "checkability" or "testability" when p.Length >= 3
                    && int.TryParse(p[1], out int rf) && int.TryParse(p[2], out int rs):
                    // Only cross-flag gates are real prerequisites; same-flag gates
                    // are implied by the give-step ordering.
                    if (QuestCrawler.IsQuestFlag(rf) && rf != flag && !prereqs.Contains((rf, rs)))
                        prereqs.Add((rf, rs));
                    break;
                case "checkitem" when p.Length >= 2 && int.TryParse(p[1], out int ci):
                    if (!requiredItems.Contains(ci)) requiredItems.Add(ci);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    if (!turnInItems.Contains(ti)) turnInItems.Add(ti);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    if (!grantedItems.Contains(gi)) grantedItems.Add(gi);
                    break;
            }
        }

        if (terminalFlag != flag) return null;

        return new QuestStep(
            terminalStep,
            ReadCommand(segments),
            location,
            requiredLevel,
            alignment,
            classes,
            prereqs,
            requiredItems,
            turnInItems,
            grantedItems);
    }

    // The first segment is a player command only when it doesn't lead with a known
    // guard/effect directive — otherwise the chain is reached by dialogue branch.
    private static string? ReadCommand(string[] segments)
    {
        string first = segments[0].Trim();
        if (first.Length == 0) return null;
        string kw = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return DirectiveKeywords.Contains(kw) ? null : first;
    }

    private static string? ReadLocation(JsonElement block)
    {
        if (!block.TryGetProperty("Called From", out JsonElement cf)) return null;
        if (cf.ValueKind != JsonValueKind.String) return null;
        string? s = cf.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // Stable structural fingerprint so the same step echoed from many rooms folds
    // to one entry (record equality is reference-based for the list fields).
    private static string CanonicalKey(QuestStep s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Order).Append('|').Append(s.Command).Append('|').Append(s.Location)
          .Append('|').Append(s.RequiredLevel).Append('|').Append((int)s.Alignment).Append('|');
        sb.AppendJoin(',', s.RequiredClasses).Append('|');
        sb.AppendJoin(',', s.RequiredQuests.Select(q => $"{q.Flag}.{q.Step}")).Append('|');
        sb.AppendJoin(',', s.RequiredItems).Append('|');
        sb.AppendJoin(',', s.TurnInItems).Append('|');
        sb.AppendJoin(',', s.GrantedItems);
        return sb.ToString();
    }

    // Guard / effect directive keywords — a chain that leads with one of these is
    // reached by dialogue branch, so its first segment is not a player command.
    private static readonly HashSet<string> DirectiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "message", "text", "takeitem", "giveitem", "checkitem", "roomitem", "failitem",
        "clearitem", "failroomitem", "teleport", "cast", "class", "race", "random",
        "failability", "checkability", "testability", "giveability", "addability",
        "removeability", "goodability", "goodaligned", "evilaligned", "neutralaligned",
        "minlevel", "maxlevel", "levelcheck", "price", "buy", "give", "givecoins",
        "summon", "monsters", "nomonsters", "needmonster", "adddelay", "delay",
        "addexp", "addevil", "testskill", "checkspell", "learnspell", "remoteaction",
        "check",
    };
}
