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
/// real data-v1.11p: single-part quests key to step 0; a flag whose minlevel gates
/// climb across different give-steps splits into level bands while per-class variants
/// of one step do not; <c>addability</c> off the quest-flag set are stat rewards and
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
        // Flag 126 (Good Alignment): four minlevel milestones climbing across give-
        // steps → four band quests. The lone reward group (give-step 8) falls in the
        // L20 band by the nearest milestone at/before its step (give-step 7 = L20),
        // even though its own chains declare no minlevel.
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
    public void Crawl_AnyUnguardedGiveabilityChain_LeavesQuestOpenToAllClasses()
    {
        // One granting chain is class-guarded, another is not → conservative rule keeps
        // the quest open to everyone (ClassIds null), since some path needs no class.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo(
                "class 1:giveability 60 1",
                "giveability 60 2:addability 4 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Null(q.ClassIds);
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
}
