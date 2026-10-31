using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the ExperienceQueryHandler replies (@exp / @level): progression numbers
// come from the game's `exp` line or the server's vitals, the session rate + earned
// total from a clock-driven SessionActivityTracker. Both reply on the sender's
// channel; an @exp with no exp line read in the last hour sends `exp` first.
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

    // The game's output, which the stat parser reads.
    private readonly ServerFeed _feed = new();

    // What the handler sent to the game, and the timer its wait for the exp line arms.
    private readonly List<string> _sent = new();
    private readonly ManualTimers _timers = new();

    private StatParser _parser = null!;   // set by Setup

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
        _parser = new StatParser(stats, now: () => clock.NowUtc.UtcDateTime);
        _parser.AttachLineExtractor(_feed.Lines);
        SessionActivityTracker activity = new(() => clock.NowUtc);
        // Isolated empty game-data root. The exp chart still resolves to the default
        // (CalcExpChart(0,0) > 0), so with exp unset the banked-levels ratio reads
        // "+0.00 lvls" — enough to pin the level-tag format here; the ratio VALUE is
        // covered by the TimeToLevelEstimator tests.
        GameDataCache gameData = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mudplay-expq-{Guid.NewGuid():N}"));
        _ = new ExperienceQueryHandler(engine, stats, activity, gameData, _parser,
            send: Send, armTimer: _timers.Schedule, now: () => clock.NowUtc.UtcDateTime);
        return (engine, stats, activity, clock, players);
    }

    // A command on the wire, which the stat parser sees go out as the client's do.
    private void Send(string command)
    {
        _sent.Add(command);
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
    }

    // The game's answer to `exp`, after the client sent it.
    private void ExpLine(long exp, int level, long toNext, long span)
    {
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("exp\r"));
        AnswerExp(exp, level, toNext, span);
    }

    private void AnswerExp(long exp, int level, long toNext, long span) =>
        _feed.Line($"Exp: {exp} Level: {level} Exp needed for next level: {toNext} ({span}) [50%]");

    // The server's vitals while they own the exp.
    private void ServerVitals(int level, long exp, long expNeeded)
    {
        _parser.GmcpOwnsVitals = () => true;
        using System.Text.Json.JsonDocument vitals = System.Text.Json.JsonDocument.Parse(
            $"{{\"hp\":10,\"maxhp\":10,\"mana\":0,\"maxmana\":0,\"manaType\":\"mana\",\"level\":{level},\"exp\":{exp},\"expNeeded\":{expNeeded}}}");
        _parser.ApplyGmcpVitals(vitals.RootElement);
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
        ExpLine(exp: 1_500_000, level: 12, toNext: 120_000, span: 500_000);

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
        // No exp line read yet: `exp` goes out, and with no answer the reply waits it out.
        Assert.Equal(new[] { "exp" }, _sent);
        Assert.Empty(Replies());
        _timers.Advance(ExperienceQueryHandler.ExpReadWait);

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
        // Working toward L13, one hour of exp remaining → "60m" (under the 90m h/m cutover).
        ExpLine(exp: 0, level: 12, toNext: 32_400, span: 500_000);

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
        // No exp line comes back — rate is known, ETA is not.

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        _timers.Advance(ExperienceQueryHandler.ExpReadWait);

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
        // Working toward L13; the server clamps to 0 once past the threshold.
        ExpLine(exp: 0, level: 12, toNext: 0, span: 500_000);

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
        // Working toward L72; 60m / 30m per hr = 2h.
        ExpLine(exp: 1_000_000_000, level: 71, toNext: 60_000_000, span: 500_000_000);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));

        Assert.Equal("Made: 30,000,000  Needed: 60,000,000 (L72, +0.00 lvls)  Rate: 30 m/hr  Will level in: 2h 0m",
            Assert.Single(Replies()));
    }

    // ----- @exp refreshing the exp line --------------------------------

    // With no exp line read, @exp sends `exp` and answers once the game prints it.
    [Fact]
    public void Exp_WithNoExpLineRead_SendsExp_AndAnswersOnceItIsRead()
    {
        var (engine, _, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(16_200);
        clock.Advance(30);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        Assert.Equal(new[] { "exp" }, _sent);
        Assert.Empty(Replies());

        AnswerExp(exp: 0, level: 12, toNext: 32_400, span: 500_000);

        Assert.Equal("Made: 16,200  Needed: 32,400 (L13, +0.00 lvls)  Rate: 32,400/hr  Will level in: 60m",
            Assert.Single(Replies()));
        // The wait that would have answered is spent: no second reply.
        _timers.Advance(ExperienceQueryHandler.ExpReadWait);
        Assert.Single(Replies());
    }

    // Read within the hour, the exp line stands; past it, @exp reads it again.
    [Fact]
    public void Exp_ReadsTheExpLineAgain_OnlyOnceItIsOverAnHourOld()
    {
        var (engine, _, _, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        ExpLine(exp: 0, level: 12, toNext: 32_400, span: 500_000);

        clock.Advance(60);
        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        Assert.Empty(_sent);
        Assert.Single(Replies());

        clock.Advance(1);
        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        Assert.Equal(new[] { "exp" }, _sent);
        Assert.Single(Replies());
        AnswerExp(exp: 1_010_000, level: 12, toNext: 22_400, span: 500_000);
        Assert.Contains("Needed: 22,400", Replies()[1]);
    }

    // Every @exp heard while `exp` is out is answered by the one read.
    [Fact]
    public void Exp_AskedTwiceWhileExpIsOut_SendsItOnce_AndAnswersBoth()
    {
        var (engine, _, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        SeedPlayer(players, "Ann", PlayerRemoteControls.QueryExperience);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        ChatLines.Hear(_routers[engine], Telepath("Ann", "@exp"));
        AnswerExp(exp: 0, level: 12, toNext: 32_400, span: 500_000);

        Assert.Equal(new[] { "exp" }, _sent);
        Assert.Equal(2, Replies().Count(r => r.Contains("Needed: 32,400")));
    }

    // The server's vitals carry the exp and the next level's total, current at every
    // update: @exp and @level answer from them with no exp line, and send nothing
    // (report paradigm-20261030-221217: they said "type exp" however often exp was typed).
    [Fact]
    public void WithTheServersVitals_ExpAndLevel_AnswerFromThem_AndSendNothing()
    {
        var (engine, _, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(16_200);
        clock.Advance(30);
        ServerVitals(level: 12, exp: 0, expNeeded: 32_400);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@exp"));
        ChatLines.Hear(_routers[engine], Telepath("Bob", "@level"));

        Assert.Empty(_sent);
        Assert.Equal(new[]
        {
            "Made: 16,200  Needed: 32,400 (L13, +0.00 lvls)  Rate: 32,400/hr  Will level in: 60m",
            "Level 12, 0 exp, 32,400 to next level",
        }, Replies());
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
