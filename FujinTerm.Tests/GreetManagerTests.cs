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
/// <see cref="GreetManager"/> — once-per-local-day greet+look on first
/// sighting of a non-party player. Covers the end-to-end classifier hook
/// plus the <see cref="GreetManager.TryGreet"/> decision seam (enable gate,
/// day-boundary dedup, self/party skip).
/// </summary>
public sealed class GreetManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public PartyState Party { get; } = new();
        public GreetManager Greet { get; }
        public List<string> Sent { get; } = new();

        public string? SelfName { get; set; }
        public DateTime Now { get; set; } = new(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Greet = new GreetManager(Classifier, Players, Party, () => SelfName)
            {
                Enabled = true,
                NowUtcProvider = () => Now,
            };
            Greet.SetWireSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes)));
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
            Greet.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void AlsoHerePlayer_FirstSighting_SendsGreetThenLook()
    {
        using Harness h = new();
        h.AddPlayer("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal(new[] { "greet Bob\r", "look Bob\r" }, h.Sent);
        Assert.Equal(h.Now, h.Players.GetLastGreetedUtc("Bob"));
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        using Harness h = new();
        h.Greet.Enabled = false;
        h.AddPlayer("Bob");

        h.Feed("Also here: Bob.");

        Assert.Empty(h.Sent);
        Assert.Null(h.Players.GetLastGreetedUtc("Bob"));
    }

    [Fact]
    public void SecondSighting_SameLocalDay_IsSilent()
    {
        using Harness h = new();

        h.Greet.TryGreet("Bob");
        Assert.Equal(2, h.Sent.Count);

        // Later the same local day — already greeted, no second pair.
        h.Now = h.Now.AddHours(3);
        h.Greet.TryGreet("Bob");

        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void NextLocalDay_ReGreets()
    {
        using Harness h = new();

        h.Greet.TryGreet("Bob");
        Assert.Equal(2, h.Sent.Count);

        // Roll past local midnight — a fresh day re-opens the greet.
        DateTime tomorrowLocalNoon = h.Now.ToLocalTime().Date.AddDays(1).AddHours(12);
        h.Now = tomorrowLocalNoon.ToUniversalTime();
        h.Greet.TryGreet("Bob");

        Assert.Equal(4, h.Sent.Count);
        Assert.Equal(new[] { "greet Bob\r", "look Bob\r", "greet Bob\r", "look Bob\r" }, h.Sent);
    }

    [Fact]
    public void Self_IsSkipped()
    {
        using Harness h = new();
        h.SelfName = "Forged Paradigm";

        h.Greet.TryGreet("Forged");

        Assert.Empty(h.Sent);
        Assert.Null(h.Players.GetLastGreetedUtc("Forged"));
    }

    [Fact]
    public void PartyMember_IsSkipped()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Bob Ironside" });

        h.Greet.TryGreet("Bob");

        Assert.Empty(h.Sent);
        Assert.Null(h.Players.GetLastGreetedUtc("Bob"));
    }

    [Fact]
    public void GreetsByGivenName_WhenSightedWithFamily()
    {
        using Harness h = new();

        h.Greet.TryGreet("Bob Ironside");

        Assert.Equal(new[] { "greet Bob\r", "look Bob\r" }, h.Sent);
    }
}
