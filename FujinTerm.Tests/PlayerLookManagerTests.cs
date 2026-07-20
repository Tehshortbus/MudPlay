using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="PlayerLookManager"/> — reactive `look &lt;player&gt;` automation.
/// Covers the end-to-end look-back pattern (validates the PlayerLooksAtYou
/// regex + subscription), the arrival hook's Player/Monster gating, and the
/// per-behaviour decision seams (enable gate, self/party skip, given-name
/// targeting).
/// </summary>
public sealed class PlayerLookManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher RoomEntry { get; }
        public PartyState Party { get; } = new();
        public PlayerLookManager Look { get; }
        public List<string> Sent { get; } = new();

        public string? SelfName { get; set; }

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            RoomEntry = new RoomEntryWatcher(Router, Classifier, Log);
            Look = new PlayerLookManager(Router, RoomEntry, Party, () => SelfName);
            Look.SetWireSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes)));
        }

        public void AddPlayer(string givenName, string familyName = "")
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: givenName,
                FamilyName: familyName,
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
            Look.Dispose();
            RoomEntry.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- Look-back -----

    [Fact]
    public void LookedAt_WhenEnabled_LooksBack()
    {
        using Harness h = new();
        h.Look.LookBackWhenLookedAt = true;

        h.Feed("Bob is looking at you.");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void LookedAt_Disabled_DoesNothing()
    {
        using Harness h = new();
        // LookBackWhenLookedAt defaults false.

        h.Feed("Bob is looking at you.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LookBack_EachSighting_Fires_NoDedup()
    {
        using Harness h = new();
        h.Look.LookBackWhenLookedAt = true;

        h.Feed("Bob is looking at you.");
        h.Feed("Bob is looking at you.");

        Assert.Equal(new[] { "look Bob\r", "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void LookBack_SkipsSelf()
    {
        using Harness h = new();
        h.SelfName = "Bob Ironside";

        h.Look.TryLookBack("Bob");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LookBack_IncludesPartyMembers()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Bob Ironside" });

        h.Look.TryLookBack("Bob");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }

    // ----- Arrival -----

    [Fact]
    public void PlayerArrival_WhenEnabled_Looks()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.Look.LookAtPlayersOnArrival = true;

        h.Feed("Bob walks into the room from the north.");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void PlayerArrival_Disabled_DoesNothing()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        // LookAtPlayersOnArrival defaults false.

        h.Feed("Bob walks into the room from the north.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MonsterArrival_IsIgnored()
    {
        using Harness h = new();
        h.Look.LookAtPlayersOnArrival = true;

        // Unknown-to-data arrival with no colour hint classifies as Monster,
        // so the look-on-arrival gate must not fire.
        h.Feed("A fierce lashworm crawls into the room from the north.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Arrival_SkipsSelf()
    {
        using Harness h = new();
        h.SelfName = "Bob Ironside";

        h.Look.TryLookAtArrival("Bob");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Arrival_SkipsPartyMember()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Bob Ironside" });

        h.Look.TryLookAtArrival("Bob");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Arrival_LooksByGivenName()
    {
        using Harness h = new();

        h.Look.TryLookAtArrival("Bob Ironside");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }
}
