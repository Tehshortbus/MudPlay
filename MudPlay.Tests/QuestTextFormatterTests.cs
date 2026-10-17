using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// QuestTextFormatter's data-independent logic: the multi-part band
// title ("Good 1" from the GoodQuest flag), the checkbox-markdown parser that
// classifies a step line as a tickable checkbox or a plain context label, and the
// (map/room) coordinate splitter that isolates clickable walk-to links from
// surrounding prose. The cache-dependent QuestTextFormatter.StepLines is covered by
// QuestItemGatheringTests and QuestTickMigrationTests; here we pin the pure string
// behaviour.
public sealed class QuestTextFormatterTests
{
    [Fact]
    public void FallbackTitle_MultiPartBand_DropsQuestSuffixAndAppendsOrdinal()
    {
        // Flag 126 → "GoodQuest"; band 1 → "Good 1".
        var q = new CrawledQuest(126, 10, 10, Array.Empty<QuestBonus>(), Array.Empty<int>(), BandOrdinal: 1);
        Assert.Equal("Good 1", QuestTextFormatter.FallbackTitle(q));
    }

    [Fact]
    public void FallbackTitle_SinglePart_UsesFlagNameVerbatim()
    {
        var q = new CrawledQuest(126, 0, 0, Array.Empty<QuestBonus>(), Array.Empty<int>());
        Assert.Equal("GoodQuest", QuestTextFormatter.FallbackTitle(q));
    }

    [Theory]
    [InlineData("[] do thing", true, "do thing")]
    [InlineData("[ ] do thing", true, "do thing")]
    [InlineData("[x] done thing", true, "done thing")]
    [InlineData("[X] done thing", true, "done thing")]
    [InlineData("plain label line", false, "plain label line")]
    public void ParseStepLines_ClassifiesCheckboxVsLabel(string line, bool checkable, string text)
    {
        (bool Checkable, string Text) row = Assert.Single(QuestTextFormatter.ParseStepLines(line));
        Assert.Equal(checkable, row.Checkable);
        Assert.Equal(text, row.Text);
    }

