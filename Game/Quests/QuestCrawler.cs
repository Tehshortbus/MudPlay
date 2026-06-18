using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

/// <summary>
/// Discovers quests and their permanent stat rewards straight from the active
/// set's <c>TBInfo</c> table — the mechanical underlay <c>QuestStore</c> hangs
/// user names / visibility off of. Stateless and recomputed per call (cheap at
/// the workshop's button-press cadence, never stale across a set switch),
/// mirroring <c>ClassCapabilities</c>'s scan pattern.
/// <para>
/// A chain (one <c>\n</c>-separated line of <c>:</c>-separated directives)
/// terminally grants a quest when its last quest-flag <c>giveability</c> names a
/// flag in <see cref="IsQuestFlag"/>. A chain is a <em>reward</em> when it also
/// carries one or more <c>addability</c> entries whose target is <em>not</em> a
/// quest flag (those are the stat bonuses); a chain with only quest-flag
/// <c>addability</c> is progress, not a reward. The alignment flags
/// (126/127/128) are sequences of per-level "Nth Alignment" quests carved by
/// <c>minlevel</c> milestones; every other flag is one quest keyed by step 0.
/// Rewards branch by <c>class N</c> with a no-class default, resolved here to the
/// requested class (matching MudProxy's <c>GetBonusesForClass</c>).
/// </para>
/// </summary>
public static class QuestCrawler
{
    // Alignment flags carve into per-level bands; all other quest flags are one
    // quest. Custom realms reuse these numbers, so the set is realm-portable.
    private static readonly HashSet<int> AlignmentFlags = new() { 126, 127, 128 };

    /// <summary>
    /// Crawl every quest in the active set, resolving reward bonuses to
    /// <paramref name="classId"/> (Classes-table <c>Number</c>); pass <c>null</c>
    /// for the no-class default. Returns ordered by flag, then band level.
    /// Empty when no set is active or <c>TBInfo</c> is missing.
    /// </summary>
    public static IReadOnlyList<CrawledQuest> Crawl(GameDataCache cache, int? classId)
    {
        ArgumentNullException.ThrowIfNull(cache);

        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (tbinfo is null) return Array.Empty<CrawledQuest>();

        var chains = new List<ParsedChain>();
        foreach (JsonElement block in tbinfo.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("Action", out JsonElement actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ParsedChain? parsed = ParseChain(raw);
                if (parsed is not null) chains.Add(parsed);
            }
        }

        var quests = new List<CrawledQuest>();
        foreach (IGrouping<int, ParsedChain> flagGroup in chains.GroupBy(c => c.Flag).OrderBy(g => g.Key))
        {
            List<ParsedChain> flagChains = flagGroup.ToList();
            if (AlignmentFlags.Contains(flagGroup.Key))
                quests.AddRange(CrawlAlignment(flagGroup.Key, flagChains, classId));
            else
                quests.Add(CrawlSingle(flagGroup.Key, flagChains, classId));
        }
        return quests;
    }

    // A single-flag quest: one identity at step 0. Its bonus (if any) is the
    // lowest-step reward group, class-resolved; gate-only flags carry none.
    private static CrawledQuest CrawlSingle(int flag, List<ParsedChain> chains, int? classId)
    {
        List<ParsedChain> rewards = chains.Where(c => c.IsReward).ToList();
        if (rewards.Count == 0) return new CrawledQuest(flag, 0, 0, Array.Empty<QuestBonus>());

        int rewardStep = rewards.Min(r => r.GiveStep);
        List<ParsedChain> group = rewards.Where(r => r.GiveStep == rewardStep).ToList();
        int requiredLevel = group.Select(g => g.MinLevel ?? 0).DefaultIfEmpty(0).Max();
        return new CrawledQuest(flag, 0, requiredLevel, ResolveClass(group, classId));
    }

    // An alignment flag: one quest per minlevel band. Bands are the distinct
    // milestone levels (any chain whose minlevel is a positive multiple of 10);
    // each reward group attaches to the band it falls in, class-resolved.
    private static IEnumerable<CrawledQuest> CrawlAlignment(int flag, List<ParsedChain> chains, int? classId)
    {
        List<(int Step, int Level)> milestones = chains
            .Where(c => c.MinLevel is int ml && ml > 0 && ml % 10 == 0)
            .Select(c => (c.GiveStep, c.MinLevel!.Value))
            .ToList();

        var bandBonuses = new Dictionary<int, IReadOnlyList<QuestBonus>>();
        foreach (IGrouping<int, ParsedChain> rewardGroup in chains.Where(c => c.IsReward).GroupBy(c => c.GiveStep))
        {
            List<ParsedChain> group = rewardGroup.ToList();
            int band = ResolveBand(group, milestones);
            if (band > 0) bandBonuses[band] = ResolveClass(group, classId);
        }

        foreach (int level in milestones.Select(m => m.Level).Distinct().OrderBy(l => l))
        {
            IReadOnlyList<QuestBonus> bonuses = bandBonuses.TryGetValue(level, out IReadOnlyList<QuestBonus>? b)
                ? b
                : Array.Empty<QuestBonus>();
            yield return new CrawledQuest(flag, level, level, bonuses);
        }
    }

