using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the read-only <see cref="ExperienceQueryHandler"/> replies
/// (<c>@exp</c> / <c>@level</c>): progression numbers come from the
/// <see cref="PlayerStats"/> snapshot, the session rate + earned total
/// from a clock-driven <see cref="SessionActivityTracker"/>. Both gate on
/// "not parsed yet" and reply on the sender's channel — never touch the wire.
/// </summary>
public sealed class ExperienceQueryHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTimeOffset NowUtc = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double minutes) => NowUtc += TimeSpan.FromMinutes(minutes);
    }

    private static (RemoteCommandManager engine, PlayerStats stats, SessionActivityTracker activity, Clock clock, PlayerDatabase players)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        PlayerStats stats = new();
        Clock clock = new();
        SessionActivityTracker activity = new(() => clock.NowUtc);
        _ = new ExperienceQueryHandler(engine, stats, activity);
        return (engine, stats, activity, clock, players);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
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

        engine.DispatchForTests(Telepath("Bob", "@level"));

        Assert.Contains("parse a stat screen", string.Join(" ", Replies(engine)));
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

        engine.DispatchForTests(Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies(engine));
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

        engine.DispatchForTests(Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("Level 12, 1,500,000 exp, exp-to-next unknown (type exp)", reply);
    }

    // ----- @exp --------------------------------------------------------

    [Fact]
    public void Exp_BeforeAnyGain_ReportsRateUnknown()
    {
        var (engine, _, _, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        clock.Advance(30); // time online, but no exp booked

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("0 exp this session, rate unknown", reply);
    }

    [Fact]
    public void Exp_WithRateAndSpan_ReportsEtaToLevel()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(16_200);
        clock.Advance(30); // 16,200 / 0.5h = 32,400/hr
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 32_400; // exactly one hour of exp remaining

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("16,200 exp this session, 32,400/hr, ~1h 0m to level", reply);
    }

    [Fact]
    public void Exp_RateKnownButNoSpan_PointsAtExp()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        // LevelExpSpan stays 0 — rate is known, ETA is not.

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("6,000 exp this session, 12,000/hr, type exp for ETA", reply);
    }

    [Fact]
    public void Exp_ReadyToLevel_WhenRemainingIsZero()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 0; // server clamps to 0 once past the threshold

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        Assert.Contains("ready to level", Assert.Single(Replies(engine)));
    }

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Exp_FromUnauthorisedSender_IsDenied()
    {
        var (engine, _, _, clock, players) = Setup();
        engine.WarnOnDenial = false; // silence the generic denial reply
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryExperience);
        clock.Advance(30);

        engine.DispatchForTests(Telepath("Stranger", "@exp"));

        Assert.Empty(Replies(engine));
    }
}
