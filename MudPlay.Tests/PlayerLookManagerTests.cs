using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PlayerLookManager — reactive `look <player>` automation.
// Covers the end-to-end look-back pattern (validates the PlayerLooksAtYou
// regex + subscription), the arrival hook's Player/Monster gating, and the
// per-behaviour decision seams (enable gate, self/party skip, given-name
// targeting).
public sealed class PlayerLookManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher RoomEntry { get; }
        public PartyState Party { get; } = new();
        public PlayerLookManager Look { get; }
        // The clock the Look reads: the real one unless a test sets its own.
        public Func<DateTime> LookNow { get; set; } = () => DateTime.UtcNow;
        public List<string> Sent { get; } = new();

        public string? SelfName { get; set; }

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            RoomEntry = new RoomEntryWatcher(Router, Classifier, Log);
            Look = new PlayerLookManager(Router, RoomEntry, Players, Party, () => SelfName, now: () => LookNow());
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

    // THIS ASSERTION IS INVERTED FROM WHAT IT WAS, on purpose. It used to
    // require a look back on EVERY sighting -- "no dedup, a look-at is a
    // deliberate social poke and mirroring it each time is the point". True of
    // a person, false of two clients: with the box ticked on both sides the
    // mirror never terminates, which is what was reported. The look-back now
    // shares the once-per-local-day rule with the arrival path.
    [Fact]
    public void LookBack_EachSighting_LooksOncePerDay()
    {
        using Harness h = new();
        h.Look.LookBackWhenLookedAt = true;

        h.Feed("Bob is looking at you.");
        h.Feed("Bob is looking at you.");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
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

    // ----- Once per local day -----

    [Fact]
    public void LookBack_TwiceInOneDay_LooksOnce()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.Look.LookBackWhenLookedAt = true;

        h.Look.TryLookBack("Bob");
        h.Look.TryLookBack("Bob");
        h.Look.TryLookBack("Bob");

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void LookBack_NextDay_LooksAgain()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.Look.LookBackWhenLookedAt = true;
        DateTime day1 = new(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
        h.LookNow = () => day1;
        h.Look.TryLookBack("Bob");

        h.LookNow = () => day1.AddDays(1);
        h.Look.TryLookBack("Bob");

        Assert.Equal(new[] { "look Bob\r", "look Bob\r" }, h.Sent);
    }

    // THE LOOP THIS EXISTS TO STOP. Two clients with the look-back box ticked
    // mirror each other forever: A looks at B, the server tells B, B looks
    // back, the server tells A, A looks back... The arrival toggle only lights
    // the fuse, so the throttle has to cover BOTH paths or the loop survives on
    // the look-back alone. Reported from a live pair looking at each other
    // non-stop.
    [Fact]
    public void ArrivalThenLookBack_SamePlayerSameDay_LooksOnce()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.Look.LookAtPlayersOnArrival = true;
        h.Look.LookBackWhenLookedAt = true;

        h.Feed("Bob walks into the room from the north.");   // we look first
        h.Look.TryLookBack("Bob");                           // Bob looks back
        h.Look.TryLookBack("Bob");                           // ...and again
        h.Feed("Bob walks into the room from the north.");   // and re-enters

        Assert.Equal(new[] { "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void LookBack_DifferentPlayers_EachGetOne()
    {
        using Harness h = new();
        h.AddPlayer("Bob");
        h.AddPlayer("Carol");
        h.Look.LookBackWhenLookedAt = true;

        h.Look.TryLookBack("Bob");
        h.Look.TryLookBack("Carol");
        h.Look.TryLookBack("Bob");

        Assert.Equal(new[] { "look Bob\r", "look Carol\r" }, h.Sent);
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
