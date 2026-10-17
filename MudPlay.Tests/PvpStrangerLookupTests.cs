using MudPlay.Game.Combat;
using MudPlay.Game.Pvp;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpStrangerLookup: a name with no player record sends one `who`, without
// spamming, and the name becomes a player in the room once the list has it.
public sealed class PvpStrangerLookupTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public RoomEntityClassifier Classifier { get; }
        public PvpStrangerLookup Lookup { get; }
        public bool PvpEnabled { get; set; } = true;
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public int Whos { get; private set; }
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            MonsterMessageStore monsters = new();
            monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M1", Name: "giant rat", Links: new[] { new GameDataLink("Monsters", 1) }));
            Classifier = new RoomEntityClassifier(Router, monsters, Players, new LogService());
            Lookup = new PvpStrangerLookup(
                Router, Classifier, Players,
                pvpEnabled: () => PvpEnabled,
                sendWho: () => Whos++,
                schedule: (delay, action) => Scheduled.Add((delay, action)),
                now: () => Clock);
        }

        public EntityKind KindOf(string rawName) =>
            Classifier.Current!.Value.Entities.Single(e => e.RawName == rawName).Kind;

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Lookup.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void UnplacedNameInTheRoom_SendsWho_AndCountsAsAPlayerUntilItIsRead()
    {
        using Harness h = new();

        h.Feed("Also here: Zed, giant rat.");

        Assert.Equal(1, h.Whos);
        Assert.True(h.Lookup.MayBePlayer("Zed"));
        Assert.False(h.Lookup.MayBePlayer("giant rat"));
    }

    [Fact]
    public void WhoThatListsThem_MakesThemAPlayerInTheRoom_Now()
    {
        using Harness h = new();
        h.Feed("Also here: Zed, giant rat.");
        Assert.Equal(EntityKind.Unknown, h.KindOf("Zed"));

        h.Players.RecordObservation("Zed", null, null, null, null, null, null, DateTime.UtcNow);
        h.Lookup.NoteWhoRead();

        Assert.Equal(EntityKind.Player, h.KindOf("Zed"));
        Assert.False(h.Lookup.MayBePlayer("Zed"));
    }

    // `who` lists everyone online: a name it didn't carry isn't a player.
    [Fact]
    public void WhoThatDoesNotListThem_StopsCountingThemAsAPlayer()
    {
        using Harness h = new();
        h.Feed("Also here: Zed, giant rat.");

        h.Lookup.NoteWhoRead();

        Assert.False(h.Lookup.MayBePlayer("Zed"));
        Assert.Equal(EntityKind.Unknown, h.KindOf("Zed"));
    }

    [Fact]
    public void RecordFromElsewhere_SettlesThemToo()
    {
        using Harness h = new();
        h.Feed("Also here: Zed.");

        h.Players.RecordObservation("Zed", "Mage", null, null, null, null, null, DateTime.UtcNow);

        Assert.Equal(EntityKind.Player, h.KindOf("Zed"));
    }

    // A fresh realm knows nobody: many names, one `who`.
    [Fact]
    public void ManyUnknownNames_SendOneWho_AndTheNextWaitsOutTheGap()
    {
        using Harness h = new();

        h.Feed("Also here: Zed, Yan, Xia.");
        Assert.Equal(1, h.Whos);
        Assert.Empty(h.Scheduled);
        h.Lookup.NoteWhoRead();

        h.Clock += TimeSpan.FromSeconds(5);
        h.Feed("Also here: Wren.");
        h.Feed("Also here: Vic.");
        Assert.Equal(1, h.Whos);
        (TimeSpan delay, Action later) = Assert.Single(h.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(25), delay);

        h.Clock += delay;
        later();
        Assert.Equal(2, h.Whos);
    }

    [Fact]
    public void SameNameAgain_IsNotAskedAboutAgainForAWhile()
    {
        using Harness h = new();
        h.Feed("Also here: Zed.");
        h.Lookup.NoteWhoRead();

        h.Clock += TimeSpan.FromMinutes(1);
        h.Feed("Also here: Zed.");
        Assert.Equal(1, h.Whos);
        Assert.Empty(h.Scheduled);

        h.Clock += PvpStrangerLookup.NameRetry;
        h.Feed("Also here: Zed.");
        Assert.Equal(2, h.Whos);
    }

    [Fact]
    public void SomeoneEnteringTheRealmWeDontKnow_SendsWho_AKnownOneDoesNot()
    {
        using Harness h = new();
        h.Players.RecordObservation("Bob", null, null, null, null, null, null, DateTime.UtcNow);

        h.Feed("Bob just entered the Realm.");
        Assert.Equal(0, h.Whos);

        h.Feed("Zed just entered the Realm.");
        Assert.Equal(1, h.Whos);
        Assert.False(h.Lookup.MayBePlayer("Zed"));      // not in our room
    }

    [Theory]
    [InlineData("large goblin")]        // lower case: a monster the data doesn't know
    [InlineData("Three Headed Dog")]    // too many words for a player's name
    public void NamesThatDontLookLikeAPlayers_AreLeftAlone(string name)
    {
        using Harness h = new();

        h.Feed($"Also here: {name}.");

        Assert.Equal(0, h.Whos);
    }

    [Fact]
    public void PvpOffForTheRealm_NothingIsSent()
    {
        using Harness h = new() { PvpEnabled = false };

        h.Feed("Also here: Zed.");
        h.Feed("Yan just entered the Realm.");

        Assert.Equal(0, h.Whos);
    }
}
