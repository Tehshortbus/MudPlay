using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Settings → Combat "Hide combat lines": a ticked kind of line stays off the screen and
// out of the scrollback, while every reader of the line still gets it. Lines go in as
// bytes with the game's colours, through the emulator the filter is hooked to.
public sealed class CombatLineDisplayFilterTests
{
    private const int Red = 1, Green = 2, Cyan = 6, White = 7;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public ServerFeed Feed { get; }
        public CombatLineDisplayFilter Filter { get; }
        public List<string> Party { get; } = new() { "Raine" };
        public HashSet<string> Players { get; } = new(StringComparer.OrdinalIgnoreCase) { "Bob" };
        public bool Unrecognized { get; set; }

        public Harness(params CombatLineCategory[] hidden)
        {
            DefaultPatterns.Seed(Router);
            Feed = new ServerFeed(Router);
            Filter = new CombatLineDisplayFilter(Router, () => Party, Players.Contains, () => Unrecognized);
            Filter.SetHidden(hidden);
            Feed.Emulator.HideFinishedRow = Filter.TakeRowHidden;
        }

        public void Engage() => Feed.Line("*Combat Engaged*");

        public void Say(string text, int colour = White, bool bold = false) =>
            Feed.Coloured(new[] { (text, colour, bold) });

        public IReadOnlyList<string> Screen()
        {
            TerminalScreen screen = Feed.Emulator.Screen;
            List<string> rows = new();
            for (int y = 0; y < screen.Rows; y++)
            {
                string row = new string(screen.Row(y).ToArray().Select(c => c.Char).ToArray()).TrimEnd();
                if (row.Length > 0) rows.Add(row);
            }
            return rows;
        }

