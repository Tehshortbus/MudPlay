using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="RoomDepartureWatcher"/> + the regex behind
/// <see cref="KnownPatterns.RoomEntryDeparture"/>. A mid-room departure line
/// ("The orc rogue walks out of the room to the above!") — most often a fleeing
/// player dragging the engaged mob out — must drop that monster from
/// <see cref="RoomEntityClassifier.Current"/> and re-fire the observation so the
/// combat gate the mob held can clear. Covers article stripping, the with-article
/// retry, and the no-op for a departing player.
/// </summary>
public sealed class RoomDepartureWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomDepartureWatcher Watcher { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Watcher = new RoomDepartureWatcher(Router, Classifier, Log);
            Classifier.EntitiesObserved += Observations.Add;
        }

        public void AddMonster(int number, string name, bool allowNoPrefix = true,
                               params string[] flavorPrefixes)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: Array.Empty<string>(),
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: flavorPrefixes,
                AllowNoPrefix: allowNoPrefix,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void AddPlayer(string givenName)
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: givenName,
                FamilyName: string.Empty,
                Class: "Warrior",
                Race: "Human",
                Alignment: "Neutral",
                Title: null,
                Gang: null,
                Role: null,
                FirstSeenUtc: DateTime.UtcNow,
                LastSeenUtc: DateTime.UtcNow));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void CanonicalDepartureLine_RemovesMonster_FiresDepartureObservation()
    {
        // The 180449 capture verbatim: the engaged orc rogue is dragged out to
        // "the above". It must leave Current so the gate it held drops.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc rogue");

        h.Feed("Also here: giant rat, orc rogue.");
        Assert.Equal(2, h.Observations[0].Entities.Count);

        h.Feed("The orc rogue walks out of the room to the above!");

        Assert.Equal(2, h.Observations.Count);
        Assert.Single(h.Observations[1].Entities);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void LastHostileDeparts_LeavesEmptyList()
    {
        // The last mob leaves — the re-fired observation is empty, which is what
        // lets the combat gate re-evaluate to zero hostiles and release the walker.
        using Harness h = new();
        h.AddMonster(2, "orc rogue");

        h.Feed("Also here: orc rogue.");
        Assert.Single(h.Observations[0].Entities);

        h.Feed("The orc rogue walks out of the room to the above!");

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void ExitsVerbDepartureLine_RemovesMonster_FiresDepartureObservation()
    {
        // paradigm-20260712-220516: fleeing players dragged the engaged
        // "dark goblin archer" out, which the server announced with the "exits
        // the room to" verb (no leading article) rather than "walks out of". The
        // stuck-combat bug was this line failing to match, so the gate the mob
        // held never released. Verbatim from that capture's live screen.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark goblin archer");

        h.Feed("Also here: giant rat, dark goblin archer.");
        Assert.Equal(2, h.Observations[0].Entities.Count);

        h.Feed("dark goblin archer exits the room to the northeast.");

        Assert.Equal(2, h.Observations.Count);
        Assert.Single(h.Observations[1].Entities);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void DepartureLine_StripsLeadingArticle_Cardinal()
    {
        // "A giant rat walks out of the room to north." — article stripped to the
        // bare stored name, plain cardinal direction (no "the ").
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("A giant rat walks out of the room to north.");

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
    }

    [Fact]
    public void MonsterNameBeginningWithThe_RemovedViaArticleRetry()
    {
        // The lone "The …"-named monster: stripping "The " gives a bare miss, so the
        // watcher retries with the article intact to recover the stored name.
        using Harness h = new();
        h.AddMonster(251, "The Eternal", allowNoPrefix: true);

        h.Feed("Also here: The Eternal.");
        Assert.Single(h.Observations[0].Entities);

        h.Feed("The Eternal walks out of the room to nowhere.");

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
    }

    [Fact]
    public void DepartingPlayer_NoRemoval_NoObservation()
    {
        // A departing player holds no combat gate, so a player entry in Current is
        // harmless — the watcher removes monster-kind only and must not re-fire.
        using Harness h = new();
        h.AddPlayer("Bob");

        h.Feed("Also here: Bob.");
        int observationsBefore = h.Observations.Count;

        h.Feed("Bob walks out of the room to south.");

        Assert.Equal(observationsBefore, h.Observations.Count);
    }

    [Fact]
    public void NonDepartureLine_NoRemoval()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        int observationsBefore = h.Observations.Count;

        h.Feed("The giant rat walks into the room from north.");   // arrival, not departure
        h.Feed("Some unrelated line.");

        Assert.Equal(observationsBefore, h.Observations.Count);
    }
}
