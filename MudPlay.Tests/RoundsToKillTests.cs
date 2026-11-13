using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Session Stats' rounds-to-kill: each monster death is charged the rounds we fought
// since the fight began or the previous kill. Lines run through the real router, the
// round ledger, the combat-state tracker that owns InCombat, and the death watcher,
// wired as AppServices wires them.
public sealed class RoundsToKillTests
{
    private sealed class Harness : IDisposable
    {
        private readonly ManualClock _clock = new();

        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RoundDamageTracker Rounds { get; }
        public CombatStateTracker Combat { get; }
        public MonsterDeathWatcher Deaths { get; }
        public CombatSessionTracker Tracker { get; }

        // deathHeardFirst: the death watcher hears *Combat Off* before the round
        // ledger closes the kill's round, so the death arrives with its round open.
        // Otherwise the round has closed by the time the death is raised. Which comes
        // first in the client depends on handler order, so both must charge alike.
        public Harness(bool deathHeardFirst = false)
        {
            DefaultPatterns.Seed(Router);
            MonsterMessageStore monsters = new();
            PlayerDatabase players = new(new ProfileService(), () => null);
            RoomEntityClassifier classifier = new(Router, monsters, players, Log);
            if (deathHeardFirst) Deaths = new MonsterDeathWatcher(Router, Log, () => _clock.Now);
            Rounds = new RoundDamageTracker(Router, State, clock: () => _clock.Now);
            Combat = new CombatStateTracker(
                Router, new MovementCoordinator(Log), classifier, monsters, State,
                () => true, resolveOverlay: _ => new MonsterOverlay(), log: Log);
            Deaths ??= new MonsterDeathWatcher(Router, Log, () => _clock.Now);
            Tracker = new CombatSessionTracker(Router, Rounds);
            Rounds.SetNameSources(() => new[] { "Bob" }, () => null);
            Deaths.MonsterDied += Tracker.OnMonsterDied;
            Rounds.NoteRoomEntities(new RoomEntitiesObservation(
                "Also here: kobold, giant rat.",
                new[]
                {
                    new RoomEntity("kobold", "kobold", EntityKind.Monster, 1),
                    new RoomEntity("giant rat", "giant rat", EntityKind.Monster, 2),
                },
                _clock.Now));
        }

        public void Feed(string line) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), _clock.Now, IsPromptLine: false));

        // One combat round that doesn't kill: the lines, then the round's tick.
        public void Round(params string[] lines)
        {
            foreach (string line in lines) Feed(line);
            _clock.Advance(TimeSpan.FromSeconds(4));
            Rounds.OnCombatTick();
        }

        // The killing round: the blow, the exp line and *Combat Off*.
        public void KillingRound(string blow = "You slash the kobold for 9 damage!")
        {
            Feed(blow);
            Feed("You gain 9 experience.");
            Feed("*Combat Off*");
            _clock.Advance(TimeSpan.FromSeconds(4));
            Rounds.OnCombatTick();
        }

        public void Wait(TimeSpan span) => _clock.Advance(span);

        public CombatSessionStats Stats => Tracker.Snapshot();

        public void Dispose()
        {
            Tracker.Dispose();
            Deaths.Dispose();
            Combat.Dispose();
            Rounds.Dispose();
        }
    }

    // A one-round kill counts 1 and a two-round kill 2, averaging 1.5 — the figure
    // the Rounds/kill row shows.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachKill_IsChargedItsOwnRounds(bool deathHeardFirst)
    {
        using Harness h = new(deathHeardFirst);

        h.Feed("*Combat Engaged*");
        h.KillingRound();

        h.Feed("*Combat Engaged*");
        h.Round("You slash the giant rat for 4 damage!");
        h.KillingRound("You slash the giant rat for 6 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(2, s.TimedKills);
        Assert.Equal(1, s.RoundsToKillMin);
        Assert.Equal(2, s.RoundsToKillMax);
        Assert.Equal(1.5, s.AvgRoundsToKill);
    }

    // A round we whiffed is still a round of the fight.
    [Fact]
    public void ARoundWeMissedIn_StillCounts()
    {
        using Harness h = new();

        h.Feed("*Combat Engaged*");
        h.Round("You slash at the kobold!", "The kobold bites you for 3 damage!");
        h.Round("You slash the kobold for 4 damage!");
        h.KillingRound();

        Assert.Equal(3, h.Stats.RoundsToKillMax);
    }

    // A room spell that drops two monsters in one round: both took that many rounds.
    [Fact]
    public void KillsInTheSameRound_ShareItsCount()
    {
        using Harness h = new();

        h.Feed("*Combat Engaged*");
        h.Round("You slash the kobold for 4 damage!");
        h.Feed("You slash the kobold for 9 damage!");
        h.Feed("You gain 9 experience.");
        h.Feed("*Combat Off*");
        h.Feed("You gain 9 experience.");
        h.Feed("*Combat Off*");

        CombatSessionStats s = h.Stats;
        Assert.Equal(2, s.TimedKills);
        Assert.Equal(2, s.RoundsToKillMin);
        Assert.Equal(4L, s.RoundsToKillTotal);
    }

    // A fight we broke off, and a later fight after a rest: the abandoned rounds
    // don't land on the next kill.
    [Fact]
    public void AnAbandonedFight_DoesNotCarryIntoTheNextKill()
    {
        using Harness h = new();

        h.Feed("*Combat Engaged*");
        h.Round("You slash the kobold for 4 damage!");
        h.Round("You slash the kobold for 4 damage!");
        h.Feed("*Combat Off*");
        h.Wait(TimeSpan.FromMinutes(2));

        h.Feed("*Combat Engaged*");
        h.KillingRound("You slash the giant rat for 9 damage!");

        Assert.Equal(1, h.Stats.TimedKills);
        Assert.Equal(1, h.Stats.RoundsToKillMax);
    }

    // A party member's kill while we stood by is no kill of ours to time, whether it
    // follows one of ours or a fight we broke off long before.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartyMembersKill_WhileWeStoodBy_IsNotTimed(bool deathHeardFirst)
    {
        using Harness h = new(deathHeardFirst);

        h.Feed("*Combat Engaged*");
        h.KillingRound();
        h.KillingRound("Bob slashes the giant rat for 9 damage!");

        Assert.Equal(1, h.Stats.TimedKills);
        Assert.Equal(1, h.Stats.RoundsToKillMax);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartyMembersKill_AfterAFightWeBrokeOff_IsNotTimed(bool deathHeardFirst)
    {
        using Harness h = new(deathHeardFirst);

        h.Feed("*Combat Engaged*");
        h.Round("You slash the kobold for 4 damage!");
        h.Round("You slash the kobold for 4 damage!");
        h.Feed("*Combat Off*");
        h.Wait(TimeSpan.FromMinutes(2));

        h.KillingRound("Bob slashes the giant rat for 9 damage!");

        Assert.Equal(0, h.Stats.TimedKills);
    }

    // Exp from a kill made with no round of ours behind it isn't timed.
    [Fact]
    public void ADeathWithNoRoundOfOurs_IsNotTimed()
    {
        using Harness h = new();

        h.Feed("You gain 9 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal(0, h.Stats.TimedKills);
        Assert.Equal(0, h.Stats.AvgRoundsToKill);
    }

    [Fact]
    public void Reset_ClearsTheCount()
    {
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Round("You slash the kobold for 4 damage!");

        h.Tracker.Reset();
        h.Feed("*Combat Engaged*");
        h.KillingRound();

        Assert.Equal(1, h.Stats.TimedKills);
        Assert.Equal(1, h.Stats.RoundsToKillMax);
    }
}