        public void Dispose() => Filter.Dispose();
    }

    public static TheoryData<string, int, bool, CombatLineCategory> EveryKind => new()
    {
        { "You slash green slime for 12 damage!", Red, true, CombatLineCategory.YourHits },
        { "You lunge at green slime!", Cyan, false, CombatLineCategory.YourMisses },
        { "Your swipe at zombie hits, but glances off its armour.", Red, false, CombatLineCategory.YourMisses },
        { "Raine hacks cave worm for 18 damage!", Red, true, CombatLineCategory.PartyHits },
        { "Raine lunges at cave worm!", Cyan, false, CombatLineCategory.PartyMisses },
        { "Bob slashes orc for 9 damage!", Red, true, CombatLineCategory.PlayerHits },
        { "Bob lunges at orc!", Cyan, false, CombatLineCategory.PlayerMisses },
        { "The zombie bludgeons you for 8 damage!", Red, true, CombatLineCategory.MonsterHitsOnYou },
        { "The red slime flails at you!", Cyan, false, CombatLineCategory.MonsterMissesOnYou },
        { "The zombie swings at you, but you dodge out of the way!", Cyan, false, CombatLineCategory.MonsterMissesOnYou },
        { "The green slime lashes at you, but your armour deflects the blow!", Red, false, CombatLineCategory.MonsterMissesOnYou },
        { "The cave worm chomps Raine for 9 damage!", Red, true, CombatLineCategory.MonsterHitsOnOthers },
        { "The cave worm lunges at Raine!", Cyan, false, CombatLineCategory.MonsterMissesOnOthers },
        { "You are poisoned for 2 damage!", Green, false, CombatLineCategory.DamageNoAttacker },
        { "The armour spikes stab cave worm for 3 damage!", White, false, CombatLineCategory.Reflects },
        { "Raine moves to attack cave worm.", White, false, CombatLineCategory.AttackStarts },
        { "Raine moves to cast fireball upon cave worm.", White, false, CombatLineCategory.AttackStarts },
        { "You gain 50 experience.", White, false, CombatLineCategory.Experience },
        { "12 copper drop to the ground.", White, false, CombatLineCategory.CoinDrops },
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EachKindOfLine_IsHiddenByItsOwnBox_AndNoOther(string text, int colour, bool bold, CombatLineCategory box)
    {
        using Harness own = new(box);
        own.Engage();
        own.Say(text, colour, bold);
        Assert.DoesNotContain(text, own.Screen());

        using Harness others = new(Enum.GetValues<CombatLineCategory>().Where(c => c != box).ToArray());
        others.Engage();
        others.Say(text, colour, bold);
        Assert.Contains(text, others.Screen());
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void WithNothingTicked_EveryLineIsShown(string text, int colour, bool bold, CombatLineCategory _)
    {
        using Harness h = new();
        h.Engage();
        h.Say(text, colour, bold);
        Assert.Contains(text, h.Screen());
    }

    [Fact]
    public void AHiddenLine_StillReachesEveryReader()
    {
        using Harness h = new(CombatLineCategory.Experience);
        List<string> dispatched = new();
        int matched = 0;
        h.Router.LineDispatched += line => dispatched.Add(line.Text);
        using IDisposable sub = h.Router.Subscribe(KnownPatterns.UserGainExperience, _ => matched++);

        h.Say("You gain 50 experience.");

        Assert.Contains("You gain 50 experience.", dispatched);
        Assert.Equal(1, matched);
        Assert.DoesNotContain("You gain 50 experience.", h.Screen());
    }

    [Fact]
    public void TheCombatMarkers_HaveTheirOwnBox_AndStillOpenTheFight()
    {
        using Harness h = new(CombatLineCategory.CombatMarkers, CombatLineCategory.YourMisses);
        h.Engage();
        h.Say("You lunge at green slime!", Cyan);
        h.Feed.Line("*Combat Off*");
        Assert.Empty(h.Screen());

        // The window closed with its hidden marker: a dark cyan line is no miss now.
        h.Say("You lunge at nothing in particular!", Cyan);
        Assert.Contains("You lunge at nothing in particular!", h.Screen());
    }

    [Fact]
    public void AHitOutsideOurFight_IsStillHidden_ButAColourAloneIsNot()
    {
        using Harness h = new(CombatLineCategory.MonsterHitsOnYou, CombatLineCategory.MonsterMissesOnYou);

        h.Say("The zombie bludgeons you for 8 damage!", Red, true);
        h.Say("The river glitters at you!", Cyan);

        Assert.DoesNotContain("The zombie bludgeons you for 8 damage!", h.Screen());
        Assert.Contains("The river glitters at you!", h.Screen());
    }

    [Fact]
    public void UnknownLines_AreHiddenOnlyInAFight_AndOnlyWhenNothingKnowsThem()
    {
        using Harness h = new(CombatLineCategory.Unknown);
        h.Unrecognized = true;
        h.Say("The moss glows before the fight.");
        h.Engage();
        h.Say("The green slime bursts open with a rancid smell.", Green);
        h.Unrecognized = false;
        h.Say("A line something does recognize.");

        Assert.Contains("The moss glows before the fight.", h.Screen());
        Assert.DoesNotContain("The green slime bursts open with a rancid smell.", h.Screen());
        Assert.Contains("A line something does recognize.", h.Screen());
    }

    [Fact]
    public void ThePrompt_ChatAndTheClientsOwnNotices_AreNeverHidden()
    {
        using Harness h = new(Enum.GetValues<CombatLineCategory>());
        h.Unrecognized = true;
        h.Engage();
        h.Feed.Prompt("[HP=100/MA=50]:");
        h.Feed.Raw("[HP=100/MA=50]:a cave worm\r\n");
        h.Say("Bob gossips: You slash green slime for 12 damage!");
        h.Say("[Round 2 totals]");
        h.Filter.ClientIsWriting = true;
        h.Say("You slash the client's own line for 1 damage!", Red, true);
        h.Filter.ClientIsWriting = false;

        IReadOnlyList<string> screen = h.Screen();
        Assert.Contains("[HP=100/MA=50]:", screen);
        Assert.Contains("[HP=100/MA=50]:a cave worm", screen);
        Assert.Contains("Bob gossips: You slash green slime for 12 damage!", screen);
        Assert.Contains("[Round 2 totals]", screen);
        Assert.Contains("You slash the client's own line for 1 damage!", screen);
    }

    [Fact]
    public void TheNextLine_TakesTheHiddenLinesPlace()
    {
        using Harness h = new(CombatLineCategory.YourHits);
        h.Say("Before");
        h.Engage();
        h.Say("You slash green slime for 12 damage!", Red, true);
        h.Say("After");

        Assert.Equal(new[] { "Before", "*Combat Engaged*", "After" }, h.Screen());
    }

    [Fact]
    public void ALineThatWrapped_IsHiddenWhole()
    {
        using Harness h = new(CombatLineCategory.YourHits);
        h.Say("Before");
        h.Engage();
        string longLine = "You " + new string('x', 90) + " slash green slime for 12 damage!";
        h.Say(longLine, Red, true);
        h.Say("After");

        Assert.Equal(new[] { "Before", "*Combat Engaged*", "After" }, h.Screen());
    }

    [Fact]
    public void AHiddenLine_AtTheBottomOfTheScreen_ScrollsNothingAndLeavesNoHistory()
    {
        using Harness h = new(CombatLineCategory.YourHits);
        for (int i = 0; i < 30; i++) h.Say($"Filler {i}");
        h.Engage();
        int scrolled = h.Feed.Emulator.Screen.Scrollback.Count;

        for (int i = 0; i < 5; i++) h.Say("You slash green slime for 12 damage!", Red, true);

        Assert.Equal(scrolled, h.Feed.Emulator.Screen.Scrollback.Count);
        Assert.DoesNotContain("You slash green slime for 12 damage!", h.Screen());
        Assert.Contains("*Combat Engaged*", h.Screen());
    }

    [Fact]
    public void ARowPaintedByCursorMoves_StaysAndDoesntPassItsAnswerOn()
    {
        using Harness h = new(CombatLineCategory.YourHits);
        h.Engage();
        h.Feed.Raw("\u001b[10;1HYou slash the painted card for 12 damage!\u001b[12;1H");
        // Chat never reaches the filter, so only a stale answer could hide it.
        h.Feed.Raw("Bob gossips: next row\r\n");

        Assert.Contains("You slash the painted card for 12 damage!", h.Screen());
        Assert.Contains("Bob gossips: next row", h.Screen());
    }

    // A kept area ranking is compared by a hash of the settings it was made with, and
    // hiding a line changes nothing a simulation does.
    [Fact]
    public void TickingABox_DoesNotMarkKeptRankingsAsOutOfDate()
    {
        CombatSettings ticked = new() { HiddenCombatLines = new() { CombatLineCategory.YourHits } };

        Assert.Equal(Game.Simulation.RankingCharacter.HashOf(new CombatSettings()),
            Game.Simulation.RankingCharacter.HashOf(ticked));
    }

    [Fact]
    public void TheSummary_NamesWhatIsHiddenAndHowMany()
    {
        using Harness none = new();
        Assert.Equal("Every combat line is shown.", none.Filter.Summary());
        using Harness h = new(CombatLineCategory.YourHits);
        h.Engage();
        h.Say("You slash green slime for 12 damage!", Red, true);

        string summary = h.Filter.Summary();
        Assert.Contains("YourHits", summary);
        Assert.Contains("YourHits 1", summary);
    }
}
