using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Pure formatting for the @quest reply, kept off the handler.
//
// A flag's bands are numbered by ordinal position (1st/2nd/3rd band in Step order),
// not by their raw Step identity: flag 126's bands are Steps 10/20/30/40/50, shown
// as 1/2/3/4/5. The band list is the union of the store's known steps for the flag
// and any steps the character's QuestLog carries, so a crawled-only band still counts.
public static class QuestQueryReport
{
    // Ordinal band numbers (1-based) of the flag's marked-complete bands, in order.
    public static IReadOnlyList<int> MarkedOrdinals(
        int flag, IReadOnlyList<QuestProgress> log, IReadOnlyList<int> knownSteps)
    {
        List<int> bands = Bands(flag, log, knownSteps);
        List<int> ordinals = new();
        foreach (QuestProgress p in log)
        {
            if (p.Flag != flag || !p.Complete) continue;
            int idx = bands.IndexOf(p.Step);
            if (idx >= 0) ordinals.Add(idx + 1);
        }
        ordinals.Sort();
        return ordinals;
    }

    // "Good align 1, 2, 3 marked complete" — or "Good align: none marked complete".
    public static string FormatFlag(
        int flag, string label, IReadOnlyList<QuestProgress> log, IReadOnlyList<int> knownSteps)
    {
        IReadOnlyList<int> ordinals = MarkedOrdinals(flag, log, knownSteps);
        return ordinals.Count == 0
            ? $"{label}: none marked complete"
            : $"{label} {string.Join(", ", ordinals)} marked complete";
    }

    // Every quest with at least one marked-complete band, grouped by flag: "Good align
    // 1, 2, 3; Evil align 1" — or "no quests marked complete". Capped so a fully-cleared
    // character doesn't overrun the chat line.
    public static string FormatAll(IReadOnlyList<QuestProgress> log, QuestStore quests)
    {
        List<int> flags = log.Where(p => p.Complete).Select(p => p.Flag).Distinct().OrderBy(f => f).ToList();
        if (flags.Count == 0) return "no quests marked complete";

        List<string> parts = new();
        foreach (int flag in flags.Take(MaxFlagsShown))
        {
            IReadOnlyList<int> ordinals = MarkedOrdinals(flag, log, quests.StepsForFlag(flag));
            if (ordinals.Count == 0) continue;
            string label = QuestNameResolver.Label(flag, UserName(flag, log, quests));
            parts.Add($"{label} {string.Join(", ", ordinals)}");
        }

        string body = string.Join("; ", parts);
        int extra = flags.Count - MaxFlagsShown;
        return extra > 0 ? $"{body}; +{extra} more" : body;
    }

    private const int MaxFlagsShown = 12;

    // Sorted union of the store's known band steps and any steps the log carries.
    private static List<int> Bands(int flag, IReadOnlyList<QuestProgress> log, IReadOnlyList<int> knownSteps)
    {
        SortedSet<int> bands = new(knownSteps);
        foreach (QuestProgress p in log) if (p.Flag == flag) bands.Add(p.Step);
        return bands.ToList();
    }

    private static string UserName(int flag, IReadOnlyList<QuestProgress> log, QuestStore quests)
    {
        foreach (int step in quests.StepsForFlag(flag).Concat(log.Where(p => p.Flag == flag).Select(p => p.Step)))
        {
            string name = quests.Resolve(flag, step).Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return string.Empty;
    }
}
