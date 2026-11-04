using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The drafted quest checklists name where to fetch what a step turns in (report
// paradigm-20261011-230844: "lay out how to complete identified quests ... similar to the
// quest guides"). Flag 208 has the shape of Paradigm's Conquest1: the four hands come off
// monsters whose death hands one over while the flag is at 1, and the turn-in takes them.
// Flag 126 carries a lost-item reissue and a turn-in item only a monster's drop slots hold;
// flag 300 an item an NPC gives on a keyword, and a roll-table line.
public sealed class QuestItemGatheringTests : IDisposable
{
    private readonly string _root;

    public QuestItemGatheringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questgather-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 3643, "Name": "hill chieftan's hand" },
          { "Number": 3644, "Name": "stone chieftan's hand" },
          { "Number": 622, "Name": "locked wooden box" },
          { "Number": 684, "Name": "gleaming shard" },
          { "Number": 700, "Name": "orc-head" },
          { "Number": 701, "Name": "quest token" },
          { "Number": 702, "Name": "Darkbane" },
          { "Number": 703, "Name": "adamant ore" },
          { "Number": 931, "Name": "iron crown" },
          { "Number": 685, "Name": "gleaming dagger" },
          { "Number": 686, "Name": "Spirit Dagger of Goijar" },
          { "Number": 687, "Name": "golden chest" },
          { "Number": 688, "Name": "severed head of Goru-Nezar" },
          { "Number": 689, "Name": "golden chest" },
          { "Number": 691, "Name": "troll tooth" },
          { "Number": 692, "Name": "jade figurine" },
          { "Number": 693, "Name": "crowbar" },
          { "Number": 694, "Name": "dusty tome" },
          { "Number": 695, "Name": "ancient chalice" },
          { "Number": 696, "Name": "silver idol" },
          { "Number": 697, "Name": "opaline crystal" },
          { "Number": 698, "Name": "spirit token" }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 50, "Name": "hill giant chieftan", "DeathSpell": 5519 },
          { "Number": 51, "Name": "stone giant chieftain", "DeathSpell": 5521 },
          { "Number": 60, "Name": "renfry", "DeathSpell": 0 },
          { "Number": 61, "Name": "metallic monstrosity", "DeathSpell": 0, "DropItem-0": 684, "DropItem%-0": 25 },
          { "Number": 62, "Name": "aldreth", "DeathSpell": 0 },
          { "Number": 63, "Name": "Commander Markus", "DeathSpell": 0, "DropItem-0": 622, "DropItem%-0": 100 },
          { "Number": 64, "Name": "kobold king", "DeathSpell": 574, "DropItem-0": 931, "DropItem%-0": 100 },
          { "Number": 65, "Name": "orc captain", "DeathSpell": 0, "DropItem-0": 700, "DropItem%-0": 30 },
          { "Number": 66, "Name": "spectral knight", "DeathSpell": 580 },
          { "Number": 70, "Name": "chancellor annora", "DeathSpell": 0 },
          { "Number": 71, "Name": "king kulgar", "DeathSpell": 0 },
          { "Number": 72, "Name": "Goru-Nezar", "DeathSpell": 0, "DropItem-0": 688, "DropItem%-0": 100 },
          { "Number": 73, "Name": "troll", "DeathSpell": 590, "DropItem-0": 691, "DropItem%-0": 50 },
          { "Number": 74, "Name": "ghoul", "DeathSpell": 0, "DropItem-0": 692, "DropItem%-0": 10 },
          { "Number": 75, "Name": "Giovanni", "DeathSpell": 0, "DropItem-0": 693, "DropItem%-0": 100 },
          { "Number": 76, "Name": "greater hellion", "DeathSpell": 600 },
          { "Number": 77, "Name": "lesser hellion", "DeathSpell": 601 },
          { "Number": 78, "Name": "cave bat", "DeathSpell": 0, "DropItem-0": 697, "DropItem%-0": 25 },
          { "Number": 79, "Name": "ethereal figure", "DeathSpell": 0 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 5519, "Name": "hill chieftan text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 5211 },
          { "Number": 5521, "Name": "stone chieftan text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 5212 },
          { "Number": 574, "Name": "kobold king text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1400 },
          { "Number": 580, "Name": "spectral knight text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1510 },
          { "Number": 590, "Name": "troll text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1900 },
          { "Number": 600, "Name": "greater hellion text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1950 },
          { "Number": 601, "Name": "lesser hellion text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1951 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 5208, "Action": "task:5209\nhands:5215\n", "Called From": "Monster #60" },
          { "Number": 5209, "Action": "", "Called From": "Textblock #5208" },
          { "Number": 5215, "Action": "", "Called From": "Textblock #5208" },
          { "Number": 5222, "Action": "class 1:minlevel 45:failability 208:giveability 208 1:text 5210\n", "Called From": "Textblock #5209" },
          { "Number": 5211, "Action": "failitem 3643:testability 208 1:checkability 208 1:message 9267:giveitem 3643\n", "Called From": "Spell #5519" },
          { "Number": 5212, "Action": "failitem 3644:testability 208 1:checkability 208 1:message 9268:giveitem 3644\n", "Called From": "Spell #5521" },
          { "Number": 5216, "Action": "checkability 208 1:testability 208 1:checkitem 3643 9271:checkitem 3644 9272:takeitem 3643:takeitem 3644:giveability 208 2:addability 96 10:text 5217\n", "Called From": "Textblock #5215" },

          { "Number": 350, "Action": "bow:testability 126 4:checkability 126 4:giveitem 622:giveability 126 5\nbow:testability 126 5:checkability 126 5:failitem 622:giveitem 622\n", "Called From": "Room 2/2518" },
          { "Number": 353, "Action": "kneel:testability 126 5:checkability 126 5:checkitem 622 3208:takeitem 622:giveability 126 6\n", "Called From": "Room 1/1333" },
          { "Number": 354, "Action": "forge:checkability 126 6:takeitem 684:giveability 126 7\n", "Called From": "Room 6/1357" },

          { "Number": 900, "Action": "heads:910\n", "Called From": "Monster #62" },
          { "Number": 910, "Action": "takeitem 700:giveitem 701\n", "Called From": "Textblock #900" },
          { "Number": 920, "Action": "return:takeitem 701:giveability 300 1\n", "Called From": "Room 1/2305" },
          { "Number": 930, "Action": "100:giveitem 702:giveability 300 2\n", "Called From": "Room 15/183" },

          { "Number": 1300, "Action": "salute:takeitem 622:giveability 128 1\n", "Called From": "Room 2/2577" },

          { "Number": 1390, "Action": "kneel:failability 129:giveability 129 1\n", "Called From": "Room 6/833" },
          { "Number": 1400, "Action": "checkability 129 1:testability 129 1:giveability 129 2\n", "Called From": "Spell #574" },
          { "Number": 1410, "Action": "crown:checkability 129 2:takeitem 931:giveability 129 3\n", "Called From": "Room 1/723" },

          { "Number": 4999, "Action": "mine vein:nomonsters:random 5000\n", "Called From": "Room 14/7127" },
          { "Number": 5000, "Action": "100:giveitem 703\n", "Called From": "Textblock(rndm) #4999" },
          { "Number": 5010, "Action": "forge ore:takeitem 703:giveability 301 1\n", "Called From": "Room 14/7225" },

          { "Number": 1500, "Action": "business:minlevel 10:failability 140:giveability 140 1\nreport:minlevel 20:takeitem 685:giveitem 687:giveability 140 3\n", "Called From": "Room 6/1387" },
          { "Number": 1505, "Action": "forge:checkability 140 1:takeitem 684:giveitem 685:giveability 140 2\n", "Called From": "Room 6/1357" },
          { "Number": 1510, "Action": "checkability 140 3:giveitem 686:giveability 140 4\n", "Called From": "Spell #580" },
          { "Number": 1520, "Action": "success:checkability 140 4:takeitem 686:giveability 140 5\n", "Called From": "Room 1/1333" },

          { "Number": 1600, "Action": "missions:1601\nhead:1602\nsuccess:1603\n", "Called From": "Monster #70" },
          { "Number": 1601, "Action": "", "Called From": "Textblock #1600" },
          { "Number": 1602, "Action": "", "Called From": "Textblock #1600" },
          { "Number": 1603, "Action": "", "Called From": "Textblock #1600" },
          { "Number": 1611, "Action": "minlevel 20:failability 141:giveability 141 1\n", "Called From": "Textblock #1601" },
          { "Number": 1612, "Action": "minlevel 30:takeitem 688:giveability 141 2\n", "Called From": "Textblock #1602" },
          { "Number": 1613, "Action": "minlevel 40:giveability 141 5\n", "Called From": "Textblock #1603" },
          { "Number": 1700, "Action": "business:1701\nreturn:1702\n", "Called From": "Monster #71" },
          { "Number": 1701, "Action": "", "Called From": "Textblock #1700" },
          { "Number": 1702, "Action": "", "Called From": "Textblock #1700" },
          { "Number": 1711, "Action": "checkability 141 2:giveability 141 3\n", "Called From": "Textblock #1701" },
          { "Number": 1712, "Action": "checkability 141 3:giveitem 689:giveability 141 4\n", "Called From": "Textblock #1702" },

          { "Number": 1890, "Action": "start:minlevel 10:failability 142:giveability 142 1\n", "Called From": "Room 5/1" },
          { "Number": 1900, "Action": "checkability 142 1:giveability 142 2\n", "Called From": "Spell #590" },
          { "Number": 1901, "Action": "reward:minlevel 20:giveability 142 3\n", "Called From": "Room 5/2" },
          { "Number": 1902, "Action": "hand:checkability 142 3:takeitem 691:giveability 142 4\n", "Called From": "Room 5/3" },

          { "Number": 2000, "Action": "message:2001\n", "Called From": "Monster #79" },
          { "Number": 2001, "Action": "", "Called From": "Textblock #2000" },
          { "Number": 2002, "Action": "failability 146:giveability 146 1\n", "Called From": "Textblock #2001" },
          { "Number": 2010, "Action": "trade:checkability 146 1:takeitem 697:giveability 146 2\n", "Called From": "Room 3/10" },
          { "Number": 2020, "Action": "offer token:takeitem 698:giveability 147 1\n", "Called From": "Room 3/11" },

          { "Number": 1800, "Action": "pry coffin:checkitem 693:random 1801\n", "Called From": "Room 17/278" },
          { "Number": 1801, "Action": "100:giveitem 692\n", "Called From": "Textblock(rndm) #1800" },
          { "Number": 1802, "Action": "offer:takeitem 692:giveability 143 1\n", "Called From": "Room 17/300" },
          { "Number": 1810, "Action": "pull book:checkitem 693:random 1811\n", "Called From": "Room 17/9786" },
          { "Number": 1811, "Action": "100:giveitem 694\n", "Called From": "Textblock(rndm) #1810" },
          { "Number": 1812, "Action": "read:takeitem 694:giveability 144 1\n", "Called From": "Room 17/301" },

          { "Number": 1950, "Action": "minlevel 18:failability 145:failitem 695:giveitem 695\n", "Called From": "Spell #600" },
          { "Number": 1951, "Action": "failability 145:checkability 999 1:giveitem 696\n", "Called From": "Spell #601" },
          { "Number": 1952, "Action": "return:takeitem 695:giveability 145 1\n", "Called From": "Room 2/2561" },
          { "Number": 1953, "Action": "bonus:checkability 145 1:takeitem 696:giveability 145 2\n", "Called From": "Room 2/2562" }
        ]
        """;

    // The slots ahead of Kingsport Gems' crystal never restock, each for one zero of its
    // own (chance, time, amount); shop 30 is out of the game.
    private const string ShopsJson = """
        [
          { "Number": 10, "Name": "Dwarven Jeweler", "In Game": 1, "Assigned To": "Room 6/1346",
            "Item-0": 697, "Time-0": 240, "Amount-0": 1, "%-0": 0,
            "Item-1": 698, "Time-1": 0, "Amount-1": 1, "%-1": 100 },
          { "Number": 15, "Name": "Pawn Shop", "In Game": 1, "Assigned To": "Room 2/2",
            "Item-0": 697, "Time-0": 240, "Amount-0": 0, "%-0": 100 },
          { "Number": 30, "Name": "Boarded Stall", "In Game": 0, "Assigned To": "Room 1/1",
            "Item-0": 697, "Time-0": 240, "Amount-0": 1, "%-0": 100 },
          { "Number": 20, "Name": "Kingsport Gems", "In Game": 1, "Assigned To": "Room 14/1785",
            "Item-0": 697, "Time-0": 500, "Amount-0": 3, "%-0": 6 }
        ]
        """;

    private GameDataCache NewCache()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        File.WriteAllText(Path.Combine(dir, "Shops.json"), ShopsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private static (ItemSourceIndex Givers, MonsterDropIndex Drops) Indexes(GameDataCache cache)
    {
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(cache.ActiveSet);
        MonsterDropIndex drops = new(cache);
        drops.OnActiveSetChanged(cache.ActiveSet);
        return (new ItemSourceIndex(cache, tb), drops);
    }

    private static readonly Dictionary<int, IReadOnlyList<RoomKey>> Placement = new()
    {
        [50] = new[] { new RoomKey(16, 801) },
        [51] = new[] { new RoomKey(16, 1095) },
        [60] = new[] { new RoomKey(14, 247) },
        [61] = new[] { new RoomKey(6, 76) },
        [62] = new[] { new RoomKey(1, 2305) },
        [63] = new[] { new RoomKey(2, 2518) },
        [64] = new[] { new RoomKey(6, 834) },
        [65] = new[] { new RoomKey(7, 1363) },
        [66] = new[] { new RoomKey(6, 994) },
        [70] = new[] { new RoomKey(1, 1333) },
        [71] = new[] { new RoomKey(6, 1387) },
        [72] = new[] { new RoomKey(3, 668) },
        [73] = new[] { new RoomKey(5, 9) },
        [74] = new[] { new RoomKey(17, 5) },
        [75] = new[] { new RoomKey(1, 334) },
        [76] = new[] { new RoomKey(2, 2600) },
        [79] = new[] { new RoomKey(3, 812) },
    };

    private static CrawledQuest Quest(GameDataCache cache, int flag) =>
        QuestCrawler.Crawl(cache, null).First(q => q.Flag == flag);

    [Fact]
    public void FlagGatedDeathGives_AreKillLines_AheadOfTheTurnInThatTakesThem()
    {
        GameDataCache cache = NewCache();

        Assert.Equal(new[]
        {
            "[] (14/247) `ask renfry task`",
            "[] (16/801) kill hill giant chieftan (hill chieftan's hand)",
            "[] (16/1095) kill stone giant chieftain (stone chieftan's hand)",
            "[] (14/247) (hill chieftan's hand, stone chieftan's hand required) `ask renfry hands`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 208), Placement));
    }

    [Fact]
    public void LostItemReissue_IsLeftOut_AndADroppedItemNamesItsDropper()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // The second `bow` line hands the box back to one who lost it; the first `bow`
        // already gave it, so it isn't a step. Nothing in the quest yields the shard: the
        // monster whose drop slot holds it is the fetch.
        Assert.Equal(new[]
        {
            "[] (2/2518) `bow` (get locked wooden box)",
            "[] (1/1333) (locked wooden box required) `kneel`",
            "[] (6/76) kill metallic monstrosity (gleaming shard)",
            "[] (6/1357) (gleaming shard required) `forge`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 126), Placement, givers, drops));
    }

    [Fact]
    public void ItemAnNpcTradesFor_IsAnAskLine_WithItsOwnFetchFirst_AndARollLineIsNotTyped()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (7/1363) kill orc captain (orc-head)",
            "[] (1/2305) (orc-head required) `ask aldreth heads` (get quest token)",
            "[] (1/2305) (quest token required) `return`",
            "[] (15/183) obtain Darkbane",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 300), Placement, givers, drops));
    }

    [Fact]
    public void TicksFromBeforeTheGatherLines_StayOnTheirSteps()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // Ticked against the old draft of flag 208: "ask renfry task" (0) and "ask renfry
        // hands" (1). Today the two kill lines sit between them.
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress> { new(208, 0) { CheckedSteps = new List<int> { 0, 1 } } },
        };

        Assert.True(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out int moved, givers, drops));

        Assert.Equal(1, moved);
        Assert.Equal(new[] { 0, 3 }, profile.QuestLog[0].CheckedSteps);
        Assert.Equal(QuestTickMigration.CurrentDraftRevision, profile.QuestDraftRevision);
        Assert.False(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out _, givers, drops));
    }

    [Fact]
    public void AnotherQuestsGatedGive_LosesToASureDrop()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // Stock's Evil 2 shape: the box is also handed out by the Good quest's own `bow`
        // line, which an evil character is refused; Commander Markus always drops it.
        Assert.Equal(new[]
        {
            "[] (2/2518) kill Commander Markus (locked wooden box)",
            "[] (2/2577) (locked wooden box required) `salute`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 128), Placement, givers, drops));
    }

    [Fact]
    public void DropOffAMonsterTheQuestKillsAnyway_IsNotedOnThatKill()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (6/833) `kneel`",
            "[] (6/834) kill kobold king (iron crown)",
            "[] (1/723) (iron crown required) `crown`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 129), Placement, givers, drops));
    }

    [Fact]
    public void RoomRollTableGive_IsTheCommandThatDraws_NotANumberToType()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // `mine vein` draws from table 5000, whose line leads with its roll band: the
        // command is the draw, not the band, and a draw is never a sure give.
        ItemGiver ore = Assert.Single(givers.GiversOf(703));
        Assert.Equal("mine vein", ore.Keyword);
        Assert.False(ore.Deterministic);
        Assert.Equal(new[]
        {
            "[] (14/7127) `mine vein` (get adamant ore) (on a roll)",
            "[] (14/7225) (adamant ore required) `forge ore`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 301), Placement, givers, drops));
    }

    [Fact]
    public void DropIndexStillOnTheLastSet_LeavesTheTicksForLater()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, _) = Indexes(cache);
        MonsterDropIndex stale = new(cache);
        stale.OnActiveSetChanged("beta");
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress> { new(208, 0) { CheckedSteps = new List<int> { 0, 1 } } },
        };

        Assert.False(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out _, givers, stale));

        Assert.Equal(0, profile.QuestDraftRevision);
        Assert.Equal(new[] { 0, 1 }, profile.QuestLog[0].CheckedSteps);
    }

    [Fact]
    public void Tiers_DraftInTheSeedGuidesShape_EachEndingOnItsRewardHandIn()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // Modelled on the seed's Good 2 / Good 3: what a step needs comes first, "(…
        // required)"; each kill names its drop; the level-gated reward hand-in (`report`,
        // the next tier's gate) closes the tier it rewards, and the next tier opens after
        // it with the tier it follows (user, 2026-10-17).
        List<CrawledQuest> tiers = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == 140).ToList();
        Assert.Equal(2, tiers.Count);

        Assert.Null(QuestTextFormatter.PriorTierLine(cache, tiers[0], q => $"Tier {q.BandOrdinal}"));
        Assert.Equal(new[]
        {
            "[] (6/1387) `business`",
            "[] (6/76) kill metallic monstrosity (gleaming shard)",
            "[] (6/1357) (gleaming shard required) `forge` (get gleaming dagger)",
            "[] (6/1387) (gleaming dagger required) `report` (get golden chest)",
        }, QuestTextFormatter.StepLines(cache, tiers[0], Placement, givers, drops));

        Assert.Equal("-Complete Tier 1 first-", QuestTextFormatter.PriorTierLine(cache, tiers[1], q => $"Tier {q.BandOrdinal}"));
        Assert.Equal(new[]
        {
            "[] (6/994) kill spectral knight (Spirit Dagger of Goijar)",
            "[] (1/1333) (Spirit Dagger of Goijar required) `success`",
        }, QuestTextFormatter.StepLines(cache, tiers[1], Placement, givers, drops));

        // The tier is complete after its hand-in, the line that closes its draft.
        Assert.Equal(3, tiers[0].CompleteValue);
    }

    [Fact]
    public void TicksOnAMovedHandIn_FollowItOntoTheTierBefore()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // Ticked against the old drafts: tier 1 was `business`, `forge`; tier 2 opened
        // with the `report` hand-in, then the knight, then `success`.
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress>
            {
                new(140, 10) { CheckedSteps = new List<int> { 1 } },
                new(140, 20) { CheckedSteps = new List<int> { 0, 1 } },
            },
        };

        Assert.True(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out int moved, givers, drops));

        Assert.Equal(2, moved);
        Assert.Equal(new[] { 2, 3 }, profile.QuestLog.Single(p => p.Step == 10).CheckedSteps);
        Assert.Equal(new[] { 0 }, profile.QuestLog.Single(p => p.Step == 20).CheckedSteps);
    }

    [Fact]
    public void GoodLadderShape_Good2EndsOnTheHeadHandIn_Good3OnAnnorasSuccess()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // The user's own tier boundary (2026-10-17): Goru-Nezar's head and its hand-in to
        // Annora close Good 2; Good 3 runs from King Kulgar's business to the success
        // hand-in back at Annora. Flag 141 carries those lines with Paradigm's names.
        List<CrawledQuest> tiers = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == 141).ToList();
        string Title(CrawledQuest q) => $"Good {q.BandOrdinal + 1}";

        Assert.Equal(new[]
        {
            "[] (1/1333) `ask chancellor annora missions`",
            "[] (3/668) kill Goru-Nezar (severed head of Goru-Nezar)",
            "[] (1/1333) (severed head of Goru-Nezar required) `ask chancellor annora head`",
        }, QuestTextFormatter.StepLines(cache, tiers[0], Placement, givers, drops));

        Assert.Equal("-Complete Good 2 first-", QuestTextFormatter.PriorTierLine(cache, tiers[1], Title));
        Assert.Equal(new[]
        {
            "[] (6/1387) `ask king kulgar business`",
            "[] (6/1387) `ask king kulgar return` (get golden chest)",
            "[] (1/1333) `ask chancellor annora success`",
        }, QuestTextFormatter.StepLines(cache, tiers[1], Placement, givers, drops));
    }

    [Fact]
    public void DropOffAKillOnAnotherTier_GetsItsOwnFetchLine()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // The troll is a tier-1 kill; tier 2 needs its tooth. The tooth isn't noted on the
        // tier-1 line (that tier never uses it) and tier 2 gets the fetch.
        List<CrawledQuest> tiers = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == 142).ToList();

        Assert.Equal(new[]
        {
            "[] (5/1) `start`",
            "[] (5/9) kill troll",
            "[] (5/2) `reward`",
        }, QuestTextFormatter.StepLines(cache, tiers[0], Placement, givers, drops));
        Assert.Equal(new[]
        {
            "[] (5/9) kill troll (troll tooth)",
            "[] (5/3) (troll tooth required) `hand`",
        }, QuestTextFormatter.StepLines(cache, tiers[1], Placement, givers, drops));
    }

    [Fact]
    public void GiveOnARoll_RanksBelowADrop_AndNamesWhatItsDrawNeeds()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        ItemGiver coffin = Assert.Single(givers.GiversOf(692));
        Assert.True(coffin.Rolled);
        Assert.Equal(new[] { 693 }, coffin.NeededItems);

        // Any drop beats a roll, however rare the drop.
        Assert.Equal(new[]
        {
            "[] (17/5) kill ghoul (jade figurine)",
            "[] (17/300) (jade figurine required) `offer`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 143), Placement, givers, drops));

        // With only the roll, it's drafted as one, after a fetch for what the draw checks.
        Assert.Equal(new[]
        {
            "[] (1/334) kill Giovanni (crowbar)",
            "[] (17/9786) (crowbar required) `pull book` (get dusty tome) (on a roll)",
            "[] (17/301) (dusty tome required) `read`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 144), Placement, givers, drops));
    }

    [Fact]
    public void GiveBeforeTheQuestStarts_IsAGatherLine_ButNotOneGatedOnAnotherFlag()
    {
        GameDataCache cache = NewCache();
        // Blood Champ's chalice: the greater hellion's death hands it to one who doesn't
        // hold the flag yet (`minlevel 18:failability 130:failitem 3867:giveitem 3867`).
        // The lesser hellion's give also asks for flag 999, so it's that quest's.
        Assert.Equal(new[]
        {
            "[] (2/2600) kill greater hellion (ancient chalice)",
            "[] (2/2561) (ancient chalice required) `return`",
            "[] (2/2562) (silver idol required) `bonus`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 145), Placement));
    }

    [Fact]
    public void FlagAtTheValueBeforeTheHandIn_LeavesTheTierOpen_AndTheHandInUnticked()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // Good 2's shape: `missions` sets 1, the head hand-in sets 2. A live flag of 1 (the
        // value the tier used to complete at) is short of the hand-in: the tier isn't done
        // and its hand-in line isn't proven.
        CrawledQuest good2 = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == 141).OrderBy(q => q.BandOrdinal).First();
        Assert.Equal(2, good2.CompleteValue);

        var observed = new Dictionary<int, int> { [141] = 1 };
        var target = new QuestFlagCompletion.Target(good2.Flag, good2.Step, good2.CompleteValue);
        Assert.Empty(QuestFlagCompletion.ResolveNewlyComplete(new[] { target }, observed, new HashSet<QuestFlagCompletion.QuestKey>()));

        IReadOnlyList<(int Order, string Line)> entries = QuestTextFormatter.StepEntries(cache, good2, Placement, givers, drops);
        (int Order, string Line) handIn = entries.Last();
        Assert.Contains("`ask chancellor annora head`", handIn.Line, StringComparison.Ordinal);
        Assert.True(handIn.Order > observed[141]);   // the Quest tab ticks lines up to the read value

        observed[141] = 2;
        Assert.Single(QuestFlagCompletion.ResolveNewlyComplete(new[] { target }, observed, new HashSet<QuestFlagCompletion.QuestKey>()));
    }

    // The Mandos shape (Paradigm flag 200): the crystal comes off a monster one time in
    // four and sits on two shelves. A shop that restocks it is the fetch, ahead of the
    // drop and of a shop that never restocks or is out of the game; and the ethereal
    // figure's `message` is asked like any other keyword.
    [Fact]
    public void ARestockingShop_IsTheFetch_AheadOfAChancyDrop_AndMessageIsAsked()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (3/812) `ask ethereal figure message`",
            "[] (14/1785) `buy opaline crystal` (get opaline crystal) (sold at Kingsport Gems)",
            "[] (3/10) (opaline crystal required) `trade`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 146), Placement, givers, drops));
    }

    [Fact]
    public void AShopThatNeverRestocks_IsStillAFetch_AndSaysSo()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (6/1346) `buy spirit token` (get spirit token) (sold at Dwarven Jeweler; never restocked)",
            "[] (3/11) (spirit token required) `offer token`",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 147), Placement, givers, drops));
    }
}

