using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The session tracker and the per-monster tracker on one router, wired as
// AppServices wires them: a spell's cast line has the shape of a missed swing, and
// the monster's record gives the miss back only when the spell is seen to land.
public sealed class MonsterObservationCastLineTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public FlavorPrefixStore Prefixes { get; } = new();
        public PlayerState State { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoundDamageTracker Rounds { get; }
        public CombatSessionTracker Session { get; }
        public MonsterObservationTracker Observations { get; }
        public string? CurrentTarget { get; set; }

        public Harness(bool withSpell = true)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(
                Router, Monsters, Players, roomTracker: null, Log, gameData: null, Prefixes);
            Rounds = new RoundDamageTracker(Router, State);
            IReadOnlyList<SpellLineMatcher>? lines = withSpell
                ? new[]
                {
                    new SpellLineMatcher("blast",
                        CasterMessageMatcher.TryCreate("You cast {s} at {target} for {damage} damage!")!),
                }
                : null;
            Session = new CombatSessionTracker(
                Router, Rounds, resolveSpellMatchers: lines is null ? null : () => lines);
            Rounds.SetOwnSpellLineCheck(Session.MatchesOwnSpell);
            Observations = new MonsterObservationTracker(Router, Classifier, () => CurrentTarget, profile: null);
            Session.CastLineMissRetracted += () => Observations.RetractLastMiss();

            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M1", Name: "giant rat", Links: new[] { new GameDataLink("Monsters", 1) }));
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M2", Name: "kobold", Links: new[] { new GameDataLink("Monsters", 2) }));
            Feed("Also here: giant rat, kobold.");
            Rounds.NoteRoomEntities(Classifier.Current!.Value);
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public int Misses(int monster) => Observations.For(monster)?.MissCount ?? 0;

        public void Dispose()
        {
            Observations.Dispose();
            Session.Dispose();
            Rounds.Dispose();
            Classifier.Dispose();
        }
    }

    // The shape in report paradigm-20261010-145330: cast line, then the spell lands.
    [Fact]
    public void CastLineThenLanding_LeavesNoMiss()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You scatter some ashes in a sweeping motion!");
        Assert.Equal(1, h.Misses(1));

        h.Feed("You cast blast at the giant rat for 809 damage!");

        Assert.Equal(0, h.Misses(1));
        Assert.Equal(0, h.Session.Snapshot().Misses);
    }

    [Fact]
    public void Whiff_CastLine_Landing_KeepsTheWhiff()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat!");
        h.Feed("You scatter some ashes in a sweeping motion!");
        h.Feed("You cast blast at the giant rat for 809 damage!");

        Assert.Equal(1, h.Misses(1));
    }

    [Fact]
    public void CastLine_Whiff_Landing_KeepsOneMiss()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You scatter some ashes in a sweeping motion!");
        h.Feed("You swing at giant rat!");
        h.Feed("You cast blast at the giant rat for 809 damage!");

        Assert.Equal(1, h.Misses(1));
    }

    // The target changes between the cast line and the landing: the count comes off
    // the monster it went on.
    [Fact]
    public void TargetSwitchBeforeLanding_RetractsFromTheMonsterCounted()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You scatter some ashes in a sweeping motion!");
        h.CurrentTarget = "kobold";
        h.Feed("You cast blast at the kobold for 809 damage!");

        Assert.Equal(0, h.Misses(1));
        Assert.Equal(0, h.Misses(2));
    }

    // A weapon user who also knows an attack spell: the round's only swing whiffs
    // and a cast has lately gone out. When the fight ends the session figures guess
    // the miss was a resisted cast; the monster's record does not follow the guess.
    [Fact]
    public void RealWhiff_ThenACastAndCombatOff_KeepsTheWhiffOnTheMonster()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat!");
        h.Rounds.NoteOwnCast();
        h.Feed("*Combat Off*");

        Assert.Equal(1, h.Misses(1));
    }

    // The same with the cast a heal or a buff: neither tally gives the whiff up.
    [Fact]
    public void RealWhiff_ThenABetweenRoundCastAndCombatOff_KeepsTheWhiffInBothTallies()
    {
        using Harness h = new();
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat!");
        h.Rounds.NoteOwnCast(attack: false);
        h.Feed("*Combat Off*");

        Assert.Equal(1, h.Misses(1));
        Assert.Equal(1, h.Session.Snapshot().Misses);
    }

    [Fact]
    public void NoAttackSpellKnown_RealWhiffThenCombatOff_KeepsTheWhiff()
    {
        using Harness h = new(withSpell: false);
        h.CurrentTarget = "giant rat";
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat!");
        h.Rounds.NoteOwnCast();
        h.Feed("*Combat Off*");

        Assert.Equal(1, h.Misses(1));
    }
}
