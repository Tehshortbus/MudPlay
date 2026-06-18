using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

/// <summary>
/// Discovers quests and their permanent rewards straight from the active set's
/// <c>TBInfo</c> table — the mechanical underlay <c>QuestStore</c> hangs user names
/// / visibility off of. Stateless and recomputed per call (cheap at the workshop's
/// button-press cadence, never stale across a set switch), mirroring
/// <c>ClassCapabilities</c>'s scan pattern.
/// <para>
/// Discovery is data-driven, not a hardcoded id list: <em>every</em> flag that a
/// <c>giveability &lt;flag&gt; &lt;step&gt;</c> grants in a TBInfo chain is a quest,
/// because that is how an NPC/textblock hands the player something — a quest flag,
/// a skill (Smash, Meditate), an alignment tier. Realms reuse and extend the flag
/// space, so the set is read from the data each crawl rather than enumerated here.
/// </para>
/// <para>
/// A <c>giveability</c> target is the quest's identity; an <c>addability</c> target
/// is a stat <em>reward</em> only when it is <em>not</em> itself a discovered quest
/// flag (a quest-flag <c>addability</c> is a progress marker). A quest is
/// <em>multi-part</em> — re-run once per level tier — when its <c>minlevel</c> gates
/// climb across <em>different</em> give-steps (the alignment flags are the canonical
/// case); per-class <c>minlevel</c> variants of the <em>same</em> give-step (Smash,
/// Meditate, Perfect Stealth) stay one quest. Rewards branch by <c>class N</c> with
/// a no-class default, resolved here to the requested class (matching MudProxy's
/// <c>GetBonusesForClass</c>).
/// </para>
/// </summary>
public static class QuestCrawler
{
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

        var rawChains = new List<string>();
        foreach (JsonElement block in tbinfo.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("Action", out JsonElement actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                rawChains.Add(raw);
        }

        // Pass 1: the quest-flag set is every giveability target in the data; an
        // addability into that set is progress, anything else is a stat reward.
        HashSet<int> grantedFlags = DiscoverGrantedFlags(rawChains);

        // Pass 2: parse each chain against that set, keyed by its terminal grant.
        var chains = new List<ParsedChain>();
        foreach (string raw in rawChains)
        {
            ParsedChain? parsed = ParseChain(raw, grantedFlags);
            if (parsed is not null) chains.Add(parsed);
        }

        var quests = new List<CrawledQuest>();
        foreach (IGrouping<int, ParsedChain> flagGroup in chains.GroupBy(c => c.Flag).OrderBy(g => g.Key))
        {
            List<ParsedChain> flagChains = flagGroup.ToList();
            (IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict) = ResolveRestrictions(flagChains);
            if (IsMultiPart(flagChains))
                quests.AddRange(CrawlMultiPart(flagGroup.Key, flagChains, classId, classRestrict, raceRestrict));
            else
                quests.Add(CrawlSinglePart(flagGroup.Key, flagChains, classId, classRestrict, raceRestrict));
        }
        return quests;
    }

    // A flag is class-restricted only when *every* giveability chain that grants it
    // carries a `class N` guard — then the allowed set is the union of those ids. If any
    // granting chain is unguarded the quest is open to all classes (null). Same rule for
    // `race`. Conservative: it never hides a quest some unguarded chain leaves open to all.
    private static (IReadOnlyList<int>?, IReadOnlyList<int>?) ResolveRestrictions(List<ParsedChain> chains)
    {
        IReadOnlyList<int>? classes = chains.Any(c => c.ClassIds.Count == 0)
            ? null
            : chains.SelectMany(c => c.ClassIds).Distinct().OrderBy(x => x).ToArray();
        IReadOnlyList<int>? races = chains.Any(c => c.RaceIds.Count == 0)
            ? null
            : chains.SelectMany(c => c.RaceIds).Distinct().OrderBy(x => x).ToArray();
        return (classes, races);
    }

    // Every distinct giveability target across the data — the discovered quest-flag
    // set, used to tell stat rewards (addability off-set) from progress (on-set).
    private static HashSet<int> DiscoverGrantedFlags(IEnumerable<string> rawChains)
    {
        var flags = new HashSet<int>();
        foreach (string raw in rawChains)
            foreach (string segment in raw.Split(':'))
            {
                string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3 && p[0].Equals("giveability", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(p[1], out int gf))
                    flags.Add(gf);
            }
        return flags;
    }

