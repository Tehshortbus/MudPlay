using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A monster on the roster casting a summon mid-fight ("The fat half-orc sentry shouts for
// aid!") must re-display the room, so the summoned monster joins the roster before its
// summoner dies instead of the kill emptying the roster and dropping combat.
public sealed class MonsterSummonWatcherTests
{
    private static MessageRecord Summon(int spell, string wording) => new(
        Id: $"S{spell}", Name: $"summon {spell}", Flags: default, RawFlagsHex: 0,
        CasterMessage: "", TargetMessage: wording, WitnessMessage: wording,
        AppliedMessage: "", AppliedEndsWith: "",
        Links: new[] { new GameDataLink("Spells", spell) });

    private static SummonLineSet ShoutsForAid() => new(new[] { 593 }, new[]
    {
        Summon(593, "The {source} shouts for aid!"),
        // Not a summon spell: its wording mustn't be picked up.
        Summon(35, "The {source} hurls a poison bolt at you!"),
    });

    [Fact]
    public void SummonLineSet_ReadsTheCasterOffTheShout()
    {
        SummonLineSet set = ShoutsForAid();
        Assert.True(set.TryMatch("The fat half-orc sentry shouts for aid!", out string? source));
        Assert.Equal("fat half-orc sentry", source);
        Assert.False(set.TryMatch("The fat half-orc sentry hurls a poison bolt at you!", out _));
        // The death line reuses the words but isn't a cast.
        Assert.False(set.TryMatch("The half-orc sentry shouts for aid, and falls dead!", out _));
    }

    [Fact]
    public void SummonSpells_AreTheSpellsWithTheSummonAbility()
    {
        GameTable doc = TestRows.Table(
            "[{\"Number\":593,\"Name\":\"summon orc warrior\",\"Abil-0\":12,\"AbilVal-0\":480}," +
            " {\"Number\":35,\"Name\":\"poison bolt\",\"Abil-0\":19,\"AbilVal-0\":5}]");
        Assert.Equal(new[] { 593 }, SummonLineSet.SummonSpells(doc));
    }

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public MonsterSummonWatcher Watcher { get; }
        public List<string> Refreshes { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, new PlayerDatabase(new ProfileService(), () => null), new LogService());
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M479", Name: "half-orc sentry", Links: new[] { new GameDataLink("Monsters", 479) }));
            Watcher = new MonsterSummonWatcher(Router, Classifier, ShoutsForAid,
                requestRoomRefresh: reason => { Refreshes.Add(reason); return true; });
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void SummonByARosterMonster_RedisplaysTheRoom()
    {
        using Harness h = new();
        h.Feed("Also here: fat half-orc sentry.");
        h.Feed("The fat half-orc sentry shouts for aid!");
        Assert.Single(h.Refreshes);
    }

    // The summoned monster is in the room and on no roster until the room is read
    // again, so the roster is known to be short from the summon line on.
    [Fact]
    public void ASummon_LeavesTheRosterShortOfSomeone_UntilTheRoomIsReadAgain()
    {
        using Harness h = new();
        h.Feed("Also here: fat half-orc sentry.");
        Assert.True(h.Classifier.RosterIsWhole);

        h.Feed("The fat half-orc sentry shouts for aid!");
        Assert.False(h.Classifier.RosterIsWhole);
        Assert.Equal("a summon: 'The fat half-orc sentry shouts for aid!'", h.Classifier.RosterNotWholeBecause);

        h.Feed("Also here: fat half-orc sentry, orc warrior.");
        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Null(h.Classifier.RosterNotWholeBecause);

        h.Feed("The burly dwarven guard shouts for aid!");      // a summoner the roster doesn't even list
        Assert.False(h.Classifier.RosterIsWhole);
    }

    [Fact]
    public void SummonByNoOneOnTheRoster_SendsNothing()
    {
        using Harness h = new();
        h.Feed("The fat half-orc sentry shouts for aid!");      // empty roster
        h.Feed("Also here: fat half-orc sentry.");
        h.Feed("The burly dwarven guard shouts for aid!");      // not on the roster
        Assert.Empty(h.Refreshes);
    }
}
