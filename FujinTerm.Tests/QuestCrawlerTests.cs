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
/// PR 10.10a — <see cref="QuestCrawler"/> discovers quests and their stat rewards
/// from <c>TBInfo</c> chains. These cases reproduce the structures verified
/// against real data-v1.11p: single-flag quests key to step 0; alignment flags
/// (126/127/128) carve into <c>minlevel</c> bands with the bonus on the 2nd-band;
/// reward bonuses come from <c>addability</c> (non-quest-flag targets only) and
/// resolve to the requested class with a no-class default. Synthetic seeded caches
/// keep the cases deterministic and CI-portable, matching ClassCapabilitiesTests.
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
        Assert.Equal(0, q.Step);              // single-flag quest keys to step 0
        Assert.Equal(15, q.RequiredLevel);
        Assert.Equal(new[] { new QuestBonus(2, 1) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_GateOnlyFlag_EmitsQuestWithoutBonus()
    {
        // Flag 133 (Phoenix): all gate steps, no addability reward.
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
    }

    [Fact]
    public void Crawl_BonusExcludesQuestFlagProgress()
    {
        // addability 50 is quest progress (a quest flag), not a stat reward — only
        // the addability 4 (max damage) survives into the bonus list.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 50 1:addability 50 1:addability 4 1"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(50, q.Flag);
        Assert.Equal(new[] { new QuestBonus(4, 1) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_SkillGiveability_NotDiscoveredAsQuest()
    {
        // giveability 32 1 is Smash (a skill), not a quest flag — nothing emitted.
        Assert.Empty(QuestCrawler.Crawl(
            CacheWithTbInfo("class 1:giveability 32 1"), classId: 1));
    }

    [Fact]
    public void Crawl_LastQuestFlagGiveabilityWins()
    {
        // A chain that grants two quest flags terminally grants the last one, so
        // the reward attaches to 130 and 125 is not discovered from this chain.
        IReadOnlyList<CrawledQuest> quests = QuestCrawler.Crawl(
            CacheWithTbInfo("giveability 125 1:giveability 130 2:addability 22 3"), classId: null);

        CrawledQuest q = Assert.Single(quests);
        Assert.Equal(130, q.Flag);
        Assert.Equal(new[] { new QuestBonus(22, 3) }, q.Bonuses);
    }

    [Fact]
    public void Crawl_AlignmentFlag_SplitsIntoMinlevelBands()
    {
        // Flag 126 (Good Alignment): four minlevel milestones → four band quests;
        // the lone reward group (give-step 8) falls in the L20 band by the nearest
        // milestone at/before its step (give-step 7 = L20), even with no own minlevel.
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
    public void Crawl_AlignmentReward_ResolvesClassDefaultWhenNoBranch()
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
    public void Crawl_AlignmentReward_BandFromDeclaredMinlevel_WhenClassChainOmitsIt()
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

    [Theory]
    [InlineData(50, true)]
    [InlineData(156, true)]
    [InlineData(125, true)]
    [InlineData(134, true)]
    [InlineData(191, true)]
    [InlineData(200, true)]
    [InlineData(400, true)]
    [InlineData(32, false)]    // Smash (skill)
    [InlineData(117, false)]   // backstab min damage (stat)
    [InlineData(124, false)]
    [InlineData(135, false)]
    [InlineData(190, false)]
    [InlineData(401, false)]
    public void IsQuestFlag_ClassifiesAbilityIds(int abilityId, bool expected) =>
        Assert.Equal(expected, QuestCrawler.IsQuestFlag(abilityId));
}
