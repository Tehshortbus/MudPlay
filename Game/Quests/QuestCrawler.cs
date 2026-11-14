using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Discovers quests and their permanent rewards straight from the active set's
// TBInfo table — the mechanical underlay QuestStore hangs user names / visibility
// off of. Stateless and recomputed per call (cheap at the workshop's button-press
// cadence, never stale across a set switch), matching ClassCapabilities's scan
// pattern.
//
// Discovery is data-driven, not a hardcoded id list: every flag that a
// `giveability <flag> <step>` grants in a TBInfo chain is a quest, because that is
// how an NPC/textblock hands the player something — a quest flag, a skill (Smash,
// Meditate), an alignment tier. Realms reuse and extend the flag space, so the set
// is read from the data each crawl rather than enumerated here.
//
// A giveability target is the quest's identity; an addability target is a stat
// reward only when it is not itself a discovered quest flag (a quest-flag
// addability is a progress marker). A quest is multi-part — re-run once per level
// tier — when its progress gates form a strict per-level staircase: across the
// giveability / testability / checkability segments that carry a minlevel, the
// lowest gate step climbs every time the level does (the five-tier alignment flags
// are the canonical case, and only the combined give+test+check gates reveal all
// five tiers — the giveability gates alone undercount them). Per-class minlevel
// variants of the same step (Smash, Meditate, Perfect Stealth) share one step so
// they never form a staircase and stay one quest. Rewards branch by `class N` with
// a no-class default, resolved here to the requested class.
//
// A flag is multi-part on a second axis when it climbs by ability value rather
// than give-step order: granted once at value 1 and advanced through 2, 3, 4… by
// relative `addability <flag>` increments (MageBane is the canonical case). The
// give-step staircase can't see this — the lone giveability sits at value 1 and
// the later tiers carry no giveability of their own — so such a flag is detected
// by its addability-into-the-granted-set advance (see DiscoverValueLadders /
// CrawlValueLadder). It is then banded by the distinct minlevel gates its value
// axis carries (one tier per level the climb pauses to turn in at, not one per
// value), so MageBane lands two tiers: the value-1 sword at level 15 and the
// value-4 finish at level 50, folding the ungated intermediate values up into the
// next gated tier. A `checkability <flag> 0` "have the flag at all" gate (a
// class-only turn-in proxy) folds into the entry band rather than forming a tier
// of its own.
//
// A quest's prize comes at the very end of its chain: the keeper items it awards
// are those handed at the final give-step (per tier for a multi-part band), so
// earlier giveitems — the quartz rod, the cloth pouch — are quest-use items the
// player consumes mid-chain, not rewards, and are dropped. A reward handed at a
// tier's own gate step caps the tier just completed (strictly-less banding), which
// is what lands the alignment ladder's ring/stats/chest/tabard/weapon on tiers 1–5
// in order. A single-part quest that grants neither keeper nor stat bonus awards
// the ability itself — the skill it teaches is the prize (Smash, Meditate,
// SeeHidden).
public static class QuestCrawler
{
    // Crawl every quest in the active set, resolving reward bonuses to classId
    // (Classes-table Number); pass null for the no-class default. Returns ordered
    // by flag, then band level. Empty when no set is active or TBInfo is missing.
    // Crawled once per game-data set and class (GameDataCache.Derived): the result
    // depends on nothing else, and the Quest tab, the quest editor, the eligibility
    // check and the login flag sync each asked for it afresh, parsing every TBInfo
    // action chain each time.
    public static IReadOnlyList<CrawledQuest> Crawl(GameDataCache cache, int? classId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        string key = "quest crawl " + (classId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any class");
        return cache.Derived(key, () => CrawlSet(cache, classId));
    }

    private static IReadOnlyList<CrawledQuest> CrawlSet(GameDataCache cache, int? classId)
    {
        GameTable? tbinfo = cache.GetTable("TBInfo");
        if (tbinfo is null) return Array.Empty<CrawledQuest>();

        var rawChains = new List<string>();
        foreach (GameRow block in tbinfo.Rows)
        {
            if (!block.TryGetProperty("Action", out GameValue actionEl)) continue;
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

        // Pass 3: the tier ladder per multi-part flag, read from its give+test+check
        // minlevel gates (see DiscoverTierLadders). A flag absent here is single-part.
        IReadOnlyDictionary<int, IReadOnlyList<(int Step, int Level)>> ladders = DiscoverTierLadders(rawChains);

        // Pass 4: flags whose value climbs past 1 via `addability <flag>` increments —
        // a tier ladder keyed by the *ability value* rather than the give-step order, the
        // shape the give-step staircase can't see (its lone giveability sits at value 1
        // and the rest advance by relative addability). See DiscoverValueLadders.
        IReadOnlyDictionary<int, int> valueLadders = DiscoverValueLadders(rawChains, grantedFlags);

        // Pass 5: which alignment (if any) each flag's quest gates on — the Quest tab's
        // Evil/Neutral/Good checkboxes filter on this.
        IReadOnlyDictionary<int, AlignmentBucket> flagAlignments = DetectFlagAlignments(rawChains);

        // Pass 6: alignment "check" helper flags (GoodCheck/NeutralCheck/EvilCheck — 216/217/218
        // in Paradigm) — sub-markers granted only inside an alignment quest chain, never quests
        // in their own right. Dropped so they don't surface as standalone quests. See
        // DiscoverAlignmentHelperFlags.
        HashSet<int> helperFlags = DiscoverAlignmentHelperFlags(rawChains);

        var quests = new List<CrawledQuest>();
        foreach (IGrouping<int, ParsedChain> flagGroup in chains.GroupBy(c => c.Flag).OrderBy(g => g.Key))
        {
            int flag = flagGroup.Key;
            if (helperFlags.Contains(flag)) continue;
            List<ParsedChain> flagChains = flagGroup.ToList();
            (IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict) = ResolveRestrictions(flagChains);
            IReadOnlyDictionary<int, int>? classLevels = ResolveClassLevels(flagChains, classRestrict);
            AlignmentBucket? align = flagAlignments.TryGetValue(flag, out AlignmentBucket b) ? b : null;
            if (valueLadders.TryGetValue(flag, out int maxValue))
                quests.AddRange(CrawlValueLadder(flag, rawChains, maxValue, grantedFlags, classId, classRestrict, raceRestrict, classLevels, align));
            else if (ladders.TryGetValue(flag, out IReadOnlyList<(int Step, int Level)>? ladder))
                quests.AddRange(CrawlMultiPart(flag, flagChains, ladder, classId, classRestrict, raceRestrict, classLevels, align));
            else
                quests.Add(CrawlSinglePart(flag, flagChains, classId, classRestrict, raceRestrict, classLevels, align));
        }
        return quests;
    }

    // A flag is class-restricted only when *every real* giveability chain that grants it
    // carries a `class N` guard — then the allowed set is the union of those ids. If any
    // real granting chain is unguarded the quest is open to all classes (null). Same rule
    // for `race`. Conservative: it never hides a quest some unguarded chain leaves open.
    //
    // "Real" excludes a bare re-grant node — `failability <flag>:giveability <flag> N` with
    // no quest content. Such a node carries no class guard because its gating lives upstream
    // in the textblock flow the crawl can't reach, so counting it as an "open to all" grant
    // wrongly unrestricts a class-locked quest: Smash's lone continuation node did exactly
    // that, making it visible to every class. Only chains that do real quest work — an
    // explicit guard, an item exchange, a level gate, or a stat reward — vote on openness.
    //
    // Nor does a chain that can only run for a character who already holds the flag (a
    // turn-in gated `checkability <flag> 1` / `testability <flag> 1` ahead of its grant):
    // the flag got onto the character through an entry chain, so the entries alone say
    // who can take the quest. Conquest1's open turn-in (`testability 208 1:…:giveability
    // 208 2`, no class guard) made the quest every class's although only classes 1–4 can
    // start it (report paradigm-20261011-230844).
    private static (IReadOnlyList<int>?, IReadOnlyList<int>?) ResolveRestrictions(List<ParsedChain> chains)
    {
        List<ParsedChain> real = chains.Where(c => IsRealGrant(c) && !c.RequiresFlag).ToList();
        if (real.Count == 0) real = chains.Where(IsRealGrant).ToList(); // no real entry: every real grant
        if (real.Count == 0) real = chains; // all bare: nothing better to judge from

        IReadOnlyList<int>? classes = real.Any(c => c.ClassIds.Count == 0)
            ? null
            : real.SelectMany(c => c.ClassIds).Distinct().OrderBy(x => x).ToArray();
        IReadOnlyList<int>? races = real.Any(c => c.RaceIds.Count == 0)
            ? null
            : real.SelectMany(c => c.RaceIds).Distinct().OrderBy(x => x).ToArray();
        return (classes, races);
    }

    // The alignment each flag's quest requires, read from the goodaligned/evilaligned
    // gate directives on the chains that build it. A chain gating good-only tags the
    // flag Good, evil-only tags Evil, and one bracketed by BOTH (a "not too good, not
    // too evil" neutral range) tags Neutral — using only which gate types appear, never
    // their thresholds. Abilities 126/127/128 are the canonical Good/Neutral/Evil
    // alignment ladders, forced to their known bucket regardless of detection quirks;
    // the same gate-shape detection catches any other alignment-gated quest. A flag
    // with no alignment gate is absent from the map (not alignment-restricted).
    private static Dictionary<int, AlignmentBucket> DetectFlagAlignments(IReadOnlyList<string> rawChains)
    {
        var result = new Dictionary<int, AlignmentBucket>();
        foreach (string raw in rawChains)
        {
            bool hasGood = false, hasEvil = false;
            var flags = new HashSet<int>();
            foreach (string segment in raw.Split(':'))
            {
                string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                switch (p[0].ToLowerInvariant())
                {
                    case "goodaligned": hasGood = true; break;
                    case "evilaligned": hasEvil = true; break;
                    case "giveability" or "addability" when p.Length >= 2 && int.TryParse(p[1], out int f):
                        flags.Add(f);
                        break;
                }
            }
            if ((!hasGood && !hasEvil) || flags.Count == 0) continue;
            AlignmentBucket bucket = hasGood && hasEvil ? AlignmentBucket.Neutral
                                   : hasGood ? AlignmentBucket.Good
                                   : AlignmentBucket.Evil;
            foreach (int f in flags) result[f] = bucket;
        }
        // Canonical alignment ladders win over any per-chain detection quirk.
        foreach ((int flag, AlignmentBucket canon) in
                 new[] { (126, AlignmentBucket.Good), (127, AlignmentBucket.Neutral), (128, AlignmentBucket.Evil) })
            if (result.ContainsKey(flag)) result[flag] = canon;
        return result;
    }

    // The lowest level gate each restricted class faces for this quest: class Number →
    // minlevel, one entry per class in classRestrict that carries a level gate. Behind the
    // "Requires: Classes …" line so a multi-class ability quest can show each class's own
    // requirement (Priest-20, Mage-15) rather than a bare name. null when the quest isn't
    // class-restricted, or when no restricted class carries a gate (nothing to append). A
    // chain guarding a class already counts as a real grant (ClassIds.Count > 0 ⇒
    // IsRealGrant), so no separate real-grant filter is needed here.
    private static IReadOnlyDictionary<int, int>? ResolveClassLevels(
        List<ParsedChain> chains, IReadOnlyList<int>? classRestrict)
    {
        if (classRestrict is null || classRestrict.Count == 0) return null;
        var levels = new Dictionary<int, int>();
        foreach (int cid in classRestrict)
        {
            List<int> gates = chains
                .Where(c => c.ClassIds.Contains(cid) && c.MinLevel is int ml && ml > 0)
                .Select(c => c.MinLevel!.Value).ToList();
            if (gates.Count > 0) levels[cid] = gates.Min();
        }
        return levels.Count > 0 ? levels : null;
    }

    // A granting chain does real quest work — i.e. is an entry/progress step rather than a
    // bare re-grant continuation — when it carries a class/race guard, an item exchange, a
    // level gate, or a stat reward. (A `failability <flag>:giveability <flag> N` node has
    // none of these.)
    private static bool IsRealGrant(ParsedChain c) =>
        c.ClassIds.Count > 0 || c.RaceIds.Count > 0 || c.GiveItems.Count > 0
        || c.TakeItems.Count > 0 || (c.MinLevel is int ml && ml > 0) || c.Bonuses.Count > 0;

    // Every distinct giveability target across the data — the discovered quest-flag
    // set, used to tell stat rewards (addability off-set) from progress (on-set).
    internal static HashSet<int> DiscoverGrantedFlags(IEnumerable<string> rawChains)
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

    // The canonical Good / Neutral / Evil alignment quest flags.
    private static readonly int[] AlignmentFlags = { 126, 127, 128 };

    // Alignment "check" helper flags — GoodCheck / NeutralCheck / EvilCheck (216 / 217 / 218 in
    // Paradigm). Each is a sub-marker granted ONLY inside an alignment quest chain: its grant is
    // gated on being at a specific progress value of a canonical alignment flag (e.g.
    // `checkability 126 7 … giveability 216 1`), it hands the 2nd-alignment turn-in item, and it's
    // failability-gated at the pledge then reverts to 0 — so it never represents a completed quest
    // and must not surface as one. A granted flag qualifies when EVERY chain that grants it
    // check/tests a canonical alignment flag at value >= 1; the alignment flags themselves are
    // never treated as helpers (they cross-check each other in the conversion pledges).
    private static HashSet<int> DiscoverAlignmentHelperFlags(IEnumerable<string> rawChains)
    {
        var grantTotal = new Dictionary<int, int>();
        var grantGatedOnAlignment = new Dictionary<int, int>();
        foreach (string raw in rawChains)
        {
            int? granted = null;
            bool checksAlignment = false;
            foreach (string segment in raw.Split(':'))
            {
                string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                switch (p[0].ToLowerInvariant())
                {
                    case "giveability" when p.Length >= 3 && int.TryParse(p[1], out int gf):
                        granted = gf; // last giveability wins, matching ParseChain
                        break;
                    case "checkability" or "testability"
                        when p.Length >= 3 && int.TryParse(p[1], out int cf) && int.TryParse(p[2], out int cv)
                        && cv >= 1 && Array.IndexOf(AlignmentFlags, cf) >= 0:
                        checksAlignment = true;
                        break;
                }
            }
            if (granted is not int g) continue;
            grantTotal[g] = grantTotal.GetValueOrDefault(g) + 1;
            if (checksAlignment) grantGatedOnAlignment[g] = grantGatedOnAlignment.GetValueOrDefault(g) + 1;
        }

        var helpers = new HashSet<int>();
        foreach ((int flag, int total) in grantTotal)
            if (Array.IndexOf(AlignmentFlags, flag) < 0 && grantGatedOnAlignment.GetValueOrDefault(flag) == total)
                helpers.Add(flag);
        return helpers;
    }

    // The tier ladder of every multi-part flag, read from its progress gates. A flag's
    // gates are the (step, minlevel) pairs of every giveability/testability/checkability
    // segment that carries a minlevel — the give-step grants and the test/check progress
    // checks alike, because the alignment tiers gate-keep with all three and the give
    // gates alone reveal only some of the tiers. The chain's minlevel applies to every
    // ability segment in it. BuildLadder then decides whether those gates form a tiered
    // staircase; a flag with no valid ladder is single-part and absent from the result.
    private static IReadOnlyDictionary<int, IReadOnlyList<(int Step, int Level)>> DiscoverTierLadders(
        IEnumerable<string> rawChains)
    {
        var gates = new Dictionary<int, List<(int Step, int Level)>>();
        foreach (string raw in rawChains)
        {
            int? minLevel = null;
            var hits = new List<(int Flag, int Step)>();
            foreach (string segment in raw.Split(':'))
            {
                string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                switch (p[0].ToLowerInvariant())
                {
                    case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                        minLevel = HighestGate(minLevel, ml);
                        break;
                    case "giveability" or "testability" or "checkability"
                        when p.Length >= 3 && int.TryParse(p[1], out int gf) && int.TryParse(p[2], out int gs):
                        hits.Add((gf, gs));
                        break;
                }
            }
            if (minLevel is not int level || level <= 0) continue;
            foreach ((int flag, int step) in hits)
                (gates.TryGetValue(flag, out List<(int, int)>? g) ? g : gates[flag] = new()).Add((step, level));
        }

        var ladders = new Dictionary<int, IReadOnlyList<(int Step, int Level)>>();
        foreach ((int flag, List<(int Step, int Level)> g) in gates)
            if (BuildLadder(g) is { } ladder) ladders[flag] = ladder;
        return ladders;
    }

    // Reduce a flag's gates to its tier ladder, or null when they don't form one. Keep
    // the lowest gate step seen at each level, order by level, and require a strict
    // ascending staircase: >= 2 levels whose steps strictly increase as the levels
    // climb. Per-class variants of one step collapse to a single (step, level) and
    // never climb; a non-monotonic give-step mix (MageBane: step1@L15, step0@L35,
    // step4@L50) is not a give-step ladder — that flag is instead an ability-value
    // ladder, caught by DiscoverValueLadders.
    private static IReadOnlyList<(int Step, int Level)>? BuildLadder(IReadOnlyList<(int Step, int Level)> gates)
    {
        var minStepByLevel = new Dictionary<int, int>();
        foreach ((int step, int level) in gates)
            minStepByLevel[level] = minStepByLevel.TryGetValue(level, out int s) ? Math.Min(s, step) : step;

        if (minStepByLevel.Count < 2) return null;

        var ladder = minStepByLevel
            .OrderBy(kv => kv.Key)
            .Select(kv => (Step: kv.Value, Level: kv.Key))
            .ToArray();

        for (int i = 1; i < ladder.Length; i++)
            if (ladder[i].Step <= ladder[i - 1].Step) return null;

        return ladder;
    }

    // Flags whose progress is an *ability-value* ladder, mapped to the top value they
    // reach. A quest of this shape grants its flag once (`giveability <flag> 1`) and then
    // climbs by relative `addability <flag> 1` increments — so the give-step staircase
    // sees only the value-1 grant and collapses the whole quest to one starter card,
    // losing every later tier. MageBane (flag 50) is the canonical case: value 1→2→3→4
    // across four NPCs/maps. The discriminator is the `addability` advance into the
    // granted-flag set — no give-step quest (alignment et al.) uses it, so they stay on
    // the give-step path untouched. A flag qualifies only when it reaches value >= 2.
    private static IReadOnlyDictionary<int, int> DiscoverValueLadders(
        IReadOnlyCollection<string> rawChains, HashSet<int> grantedFlags)
    {
        var advanced = new HashSet<int>();
        foreach (string raw in rawChains)
            foreach (string segment in raw.Split(':'))
            {
                string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3 && p[0].Equals("addability", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(p[1], out int f) && grantedFlags.Contains(f))
                    advanced.Add(f);
            }

        // The top value is the highest AbilityValueOf any chain resolves to — not the
        // raw check/give segment values, which stop one short: the final tier gates on
        // value N (`checkability <flag> N`) then climbs to N+1 by `addability`, so the
        // climbed result is what bounds the ladder. AbilityValueOf is 0 for chains that
        // don't touch the flag, so summing over all chains is safe.
        var maxValue = new Dictionary<int, int>();
        foreach (int flag in advanced)
            foreach (string raw in rawChains)
            {
                int v = AbilityValueOf(raw, flag);
                if (v > maxValue.GetValueOrDefault(flag)) maxValue[flag] = v;
            }

        return advanced
            .Where(f => maxValue.GetValueOrDefault(f) >= 2)
            .ToDictionary(f => f, f => maxValue[f]);
    }

    // The ability value a chain pertains to for a value-laddered flag: the absolute value
    // a `giveability <flag> V` sets, else the value a relative `addability <flag> k` climbs
    // to from the `checkability`/`testability <flag> N` it gates on (N + k), else — for a
    // content/turn-in step that only gates on a value without advancing it — that gate
    // value N itself. Returns 0 for a "have the flag at all" gate (`checkability <flag> 0`,
    // the witchunter-only turn-in MageBane uses at its level-35 step); the caller folds
    // that into the entry band. Shared with QuestStepGraph so the crawl's banding and
    // the followable-step draft agree on which tier a chain belongs to.
    internal static int AbilityValueOf(string rawChain, int flag)
    {
        int? giveValue = null;
        int requiredValue = 0;
        int addIncrement = 0;
        foreach (string segment in rawChain.Split(':'))
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !int.TryParse(p[1], out int f) || f != flag || !int.TryParse(p[2], out int v))
                continue;
            switch (p[0].ToLowerInvariant())
            {
                case "giveability": giveValue = v; break; // absolute set, last wins
                case "addability": addIncrement += v; break; // relative climb
                case "checkability" or "testability": requiredValue = Math.Max(requiredValue, v); break;
            }
        }
        return giveValue ?? (addIncrement > 0 ? requiredValue + addIncrement : requiredValue);
    }

