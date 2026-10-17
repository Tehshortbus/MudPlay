using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only @death handler: reports deaths in the recovery log that
// aren't marked fully recovered (Status != Recovered) — the most recent one for
// a bare @death, or all of them for @death all — gated on the QueryDeaths grant.
public sealed class DeathQueryHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly System.DateTime Now = new(2026, 6, 20, 0, 0, 0, System.DateTimeKind.Utc);

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private (RemoteCommandManager engine, PlayerDatabase players) Setup(
        IReadOnlyList<DeathRecord> records, string? activeGameData = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        _ = new DeathQueryHandler(engine, () => records,
            r => r.GameData is { } set && !string.Equals(set, activeGameData, System.StringComparison.OrdinalIgnoreCase));
        return (engine, players);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private string LastReply() =>
        Encoding.Latin1.GetString(_replies[^1]);

    private IReadOnlyList<string> Replies() =>
        _replies.Select(b => Encoding.Latin1.GetString(b)).ToList();

    private static DeathRecord Death(int number, int daysAgo, DeathRecoveryStatus status,
        string? roomName = "Crypt", int lives = 5) => new()
    {
        RecordNumber = number,
        At = new System.DateTimeOffset(Now.AddDays(-daysAgo)),
        Room = new RoomRef(1, 100 + number),
        RoomName = roomName,
        LivesRemaining = lives,
        Status = status,
    };

    [Fact]
    public void Death_ReportsMostRecentUnrecovered()
    {
        // Two unrecovered deaths (Active newer than Partial) and one Recovered —
        // the bare @death names only the most recent unrecovered one.
        var records = new List<DeathRecord>
        {
            Death(1, 3, DeathRecoveryStatus.Recovered),   // fully recovered — skipped
            Death(2, 2, DeathRecoveryStatus.Partial),
            Death(3, 1, DeathRecoveryStatus.Active),      // most recent unrecovered
        };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@death"));

        string reply = LastReply();
        Assert.Contains("death #3", reply);
        Assert.Contains("active", reply);
        Assert.DoesNotContain("death #2", reply);
        Assert.DoesNotContain("death #1", reply);
    }

    [Fact]
    public void DeathAll_ReportsEveryUnrecovered_MostRecentFirst()
    {
        var records = new List<DeathRecord>
        {
            Death(1, 3, DeathRecoveryStatus.Missing),
            Death(2, 2, DeathRecoveryStatus.Recovered),   // skipped
            Death(3, 1, DeathRecoveryStatus.Partial),
        };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@death all"));

        var replies = Replies();
        Assert.Contains(replies, r => r.Contains("death #3"));
        Assert.Contains(replies, r => r.Contains("death #1"));
        Assert.DoesNotContain(replies, r => r.Contains("death #2"));  // the Recovered one
    }

    [Fact]
    public void Death_NoUnrecovered_ReportsNone()
    {
        var records = new List<DeathRecord> { Death(1, 1, DeathRecoveryStatus.Recovered) };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@death"));

        Assert.Contains("no unrecovered deaths", LastReply());
    }

    // The asker reads the room number on the map: a death of another game data than
    // the active one says which, since that number is another room here.
    [Fact]
    public void Death_OfAnotherGameData_NamesIt()
    {
        DeathRecord other = Death(2, 1, DeathRecoveryStatus.Active);
        other.GameData = "alpha";
        DeathRecord own = Death(1, 2, DeathRecoveryStatus.Active);
        own.GameData = "beta";
        var (engine, players) = Setup(new List<DeathRecord> { own, other }, activeGameData: "beta");
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@death all"));

        var replies = Replies();
        Assert.Contains(replies, r => r.Contains("death #2") && r.Contains("(1/102), recorded on game data 'alpha', 5 lives left"));
        Assert.Contains(replies, r => r.Contains("death #1") && r.Contains("(1/101), 5 lives left"));
    }

    [Fact]
    public void Death_WithoutGrant_ReportsNoDeath()
    {
        // No QueryDeaths grant → the engine gates it before the handler, so the
        // death log is never reported (any reply is the denial, not a death line).
        var records = new List<DeathRecord> { Death(1, 1, DeathRecoveryStatus.Active) };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@death"));

        Assert.DoesNotContain(Replies(), r => r.Contains("death #"));
    }
}
