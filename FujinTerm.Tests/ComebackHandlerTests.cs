using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Recovery;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Cluster 5d — <see cref="ComebackHandler"/> forwards an inbound
/// <c>@comeback</c> to <see cref="DeathRecoveryManager.RequestComeback"/>.
/// Catalog permission tier is <c>None</c>; any active party member
/// can request.
/// </summary>
public sealed class ComebackHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    [Fact]
    public void Comeback_FromPartyMember_FiresEvent()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        party.Members.Add(new PartyMember { Name = "Tank" });
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        DeathLineWatcher watcher = new(router);
        ProfileService profile = new();
        DeathRecoveryManager recovery = new(watcher, profile);
        ComebackHandler handler = new(engine, recovery);

        List<string?> requests = new();
        recovery.ComebackRequested += r => requests.Add(r);

        engine.DispatchForTests(Telepath("Tank", "@comeback"));

        Assert.Single(requests);
        Assert.Equal("Tank", requests[0]);

        handler.Dispose();
        recovery.Dispose();
        watcher.Dispose();
    }
}