    // A quest is multi-part when its minlevel gates climb across different give-steps
    // (re-run once per tier). Per-class minlevel variants of one give-step are not:
    // they share a single step, so the band count collapses to one.
    private static bool IsMultiPart(List<ParsedChain> chains)
    {
        var gated = chains.Where(c => c.MinLevel is int ml && ml > 0).ToList();
        int distinctLevels = gated.Select(c => c.MinLevel!.Value).Distinct().Count();
        int distinctSteps = gated.Select(c => c.GiveStep).Distinct().Count();
        return distinctLevels >= 2 && distinctSteps >= 2;
    }

    // A single-part quest: one identity at step 0. Its level gate resolves to the
    // requested class; its stat bonus (if any) is the lowest-step reward group,
    // class-resolved; its keeper items are every giveitem the flag never takes back.
    private static CrawledQuest CrawlSinglePart(int flag, List<ParsedChain> chains, int? classId,
        IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict)
    {
        int requiredLevel = ResolveLevel(chains, classId);
        IReadOnlyList<QuestBonus> bonuses = ResolveLowestRewardBonuses(chains, classId);
        IReadOnlyList<int> awardItems = KeeperItems(chains).Distinct().ToArray();
        return new CrawledQuest(flag, 0, requiredLevel, bonuses, awardItems, classRestrict, raceRestrict);
    }

    // A multi-part quest: one quest per minlevel band. Each band carries the reward
    // group and keeper items that fall in it, class-resolved; its required level is
    // the band level itself. Each band also carries the give-step Order range that
    // feeds its followable checklist — consistent with ResolveBand's bonus-banding:
    // band i spans [milestone-step of band i, milestone-step of band i+1 minus 1],
    // band 1 lowered to 1 (absorbing pre-first steps) and the last band's upper bound
    // raised to int.MaxValue (absorbing overflow past the final milestone), so no
    // give-step is ever dropped from every band's checklist.
    private static IEnumerable<CrawledQuest> CrawlMultiPart(int flag, List<ParsedChain> chains, int? classId,
        IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict)
    {
        var milestones = chains
            .Where(c => c.MinLevel is int ml && ml > 0)
            .Select(c => (c.GiveStep, Level: c.MinLevel!.Value))
            .ToList();
        var bandLevels = milestones.Select(m => m.Level).ToHashSet();

        var bandBonuses = new Dictionary<int, IReadOnlyList<QuestBonus>>();
        foreach (IGrouping<int, ParsedChain> rewardGroup in chains.Where(c => c.Bonuses.Count > 0).GroupBy(c => c.GiveStep))
        {
            List<ParsedChain> group = rewardGroup.ToList();
            int band = ResolveBand(group, bandLevels, milestones);
            if (band > 0) bandBonuses[band] = ResolveClass(group, classId);
        }

        var bandItems = new Dictionary<int, List<int>>();
        foreach (ParsedChain chain in chains)
            foreach (int item in chain.GiveItems.Where(i => !TakenAnywhere(chains, i)))
            {
                int band = ResolveBand(new[] { chain }, bandLevels, milestones);
                if (band == 0) continue;
                List<int> bucket = bandItems.TryGetValue(band, out List<int>? b) ? b : (bandItems[band] = new List<int>());
                if (!bucket.Contains(item)) bucket.Add(item);
            }

        // The give-step each band opens at — the lowest milestone give-step for its level.
        var levelStartStep = milestones
            .GroupBy(m => m.Level)
            .ToDictionary(g => g.Key, g => g.Min(m => m.GiveStep));
        var orderedLevels = bandLevels.OrderBy(l => l).ToList();

        for (int i = 0; i < orderedLevels.Count; i++)
        {
            int level = orderedLevels[i];
            IReadOnlyList<QuestBonus> bonuses = bandBonuses.TryGetValue(level, out IReadOnlyList<QuestBonus>? bb)
                ? bb : Array.Empty<QuestBonus>();
            IReadOnlyList<int> items = bandItems.TryGetValue(level, out List<int>? bi)
                ? bi.ToArray() : Array.Empty<int>();

            int rangeStart = i == 0 ? 1 : levelStartStep[level];
            int rangeEnd = i == orderedLevels.Count - 1 ? int.MaxValue : levelStartStep[orderedLevels[i + 1]] - 1;

            yield return new CrawledQuest(
                flag, level, level, bonuses, items, classRestrict, raceRestrict,
                BandOrdinal: i + 1, StepRangeStart: rangeStart, StepRangeEnd: rangeEnd);
        }
    }

