using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins how a TBInfo script line is read for the Quest Flags table's requirement columns and
// for the Quest Flag Steps window: which directives are put into words, that every other step
// is carried over untouched, how a line's way in is found (room command, NPC keyword, a
// monster's death spell), which lines fold into one entry, and the order the entries come in.
public sealed class QuestFlagWalkthroughTests : IDisposable
{
    private const int Flag = 300;

    private readonly string _root;

    public QuestFlagWalkthroughTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questwalk-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 10 is an NPC with keywords; 11 dies into spell 40, which ends by casting 41; 12's death
    // spell targets a user, so it never fires.
    private const string MonstersJson = """
        [
          { "Number": 10, "Name": "Old Sage", "DeathSpell": 0 },
          { "Number": 11, "Name": "bog troll", "DeathSpell": 40 },
          { "Number": 12, "Name": "town crier", "DeathSpell": 45 },
          { "Number": 13, "Name": "The Grey Sage", "DeathSpell": 0 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 40, "Name": "troll temp", "Targets": 12, "Abil-0": 151, "AbilVal-0": 41 },
          { "Number": 41, "Name": "troll text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 600 },
          { "Number": 45, "Name": "crier text", "Targets": 8, "Abil-0": 148, "AbilVal-0": 610, "Casted By": "Monster #12" },
          { "Number": 60, "Name": "reveal", "Targets": 1 },
          { "Number": 70, "Name": "fireball", "Targets": 1 }
        ]
        """;

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 100, "Name": "Mossy Shrine" },
          { "Map Number": 1, "Room Number": 101, "Name": "Sage's Hut" }
        ]
        """;

    private const string ItemsJson = """
        [
          { "Number": 900, "Name": "troll tooth" },
          { "Number": 901, "Name": "silver key" },
          { "Number": 902, "Name": "old map" }
        ]
        """;

    private const string ClassesJson = """
        [
          { "Number": 1, "Name": "Warrior" },
          { "Number": 2, "Name": "Mage" },
          { "Number": 3, "Name": "Thief" }
        ]
        """;

    private const string RacesJson = """
        [
          { "Number": 5, "Name": "Elf" }
        ]
        """;

    // 500 — the Old Sage's keywords: tale / story → 501, reward → 510, and further down
    //       reconcile → 900 (shared with the Grey Sage's 520), replace → 530, greeting → 540.
    // 502 — start: without the flag, level 15, sets 1.
    // 511 — turn-in at exactly 2: the Warrior and Mage lines differ only in the class step; the
    //       Thief line has its own level and no reward.
    // 600 — the bog troll's death script: at exactly 1, sets 2.
    // 610 — hangs off a death spell that never fires: at 1 or more, adds 1.
    // 700 — a room command with two wordings: at 3 or more, clears the flag.
    // 710 — a room command only for a character without the flag.
    // 720 — one band of a random table, with a failability written a way that isn't settled.
    // 730 — a room command that reads and changes other flags.
    // 4702 — a line that checks the flag right after setting it: one wording sets a value the
    //       check passes at, one a value it cannot pass at, and one doesn't pin the value.
    // 800 / 810 — a room command that draws from a random table whose line sets flag 310.
    // 900 / 901 — dialogue shared by two NPCs. 530 — a line straight under a keyword.
    // 540 — a line under the NPC's auto-shown `greeting` keyword.
    // 750 — the only line of flag 350, and it only runs without it.
    // 760 — conditions written after a give, and after the flag change.
    // 780 / 781, 782 — a random draw or a cast between a pinned value and a check on it.
    // 783 — an exact value pinned, then added to, set lower, or cleared, then checked.
    // 801 — a second line drawing table 810. 820 → 821 → 822 — a table drawn from a drawn table.
    // 3323, 4703 — gives written ahead of the conditions (the two real lines, with this
    //       fixture's rooms, items and spell).
    private const string TBInfoJson = """
        [
          { "Number": 500, "Action": "tale:501\nstory:501\nreward:510\nreconcile:900\nreplace:530\ngreeting:540\nbless:checkability 318 1:giveability 318 2\n", "Called From": "Monster #10" },
          { "Number": 520, "Action": "reconcile:900\n", "Called From": "Monster #13" },
          { "Number": 530, "Action": "checkability 330 1:giveitem 902\n", "Called From": "Textblock #500" },
          { "Number": 540, "Action": "checkability 340 1:giveability 340 2\n", "Called From": "Textblock #500" },
          { "Number": 900, "Action": "\u0000", "Called From": "Textblock #500, Textblock #520" },
          { "Number": 901, "Action": "checkability 320 1:testability 320 1:giveability 320 2\n", "Called From": "Textblock #900" },
          { "Number": 4702, "Action": "touch ruby:nomonsters 503:minlevel 35 541:failability 131:giveability 131 3:checkability 131 3:testability 131 3:addexp 15000000:message 541:addability 58 1:addability 70 2\nmove ruby:nomonsters 503:minlevel 35 541:failability 131:giveability 131 1:checkability 131 3:testability 131 3:addexp 15000000:message 541:addability 58 1:addability 70 2\nrub ruby:checkability 131 1:giveability 131 2:checkability 131 3:addexp 5\n", "Called From": "Room 1/100" },
          { "Number": 800, "Action": "draw lot:class 1:minlevel 50 3020:failability 310 3667:takeitem 900:random 810\ndraw lot:class 2:minlevel 50 3020:failability 310 3667:takeitem 900:random 810\n", "Called From": "Room 1/100" },
          { "Number": 810, "Action": "75:giveitem 902:message 1\n100:giveitem 901:giveability 310 1:message 2\n", "Called From": "Textblock(rndm) #800" },
          { "Number": 770, "Action": "nod:giveability 380 1:checkability 380 1:giveability 390 2\n", "Called From": "Room 1/101" },
          { "Number": 780, "Action": "hum:failability 309:giveability 309 1:random 781:checkability 309 2:addexp 5\n", "Called From": "Room 1/101" },
          { "Number": 781, "Action": "50:giveability 309 9\n100:message 1\n", "Called From": "Textblock(rndm) #780" },
          { "Number": 782, "Action": "sing:failability 311:cast 60:giveability 311 1:checkability 311 3:addexp 7\nchant:cast 60:failability 312:giveability 312 1:checkability 312 3:addexp 7\n", "Called From": "Room 1/101" },
          { "Number": 783, "Action": "x1:checkability 313 2:testability 313 2:addability 313 1:checkability 313 3:addexp 1\nx2:checkability 314 5:testability 314 5:giveability 314 2:testability 314 2:addexp 1\nx3:checkability 315 1:removeability 315:failability 315:addexp 1\nx4:checkability 316 1:removeability 316:testability 316 3:addexp 1\n", "Called From": "Room 1/101" },
          { "Number": 801, "Action": "pull lot:minlevel 10:random 810\n", "Called From": "Room 1/101" },
          { "Number": 820, "Action": "spin:failability 317:minlevel 5:random 821\n", "Called From": "Room 1/100" },
          { "Number": 821, "Action": "100:takeitem 900:random 822\n", "Called From": "Textblock(rndm) #820" },
          { "Number": 822, "Action": "40:message 1\n100:giveability 317 4\n", "Called From": "Monster #12, Textblock(rndm) #821" },
          { "Number": 3323, "Action": "text 1310:teleport 101 1:testability 128 9:checkability 128 9:evilaligned 60 865:failability 126 866:failability 127 866:checkitem 900 1684:takeitem 900:giveitem 901\n", "Called From": "Spell #60" },
          { "Number": 4703, "Action": "touch gem:nomonsters 289:message 290:cast 60:teleport 101 1:message 291:minlevel 12:failability 129:addability 70 1:giveability 129 2:addexp 250000:message 3388\n", "Called From": "Room 1/100" },
          { "Number": 750, "Action": "kneel twice:failability 350:price 5 G:text 1\n", "Called From": "Room 1/100" },
          { "Number": 760, "Action": "beg:giveitem 901:failability 360:text 9\nbow:failability 370:giveability 370 1:failitem 900:giveitem 900\n", "Called From": "Room 1/101" },
          { "Number": 501, "Action": "\u0000", "Called From": "Textblock #500" },
          { "Number": 502, "Action": "minlevel 10 3000:minlevel 15:failability 300 3001:giveability 300 1:text 503\n", "Called From": "Textblock #501" },
          { "Number": 510, "Action": "\u0000", "Called From": "Textblock #500" },
          { "Number": 511, "Action": "check class:class 1:checkability 300 2:testability 300 2:checkitem 900 3002:takeitem 900:price 500 3003:giveability 300 3:giveitem 901:addexp 2500:addability 4 1:learnspell 70:teleport 101 1:remoteaction 5 6 0 3:cast 60\ncheck class:class 2:checkability 300 2:testability 300 2:checkitem 900 3002:takeitem 900:price 500 3003:giveability 300 3:giveitem 901:addexp 2500:addability 4 1:learnspell 70:teleport 101 1:remoteaction 5 6 0 3:cast 60\ncheck class:class 3:minlevel 20:checkability 300 2:testability 300 2:giveability 300 3\n", "Called From": "Textblock #510" },
          { "Number": 600, "Action": "checkability 300 1:testability 300 1:failitem 900:giveitem 900:giveability 300 2\n", "Called From": "Spell #41" },
          { "Number": 610, "Action": "checkability 300 1:addability 300 1\n", "Called From": "Spell #45" },
          { "Number": 700, "Action": "touch altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300\npress altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300\n", "Called From": "Room 1/100" },
          { "Number": 710, "Action": "kneel:failability 300:testskill agility 25 955\n", "Called From": "Room 1/100" },
          { "Number": 720, "Action": "60:addexp 0\n100:giveability 300 9:failability 301 1 4105\n", "Called From": "Textblock(rndm) #719" },
          { "Number": 730, "Action": "pray:checkability 300 1:failability 301:giveability 302 1:addability 301 2\n", "Called From": "Room 1/101" },
          { "Number": 740, "Action": "giveability 301 1\n", "Called From": "" }
        ]
        """;

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<RoomKey>> MonsterRooms =
        new Dictionary<int, IReadOnlyList<RoomKey>>
        {
            [10] = new[] { new RoomKey(1, 101) },
            [11] = new[] { new RoomKey(1, 100) },
        };

    private GameDataCache NewCache()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), ClassesJson);
        File.WriteAllText(Path.Combine(dir, "Races.json"), RacesJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private QuestFlagWalkthrough Walk(int flag = Flag)
    {
        GameDataCache cache = NewCache();
        return QuestFlagWalkthroughBuilder.Build(new QuestFlagIndex(cache), cache, flag, MonsterRooms);
    }

    private static IEnumerable<string> Lines(QuestFlagStepEntry step, string label) =>
        step.Parts.Where(p => p.Label == label).SelectMany(p => p.Lines);

    private static IEnumerable<string> Needs(QuestFlagStepEntry step) => Lines(step, "Needs");
    private static IEnumerable<string> Gives(QuestFlagStepEntry step) => Lines(step, "Gives");

    private static QuestFlagStepEntry Step(QuestFlagWalkthrough walk, string headingStart) =>
        walk.Steps.Concat(walk.WithoutFlagSteps).Single(s => s.Heading.StartsWith(headingStart, StringComparison.Ordinal));

    // ----- The script line itself -----

    [Theory]
    [InlineData("throw egg:checkitem 998", "throw egg")]
    [InlineData("summon avatar:minlevel 30 1674", "summon avatar")]
    [InlineData("check class:class 2:giveability 50 1", null)]
    [InlineData("minlevel 15:giveability 133 1", null)]
    [InlineData("summon 595:message 12", null)]
    [InlineData("100:giveitem 1449", null)]
    public void Line_LeadingStep_IsACommandOnlyWhenAPlayerTypesIt(string raw, string? command)
    {
        Assert.Equal(command, QuestScriptLine.Parse(raw).Command);
    }

    [Fact]
    public void Line_LeadingNumber_IsABandOnlyInARandomTable()
    {
        QuestScriptLine band = QuestScriptLine.Parse("100:giveitem 1449:giveability 205 1", randomTable: true);
        Assert.Equal(100, band.RollBand);
        Assert.Equal(new[] { "giveitem 1449", "giveability 205 1" }, band.Steps.Select(s => s.Raw));

        // Anywhere else the number is just a step, kept as written.
        QuestScriptLine plain = QuestScriptLine.Parse("100:giveitem 1449:giveability 205 1");
        Assert.Null(plain.RollBand);
        Assert.Null(plain.Command);
        Assert.Equal("100", plain.Steps[0].Raw);
    }

    [Theory]
    [InlineData("minlevel 15:giveability 133 1", "15+")]
    [InlineData("minlevel 10 3246:takeitem 5:minlevel 15", "15+")]   // every gate is enforced
    [InlineData("minlevel 27 2614:addexp 5000:minlevel 23", "27+")]  // a later, lower one relaxes nothing
    [InlineData("maxlevel 19", "up to 19")]
    [InlineData("minlevel 20:maxlevel 29", "20 to 29")]
    [InlineData("giveability 133 1", "")]
    public void Line_Level_TakesTheHighestGate(string raw, string level)
    {
        Assert.Equal(level, QuestScriptLine.Parse(raw).LevelText);
    }

    // ----- Table columns -----

    private static QuestFlagRef Row(QuestFlagIndex index, int textblock, QuestFlagRelation relation, int order = 0) =>
        index.Entries.Single(e => e.Relation == relation
            && e.Line.Textblock == textblock && e.Line.Order == order);

    [Fact]
    public void Table_NpcKeywordLine_ShowsTheAskAndItsRequirements()
    {
        QuestFlagIndex index = new(NewCache());

        QuestFlagRef start = Row(index, 502, QuestFlagRelation.Grants);
        Assert.Equal("ask old sage tale (or story)", start.Command);
        Assert.Equal("15+", start.Line.Level);
        Assert.Equal(1, start.Value);

        QuestFlagRef turnIn = Row(index, 511, QuestFlagRelation.Grants);
        Assert.Equal("ask old sage reward", turnIn.Command);
        Assert.Equal("Warrior", turnIn.Line.Classes);
        Assert.Equal("troll tooth (taken)", turnIn.Line.Items);
        Assert.Equal(QuestFlagSourceKind.Monster, turnIn.SourceKind);
        Assert.Equal("Old Sage", turnIn.SourceName);
    }

    [Fact]
    public void Table_KeepsOneRowPerScriptLine()
    {
        QuestFlagIndex index = new(NewCache());

        // Three lines of one textblock each grant the flag the same value from the same NPC.
        List<QuestFlagRef> grants = index.Entries
            .Where(e => e.Flag == Flag && e.Relation == QuestFlagRelation.Grants && e.Line.Textblock == 511)
            .ToList();
        Assert.Equal(new[] { "Warrior", "Mage", "Thief" }, grants.Select(g => g.Line.Classes));
        Assert.Equal(new[] { "", "", "20+" }, grants.Select(g => g.Line.Level));
    }

    [Fact]
    public void Table_RoomCommand_IsTypedAsWritten()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal("touch altar", Row(index, 700, QuestFlagRelation.Clears, order: 0).Command);
        Assert.Equal("press altar", Row(index, 700, QuestFlagRelation.Clears, order: 1).Command);
    }

    [Fact]
    public void Table_DeathSpellLine_NamesTheMonsterToKill()
    {
        QuestFlagIndex index = new(NewCache());
        QuestFlagRef row = Row(index, 600, QuestFlagRelation.Grants);
        // Spell 41 is reached from the bog troll's death spell 40 through its EndCast.
        Assert.Equal("kill bog troll", row.Command);
        Assert.Equal(QuestFlagSourceKind.Spell, row.SourceKind);
    }

    [Fact]
    public void Table_DeathSpellThatNeverFires_GivesNoCommand()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal(string.Empty, Row(index, 610, QuestFlagRelation.Advances).Command);
    }

    [Fact]
    public void Table_SharedDialogue_GivesEachRowItsOwnNpcsCommand()
    {
        QuestFlagIndex index = new(NewCache());

        List<QuestFlagRef> rows = index.Entries
            .Where(e => e.Line.Textblock == 901 && e.Relation == QuestFlagRelation.Grants).ToList();
        // The ask target drops a leading article.
        Assert.Equal(
            new[] { ("Old Sage", "ask old sage reconcile"), ("The Grey Sage", "ask grey sage reconcile") },
            rows.Select(r => (r.SourceName, r.Command)));
    }

    [Fact]
    public void Table_LineStraightUnderAKeyword_IsAsked()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal("ask old sage replace", Row(index, 530, QuestFlagRelation.Requires).Command);
    }

    [Fact]
    public void Table_AutoShownKeyword_IsNotACommand()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal(string.Empty, Row(index, 540, QuestFlagRelation.Grants).Command);
    }

    [Fact]
    public void Table_FailabilityMessageNumber_IsNotAStep()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal(0, Row(index, 502, QuestFlagRelation.Gate).Value);
    }

    // ----- Steps: the settled directives -----

    [Fact]
    public void Step_Start_ReadsWithoutTheFlagAndTheHighestLevel()
    {
        QuestFlagStepEntry step = Step(Walk(), "Start");

        Assert.Equal("Start — without the flag → sets 1", step.Heading);
        Assert.Equal(new[] { "Type \"ask old sage tale\" (the same reply comes from: story) — Old Sage is in Sage's Hut (1/101)" }, step.Do);
        Assert.Equal(new[] { "Level 15 or higher" }, Needs(step));
        Assert.Empty(Gives(step));
        Assert.Equal(new[] { "text 503" }, step.Also);
        Assert.Equal(new[] { "Textblock #502: minlevel 10 3000:minlevel 15:failability 300 3001:giveability 300 1:text 503" }, step.Script);
    }

    [Fact]
    public void Step_TurnIn_PutsTheSettledStepsIntoWordsAndQuotesTheRest()
    {
        QuestFlagStepEntry step = Walk().Steps.First(s => s.Heading == "At exactly 2 → sets 3");

        Assert.Equal(new[]
        {
            "Class: Warrior, Mage",
            "Taken from you: troll tooth (item 900)",
            "Costs 500 copper",
        }, Needs(step));
        Assert.Equal(new[]
        {
            "silver key (item 901)",
            "2,500 experience",
            "MaxDamage +1",
            "Teaches the spell fireball (spell 70)",
            "Teleports you to Sage's Hut (1/101)",
        }, Gives(step));
        // What isn't settled stays word for word; a cast only gains the spell's name.
        Assert.Equal(new[] { "check class", "remoteaction 5 6 0 3", "cast 60  [spell: reveal]" }, step.Also);
    }

    [Fact]
    public void Step_ClassVariants_FoldIntoOneEntry_OtherDifferencesStayApart()
    {
        List<QuestFlagStepEntry> turnIns = Walk().Steps.Where(s => s.Heading == "At exactly 2 → sets 3").ToList();

        Assert.Equal(2, turnIns.Count);
        Assert.Equal(2, turnIns[0].Script.Count);                       // the Warrior and Mage lines
        Assert.Contains("Class: Warrior, Mage", Needs(turnIns[0]));
        Assert.Equal(new[] { "Level 20 or higher", "Class: Thief" }, Needs(turnIns[1]));
        Assert.Single(turnIns[1].Script);
    }

    [Fact]
    public void Step_KillLine_NamesTheMonsterAndItsRoom()
    {
        QuestFlagStepEntry step = Step(Walk(), "At exactly 1");

        Assert.Equal("At exactly 1 → sets 2", step.Heading);
        Assert.Equal(new[] { "Kill bog troll (monster 11), found in Mossy Shrine (1/100) — its death runs this script" }, step.Do);
        Assert.Equal(new[] { "Must not have troll tooth (item 900)" }, Needs(step));
        Assert.Equal(new[] { "troll tooth (item 900)" }, Gives(step));
    }

    [Fact]
    public void Step_RoomCommand_ListsEveryWordingOnce()
    {
        QuestFlagStepEntry step = Step(Walk(), "At 3 or more");

        Assert.Equal("At 3 or more → clears the flag", step.Heading);
        Assert.Equal(new[] { "Type any of: \"touch altar\", \"press altar\" in Mossy Shrine (1/100)" }, step.Do);
        Assert.Equal(new[]
        {
            "old map (item 902) must be in the room",
            "No monster in the room (an NPC counts as one)",
            "bog troll (monster 11) in the room",
            // The threshold is quoted, without the message number that follows it.
            "Alignment, as the script writes it: goodaligned -51",
        }, Needs(step));
        Assert.Equal(new[]
        {
            "Textblock #700: touch altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300",
            "    (and 1 more line with the other wording of the command, otherwise the same)",
        }, step.Script);
    }

    [Fact]
    public void Step_OtherFlags_AreNamedAndListed()
    {
        QuestFlagWalkthrough walk = Walk();
        QuestFlagStepEntry step = Step(walk, "At 1 or more → no change");

        Assert.Equal(new[] { "Type \"pray\" in Sage's Hut (1/101)" }, step.Do);
        Assert.Equal(new[] { "Without QuestFlag301 (301)" }, Needs(step));
        Assert.Equal(new[] { "Sets QuestFlag302 (302) to 1", "Adds 2 to QuestFlag301 (301)" }, Gives(step));
        Assert.Equal(new[] { 301, 302 }, walk.OtherFlags);
    }

    // ----- Steps: what isn't settled stays raw -----

    [Fact]
    public void Step_UnsettledWayIn_SaysWhereTheLineIsReachedFrom()
    {
        QuestFlagStepEntry step = Step(Walk(), "At 1 or more → adds 1");
        Assert.Equal(new[] { "Reached from spell crier text (#45; the data lists it as cast by Monster #12)" }, step.Do);
    }

    [Fact]
    public void Step_RandomBand_GivesItsShareAndKeepsAnUnsettledFailabilityRaw()
    {
        QuestFlagStepEntry step = Step(Walk(), "No check on the flag");

        Assert.Equal("No check on the flag → sets 9", step.Heading);
        Assert.Equal(new[]
        {
            "Reached from Textblock(rndm) #719",
            "One outcome of a random draw from textblock #720: the band up to 100 (40% of draws)",
        }, step.Do);
        Assert.Equal(new[] { "failability 301 1 4105" }, step.Also);
        Assert.Empty(Needs(step));
    }

    [Fact]
    public void Step_UnknownDirective_IsShownVerbatim()
    {
        QuestFlagStepEntry step = Step(Walk(), "Without the flag");
        Assert.Equal(new[] { "testskill agility 25 955" }, step.Also);
        Assert.Empty(Needs(step));
        Assert.Empty(Gives(step));
    }

    // ----- Order -----

    [Fact]
    public void Steps_AreOrderedByTheValueGoingIn_ThenTheValueLeft()
    {
        QuestFlagWalkthrough walk = Walk();

        Assert.Equal(new[]
        {
            "Start — without the flag → sets 1",
            "At exactly 1 → sets 2",
            "At 1 or more → no change",
            "At 1 or more → adds 1",
            "At exactly 2 → sets 3",
            "At exactly 2 → sets 3",
            "At 3 or more → clears the flag",
            // A line that sets the flag without checking it sits where its value leads.
            "No check on the flag → sets 9",
        }, walk.Steps.Select(s => s.Heading));
        // A line that only bars a character who has the flag is kept apart.
        Assert.Equal(new[] { "Without the flag → no change" }, walk.WithoutFlagSteps.Select(s => s.Heading));
    }

    [Fact]
    public void OnlyWithoutLines_AreTheStepsWhenThereAreNoOthers()
    {
        QuestFlagWalkthrough walk = Walk(350);

        Assert.Equal(new[] { "Without the flag → no change" }, walk.Steps.Select(s => s.Heading));
        Assert.Empty(walk.WithoutFlagSteps);
        // A price that names a coin is quoted rather than read as copper.
        Assert.Equal(new[] { "price 5 G", "text 1" }, walk.Steps[0].Also);
        Assert.Empty(Needs(walk.Steps[0]));
    }

    // ----- A check on a flag the line has just changed -----

    [Fact]
    public void CheckAfterOwnChange_ThatCannotPass_StopsTheLineThere()
    {
        // Without the flag, then set to 1, then asked to be at least 3: nothing after it runs.
        QuestFlagStepEntry step = Walk(131).Steps.Single(s => s.Do[0].Contains("move ruby"));

        Assert.Equal("Start — without the flag → sets 1, then stops at `checkability 131 3`", step.Heading);
        Assert.Empty(Gives(step));
        Assert.Equal("Not reached — the line stops at `checkability 131 3`, having just left the flag at 1", step.LaterLabel);
        Assert.Equal(new[]
        {
            "testability 131 3", "addexp 15000000", "message 541", "addability 58 1", "addability 70 2",
        }, step.Later);
    }

    [Fact]
    public void CheckAfterOwnChange_ThatPasses_LetsTheLineRunOn()
    {
        QuestFlagStepEntry step = Walk(131).Steps.Single(s => s.Do[0].Contains("touch ruby"));

        Assert.Equal("Start — without the flag → sets 3", step.Heading);
        Assert.Equal(new[] { "15,000,000 experience", "Crits +1", "Spellcasting +2" }, Gives(step));
        Assert.Equal(new[]
        {
            "checkability 131 3  [passes: this line has just left the flag at 3]",
            "testability 131 3  [passes: this line has just left the flag at 3]",
            "message 541",
        }, step.Also);
        Assert.Empty(step.Later);
    }

    [Fact]
    public void CheckAfterOwnChange_WithTheValueNotPinned_IsQuotedWithoutAVerdict()
    {
        // "At least 1" doesn't say what the flag was, and Stock keeps the higher value, so
        // whether "at least 3" then passes is left open.
        QuestFlagStepEntry step = Walk(131).Steps.Single(s => s.Do[0].Contains("rub ruby"));

        Assert.Equal("At 1 or more → sets 2", step.Heading);
        Assert.Empty(Gives(step));
        Assert.StartsWith("After `checkability 131 3` — a check on the flag as this line has just changed it", step.LaterLabel);
        Assert.Equal(new[] { "addexp 5" }, step.Later);
    }

    [Fact]
    public void CheckAfterOwnChange_SeenFromAnotherAbility_IsNotANeed()
    {
        // The same lines read for Crits (58): the stat is only added on the wording that
        // passes the check on 131, and that check is never listed beside "without 131".
        QuestFlagWalkthrough walk = Walk(58);
        QuestFlagStepEntry stops = walk.Steps.Single(s => s.Do[0].Contains("move ruby"));
        QuestFlagStepEntry runs = walk.Steps.Single(s => s.Do[0].Contains("touch ruby"));

        Assert.Equal("No check on the flag → no change, then stops at `checkability 131 3` before its `addability 58 1`", stops.Heading);
        Assert.Equal(new[]
        {
            "Level 35 or higher",
            "Without SheDragonQuest (131)",
            "No monster in the room (an NPC counts as one)",
        }, Needs(stops));
        Assert.Equal(new[] { "Sets SheDragonQuest (131) to 1" }, Gives(stops));
        Assert.Equal("No check on the flag → adds 1", runs.Heading);
    }

    [Fact]
    public void FlagChangeBehindAnUnsettledCheck_IsNotPromised()
    {
        // 380 is set with nothing said about what it was, then checked; the change to 390
        // comes after that check.
        QuestFlagStepEntry step = Walk(390).Steps.Single();

        Assert.Equal("No check on the flag → no change, then checks `checkability 380 1` before its `giveability 390 2`", step.Heading);
        Assert.Equal(new[] { "Sets QuestFlag380 (380) to 1" }, Gives(step));
        Assert.Equal(new[] { "giveability 390 2" }, step.Later);
    }

    // ----- A value the line pinned, and what can unpin it -----

    [Fact]
    public void RandomDrawBetweenTheChangeAndTheCheck_LeavesTheCheckUnsettled()
    {
        // Half the draws from 781 set the flag to 9, so "at least 2" may well pass.
        QuestFlagStepEntry step = Walk(309).Steps.Single(s => s.Do[0].Contains("hum") && s.Script.Count == 1);

        Assert.Equal("Start — without the flag → sets 1", step.Heading);
        Assert.StartsWith("After `checkability 309 2`", step.LaterLabel);
        Assert.Equal(new[] { "addexp 5" }, step.Later);
    }

    [Fact]
    public void CastBetweenThePinAndTheChange_DiscardsThePin()
    {
        // "Without the flag" was asked before the cast, which may have given it.
        QuestFlagStepEntry step = Walk(311).Steps.Single();

        Assert.Equal("Start — without the flag → sets 1", step.Heading);
        Assert.StartsWith("After `checkability 311 3`", step.LaterLabel);
    }

    [Fact]
    public void PinMadeAfterTheCast_StillHolds_AndTheCheckIsNoEntryCondition()
    {
        QuestFlagStepEntry step = Walk(312).Steps.Single();

        Assert.Equal("Without the flag, checked after `cast 60` → sets 1, then stops at `checkability 312 3`", step.Heading);
        Assert.Equal(new[] { "Without this flag" }, Lines(step, "Needs (after `cast 60`)"));
        Assert.Equal(new[] { "addexp 7" }, step.Later);
    }

    [Fact]
    public void ExactValueThenAddability_IsStillPinned()
    {
        QuestFlagStepEntry step = Walk(313).Steps.Single();

        Assert.Equal("At exactly 2 → adds 1", step.Heading);
        Assert.Contains("checkability 313 3  [passes: this line has just left the flag at 3]", step.Also);
        Assert.Equal(new[] { "1 experience" }, Gives(step));
    }

    [Fact]
    public void ExactValueThenALowerGiveability_IsNotJudged()
    {
        // Stock keeps the higher value, so the flag may still be 5 after "sets 2".
        QuestFlagStepEntry step = Walk(314).Steps.Single();

        Assert.Equal("At exactly 5 → sets 2", step.Heading);
        Assert.StartsWith("After `testability 314 2`", step.LaterLabel);
        Assert.Empty(Gives(step));
    }

    [Fact]
    public void Removeability_LeavesTheFlagAbsent()
    {
        QuestFlagStepEntry passes = Walk(315).Steps.Single();
        Assert.Contains("failability 315  [passes: this line has just cleared the flag]", passes.Also);
        Assert.Equal(new[] { "1 experience" }, Gives(passes));

        // "At most 3" fails for a character without the flag.
        QuestFlagStepEntry stops = Walk(316).Steps.Single();
        Assert.Equal("At 1 or more → clears the flag, then stops at `testability 316 3`", stops.Heading);
        Assert.Equal("Not reached — the line stops at `testability 316 3`, having just cleared the flag", stops.LaterLabel);
    }

    // ----- Conditions written late -----

    [Fact]
    public void ConditionAfterTheFlagChange_IsCheckedAfterwards_NotANeed()
    {
        QuestFlagStepEntry step = Walk(370).Steps.Single();

        Assert.Equal("Start — without the flag → sets 1", step.Heading);
        // The runs in the order the line goes through them: the change, a check, a give.
        Assert.Equal(new[]
        {
            ("Gives", "This flag: sets 1"),
            ("Then checks", "Must not have troll tooth (item 900)"),
            ("Then gives", "troll tooth (item 900)"),
        }, step.Parts.Select(p => (p.Label, string.Join(" / ", p.Lines))));
    }

    [Fact]
    public void FlagCheckAfterAGive_IsMarkedAsLate()
    {
        QuestFlagStepEntry step = Walk(360).Steps.Single();

        Assert.Equal("Without the flag, checked after the line's gives → no change", step.Heading);
        Assert.Equal(new[]
        {
            ("First, whatever the checks say", "silver key (item 901)"),
            ("Needs", "Without this flag"),
        }, step.Parts.Select(p => (p.Label, string.Join(" / ", p.Lines))));
    }

    [Fact]
    public void GiveAheadOfTheConditions_IsListedFirst_AndTheRestAfterThem()
    {
        // Only the teleport runs before the checks; the item depends on all of them.
        QuestFlagStepEntry step = Walk(128).Steps.Single();

        Assert.Equal("At exactly 9, checked after `text 1310` → no change", step.Heading);
        Assert.Equal(new[]
        {
            ("First, whatever the checks say", "Teleports you to Sage's Hut (1/101)"),
            ("Needs (after `text 1310`)",
                "This flag exactly 9 / Without GoodQuest (126) / Without NeutralQuest (127) / "
                + "Taken from you: troll tooth (item 900) / Alignment, as the script writes it: evilaligned 60"),
            ("Gives", "silver key (item 901)"),
        }, step.Parts.Select(p => (p.Label, string.Join(" / ", p.Lines))));
    }

    [Fact]
    public void GiveBetweenTwoRunsOfConditions_DependsOnlyOnTheFirst()
    {
        // The teleport needs an empty room and nothing else: the level and flag checks
        // come after it, and after a cast.
        QuestFlagStepEntry step = Walk(129).Steps.Single();

        Assert.Equal("Without the flag, checked after `cast 60` → sets 2", step.Heading);
        Assert.Equal(new[]
        {
            ("Needs", "No monster in the room (an NPC counts as one)"),
            ("Gives", "Teleports you to Sage's Hut (1/101)"),
            ("Then checks (after `cast 60`)", "Level 12 or higher / Without this flag"),
            ("Then gives", "Spellcasting +1 / 250,000 experience / This flag: sets 2"),
        }, step.Parts.Select(p => (p.Label, string.Join(" / ", p.Lines))));
    }

    // ----- A line drawn from a random table -----

    [Fact]
    public void DrawnLine_TakesItsConditionsFromTheLineThatDrawsIt()
    {
        QuestFlagWalkthrough walk = Walk(310);

        Assert.Equal(new[]
        {
            // The drawing line belongs to the flag's steps although it leaves the flag alone.
            "Without the flag → no change on this line, which draws from textblock #810",
            "Start — without the flag → sets 1",
            // The same table line read behind its other caller, which asks nothing of the flag.
            "No check on the flag → sets 1",
        }, walk.Steps.Select(s => s.Heading));
        Assert.Empty(walk.WithoutFlagSteps);

        QuestFlagStepEntry drawn = walk.Steps[1];
        Assert.Equal(new[]
        {
            "Type \"draw lot\" in Mossy Shrine (1/100)",
            "Then a random draw: textblock #800 draws from textblock #810 with `random 810`, and this outcome is the band up to 100 (25% of draws)",
        }, drawn.Do);
        Assert.Equal(new[]
        {
            "Level 50 or higher",
            "Class: Warrior, Mage",
            "Taken from you: troll tooth (item 900)",
        }, Needs(drawn));
        Assert.Equal(new[] { "silver key (item 901)" }, Gives(drawn));
        Assert.Equal(new[]
        {
            "Textblock #800: draw lot:class 1:minlevel 50 3020:failability 310 3667:takeitem 900:random 810",
            "Textblock #800: draw lot:class 2:minlevel 50 3020:failability 310 3667:takeitem 900:random 810",
            "Textblock #810: 100:giveitem 901:giveability 310 1:message 2",
        }, drawn.Script);
    }

    [Fact]
    public void DrawnLine_IsReadBehindEachLineThatDrawsIt()
    {
        QuestFlagStepEntry viaSecond = Walk(310).Steps[2];

        Assert.Equal(new[]
        {
            "Type \"pull lot\" in Sage's Hut (1/101)",
            "Then a random draw: textblock #801 draws from textblock #810 with `random 810`, and this outcome is the band up to 100 (25% of draws)",
        }, viaSecond.Do);
        Assert.Equal(new[] { "Level 10 or higher" }, Needs(viaSecond));
    }

    [Fact]
    public void TableDrawnFromADrawnTable_KeepsTheOuterLinesConditionsAndCommand()
    {
        QuestFlagWalkthrough walk = Walk(317);

        Assert.Equal(new[]
        {
            "Without the flag → no change on this line, which draws from textblock #821",
            "Start — without the flag → sets 4",
        }, walk.Steps.Select(s => s.Heading));

        QuestFlagStepEntry drawn = walk.Steps[1];
        Assert.Equal(new[]
        {
            "Type \"spin\" in Mossy Shrine (1/100)",
            // A root none of the ways in accounts for is still named.
            "Also reached from monster town crier (#12)",
            "Then a random draw: textblock #820 draws from textblock #821 with `random 821`, and this outcome is the band up to 100 (100% of draws)",
            "Then a random draw: textblock #821 draws from textblock #822 with `random 822`, and this outcome is the band up to 100 (60% of draws)",
        }, drawn.Do);
        Assert.Equal(new[] { "Level 5 or higher", "Taken from you: troll tooth (item 900)" }, Needs(drawn));
        Assert.Equal(new[]
        {
            "Textblock #820: spin:failability 317:minlevel 5:random 821",
            "Textblock #821: 100:takeitem 900:random 822",
            "Textblock #822: 100:giveability 317 4",
        }, drawn.Script);
    }

    // ----- NPC keywords -----

    [Fact]
    public void KeywordWrittenOnTheNpcsOwnBlock_IsAsked()
    {
        GameDataCache cache = NewCache();
        QuestFlagIndex index = new(cache);

        Assert.Equal("ask old sage bless",
            index.Entries.Single(e => e.Flag == 318 && e.Relation == QuestFlagRelation.Grants).Command);
        QuestFlagStepEntry step = QuestFlagWalkthroughBuilder.Build(index, cache, 318, MonsterRooms).Steps.Single();
        Assert.Equal(new[] { "Type \"ask old sage bless\" — Old Sage is in Sage's Hut (1/101)" }, step.Do);
    }

    [Fact]
    public void SharedDialogue_ListsEveryNpc()
    {
        QuestFlagStepEntry step = Walk(320).Steps.Single();

        Assert.Equal(new[]
        {
            "Type \"ask old sage reconcile\" — Old Sage is in Sage's Hut (1/101)",
            "Type \"ask grey sage reconcile\" — The Grey Sage",
        }, step.Do);
    }

    [Fact]
    public void AutoShownKeyword_IsNamedAsWhereTheLineComesFrom_NotAsACommandOrAKill()
    {
        QuestFlagStepEntry step = Walk(340).Steps.Single();

        Assert.Equal(new[]
        {
            "Reached from the \"greeting\" keyword of Old Sage (monster 10), listed in textblock #500; "
            + "the NPC is in Sage's Hut (1/101) — a keyword the NPC shows by itself, not one you ask",
        }, step.Do);
    }

    // ----- The quest list's findings -----

    [Fact]
    public void Restriction_IsLeftOutWhenTheCrawlRecordsNone()
    {
        Assert.Equal(string.Empty, Walk().RestrictionText);
    }

    [Fact]
    public void FlagNoScriptTouches_HasNoSteps()
    {
        GameDataCache cache = NewCache();
        QuestFlagWalkthrough walk = QuestFlagWalkthroughBuilder.Build(new QuestFlagIndex(cache), cache, 399);
        Assert.Empty(walk.Steps);
        Assert.Empty(walk.WithoutFlagSteps);
    }
}