    [Fact]
    public void ParseStepLines_SkipsBlankLines()
    {
        var rows = QuestTextFormatter.ParseStepLines("[] a\n\n   \n[] b").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0].Text);
        Assert.Equal("b", rows[1].Text);
    }

    [Fact]
    public void SplitStepLinks_IsolatesCoordinateAsLinkBetweenProseRuns()
    {
        var segs = QuestTextFormatter.SplitStepLinks("sit throne @ (5/297) receive crown").ToList();
        Assert.Equal(3, segs.Count);
        Assert.Equal(("sit throne @ ", (RoomKey?)null, (string?)null), segs[0]);
        Assert.Equal(("(5/297)", (RoomKey?)new RoomKey(5, 297), (string?)null), segs[1]);
        Assert.Equal((" receive crown", (RoomKey?)null, (string?)null), segs[2]);
    }

    [Fact]
    public void SplitStepLinks_LeadingCoordinateEmitsNoEmptyProseRun()
    {
        var segs = QuestTextFormatter.SplitStepLinks("(1/3) go north").ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal(("(1/3)", (RoomKey?)new RoomKey(1, 3), (string?)null), segs[0]);
        Assert.Equal((" go north", (RoomKey?)null, (string?)null), segs[1]);
    }

    [Fact]
    public void SplitStepLinks_MultipleCoordinates_EachBecomesItsOwnLink()
    {
        var segs = QuestTextFormatter.SplitStepLinks("(1/2) then (3/4)").ToList();
        Assert.Equal(new RoomKey?[] { new RoomKey(1, 2), null, new RoomKey(3, 4) },
                     segs.Select(s => s.Room).ToArray());
    }

    [Fact]
    public void SplitStepLinks_NoToken_ReturnsSinglePlainRun()
    {
        var seg = Assert.Single(QuestTextFormatter.SplitStepLinks("just plain prose"));
        Assert.Equal(("just plain prose", (RoomKey?)null, (string?)null), seg);
    }

    [Fact]
    public void SplitStepLinks_OverRangeCoordinate_StaysFoldedInProse()
    {
        // A number too large for int isn't a real room — it must not become a link,
        // and the whole token stays in the prose run.
        var seg = Assert.Single(QuestTextFormatter.SplitStepLinks("go (99999999999/3)"));
        Assert.Null(seg.Room);
        Assert.Equal("go (99999999999/3)", seg.Text);
    }

    [Fact]
    public void SplitStepLinks_EmptyInput_ReturnsNoSegments()
    {
        Assert.Empty(QuestTextFormatter.SplitStepLinks(string.Empty));
    }

    // ----- single-quoted command tokens -----

    [Fact]
    public void SplitStepLinks_IsolatesSingleQuotedCommand_UnquotesTheCommand()
    {
        var segs = QuestTextFormatter.SplitStepLinks("then 'ask jorah transport' and wait").ToList();
        Assert.Equal(3, segs.Count);
        Assert.Equal(("then ", (RoomKey?)null, (string?)null), segs[0]);
        // The shown text keeps the quotes; the Command carries the unquoted verb.
        Assert.Equal(("'ask jorah transport'", (RoomKey?)null, (string?)"ask jorah transport"), segs[1]);
        Assert.Equal((" and wait", (RoomKey?)null, (string?)null), segs[2]);
    }

    [Fact]
    public void SplitStepLinks_ProseApostrophes_AreNotTreatedAsCommand()
    {
        // Apostrophes sitting between letters (don't, they're) must not open a bogus
        // quoted run — the whole line stays one plain segment.
        var seg = Assert.Single(
            QuestTextFormatter.SplitStepLinks("don't bother buffing, they're stripped"));
        Assert.Null(seg.Command);
        Assert.Null(seg.Room);
        Assert.Equal("don't bother buffing, they're stripped", seg.Text);
    }

    [Fact]
    public void SplitStepLinks_MixedRoomAndCommand_BothBecomeLinks()
    {
        var segs = QuestTextFormatter.SplitStepLinks("(2/2566) 'touch ruby'").ToList();
        Assert.Equal(3, segs.Count);
        Assert.Equal(new RoomKey(2, 2566), segs[0].Room);
        Assert.Null(segs[0].Command);
        Assert.Equal("touch ruby", segs[2].Command);
        Assert.Null(segs[2].Room);
    }

    [Fact]
    public void SplitStepLinks_EmptyQuote_StaysFoldedInProse()
    {
        // '' carries no command — leave it in the prose run, no dead link.
        var seg = Assert.Single(QuestTextFormatter.SplitStepLinks("nothing here ''"));
        Assert.Null(seg.Command);
        Assert.Equal("nothing here ''", seg.Text);
    }

    // ----- the other ways guides mark a command ------------------------------
    //
    // Report paradigm-20261011-230844: the drafted steps write `ask renfry hands` in
    // backticks and pasted write-ups use "…" / “…”, and none of those were links.

    [Fact]
    public void SplitStepLinks_BacktickCommand_IsALink_ApostrophesAndAll()
    {
        var segs = QuestTextFormatter.SplitStepLinks("(14/247) `ask renfry hands` (turn in X)").ToList();
        Assert.Equal(new RoomKey(14, 247), segs[0].Room);
        Assert.Equal(("`ask renfry hands`", (RoomKey?)null, (string?)"ask renfry hands"), segs[2]);
        Assert.Equal(" (turn in X)", segs[3].Text);

        Assert.Equal("ask spirit of draka's ghost",
            QuestTextFormatter.SplitStepLinks("`ask spirit of draka's ghost`").Single().Command);
    }

    [Theory]
    [InlineData("Talk with him and say “ask Demon Imp return”, and he will", "ask Demon Imp return")]
    [InlineData("you can \"ask Demon Imp back\" and you will be teleported", "ask Demon Imp back")]
    [InlineData("Travel W and “say nightfall” to see a flash of light.", "say nightfall")]
    public void SplitStepLinks_DoubleQuotedCommand_IsALink(string line, string command)
    {
        Assert.Equal(command, Assert.Single(QuestTextFormatter.SplitStepLinks(line), s => s.Command is not null).Command);
    }

    [Theory]
    [InlineData("Equip and \"use\" the Multicoloured Staff.")]
    [InlineData("This part is to finish \"Rune-ing\" your character")]
    [InlineData("He says \"This is not what I want.\" and walks off")]
    public void SplitStepLinks_DoubleQuotedWordOrSentence_StaysProse(string line)
    {
        Assert.Null(Assert.Single(QuestTextFormatter.SplitStepLinks(line)).Command);
    }

    [Fact]
    public void SeedGuides_MarkEveryAskWithBackticks()
    {
        // The shipped guides use the one mark: no ask is left in single or double quotes.
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-questseed-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            using JsonDocument seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "QuestDefs.seed.json")));
            int commands = 0;
            foreach (JsonElement quest in seed.RootElement.EnumerateArray())
            {
                string steps = quest.GetProperty("Steps").GetString() ?? string.Empty;
                foreach (string line in steps.Split('\n'))
                    foreach (var seg in QuestTextFormatter.SplitStepLinks(line))
                        if (seg.Command is not null)
                        {
                            Assert.StartsWith("`", seg.Text, StringComparison.Ordinal);
                            commands++;
                        }
            }
            Assert.True(commands > 100);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ----- Step: seed-style auto-draft rendering -----------------------------
    //
    // Step resolves item / monster names off the cache; with no active set those
    // lookups fall back to "#id" / "monster #id", which is enough to pin the
    // seed-style shape (room-link-first, backtick command, kill/obtain narration,
    // trailing item note) without loading a real game-data set.
    private static readonly GameDataCache NoSet = new(Path.GetTempPath());

    private static QuestStep MakeStep(int order, string? command, string? location,
        int[]? required = null, int[]? turnIn = null, int[]? granted = null) =>
        new(order, command, location,
            required ?? Array.Empty<int>(), turnIn ?? Array.Empty<int>(), granted ?? Array.Empty<int>());

    [Fact]
    public void Step_CommandWithRoom_RendersRoomLinkThenBacktickCommand()
    {
        Assert.Equal("(9/1259) `ask old man phoenix`",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(1, "ask old man phoenix", "Room 9/1259")));
    }

    // A set where monster 39 dies into spell 610, which ends by casting 611 — the spell a
    // kill step's textblock is called from. Monster 40 is an NPC with nothing of the kind.
    private static GameDataCache KillSet()
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-questtext-tests-" + Path.GetRandomFileName());
        string dir = Path.Combine(root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), """
            [
              { "Number": 39, "Name": "queen ant", "DeathSpell": 610 },
              { "Number": 40, "Name": "old hermit", "DeathSpell": 0 }
            ]
            """);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), """
            [
              { "Number": 610, "Name": "ant queen temp", "Targets": 12, "Abil-0": 151, "AbilVal-0": 611 },
              { "Number": 611, "Name": "ant queen text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1444 }
            ]
            """);
        GameDataCache cache = new(root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<RoomKey>> Placement(int monster, params RoomKey[] keys) =>
        new Dictionary<int, IReadOnlyList<RoomKey>> { [monster] = keys };

    [Fact]
    public void Step_ScriptOffAMonstersDeathSpell_NarratesKillWithDrop()
    {
        // No placement map → the kill narrates room-less.
        Assert.Equal("kill queen ant (#100)",
            QuestTextFormatter.StepOrNull(KillSet(), MakeStep(2, null, "Spell #611", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_KillStep_PrependsMonsterPlacementRoomLink()
    {
        // The room link comes from where the quest places the monster whose death runs
        // the step, resolved into the threaded-in placement map.
        Assert.Equal("(9/717) kill queen ant (#100)",
            QuestTextFormatter.StepOrNull(KillSet(), MakeStep(2, null, "Spell #611", granted: new[] { 100 }),
                Placement(39, new RoomKey(9, 717))));
    }

    [Fact]
    public void Step_KillStep_MultiplePlacements_ListEveryRoomAsItsOwnLink()
    {
        Assert.Equal("(1/2) (3/4) kill queen ant",
            QuestTextFormatter.StepOrNull(KillSet(), MakeStep(2, null, "Spell #611"),
                Placement(39, new RoomKey(1, 2), new RoomKey(3, 4))));
    }

    [Fact]
    public void Step_KillStep_UnplacedMonster_StaysRoomless()
    {
        // Monster absent from the map (nothing places it) → no dead link, the kill
        // narration stands alone.
        Assert.Equal("kill queen ant",
            QuestTextFormatter.StepOrNull(KillSet(), MakeStep(2, null, "Spell #611"),
                Placement(99, new RoomKey(1, 2))));
    }

    [Fact]
    public void Step_KillStep_TwoRecordsOfOneMonster_NamesItOnceWithEachPlacedRoomOnce()
    {
        // Two spells off two records of the same boss run the step; the first has no
        // placement, the second stands in two rooms, one of them shared with a third record.
        string root = Path.Combine(Path.GetTempPath(), "mudplay-questtext-tests-" + Path.GetRandomFileName());
        string dir = Path.Combine(root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), """
            [
              { "Number": 2203, "Name": "dark phoenix", "DeathSpell": 5272 },
              { "Number": 700, "Name": "dark phoenix", "DeathSpell": 871 },
              { "Number": 701, "Name": "dark phoenix", "DeathSpell": 871 }
            ]
            """);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), """
            [
              { "Number": 5272, "Name": "phoenix text a", "Targets": 1 },
              { "Number": 871, "Name": "phoenix text b", "Targets": 1 }
            ]
            """);
        GameDataCache set = new(root);
        set.SwitchSet("alpha");
        QuestStep first = MakeStep(18, null, "Spell #5272");
        QuestStep second = MakeStep(18, null, "Spell #871");
        var placement = new Dictionary<int, IReadOnlyList<RoomKey>>
        {
            [700] = new[] { new RoomKey(16, 2160), new RoomKey(16, 2161) },
            [701] = new[] { new RoomKey(16, 2160) },
        };

        Assert.Equal("(16/2160) (16/2161) kill dark phoenix",
            QuestTextFormatter.StepOrNull(set, first, placement, sameStep: new[] { first, second }));
        // On its own the unplaced record still narrates, room-less.
        Assert.Equal("kill dark phoenix", QuestTextFormatter.StepOrNull(set, first, placement));
    }

    [Fact]
    public void Step_SpellThatIsNoMonstersDeath_IsNotAKill()
    {
        Assert.Equal("obtain #100",
            QuestTextFormatter.StepOrNull(KillSet(), MakeStep(2, null, "Spell #999", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_CommandlessStepOffAnNpc_IsNotAKill()
    {
        // A script that hangs off a monster with no command is that NPC's dialogue (a
        // keyword it shows by itself), not its death: nothing to kill and no room to walk to.
        GameDataCache set = KillSet();
        Assert.Equal("obtain #100",
            QuestTextFormatter.StepOrNull(set, MakeStep(2, null, "Monster #40", granted: new[] { 100 }),
                Placement(40, new RoomKey(2, 340))));
        Assert.Null(QuestTextFormatter.StepOrNull(set, MakeStep(2, null, "Monster #40"),
            Placement(40, new RoomKey(2, 340))));
    }

    [Fact]
    public void Step_CommandlessGrant_NarratesObtain()
    {
        Assert.Equal("obtain #100",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(3, null, "Textblock #5", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_AskStep_MonsterCommand_RendersNpcRoomThenBacktickCommand()
    {
        // QuestStepGraph re-anchors a dialogue step on its NPC (Location "Monster #N")
        // and fills in the ask command — the room link comes from that NPC's placement,
        // even though the step carries a command (unlike a kill step).
        Assert.Equal("(2/340) `ask archmage valduin crystal`",
            QuestTextFormatter.StepOrNull(NoSet,
                MakeStep(2, "ask archmage valduin crystal", "Monster #1266"),
                Placement(1266, new RoomKey(2, 340))));
    }

    [Fact]
    public void Step_RequiredItemAlsoTurnedIn_ListedOnce()
    {
        // A step that checks the same item it takes (checkitem + takeitem of one id)
        // names it once.
        Assert.Equal("(2/340) (#100 required) `ask valduin crystal`",
            QuestTextFormatter.StepOrNull(NoSet,
                MakeStep(2, "ask valduin crystal", "Room 2/340",
                    required: new[] { 100 }, turnIn: new[] { 100 })));
    }

    [Fact]
    public void Step_TakenAndCheckedItems_AreBothRequired()
    {
        Assert.Equal("(2/340) (#100, #200 required) `ask x`",
            QuestTextFormatter.StepOrNull(NoSet,
                MakeStep(2, "ask x", "Room 2/340",
                    required: new[] { 200 }, turnIn: new[] { 100 })));
    }

    [Fact]
    public void Step_HandInWithNothingElseToDo_ReadsTurnIn()
    {
        Assert.Equal("(6/3109) turn in #100, #200",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(10, null, "Room 6/3109", turnIn: new[] { 100, 200 })));
    }

    [Fact]
    public void Step_MultiRoomLocation_ListsEveryRoomAsItsOwnLink()
    {
        Assert.Equal("(5/294) (16/368) (1/527)",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(1, null, "Room 5/294, Room 16/368, Room 1/527")));
    }

    [Fact]
    public void Step_NeededItem_GoesAheadOfTheCommand_AsTheGuidesWriteIt()
    {
        // The seed guide's "[](6/1357) (Gleaming Shard required) `ask martok forge`".
        Assert.Equal("(6/1357) (#100 required) `ask martok forge`",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(4, "ask martok forge", "Room 6/1357", required: new[] { 100 })));
        Assert.Equal("(1/1333) (#100 required) `ask annora head`",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(5, "ask annora head", "Room 1/1333", turnIn: new[] { 100 })));
    }

    [Fact]
    public void Step_CommandGrant_TrailsAsGetNote()
    {
        Assert.Equal("(9/1259) `ask smith key` (get #100)",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(6, "ask smith key", "Room 9/1259", granted: new[] { 100 })));
    }

    [Fact]
    public void StepOrNull_NonActionableStep_ReturnsNull()
    {
        // A pure flag-advance (Called-From another textblock, no room / command /
        // kill / item) carries nothing to do — StepOrNull reports that with null so
        // the auto-draft can drop it instead of listing an opaque "Step N".
        Assert.Null(QuestTextFormatter.StepOrNull(NoSet, MakeStep(31, null, "Textblock #5")));
    }

    [Fact]
    public void StepOrNull_ActionableStep_ReturnsBody()
    {
        Assert.Equal("(9/1259) `ask old man phoenix`",
            QuestTextFormatter.StepOrNull(NoSet, MakeStep(1, "ask old man phoenix", "Room 9/1259")));
    }
}
