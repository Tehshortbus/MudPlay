using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

/// <summary>
/// Drafts an ordered, followable step checklist for a single quest flag by walking
/// the active set's <c>TBInfo</c> chains — the auto baseline behind
/// <c>QuestDefinition.Steps</c> that a user then refines. Stateless and recomputed
/// per call, mirroring <see cref="QuestCrawler"/>'s scan.
/// <para>
/// Every chain that terminally advances the flag (its last <c>giveability</c> names
/// <paramref name="flag"/>) becomes one <see cref="QuestStep"/>, ordered by the
/// chain's give-step — the quest's own progress counter. The crawl surfaces the
/// player command, the location, and the items the step checks / takes / gives;
/// identical steps (the same chain echoed from multiple rooms) collapse to one
/// entry.
/// </para>
/// </summary>
public static class QuestStepGraph
{
    /// <summary>
    /// Build the ordered step draft for <paramref name="flag"/> in the active set.
    /// Empty when no set is active, <c>TBInfo</c> is missing, or the flag has no
    /// chains. Steps are de-duplicated and ordered by give-step, then location.
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

    // Parse one chain into a step when its terminal (last) giveability advances
    // `flag`; else null. The give-step of that grant orders the step.
    private static QuestStep? BuildStep(string raw, int flag, string? location)
    {
        string[] segments = raw.Split(':');
        if (segments.Length == 0) return null;

        int? terminalFlag = null;
        int terminalStep = 0;
        var requiredItems = new List<int>();
        var turnInItems = new List<int>();
        var grantedItems = new List<int>();

        foreach (string segment in segments)
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;

            switch (p[0].ToLowerInvariant())
            {
                case "giveability" when p.Length >= 3
                    && int.TryParse(p[1], out int gf) && int.TryParse(p[2], out int gs):
                    // Last grant wins: the chain belongs to the flag it terminally
                    // advances, matching the crawler's identity rule.
                    terminalFlag = gf; terminalStep = gs;
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

        return new QuestStep(terminalStep, ReadCommand(segments), location, requiredItems, turnInItems, grantedItems);
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
        sb.Append(s.Order).Append('|').Append(s.Command).Append('|').Append(s.Location).Append('|');
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
