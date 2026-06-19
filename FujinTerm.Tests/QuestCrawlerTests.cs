using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Quests;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.10a — <see cref="QuestCrawler"/> discovers quests and their rewards from
/// <c>TBInfo</c> chains. Discovery is data-driven: every <c>giveability</c> target
/// is a quest, including skill grants (Smash 32, Meditate 187, Perfect Stealth 186);
/// the only number-based notion is gone. Cases reproduce shapes verified against
/// real data-v1.11p: single-part quests key to step 0; a flag whose give/test/check
/// minlevel gates form a strict per-level staircase splits into level tiers (the
/// test/check gates surface tiers the give gates alone miss) while per-class variants
/// of one step and non-monotonic gate sets stay single-part; <c>addability</c> off
/// the quest-flag set are stat rewards and
/// resolve to the requested class with a no-class default; <c>giveitem</c> never
/// taken back is a keeper award. Synthetic seeded caches keep it deterministic and
/// CI-portable, matching ClassCapabilitiesTests.
/// </summary>
public sealed class QuestCrawlerTests : IDisposable
{
    private readonly string _root;

    public QuestCrawlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-questcrawl-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Seeds a TBInfo table (one Action string per chain) into a set and activates it.
    private GameDataCache CacheWithTbInfo(params string[] actions)
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        var rows = actions.Select(a => new Dictionary<string, object> { ["Action"] = a }).ToArray();
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(rows));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static CrawledQuest Find(IReadOnlyList<CrawledQuest> all, int flag, int step) =>
        all.Single(q => q.Flag == flag && q.Step == step);

    [Fact]
    public void Crawl_NoTbInfoTable_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "test-set"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");

        Assert.Empty(QuestCrawler.Crawl(cache, classId: null));
    }

    [Fact]
    public void Crawl_SingleFlagQuest_EmitsStepZeroWithClasslessBonus()
    {
        // Flag 125 (Ice Sorceress): a single reward chain, no class branch.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 125 2:minlevel 15:addability 2 1"), classId: 7);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(125, q.Flag);
        Assert.Equal(0, q.Step);              // single-part quest keys to step 0
        Assert.Equal(15, q.RequiredLevel);
        Assert.Equal(new[] { new QuestBonus(2, 1) }, q.Bonuses);
        Assert.Empty(q.AwardItems);
    }

    [Fact]
    public void Crawl_GateOnlyFlag_EmitsQuestWithoutReward()
    {
        // Flag 133 (Phoenix) reduced to gate steps with no reward: still a quest.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 133 1",
                "giveability 133 5",
                "giveability 133 9"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(133, q.Flag);
        Assert.Equal(0, q.Step);
        Assert.Equal(0, q.RequiredLevel);
        Assert.Empty(q.Bonuses);
        Assert.Empty(q.AwardItems);
    }

    [Fact]
    public void Crawl_SkillGiveability_IsDiscoveredAsQuest()
    {
        // giveability 32 1 is Smash, granted by an NPC after a turn-in — that makes
        // it a quest, not something to filter out. The level gate resolves per class.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:minlevel 22:takeitem 1247:giveability 32 1",
                "class 2:minlevel 20:takeitem 1247:giveability 32 1"), classId: 2);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(32, q.Flag);
        Assert.Equal(0, q.Step);            // per-class minlevels share one step → single-part
        Assert.Equal(20, q.RequiredLevel);  // class 2's gate
        Assert.Empty(q.Bonuses);
        Assert.Empty(q.AwardItems);         // the turn-in token is taken back, not kept
    }

    [Fact]
    public void Crawl_PerClassSkillQuest_ResolvesViewingClassGate_NotGlobalMinimum()
    {
        // Meditate (flag 187): a class-restricted skill quest gated per class — an
        // intro `minlevel 20` shared by every branch, then a per-class `minlevel`
        // (last-wins) that is the real requirement. Only Paladin(3)/Cleric(4)/Priest(5)
        // can learn it here. The viewing class must see *its own* gate; a class outside
        // the list — or no class at all — must see no gate, never the global minimum of
        // the other classes' gates (which would surface a misleading "Level 20" on a
        // Warrior who can't meditate).
        string[] chains =
        {
            "check class:class 3:minlevel 20 2614:takeitem 1351:addexp 5000:minlevel 27:failability 187:giveability 187 1",
            "check class:class 4:minlevel 20 2614:takeitem 1351:addexp 5000:minlevel 23:failability 187:giveability 187 1",
            "check class:class 5:minlevel 20 2614:takeitem 1351:addexp 5000:minlevel 20:failability 187:giveability 187 1",
        };

        Assert.Equal(27, Find(QuestCrawler.Crawl(CacheWithTbInfo(chains), classId: 3), 187, 0).RequiredLevel);
        Assert.Equal(20, Find(QuestCrawler.Crawl(CacheWithTbInfo(chains), classId: 5), 187, 0).RequiredLevel);

        // Warrior(1) is excluded → no applicable gate (not the others' min of 20).
        Assert.Equal(0, Find(QuestCrawler.Crawl(CacheWithTbInfo(chains), classId: 1), 187, 0).RequiredLevel);
        // Classless default profile → no gate either.
        Assert.Equal(0, Find(QuestCrawler.Crawl(CacheWithTbInfo(chains), classId: null), 187, 0).RequiredLevel);
    }

    [Fact]
    public void Crawl_AddabilityIntoQuestFlag_IsProgressNotReward()
    {
        // addability 50 targets a discovered quest flag → progress marker, filtered;
        // addability 4 (max damage) is off the flag set → a real stat reward.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 50 1:addability 50 1:addability 4 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(50, q.Flag);
        Assert.Equal(new[] { new QuestBonus(4, 1) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_KeeperItem_SurfacedAsAward_TurnInTokenExcluded()
    {
        // Flag 130: hand over item 499 (turn-in token), keep items 406 and 431 (the
        // rewards) plus a stat — only the kept items become awards.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "takeitem 499:giveitem 406:giveitem 431:minlevel 15:giveability 130 2:addability 22 3"),
            classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(130, q.Flag);
        Assert.Equal(new[] { 406, 431 }, q.AwardItems);
        Assert.Equal(new[] { new QuestBonus(22, 3) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_LastGiveabilityWins()
    {
        // A chain that grants two flags terminally grants the last one, so the reward
        // attaches to 130 and 125 is not discovered from this chain.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 125 1:giveability 130 2:addability 22 3"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(130, q.Flag);
        Assert.Equal(new[] { new QuestBonus(22, 3) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_PerClassMinlevelVariants_StayOneQuest()
    {
        // Meditate (187): every class takes the same step 1, differing only in the
        // level gate — one minlevel band, not many, so it stays a single-part quest.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 3:minlevel 27:takeitem 1351:giveability 187 1",
                "class 5:minlevel 20:takeitem 1351:giveability 187 1",
                "class 6:minlevel 23:takeitem 1351:giveability 187 1"), classId: 5);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(187, q.Flag);
        Assert.Equal(0, q.Step);
        Assert.Equal(20, q.RequiredLevel);
    }

    [Fact]
    public void Crawl_MultiPartFlag_SplitsIntoMinlevelBands()
    {
        // Flag 126 (Good Alignment) reduced to four giveability minlevel gates climbing
        // across give-steps → four tier quests. The lone reward group (give-step 8)
        // falls in the L20 band by the nearest milestone at/before its step (give-step
        // 7 = L20), even though its own chains declare no minlevel. (The real set has a
        // fifth tier surfaced only by the test/check gates; see the ladder-refines test.)
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 126 4:minlevel 10",
                "giveability 126 7:minlevel 20",
                "giveability 126 10:minlevel 30",
                "giveability 126 18:minlevel 40",
                "giveability 126 8:addability 4 1",
                "class 6:giveability 126 8:addability 117 6:addability 118 6:addability 27 1:addability 69 4"),
            classId: 6);

        Assert.Equal(new[] { 10, 20, 30, 40 }, quests.Where(q => q.Flag == 126).Select(q => q.Step).OrderBy(s => s));
        Assert.Empty(Find(quests, 126, 10).Bonuses);
        Assert.Empty(Find(quests, 126, 30).Bonuses);
        Assert.Empty(Find(quests, 126, 40).Bonuses);

        CrawledQuest band2 = Find(quests, 126, 20);
        Assert.Equal(20, band2.RequiredLevel);
        Assert.Equal(
            new[] { new QuestBonus(117, 6), new QuestBonus(118, 6), new QuestBonus(27, 1), new QuestBonus(69, 4) },
            band2.Bonuses);
    }

    [Fact]
    public void Crawl_MultiPartReward_ResolvesClassDefaultWhenNoBranch()
    {
        // Same 126 structure, but resolve for a class with no specific branch
        // (Warrior=1) and for the no-class request — both get the default bonus.
        string[] tbinfo =
        {
            "giveability 126 4:minlevel 10",
            "giveability 126 7:minlevel 20",
            "giveability 126 10:minlevel 30",
            "giveability 126 18:minlevel 40",
            "giveability 126 8:addability 4 1",
            "class 6:giveability 126 8:addability 117 6:addability 118 6:addability 27 1:addability 69 4",
        };

        Assert.Equal(
            new[] { new QuestBonus(4, 1) },
            Find(QuestCrawler.Crawl(CacheWithTbInfo(tbinfo), classId: 1), 126, 20).Bonuses);
        Assert.Equal(
            new[] { new QuestBonus(4, 1) },
            Find(QuestCrawler.Crawl(CacheWithTbInfo(tbinfo), classId: null), 126, 20).Bonuses);
    }

    [Fact]
    public void Crawl_MultiPartReward_BandFromDeclaredMinlevel_WhenClassChainOmitsIt()
    {
        // Flag 128 (Evil) structure: the reward group sits at give-step 4, above the
        // L10 gate (give-step 3) — the nearest-milestone rule alone would mis-band it
        // to L10. The default reward declares its own minlevel 20, so the whole group
        // (including the class-5 chain that omits minlevel) lands in the L20 band.
        string[] tbinfo =
        {
            "giveability 128 3:minlevel 10",
            "giveability 128 13:minlevel 40",
            "giveability 128 4:minlevel 20:addability 4 1",
            "class 5:giveability 128 4:addability 69 10:addability 70 1",
        };

        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(CacheWithTbInfo(tbinfo), classId: 5);

        Assert.Equal(new[] { 10, 20, 40 }, quests.Where(q => q.Flag == 128).Select(q => q.Step).OrderBy(s => s));
        Assert.Empty(Find(quests, 128, 10).Bonuses);
        Assert.Empty(Find(quests, 128, 40).Bonuses);
        Assert.Equal(
            new[] { new QuestBonus(69, 10), new QuestBonus(70, 1) },
            Find(quests, 128, 20).Bonuses);
    }

    [Fact]
    public void Crawl_EveryGiveabilityChainClassGuarded_RestrictsToUnionOfClasses()
    {
        // Smash (32): both granting chains carry a `class N` guard → the quest is
        // class-restricted to the union {1,2}; race is unguarded → open to all races.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:minlevel 22:takeitem 1247:giveability 32 1",
                "class 2:minlevel 20:takeitem 1247:giveability 32 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(new[] { 1, 2 }, q.ClassIds);
        Assert.Null(q.RaceIds);
    }

    [Fact]
    public void Crawl_AnyUnguardedRealGiveabilityChain_LeavesQuestOpenToAllClasses()
    {
        // One granting chain is class-guarded, another is unguarded but does real quest
        // work (a stat reward) → conservative rule keeps the quest open to everyone
        // (ClassIds null), since some genuine path needs no class.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:giveability 60 1",
                "giveability 60 2:addability 4 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Null(q.ClassIds);
        Assert.Null(q.RaceIds);
    }

    [Fact]
    public void Crawl_DegenerateContinuationChain_DoesNotUnrestrictAClassLockedQuest()
    {
        // Smash's real shape: every entry grant is class-guarded, plus a bare
        // `failability 32:giveability 32 1` re-grant node with no quest content — its class
        // gate lives upstream in the textblock flow the crawl can't see. That bare node must
        // NOT count as an "open to all" path (the bug that made Smash visible to every
        // class); the quest stays restricted to the union {1,2} of its real grants.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:minlevel 22:takeitem 1247:giveability 32 1",
                "class 2:minlevel 20:takeitem 1247:giveability 32 1",
                "failability 32:giveability 32 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(new[] { 1, 2 }, q.ClassIds);
        Assert.Null(q.RaceIds);
    }

    [Fact]
    public void Crawl_EveryGiveabilityChainRaceGuarded_RestrictsToUnionOfRaces()
    {
        // A race-locked quest: every granting chain carries a `race N` guard → restricted
        // to the union {13}; class is unguarded → open to all classes.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "race 13:minlevel 5:giveability 57 1",
                "race 13:minlevel 5:giveability 57 2"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Null(q.ClassIds);
        Assert.Equal(new[] { 13 }, q.RaceIds);
    }

    [Fact]
    public void Crawl_UnrestrictedQuest_HasNullClassAndRaceSets()
    {
        // A plain single-part quest with no class/race guard anywhere → open to all.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 125 2:minlevel 15:addability 2 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Null(q.ClassIds);
        Assert.Null(q.RaceIds);
    }

    [Fact]
    public void Crawl_MultiPartFlag_PropagatesRestrictionToEveryBand()
    {
        // A class-guarded multi-part flag: each band inherits the same {3,6} restriction.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 3:giveability 150 4:minlevel 10",
                "class 6:giveability 150 7:minlevel 20",
                "class 3:giveability 150 10:minlevel 30",
                "class 6:giveability 150 18:minlevel 40"), classId: null);

        Assert.Equal(new[] { 10, 20, 30, 40 }, quests.Select(q => q.Step).OrderBy(s => s));
        Assert.All(quests, q => Assert.Equal(new[] { 3, 6 }, q.ClassIds));
    }

    [Fact]
    public void Crawl_MultiPartKeeperItems_AttachToTheirBand()
    {
        // A two-band flag where each band's reward step hands over a keeper item:
        // band L10 gives item 700, band L20 gives item 800 — each lands in its band.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 140 2:minlevel 10",
                "giveability 140 8:minlevel 20",
                "giveability 140 3:minlevel 10:giveitem 700",
                "giveability 140 9:minlevel 20:giveitem 800"),
            classId: null);

        Assert.Equal(new[] { 700 }, Find(quests, 140, 10).AwardItems);
        Assert.Equal(new[] { 800 }, Find(quests, 140, 20).AwardItems);
    }

    [Fact]
    public void Crawl_MultiPartFlag_NumbersBandsAndCarriesGiveStepRanges()
    {
        // Flag 126 (Good): milestones 4→L10, 7→L20, 10→L30, 18→L40. Bands number
        // 1..4 in ascending level order; each band's give-step range spans from its
        // own milestone step to one below the next band's — band 1 lowered to 1 to
        // absorb pre-first steps, the last band raised to int.MaxValue to absorb
        // every overflow step. This mirrors the bonus-banding (nearest-milestone) rule.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 126 4:minlevel 10",
                "giveability 126 7:minlevel 20",
                "giveability 126 10:minlevel 30",
                "giveability 126 18:minlevel 40"),
            classId: null);

        CrawledQuest b1 = Find(quests, 126, 10);
        CrawledQuest b2 = Find(quests, 126, 20);
        CrawledQuest b3 = Find(quests, 126, 30);
        CrawledQuest b4 = Find(quests, 126, 40);

        Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { b1.BandOrdinal, b2.BandOrdinal, b3.BandOrdinal, b4.BandOrdinal });

        Assert.Equal((1, 6), (b1.StepRangeStart, b1.StepRangeEnd));
        Assert.Equal((7, 9), (b2.StepRangeStart, b2.StepRangeEnd));
        Assert.Equal((10, 17), (b3.StepRangeStart, b3.StepRangeEnd));
        Assert.Equal((18, int.MaxValue), (b4.StepRangeStart, b4.StepRangeEnd));
    }

    [Fact]
    public void Crawl_SinglePartQuest_HasNoBandOrdinalOrStepRange()
    {
        // A single-part quest carries no band identity: ordinal 0 and an empty (0,0)
        // step range, so StepLines emits every give-step unfiltered.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 125 2:minlevel 15:addability 2 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(0, q.BandOrdinal);
        Assert.Equal((0, 0), (q.StepRangeStart, q.StepRangeEnd));
    }

    [Fact]
    public void Crawl_TestabilityGates_AddTierTheGiveGatesMiss()
    {
        // Real alignment shape: the giveability minlevel gates stop at L40 (four tiers),
        // but a fifth tier (L50) is gate-kept only by a test/check progress chain with
        // no giveability of its own. The ladder is built from give+test+check gates
        // together, so the L50 tier appears — and its give-step range opens at the
        // test-gate step (19), with the prior tier's upper bound shifted down to 18.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 126 4:minlevel 10",
                "giveability 126 7:minlevel 20",
                "giveability 126 10:minlevel 30",
                "giveability 126 18:minlevel 40",
                "testability 126 19:checkability 126 19:minlevel 50"),
            classId: null);

        Assert.Equal(
            new[] { 10, 20, 30, 40, 50 },
            quests.Where(q => q.Flag == 126).Select(q => q.Step).OrderBy(s => s));

        CrawledQuest tier4 = Find(quests, 126, 40);
        CrawledQuest tier5 = Find(quests, 126, 50);
        Assert.Equal(5, tier5.BandOrdinal);
        Assert.Equal((18, 18), (tier4.StepRangeStart, tier4.StepRangeEnd));
        Assert.Equal((19, int.MaxValue), (tier5.StepRangeStart, tier5.StepRangeEnd));
    }

    [Fact]
    public void Crawl_SinglePartAward_IsFinalStepKeeperOnly_MidChainItemsAreQuestUse()
    {
        // Phoenix (133) shape: the chain hands a note (step 2), paper (step 3), rod
        // (step 7) and key (step 8) used along the way, then the phoenix feather at the
        // final step 9. Only the last keeper is the prize; the mid-chain items are
        // consumed, not awarded — so the award is the feather alone.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 133 2:giveitem 989",
                "giveability 133 3:giveitem 991",
                "giveability 133 7:giveitem 996",
                "giveability 133 8:giveitem 987",
                "giveability 133 9:giveitem 1000"),
            classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(new[] { 1000 }, q.AwardItems);
        Assert.False(q.AwardsAbility);
    }

    [Fact]
    public void Crawl_SinglePartNoKeeperNoBonus_AwardsTheAbilityItself()
    {
        // Smash (32): the turn-in token is taken back and the quest grants no stat — the
        // reward *is* the Smash skill it teaches, so AwardsAbility flags the flag itself.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:minlevel 22:takeitem 1247:giveability 32 1",
                "class 2:minlevel 20:takeitem 1247:giveability 32 1"), classId: 2);

        CrawledQuest q = Assert.Single(quests);
        Assert.Empty(q.AwardItems);
        Assert.Empty(q.Bonuses);
        Assert.True(q.AwardsAbility);
    }

    [Fact]
    public void Crawl_SinglePartWithKeeper_DoesNotAwardAbility()
    {
        // A quest that hands a kept item is rewarded by that item, not the ability — the
        // ability-award signal stays off whenever a keeper (or a stat bonus) is present.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 134 12:giveitem 1180"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(new[] { 1180 }, q.AwardItems);
        Assert.False(q.AwardsAbility);
    }

    [Fact]
    public void Crawl_BoundaryStepReward_BandsToTheCompletedTier_NotTheOpeningOne()
    {
        // A keeper handed at a tier's own gate step marks that tier *unlocking*, so it
        // caps the tier just completed. Milestones 5→L10, 10→L20: the ring at give-step
        // 10 (where L20 opens) is the reward for finishing tier 1, banding to L10 — the
        // strictly-less rule, the fix for alignment rings landing a tier too high.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 200 5:minlevel 10",
                "giveability 200 10:minlevel 20",
                "giveability 200 10:giveitem 900"),
            classId: null);

        Assert.Equal(new[] { 900 }, Find(quests, 200, 10).AwardItems);
        Assert.Empty(Find(quests, 200, 20).AwardItems);
    }

    [Fact]
    public void Crawl_MultiPartBand_PrunesMidTierQuestUseItem_KeepsFinalStepAward()
    {
        // Evil (128) tier shape: within one band a cloth pouch (step 6) is used mid-tier
        // and the chest (step 12) is the tier's prize. Milestones 4→L10, 14→L20 put both
        // keepers in the L10 band (strictly-less of 14), but only the final-step keeper
        // survives — the pouch is dropped as quest-use, the chest is the award.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 128 4:minlevel 10",
                "giveability 128 14:minlevel 20",
                "giveability 128 6:giveitem 960",
                "giveability 128 12:giveitem 958"),
            classId: null);

        Assert.Equal(new[] { 958 }, Find(quests, 128, 10).AwardItems);
        Assert.Empty(Find(quests, 128, 20).AwardItems);
    }

    [Fact]
    public void Crawl_MultiPartBand_ClassResolvesFinalStepAward_DropsOtherClassVariants()
    {
        // Alignment tabard shape: at the band's final step every class is handed its own
        // cloak (1310=class 2 … 1314=class 6) alongside a generic tabard (1309). With
        // milestones 4→L10, 8→L20 the step-19 tabards all land in the L20 band. Crawled
        // for class 6, only that class's item survives — the generic and other classes'
        // variants drop, mirroring how a stat bonus resolves to one class.
        string[] tbinfo =
        {
            "giveability 126 4:minlevel 10",
            "giveability 126 8:minlevel 20",
            "giveability 126 19:giveitem 1309",
            "class 2:giveability 126 19:giveitem 1310",
            "class 6:giveability 126 19:giveitem 1314",
        };

        Assert.Equal(
            new[] { 1314 },
            Find(QuestCrawler.Crawl(CacheWithTbInfo(tbinfo), classId: 6), 126, 20).AwardItems);
        // No class request → the generic tabard is the default award.
        Assert.Equal(
            new[] { 1309 },
            Find(QuestCrawler.Crawl(CacheWithTbInfo(tbinfo), classId: null), 126, 20).AwardItems);
    }

    [Fact]
    public void Crawl_NonMonotonicGivestepGates_WithoutAddabilityAdvance_StaySinglePart()
    {
        // A flag whose give-step minlevel gates are step1@L15, step0@L35, step4@L50 —
        // ordered by level the steps go 1 → 0 → 4, not a strictly climbing staircase, so
        // it forms no give-step ladder. With no `addability <flag>` advance either it is
        // not a value ladder, so it stays single-part (one quest at step 0). (Real
        // MageBane *does* carry an addability advance and is a value ladder — see the
        // ability-value-ladder test below.)
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "giveability 70 1:minlevel 15",
                "checkability 70 0:minlevel 35",
                "checkability 70 4:minlevel 50"),
            classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(70, q.Flag);
        Assert.Equal(0, q.Step);
        Assert.Equal(0, q.BandOrdinal);
        Assert.Equal(15, q.RequiredLevel);
    }

    [Fact]
    public void Crawl_AbilityValueLadder_BandsByDistinctLevelGatesNotPerValue()
    {
        // MageBane (flag 50, Witchhunter=class 2) shape: granted once at value 1 (the
        // dwarven witchhunter sword, L15), then climbed to 2, 3, 4 by relative
        // `addability 50 1` increments gated on the current value, finishing at L50. The
        // give-step staircase can't see this (the lone giveability sits at value 1, the
        // rest carry no giveability of their own), so it bands on the value axis — but by
        // the distinct *level gates* that axis carries, not one card per value. Only value 1
        // (L15) and value 4 (L50) carry a real minlevel, so the ladder is two tiers: the
        // ungated intermediate values 2–3 fold up into the L50 finish. The
        // `checkability 50 0:minlevel 35` witchhunter-only turn-in proxy (value 0) folds
        // into the entry band rather than forming a tier of its own.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 2:minlevel 15:giveability 50 1:giveitem 100",        // value 1 grant + sword, L15
                "class 2:checkability 50 0:minlevel 35:giveitem 200",       // value-0 turn-in folds to entry band
                "class 2:checkability 50 1:addability 50 1:giveitem 300",   // value 1 → 2 (ungated)
                "class 2:checkability 50 2:addability 50 1:giveitem 400",   // value 2 → 3 (ungated)
                "class 2:checkability 50 3:addability 50 1:minlevel 50:giveitem 500"), // value 3 → 4, L50
            classId: 2);

        CrawledQuest[] bands = quests.Where(q => q.Flag == 50).OrderBy(q => q.Step).ToArray();

        // Two tiers, not four: the entry sword tier (Step = boundary value 1) and the finish
        // tier (Step = boundary value 4). Values 2–3 carry no level gate so they fold up.
        Assert.Equal(new[] { 1, 4 }, bands.Select(b => b.Step));
        Assert.All(bands, b => Assert.True(b.ProgressByValue));
        Assert.Equal(new[] { 1, 2 }, bands.Select(b => b.BandOrdinal));

        // Entry tier is level-gated at 15 (the value-0 L35 turn-in folds in but doesn't
        // raise the entry level); the finish tier completes at L50.
        Assert.Equal(new[] { 15, 50 }, bands.Select(b => b.RequiredLevel));

        // The award is each tier's top-value keeper: the sword (value-1 grant) caps the
        // entry tier; the value-4 finish keeper caps the last. Lower-value turn-in tokens
        // (the value-0 item 200, the intermediate 300/400) are dropped.
        Assert.Equal(new[] { 100 }, bands[0].AwardItems);
        Assert.Equal(new[] { 500 }, bands[1].AwardItems);

        // Step-range covers each tier's value span so the followable-step draft walks the
        // same axis: the entry tier is just value 1; the finish tier sweeps value 2 up.
        Assert.Equal((1, 1), (bands[0].StepRangeStart, bands[0].StepRangeEnd));
        Assert.Equal((2, int.MaxValue), (bands[1].StepRangeStart, bands[1].StepRangeEnd));

        // Every granting chain is class-2 guarded → the whole ladder restricts to {2}.
        Assert.All(bands, b => Assert.Equal(new[] { 2 }, b.ClassIds));
    }

    [Fact]
    public void Crawl_DirectGiveabilityValueTwo_WithoutAddability_StaysSinglePart()
    {
        // A flag set directly to value 2 by `giveability <flag> 2` with no `addability`
        // advance is *not* a value ladder — the value-ladder discriminator is the
        // addability climb into the granted-flag set, so a plain high-value direct grant
        // (e.g. flag 130's `giveability 130 2`) stays a single-part quest.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 130 2:minlevel 15:giveitem 406"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(130, q.Flag);
        Assert.Equal(0, q.Step);
        Assert.False(q.ProgressByValue);
        Assert.Equal(new[] { 406 }, q.AwardItems);
    }
}