    // A value-laddered quest, banded by the distinct level gates its value axis carries:
    // one tier per `minlevel` the climb pauses to turn in at, not one card per ability
    // value. MageBane grants at value 1 (minlevel 15, the sword) and finishes at value 4
    // (minlevel 50) — two tiers, the ungated intermediate values 2–3 folding up into the
    // level-50 band. A band collects every chain whose ability value ceilings to its
    // boundary (the grant, each `addability` advance, the content/turn-in steps that gate
    // on a value without advancing it, and the value-0 "have the flag" proxy in the entry
    // band). Per band: the level is the lowest `minlevel` its chains declare (0 when
    // area-gated rather than level-gated), the keepers it hands — tagged with their ability
    // value as the give-step so each band awards its top-value keeper — are its award draft,
    // and stat bonuses class-resolve as elsewhere. ProgressByValue flags the band so the
    // step draft (QuestStepGraph) walks the same ability-value axis.
    private static IEnumerable<CrawledQuest> CrawlValueLadder(int flag, IReadOnlyList<string> rawChains,
        int maxValue, HashSet<int> grantedFlags, int? classId,
        IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict,
        IReadOnlyDictionary<int, int>? classLevels, AlignmentBucket? alignment)
    {
        var parsed = new List<(int Value, ParsedChain Chain)>();
        var takenAnywhere = new HashSet<int>();
        foreach (string raw in rawChains)
        {
            ParsedChain? chain = ParseValueChain(raw, flag, grantedFlags);
            if (chain is null) continue;
            foreach (int t in chain.TakeItems) takenAnywhere.Add(t);
            parsed.Add((AbilityValueOf(raw, flag), chain));
        }

        // Tier boundaries: the distinct ability values that carry a real level gate — the
        // points the climb pauses to turn in. Absent any, the whole ladder is one tier
        // topping out at maxValue.
        int[] boundaries = parsed
            .Where(p => p.Value >= 1 && p.Chain.MinLevel is int ml && ml > 0)
            .Select(p => p.Value).Distinct().OrderBy(v => v).ToArray();
        if (boundaries.Length == 0) boundaries = new[] { Math.Max(maxValue, 1) };

        for (int i = 0; i < boundaries.Length; i++)
        {
            int upper = boundaries[i];
            List<(int Value, ParsedChain Chain)> members = parsed
                .Where(p => BandUpperFor(p.Value, boundaries) == upper).ToList();
            List<ParsedChain> group = members.Select(m => m.Chain).ToList();

            int level = group.Where(c => c.MinLevel is int ml && ml > 0)
                .Select(c => c.MinLevel!.Value).DefaultIfEmpty(0).Min();
            IReadOnlyList<QuestBonus> bonuses = ResolveClass(
                group.Where(c => c.Bonuses.Count > 0).ToList(), classId);
            // Tag each keeper with its ability value as the give-step so AwardItemsFrom hands
            // the band its top-value keeper (the sword in the entry tier, the final reward in
            // the last), dropping the lower-value turn-in tokens.
            IReadOnlyList<int> awards = AwardItemsFrom(
                members.SelectMany(m => m.Chain.GiveItems
                    .Where(it => !takenAnywhere.Contains(it))
                    .Select(it => (it, m.Value, (IReadOnlyList<int>)m.Chain.ClassIds))).ToList(), classId);
            // Exp keys off the ability value (this axis's step), so tiers that share a value
            // don't double-count the same addexp.
            long exp = SumDistinctStepExp(members.Select(m => (m.Value, m.Chain.Exp)));

            int rangeStart = i == 0 ? 1 : boundaries[i - 1] + 1;
            int rangeEnd = i == boundaries.Length - 1 ? int.MaxValue : upper;

            yield return new CrawledQuest(
                flag, upper, level, bonuses, awards, classRestrict, raceRestrict, classLevels,
                BandOrdinal: i + 1, StepRangeStart: rangeStart, StepRangeEnd: rangeEnd,
                ProgressByValue: true, ExpAward: exp, RequiredAlignment: alignment,
                // A value-laddered band tops out at its own boundary value (MageBane 1→4);
                // its climbed ability value IS the completion mark.
                CompleteValue: DetectableValue(upper));
        }
    }

