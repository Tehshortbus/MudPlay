using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// RoomDepartureWatcher + the regex behind
// KnownPatterns.RoomEntryDeparture. A mid-room departure line
// ("The orc rogue walks out of the room to the above!") — most often a fleeing
// player dragging the engaged mob out — must drop that monster from
// RoomEntityClassifier.Current and re-fire the observation so the
// combat gate the mob held can clear. Covers article stripping, the with-article
// retry, and the no-op for a departing player.
public sealed class RoomDepartureWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
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

        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
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
    public void MonsterSelfFlee_RemovesMonster_FiresDepartureObservation()
    {
        // paradigm-20260713-193205: the engaged "big forest spider" broke off and
        // fled on its own — "The big forest spider scuttles out to the west!" — a
        // wording with no "the room" and a per-monster verb. This line failing to
        // match left the Combat gate asserted and the "fighting" state stuck while
        // the client kept swinging at the empty room. Verbatim from that capture.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "big forest spider");

        h.Feed("Also here: giant rat, big forest spider.");
        Assert.Equal(2, h.Observations[0].Entities.Count);

        h.Feed("The big forest spider scuttles out to the west!");

        Assert.Equal(2, h.Observations.Count);
        Assert.Single(h.Observations[1].Entities);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void LastHostileSelfFlees_LeavesEmptyList()
    {
        // The lone engaged mob self-flees — the empty re-fired observation is what
        // lets the combat gate drop to zero hostiles and clear the fighting state.
        using Harness h = new();
        h.AddMonster(2, "big forest spider");

        h.Feed("Also here: big forest spider.");
        Assert.Single(h.Observations[0].Entities);

        h.Feed("The big forest spider scuttles out to the west!");

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
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
        h.AddMonster(251, "The Eternal");

        h.Feed("Also here: The Eternal.");
        Assert.Single(h.Observations[0].Entities);

        h.Feed("The Eternal walks out of the room to nowhere.");

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
    }

    // A player's presence is acted on now (a room attack is held for one outside
    // the party), so one who walks out comes off the roster like a monster does.
    [Fact]
    public void DepartingPlayer_IsRemoved_AndReported()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddPlayer("Bob");
        List<(string Name, string Direction)> departed = new();
        h.Watcher.PlayerDeparted += (name, direction) => departed.Add((name, direction));

        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob just left to the south.");

        RoomEntitiesObservation last = h.Observations[^1];
        Assert.Equal(RoomObservationSource.Departure, last.Source);
        Assert.Equal("giant rat", Assert.Single(last.Entities).ResolvedName);
        Assert.Equal(("Bob", "south"), Assert.Single(departed));
    }

    [Fact]
    public void DepartureLineNamingNobodyOnTheRoster_ChangesNothing()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.AddPlayer("Ann");

        h.Feed("Also here: Bob.");
        int observationsBefore = h.Observations.Count;

        h.Feed("Ann just left to the south.");

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