    // The band a reward group belongs to: the largest band level its own chains
    // declare (some per-class variants omit minlevel and lean on their siblings),
    // else the level of the nearest milestone at or before the reward's give-step.
    private static int ResolveBand(List<ParsedChain> rewardGroup, List<(int Step, int Level)> milestones)
    {
        int declared = rewardGroup
            .Where(c => c.MinLevel is int ml && ml > 0 && ml % 10 == 0)
            .Select(c => c.MinLevel!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (declared > 0) return declared;

        int step = rewardGroup[0].GiveStep;
        int bestStep = -1, bestLevel = 0;
        foreach ((int Step, int Level) m in milestones)
        {
            if (m.Step <= step && m.Step > bestStep)
            {
                bestStep = m.Step;
                bestLevel = m.Level;
            }
        }
        return bestLevel;
    }

    // Resolve a reward group to one class: the class-specific branch when present,
    // else the no-class default; empty when neither exists for this class.
    private static IReadOnlyList<QuestBonus> ResolveClass(List<ParsedChain> group, int? classId)
    {
        if (classId is int cid)
        {
            ParsedChain? specific = group.FirstOrDefault(c => c.ClassIds.Contains(cid));
            if (specific is not null) return specific.Bonuses;
        }
        ParsedChain? fallback = group.FirstOrDefault(c => c.ClassIds.Count == 0);
        return fallback?.Bonuses ?? (IReadOnlyList<QuestBonus>)Array.Empty<QuestBonus>();
    }

    // Parse one chain into its quest-relevant directives. Returns null when the
    // chain grants no quest flag. "Last quest-flag giveability wins" matches the
    // game's terminal-grant semantics (and MudProxy's parser).
    private static ParsedChain? ParseChain(string raw)
    {
        int? flag = null;
        int giveStep = 0;
        int? minLevel = null;
        var classIds = new List<int>();
        var bonuses = new List<QuestBonus>();

        foreach (string segment in raw.Split(':'))
        {
            string t = segment.Trim();
            if (t.Length == 0) continue;
            string[] p = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;

            switch (p[0].ToLowerInvariant())
            {
                case "giveability" when p.Length >= 3
                    && int.TryParse(p[1], out int gf) && int.TryParse(p[2], out int gs):
                    if (IsQuestFlag(gf)) { flag = gf; giveStep = gs; }
                    break;
                case "addability" when p.Length >= 3
                    && int.TryParse(p[1], out int af) && int.TryParse(p[2], out int av):
                    if (!IsQuestFlag(af)) bonuses.Add(new QuestBonus(af, av));
                    break;
                case "class" when p.Length >= 2 && int.TryParse(p[1], out int cid):
                    classIds.Add(cid);
                    break;
                case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                    minLevel = ml;
                    break;
            }
        }

        if (flag is null) return null;
        return new ParsedChain(flag.Value, giveStep, minLevel, classIds, bonuses);
    }

    /// <summary>
    /// Whether an ability id is a quest-flag id (the <c>giveability</c> target a
    /// quest terminally grants) rather than a skill or stat ability. Custom
    /// realms reuse these numbers: <c>50</c>, <c>156</c>, the alignment / named
    /// quest band <c>125–134</c>, and the GreaterMUD quest space <c>191–400</c>.
    /// Skills (e.g. 32 Smash) and stat abilities (e.g. 117 backstab) fall outside.
    /// </summary>
    public static bool IsQuestFlag(int abilityId) =>
        abilityId is 50 or 156
        || abilityId is >= 125 and <= 134
        || abilityId is >= 191 and <= 400;

    // Scratch record for one parsed chain; never escapes the crawl.
    private sealed record ParsedChain(
        int Flag,
        int GiveStep,
        int? MinLevel,
        List<int> ClassIds,
        List<QuestBonus> Bonuses)
    {
        public bool IsReward => Bonuses.Count > 0;
    }
}
