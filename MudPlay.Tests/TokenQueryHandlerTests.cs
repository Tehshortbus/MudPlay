using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Game.Tokens;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// What @token answers and what it refuses: a bare @token with no charges read
// is a result (as "no limited-use items carried" is for @uses), a named token
// whose charges aren't known is the command failing.
public sealed class TokenQueryHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    // Each engine's own wire, captured through the sender the client binds.
    private readonly Dictionary<RemoteCommandManager, List<byte[]>> _wire = new();

    private RemoteCommandManager Engine(bool warn)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Bob", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryInventory));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players) { WarnOnDenial = warn };
        _routers[engine] = router;
        engine.SetWireSender((_wire[engine] = new()).Add);
        TokenTracker tokens = new(
            send: _ => { }, carried: () => Array.Empty<string>(), onParadigm: () => true,
            matchSelfUse: _ => null, matchMemberDeparted: _ => null, schedule: (_, _) => { });
        _ = new TokenQueryHandler(engine, tokens);
        return engine;
    }

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, "Bob", msg);

    private List<string> Sent(RemoteCommandManager engine) =>
        _wire[engine].Select(b => Encoding.Latin1.GetString(b)).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bare_WithNoneRead_IsAnAnswer_WhateverTheWarnSetting(bool warn)
    {
        RemoteCommandManager engine = Engine(warn);

        ChatLines.Hear(_routers[engine], Telepath("@token"));

        Assert.Equal("/Bob {no token charges read yet (hold transport tokens and log in on Paradigm)}\r",
            Assert.Single(Sent(engine)));
    }

    [Fact]
    public void ANamedToken_WithNoCount_IsRefused_AndSilentWithDenialsSilenced()
    {
        RemoteCommandManager loud = Engine(warn: true);
        ChatLines.Hear(_routers[loud], Telepath("@token arlysia"));
        Assert.StartsWith("/Bob {no charge count for a token of ", Assert.Single(Sent(loud)));

        RemoteCommandManager quiet = Engine(warn: false);
        ChatLines.Hear(_routers[quiet], Telepath("@token arlysia"));
        Assert.Empty(Sent(quiet));
    }
}
