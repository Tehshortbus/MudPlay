using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Sneaking or hidden, nothing the engine sends for a say-channel command is said
// aloud: a reply and a `&@` relay both go by telepath, since either would end the
// sneak.
public sealed class RemoteCommandStealthChannelTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Each engine's own wire, captured through the sender the client binds.
    private readonly Dictionary<RemoteCommandManager, List<byte[]>> _wire = new();

    private RemoteCommandManager Engine(bool stealthed)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Leader", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Leader", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players)
        {
            StealthedProvider = () => stealthed,
        };
        _routers[engine] = router;
        engine.SetWireSender((_wire[engine] = new()).Add);
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, ctx => ctx.Reply("on my way"));
        return engine;
    }

    private static ChatLine Say(string msg) =>
        new(ChatChannel.Local, "Leader", msg);

    private string Only(RemoteCommandManager engine) =>
        Encoding.Latin1.GetString(Assert.Single(_wire[engine]));

    [Fact]
    public void RelayOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);

        ChatLines.Hear(_routers[engine], Say("&@invite"));

        Assert.Equal("/Leader @invite\r", Only(engine));
    }

    [Fact]
    public void RelayOnSay_InTheOpen_IsADirectedSay()
    {
        RemoteCommandManager engine = Engine(stealthed: false);

        ChatLines.Hear(_routers[engine], Say("&@invite"));

        Assert.Equal(">Leader @invite\r", Only(engine));
    }

    [Fact]
    public void ReplyOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);

        ChatLines.Hear(_routers[engine], Say("@invite"));

        Assert.Equal("/Leader {on my way}\r", Only(engine));
    }

    [Fact]
    public void RefusalOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);
        engine.RegisterHandler("@probe", PlayerRemoteControls.QueryVersion, ctx => ctx.Refuse("no"));

        ChatLines.Hear(_routers[engine], Say("@probe"));

        Assert.Equal("/Leader {no}\r", Only(engine));
    }
}
