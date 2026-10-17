using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// KillHandler — @kill <target> retargets our combat
// engine to a named monster. Pins the retarget call (single + multi-word
// names), silent-on-success, the WarnOnDenial-gated missing-target failure,
// and ExecuteCommands permission gating.
public sealed class KillHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        // The router the engine's chat lines arrive through.
        public MessageRouter Router { get; }
        // What Engine put on the wire, through the sender the client binds.
        public List<byte[]> Sent { get; } = new();
        public KillHandler Handler { get; }
        public PlayerDatabase Players { get; }
        public List<string> Retargets { get; } = new();

        // What the order's taker answers; null (the order was taken) by default.
        public string? Refusal { get; set; }

        public Setup()
        {
            MessageRouter router = new();
            Router = router;
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase(new ProfileService(), () => null);
            Engine = new RemoteCommandManager(chat, party, Players);
            Engine.SetWireSender(Sent.Add);
            Handler = new KillHandler(Engine, name => { Retargets.Add(name); return Refusal; });
        }

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    [Fact]
    public void Kill_WithTarget_Retargets()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill kobold"));

        Assert.Equal(new[] { "kobold" }, s.Retargets);
    }

    [Fact]
    public void Kill_MultiWordName_JoinsTokens()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill giant rat"));

        Assert.Equal(new[] { "giant rat" }, s.Retargets);
    }

    [Fact]
    public void Kill_Success_IsSilent()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill kobold"));

        Assert.Empty(s.Sent);
    }

    [Fact]
    public void Kill_NoTarget_RepliesFailureWhenWarnOn()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);
        s.Engine.WarnOnDenial = true;

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill"));

        Assert.Empty(s.Retargets);
        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains(s.Engine.FailureMessage, reply);
    }

    [Fact]
    public void Kill_NoTarget_SilentWhenWarnOff()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);
        s.Engine.WarnOnDenial = false;

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill"));

        Assert.Empty(s.Retargets);
        Assert.Empty(s.Sent);
    }

    // An order that wasn't taken (a player we won't fight) is refused with the
    // taker's reason, under the Warn setting.
    [Fact]
    public void Kill_NotTaken_RefusesWithTheReason()
    {
        using Setup s = new() { Refusal = "not attacking Bob: PvP is off" };
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill Bob"));

        Assert.Equal("/Tank {not attacking Bob: PvP is off}\r",
            Encoding.Latin1.GetString(Assert.Single(s.Sent)));
    }

    [Fact]
    public void Kill_NotTaken_IsSilentWhenWarnOff()
    {
        using Setup s = new() { Refusal = "not attacking Bob: PvP is off" };
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);
        s.Engine.WarnOnDenial = false;

        ChatLines.Hear(s.Router, Telepath("Tank", "@kill Bob"));

        Assert.Empty(s.Sent);
    }

    [Fact]
    public void Kill_WithoutExecutePermission_DoesNotRetarget()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Rando", PlayerRemoteControls.QueryHealthStatus);

        ChatLines.Hear(s.Router, Telepath("Rando", "@kill kobold"));

        Assert.Empty(s.Retargets);
    }
}