    // The lowest level the quest can be taken at, resolved to the class: the class's
    // own minlevel branch when it has one, else the no-class branch, else the lowest
    // gate any branch declares; 0 when the quest imposes no level gate.
    private static int ResolveLevel(List<ParsedChain> chains, int? classId)
    {
        if (classId is int cid)
        {
            List<int> classGates = chains
                .Where(c => c.ClassIds.Contains(cid) && c.MinLevel is int ml && ml > 0)
                .Select(c => c.MinLevel!.Value).ToList();
            if (classGates.Count > 0) return classGates.Min();
        }
        List<int> defaultGates = chains
            .Where(c => c.ClassIds.Count == 0 && c.MinLevel is int ml && ml > 0)
            .Select(c => c.MinLevel!.Value).ToList();
        if (defaultGates.Count > 0) return defaultGates.Min();

        return chains.Where(c => c.MinLevel is int ml && ml > 0)
            .Select(c => c.MinLevel!.Value).DefaultIfEmpty(0).Min();
    }

    // The class-resolved bonuses of the lowest-step reward group; empty when the
    // quest grants no stat reward.
    private static IReadOnlyList<QuestBonus> ResolveLowestRewardBonuses(List<ParsedChain> chains, int? classId)
    {
        List<ParsedChain> rewards = chains.Where(c => c.Bonuses.Count > 0).ToList();
        if (rewards.Count == 0) return Array.Empty<QuestBonus>();
        int rewardStep = rewards.Min(r => r.GiveStep);
        List<ParsedChain> group = rewards.Where(r => r.GiveStep == rewardStep).ToList();
        return ResolveClass(group, classId);
    }

    // The band a give-step group belongs to: the largest band level its own chains
    // declare (per-class variants sometimes omit minlevel and lean on a sibling),
    // else the level of the nearest milestone at or before the group's give-step.
    private static int ResolveBand(
        IReadOnlyList<ParsedChain> group, HashSet<int> bandLevels, List<(int Step, int Level)> milestones)
    {
        int declared = group
            .Where(c => c.MinLevel is int ml && bandLevels.Contains(ml))
            .Select(c => c.MinLevel!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (declared > 0) return declared;

        int step = group[0].GiveStep;
        int bestStep = -1, bestLevel = 0;
        foreach ((int Step, int Level) m in milestones)
            if (m.Step <= step && m.Step > bestStep)
            {
                bestStep = m.Step;
                bestLevel = m.Level;
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

    // Keeper items: every giveitem the flag hands out and never takes back. A
    // giveitem later takeitem'd under the same flag is a turn-in token, not a reward.
    private static IEnumerable<int> KeeperItems(List<ParsedChain> chains) =>
        chains.SelectMany(c => c.GiveItems).Where(i => !TakenAnywhere(chains, i));

    private static bool TakenAnywhere(List<ParsedChain> chains, int item) =>
        chains.Any(c => c.TakeItems.Contains(item));

    // Parse one chain into its quest-relevant directives. Returns null when the
    // chain grants no quest flag. "Last giveability wins" matches the game's
    // terminal-grant semantics (and MudProxy's parser).
    private static ParsedChain? ParseChain(string raw, HashSet<int> grantedFlags)
    {
        int? flag = null;
        int giveStep = 0;
        int? minLevel = null;
        var classIds = new List<int>();
        var raceIds = new List<int>();
        var bonuses = new List<QuestBonus>();
        var giveItems = new List<int>();
        var takeItems = new List<int>();

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
                    flag = gf; giveStep = gs;
                    break;
                case "addability" when p.Length >= 3
                    && int.TryParse(p[1], out int af) && int.TryParse(p[2], out int av):
                    if (!grantedFlags.Contains(af)) bonuses.Add(new QuestBonus(af, av));
                    break;
                case "class" when p.Length >= 2 && int.TryParse(p[1], out int cid):
                    classIds.Add(cid);
                    break;
                case "race" when p.Length >= 2 && int.TryParse(p[1], out int rid):
                    raceIds.Add(rid);
                    break;
                case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                    minLevel = ml; // last wins: the per-class gate follows any earlier intro gate
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    giveItems.Add(gi);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    takeItems.Add(ti);
                    break;
            }
        }

        if (flag is null) return null;
        return new ParsedChain(flag.Value, giveStep, minLevel, classIds, raceIds, bonuses, giveItems, takeItems);
    }

    // Scratch record for one parsed chain; never escapes the crawl.
    private sealed record ParsedChain(
        int Flag,
        int GiveStep,
        int? MinLevel,
        List<int> ClassIds,
        List<int> RaceIds,
        List<QuestBonus> Bonuses,
        List<int> GiveItems,
        List<int> TakeItems);
}
