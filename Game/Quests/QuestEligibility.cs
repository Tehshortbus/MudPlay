using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// One quest available to the character: its (flag, step) identity and display name.
public readonly record struct QuestAvailabilityInfo(int Flag, int Step, string Name);

// Resolves which quests are available to the character at a given level — the same
// eligibility the Quest Status journal applies: a level gate of 1+ that's now met, the
// character's class/race can do it, it's visible + unblocked, and it isn't already
// complete. Kept separate from QuestAvailabilityAnnouncer, which takes it as a provider.
public static class QuestEligibility
{
    public static IReadOnlyList<QuestAvailabilityInfo> Resolve(
        GameDataCache gameData, QuestStore quests, PlayerStats stats, ProfileService profile,
        Func<CrawledQuest, QuestDefinition, string> resolveName, int level)
    {
        var result = new List<QuestAvailabilityInfo>();
        if (level <= 0) return result;

        int? classId = ResolveId(gameData, "Classes", stats.Class);
        int? raceId = ResolveId(gameData, "Races", stats.Race);
        Dictionary<(int, int), bool> complete = ProgressComplete(profile);
        bool alGood = profile.Current?.QuestAlignGood ?? false;
        bool alNeutral = profile.Current?.QuestAlignNeutral ?? false;
        bool alEvil = profile.Current?.QuestAlignEvil ?? false;

        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
        {
            QuestDefinition def = quests.Resolve(q.Flag, q.Step);
            if (def.Blocked || !def.Visible) continue;
            if (QuestEligibilityResolver.IsIneligible(q, classId, raceId, def.ClassRestrict, alGood, alNeutral, alEvil)) continue;
            int required = def.RequiredLevel ?? q.RequiredLevel;
            if (required <= 0 || required > level) continue;
            if (complete.TryGetValue((q.Flag, q.Step), out bool done) && done) continue;
            string name = resolveName(q, def);
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(new QuestAvailabilityInfo(q.Flag, q.Step, name));
        }

        // User-added quests the crawl never produces — level-gated the same way.
        foreach (QuestDefinition def in quests.ManualQuests())
        {
            if (def.Blocked || !def.Visible) continue;
            int required = def.RequiredLevel ?? 0;
            if (required <= 0 || required > level) continue;
            if (complete.TryGetValue((def.Flag, def.Step), out bool done) && done) continue;
            if (!string.IsNullOrWhiteSpace(def.Name))
                result.Add(new QuestAvailabilityInfo(def.Flag, def.Step, def.Name));
        }

        return result;
    }

    private static Dictionary<(int, int), bool> ProgressComplete(ProfileService profile)
    {
        var map = new Dictionary<(int, int), bool>();
        if (profile.Current?.QuestLog is { } log)
            foreach (QuestProgress p in log) map[(p.Flag, p.Step)] = p.Complete;
        return map;
    }

    private static int? ResolveId(GameDataCache gameData, string table, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        int num = GetInt(gameData.FindRowByName(table, name), "Number");
        return num > 0 ? num : null;
    }

    private static int GetInt(GameRow? rowOpt, string prop)
    {
        if (rowOpt is not GameRow row) return 0;
        if (!row.TryGetProperty(prop, out GameValue v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