    // The tier boundary (upper value) a given ability value folds into: the first boundary
    // at or above it, or the top boundary when the value climbs past the last gate. The
    // value-0 "have the flag" proxy folds into the entry band like any sub-boundary value.
    private static int BandUpperFor(int value, int[] boundaries)
    {
        int v = value <= 0 ? 0 : value;
        foreach (int b in boundaries)
            if (v <= b) return b;
        return boundaries[^1];
    }

    // Parse a chain for the value-ladder pass: any chain that touches `flag` via a
    // give/add/check/test ability directive (an advance or a value gate), keeping the
    // level gate, class/race guards, stat bonuses (addability off the granted-flag set),
    // and items. Returns null when the chain doesn't touch the flag. Distinct from
    // ParseChain, which keys only off the terminal giveability and so never sees the
    // addability-advanced tiers.
    private static ParsedChain? ParseValueChain(string raw, int flag, HashSet<int> grantedFlags)
    {
        bool touches = false;
        int? minLevel = null;
        var classIds = new List<int>();
        var raceIds = new List<int>();
        var bonuses = new List<QuestBonus>();
        var giveItems = new List<int>();
        var takeItems = new List<int>();
        long exp = 0;

        foreach (string segment in raw.Split(':'))
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "giveability" or "addability" or "checkability" or "testability"
                    when p.Length >= 3 && int.TryParse(p[1], out int af) && int.TryParse(p[2], out int av):
                    if (af == flag) touches = true;
                    else if (p[0].Equals("addability", StringComparison.OrdinalIgnoreCase) && !grantedFlags.Contains(af))
                        bonuses.Add(new QuestBonus(af, av));
                    break;
                case "class" when p.Length >= 2 && int.TryParse(p[1], out int cid):
                    classIds.Add(cid);
                    break;
                case "race" when p.Length >= 2 && int.TryParse(p[1], out int rid):
                    raceIds.Add(rid);
                    break;
                case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                    minLevel = HighestGate(minLevel, ml);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    giveItems.Add(gi);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    takeItems.Add(ti);
                    break;
                case "addexp" when p.Length >= 2 && long.TryParse(p[1], out long xp):
                    exp += xp;
                    break;
            }
        }

        return touches
            ? new ParsedChain(flag, 0, minLevel, classIds, raceIds, bonuses, giveItems, takeItems, exp)
            : null;
    }

    // A single-part quest: one identity at step 0. Its level gate resolves to the
    // requested class; its stat bonus (if any) is the lowest-step reward group,
    // class-resolved; its award is the keeper(s) handed at the final give-step,
    // class-resolved (earlier giveitems are quest-use items, not the prize). A quest
    // that grants neither a keeper nor a stat bonus awards the ability itself — the
    // skill it teaches is the reward (Smash, Meditate, Perfect Stealth, SeeHidden).
    private static CrawledQuest CrawlSinglePart(int flag, List<ParsedChain> chains, int? classId,
        IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict,
        IReadOnlyDictionary<int, int>? classLevels, AlignmentBucket? alignment)
    {
        int requiredLevel = ResolveLevel(chains, classId, classRestrict);
        IReadOnlyList<QuestBonus> bonuses = ResolveLowestRewardBonuses(chains, classId);
        IReadOnlyList<int> awardItems = AwardItemsFrom(KeeperCandidates(chains), classId);
        bool awardsAbility = awardItems.Count == 0 && bonuses.Count == 0;
        long exp = SumDistinctStepExp(chains.Select(c => (c.GiveStep, c.Exp)));
        return new CrawledQuest(
            flag, 0, requiredLevel, bonuses, awardItems, classRestrict, raceRestrict, classLevels,
            AwardsAbility: awardsAbility, ExpAward: exp, RequiredAlignment: alignment,
            CompleteValue: DetectableValue(MaxGiveValue(chains)));
    }

    // The highest absolute value any `giveability <flag> V` sets across a flag's chains —
    // the terminal value a completed give-step quest reaches. 0 when the flag is only ever
    // set to 0 (Perfect Stealth) or never set absolutely.
    private static int MaxGiveValue(IEnumerable<ParsedChain> chains) =>
        chains.Select(c => c.GiveStep).DefaultIfEmpty(0).Max();

    // A completion value the client can actually detect via `abil`, or null: a flag that
    // tops out at 0 reads identically whether or not the quest is done, so it can't be
    // auto-marked and is left to the manual checkbox / the editor override.
    private static int? DetectableValue(int value) => value > 0 ? value : null;

    // A multi-part quest: one quest per ladder tier. Each band carries the reward group
    // and keeper items that fall in it, class-resolved; its required level is the band
    // (ladder) level itself. The ladder — one (step, level) per tier, ascending with
    // strictly-climbing steps — doubles as the bonus-banding milestones and the give-
    // step range source: band i spans [ladder step of band i, ladder step of band i+1
    // minus 1], band 1 lowered to 1 (absorbing pre-first steps) and the last band's
    // upper bound raised to int.MaxValue (absorbing overflow past the final tier), so no
    // give-step is ever dropped from every band's checklist.
    private static IEnumerable<CrawledQuest> CrawlMultiPart(int flag, List<ParsedChain> chains,
        IReadOnlyList<(int Step, int Level)> ladder, int? classId,
        IReadOnlyList<int>? classRestrict, IReadOnlyList<int>? raceRestrict,
        IReadOnlyDictionary<int, int>? classLevels, AlignmentBucket? alignment)
    {
        var bandLevels = ladder.Select(m => m.Level).ToHashSet();

        var bandBonuses = new Dictionary<int, IReadOnlyList<QuestBonus>>();
        foreach (IGrouping<int, ParsedChain> rewardGroup in chains.Where(c => c.Bonuses.Count > 0).GroupBy(c => c.GiveStep))
        {
            List<ParsedChain> group = rewardGroup.ToList();
            int band = ResolveBand(group, bandLevels, ladder);
            if (band > 0) bandBonuses[band] = ResolveClass(group, classId);
        }

        var bandKeepers = new Dictionary<int, List<(int Item, int Step, IReadOnlyList<int> ClassIds)>>();
        foreach (ParsedChain chain in chains)
            foreach (int item in chain.GiveItems.Where(i => !TakenAnywhere(chains, i)))
            {
                int band = ResolveBand(new[] { chain }, bandLevels, ladder);
                if (band == 0) continue;
                List<(int, int, IReadOnlyList<int>)> bucket = bandKeepers.TryGetValue(band, out List<(int, int, IReadOnlyList<int>)>? b)
                    ? b : (bandKeepers[band] = new List<(int, int, IReadOnlyList<int>)>());
                bucket.Add((item, chain.GiveStep, chain.ClassIds));
            }

        // Attribute each exp-bearing chain to its tier the same way keepers are, so a
        // band's experience is only the addexp handed at its own give-steps.
        var bandExpSteps = new Dictionary<int, List<(int Step, long Exp)>>();
        foreach (ParsedChain chain in chains.Where(c => c.Exp > 0))
        {
            int band = ResolveBand(new[] { chain }, bandLevels, ladder);
            if (band == 0) continue;
            List<(int, long)> bucket = bandExpSteps.TryGetValue(band, out List<(int, long)>? es)
                ? es : (bandExpSteps[band] = new List<(int, long)>());
            bucket.Add((chain.GiveStep, chain.Exp));
        }

        for (int i = 0; i < ladder.Count; i++)
        {
            int level = ladder[i].Level;
            IReadOnlyList<QuestBonus> bonuses = bandBonuses.TryGetValue(level, out IReadOnlyList<QuestBonus>? bb)
                ? bb : Array.Empty<QuestBonus>();
            IReadOnlyList<int> items = bandKeepers.TryGetValue(level, out List<(int, int, IReadOnlyList<int>)>? bk)
                ? AwardItemsFrom(bk, classId) : Array.Empty<int>();
            long exp = bandExpSteps.TryGetValue(level, out List<(int, long)>? xs) ? SumDistinctStepExp(xs) : 0;

            int rangeStart = i == 0 ? 1 : ladder[i].Step;
            int rangeEnd = i == ladder.Count - 1 ? int.MaxValue : ladder[i + 1].Step - 1;

            // A tier is complete after its reward hand-in, which is the next tier's gate step
            // (user, 2026-10-17; GAME_MECHANICS "Quest-flag values (`abil` / `sys god abil`)"):
            // the highest actual give value up to that gate, so Paradigm's Evil tiers complete
            // at 3 / 4 / 12 / 14 and the last tier at the highest give it reaches.
            int completeBound = i == ladder.Count - 1 ? rangeEnd : ladder[i + 1].Step;
            int? completeValue = DetectableValue(
                chains.Select(c => c.GiveStep).Where(v => v <= completeBound).DefaultIfEmpty(0).Max());

            yield return new CrawledQuest(
                flag, level, level, bonuses, items, classRestrict, raceRestrict, classLevels,
                BandOrdinal: i + 1, StepRangeStart: rangeStart, StepRangeEnd: rangeEnd, ExpAward: exp,
                RequiredAlignment: alignment, CompleteValue: completeValue);
        }
    }

    // The lowest level the quest can be taken at, resolved to the class: the class's
    // own minlevel branch when it has one, else the no-class branch. When neither
    // resolves and the quest is class-restricted, the only gates left belong to *other*
    // classes — their minimum is meaningless to a viewer who can't take the quest (a
    // Warrior viewing Meditate, a Cleric-and-up skill quest) or who hasn't picked a
    // class at all (the classless default profile). Report no gate (0) rather than that
    // misleading number; the card's "Requires: Classes …" line already says who it's
    // for. An unrestricted quest that still carries only per-class gates falls back to
    // the global minimum as a best estimate. 0 also means the quest imposes no gate.
    private static int ResolveLevel(List<ParsedChain> chains, int? classId, IReadOnlyList<int>? classRestrict)
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

        if (classRestrict is not null) return 0;

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
    // else the level of the nearest milestone *strictly before* the group's give-step —
    // the tier the player has just completed. A reward handed at a tier's own gate step
    // marks that tier unlocking, so it belongs to the prior (completed) tier, not the one
    // opening at that step; strictly-less banding is what attributes each alignment award
    // (ring → tier 1, stats → tier 2, chest → tier 3, …) to the tier it actually caps.
    private static int ResolveBand(
        IReadOnlyList<ParsedChain> group, HashSet<int> bandLevels, IReadOnlyList<(int Step, int Level)> milestones)
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
            if (m.Step < step && m.Step > bestStep)
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

    // Keeper candidates: every giveitem the flag hands out and never takes back, tagged
    // with the give-step it arrives at and its chain's class guard. A giveitem later
    // takeitem'd under the same flag is a turn-in token, not a reward, and is excluded.
    private static IReadOnlyList<(int Item, int Step, IReadOnlyList<int> ClassIds)> KeeperCandidates(List<ParsedChain> chains) =>
        chains.SelectMany(c => c.GiveItems
            .Where(i => !TakenAnywhere(chains, i))
            .Select(i => (i, c.GiveStep, (IReadOnlyList<int>)c.ClassIds))).ToList();

    // The award items from a pool of keeper candidates: the keepers handed at the
    // highest give-step — a quest's prize comes at the very end of its chain, so any
    // keeper handed earlier is a quest-use item the player consumes, not a reward — then
    // class-resolved like a stat bonus: the requested class's own items when any final-
    // step keeper is guarded to it, else the no-class defaults (and, failing both, the
    // whole final-step set so an award is never silently dropped). Distinct, first-seen.
    private static IReadOnlyList<int> AwardItemsFrom(
        IReadOnlyList<(int Item, int Step, IReadOnlyList<int> ClassIds)> keepers, int? classId)
    {
        if (keepers.Count == 0) return Array.Empty<int>();

        int finalStep = keepers.Max(k => k.Step);
        List<(int Item, int Step, IReadOnlyList<int> ClassIds)> final = keepers.Where(k => k.Step == finalStep).ToList();

        List<(int Item, int Step, IReadOnlyList<int> ClassIds)> pool =
            classId is int cid && final.Any(k => k.ClassIds.Contains(cid))
                ? final.Where(k => k.ClassIds.Contains(cid)).ToList()
                : final.Where(k => k.ClassIds.Count == 0).ToList();
        if (pool.Count == 0) pool = final;

        var result = new List<int>();
        foreach ((int item, _, _) in pool)
            if (!result.Contains(item)) result.Add(item);
        return result;
    }

    // Total experience across a set of (step, exp) grants: one value per distinct step
    // (the max, though class-branched chains repeat the same figure), summed. Keying on
    // the step is what stops a quest whose exp grant fans out across 15 class variants
    // from reporting 15× the award. Zero/negative addexp entries don't contribute. A
    // long: MudRev hands 500,000,000 a step, and a quest's steps pass int's range.
    private static long SumDistinctStepExp(IEnumerable<(int Step, long Exp)> steps) =>
        steps.Where(s => s.Exp > 0)
            .GroupBy(s => s.Step)
            .Sum(g => g.Max(s => s.Exp));

    private static bool TakenAnywhere(List<ParsedChain> chains, int item) =>
        chains.Any(c => c.TakeItems.Contains(item));

    // Parse one chain into its quest-relevant directives. Returns null when the
    // chain grants no quest flag. "Last giveability wins" matches the game's
    // terminal-grant semantics.
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
        long exp = 0;
        // Abilities this line asks to be held, before it changes them itself.
        var heldGates = new HashSet<int>();
        var changed = new HashSet<int>();

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
                    changed.Add(gf);
                    break;
                case "removeability" when p.Length >= 2 && int.TryParse(p[1], out int rf):
                    changed.Add(rf);
                    break;
                // `checkability <a> N` passes at a value of N or more, `testability <a> N` at
                // N or less and never without the ability when N >= 0 (GAME_MECHANICS "NPC
                // keyword hand-over detection"), so either, at those values, needs it held.
                case "checkability" when p.Length >= 3 && int.TryParse(p[1], out int ca)
                    && int.TryParse(p[2], out int cv) && cv >= 1 && !changed.Contains(ca):
                    heldGates.Add(ca);
                    break;
                case "testability" when p.Length >= 3 && int.TryParse(p[1], out int ta)
                    && int.TryParse(p[2], out int tv) && tv >= 0 && !changed.Contains(ta):
                    heldGates.Add(ta);
                    break;
                case "addability" when p.Length >= 3
                    && int.TryParse(p[1], out int af) && int.TryParse(p[2], out int av):
                    if (!grantedFlags.Contains(af)) bonuses.Add(new QuestBonus(af, av));
                    changed.Add(af);
                    break;
                case "class" when p.Length >= 2 && int.TryParse(p[1], out int cid):
                    classIds.Add(cid);
                    break;
                case "race" when p.Length >= 2 && int.TryParse(p[1], out int rid):
                    raceIds.Add(rid);
                    break;
                case "minlevel" when p.Length >= 2 && int.TryParse(p[1], out int ml):
                    minLevel = HighestGate(minLevel, ml);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    giveItems.Add(gi);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    takeItems.Add(ti);
                    break;
                case "addexp" when p.Length >= 2 && long.TryParse(p[1], out long xp):
                    exp += xp; // the completion experience handed alongside this give-step
                    break;
            }
        }

        if (flag is null) return null;
        return new ParsedChain(flag.Value, giveStep, minLevel, classIds, raceIds, bonuses, giveItems, takeItems, exp,
            RequiresFlag: heldGates.Contains(flag.Value));
    }

    // The level a chain with more than one `minlevel` asks for: the highest of them.
    // MajorMUD runs a chain left to right and stops it at the first `minlevel` the
    // character is below, so every gate has to be met to reach the grant and a later,
    // lower number never relaxes an earlier one. Paradigm's Mystic Meditate chain reads
    // `minlevel 27 … minlevel 23` and the game refuses below 27; Stock's Meditate chains
    // read `minlevel 20 … minlevel 27`, an intro gate under the class's own.
    // GAME_MECHANICS "Quest level gates".
    internal static int HighestGate(int? soFar, int gate) => soFar is int g && g > gate ? g : gate;

    // Scratch record for one parsed chain; never escapes the crawl. RequiresFlag: the
    // line runs only for a character already holding its own flag (see
    // ResolveRestrictions).
    private sealed record ParsedChain(
        int Flag,
        int GiveStep,
        int? MinLevel,
        List<int> ClassIds,
        List<int> RaceIds,
        List<QuestBonus> Bonuses,
        List<int> GiveItems,
        List<int> TakeItems,
        long Exp,
        bool RequiresFlag = false);
}
