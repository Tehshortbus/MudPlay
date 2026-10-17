using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only ExperienceQueryHandler replies
// (@exp / @level): progression numbers come from the
// PlayerStats snapshot, the session rate + earned total
// from a clock-driven SessionActivityTracker. Both gate on
// "not parsed yet" and reply on the sender's channel — never touch the wire.
public sealed class ExperienceQueryHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTimeOffset NowUtc = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double minutes) => NowUtc += TimeSpan.FromMinutes(minutes);
    }

    private (RemoteCommandManager engine, PlayerStats stats, SessionActivityTracker activity, Clock clock, PlayerDatabase players)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        PlayerStats stats = new();
        Clock clock = new();
        SessionActivityTracker activity = new(() => clock.NowUtc);
        // Isolated empty game-data root. The exp chart still resolves to the default
        // (CalcExpChart(0,0) > 0), so with exp unset the banked-levels ratio reads
        // "+0.00 lvls" — enough to pin the level-tag format here; the ratio VALUE is
        // covered by the TimeToLevelEstimator tests.
        GameDataCache gameData = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mudplay-expq-{Guid.NewGuid():N}"));
        _ = new ExperienceQueryHandler(engine, stats, activity, gameData);
        return (engine, stats, activity, clock, players);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private List<string> Replies() =>
        _replies
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // ----- @level ------------------------------------------------------

    [Fact]
    public void Level_BeforeStatScreen_PointsAtStat()
    {
        var (engine, _, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@level"));

        Assert.Contains("parse a stat screen", string.Join(" ", Replies()));
    }

    [Fact]
    public void Level_WithExpSpan_ReportsToNext()
    {
        var (engine, stats, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        stats.Level = 12;
        stats.Exp = 1_500_000;
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 120_000;

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies());
        Assert.Equal("Level 12, 1,500,000 exp, 120,000 to next level", reply);
    }

    [Fact]
    public void Level_WithoutExpSpan_FlagsExpToNextUnknown()
    {
        var (engine, stats, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        stats.Level = 12;
        stats.Exp = 1_500_000;
        // LevelExpSpan left 0 — the exp line hasn't been parsed yet.

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies());
        Assert.Equal("Level 12, 1,500,000 exp, exp-to-next unknown (type exp)", reply);
    }

    // ----- @exp --------------------------------------------------------

    [Fact]
    public void Exp_BeforeAnyGain_ReportsRateUnknown()
    {
        var (engine, _, _, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        clock.Advance(30); // time online, but no exp booked

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies());
        Assert.Equal("Made: 0  Rate: unknown (type exp for needed + time to level)", reply);
    }

    [Fact]
    public void Exp_WithRateAndSpan_ReportsEtaToLevel()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(16_200);
        clock.Advance(30); // 16,200 / 0.5h = 32,400/hr
        stats.Level = 12;            // working toward L13
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 32_400; // one hour of exp remaining → "60m" (under the 90m h/m cutover)

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies());
        Assert.Equal("Made: 16,200  Needed: 32,400 (L13, +0.00 lvls)  Rate: 32,400/hr  Will level in: 60m", reply);
    }

    [Fact]
    public void Exp_RateKnownButNoSpan_PointsAtExp()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        // LevelExpSpan stays 0 — rate is known, ETA is not.

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies());
        Assert.Equal("Made: 6,000  Rate: 12,000/hr (type exp for needed + time to level)", reply);
    }

    [Fact]
    public void Exp_ReadyToLevel_WhenRemainingIsZero()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        stats.Level = 12;            // working toward L13
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 0; // server clamps to 0 once past the threshold

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        Assert.Equal("Made: 6,000  Needed: 0 (L13, +0.00 lvls)  Rate: 12,000/hr  Will level in: ready to level",
            Assert.Single(Replies()));
    }

    // The full MegaMUD-style line: session Made + Needed (with the level being
    // worked toward) + a millions-tier rate + a multi-hour time to level.
    [Fact]
    public void Exp_FullSessionLine_MegaMudStyle()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(30_000_000);
        clock.Advance(60); // 30,000,000 / 1h = 30m/hr
        stats.Level = 71;                  // working toward L72
        stats.LevelExpSpan = 500_000_000;
        stats.ExpToNext = 60_000_000;      // 60m / 30m per hr = 2h

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        Assert.Equal("Made: 30,000,000  Needed: 60,000,000 (L72, +0.00 lvls)  Rate: 30 m/hr  Will level in: 2h 0m",
            Assert.Single(Replies()));
    }

    // ----- @exp rate abbreviation --------------------------------------

    [Theory]
    [InlineData(0, "0")]                    // guarded upstream (rate>0), but exact below 100k
    [InlineData(999, "999")]
    [InlineData(85_377, "85,377")]          // < 100k → exact, comma-grouped
    [InlineData(99_999, "99,999")]
    [InlineData(100_000, "100 k")]          // 100k–999k → whole thousands (floor), space before unit
    [InlineData(853_777, "853 k")]
    [InlineData(999_999, "999 k")]
    [InlineData(1_000_000, "1 m")]          // millions → one decimal, trailing .0 dropped
    [InlineData(1_100_000, "1.1 m")]
    [InlineData(1_193_744, "1.2 m")]
    [InlineData(10_100_000, "10.1 m")]
    [InlineData(30_000_000, "30 m")]
    public void FormatExpRate_TiersByMagnitude(double rate, string expected)
        => Assert.Equal(expected, ExperienceQueryHandler.FormatExpRate(rate));

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Exp_FromUnauthorisedSender_IsDenied()
    {
        var (engine, _, _, clock, players) = Setup();
        engine.WarnOnDenial = false; // silence the generic denial reply
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryExperience);
        clock.Advance(30);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@exp"));

        Assert.Empty(Replies());
    }
}
