using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the @relog handler's wire behaviour: only fires for a sender
// with the HangupDisconnect permission, sends the configured
// GameCommands.ExitCommand verbatim + CR (graceful main-menu logoff),
// and arms RelogSignal so MainWindowVM forces a reconnect-and-login.
// Sibling of HangupHandler — same wire shape, opposite reconnect
// intent (relog does NOT suppress the entry automation).
public sealed class RelogHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, RelogHandler handler, PlayerDatabase players, List<byte[]> wire, GameCommands commands, RelogSignal signal) Setup(bool bindWire = true)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        GameCommands commands = new();   // defaults: "E" / "=x"
        RelogSignal signal = new();
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        RelogHandler handler = new(engine, commands, signal);
        List<byte[]> wire = new();
        if (bindWire) handler.SetWireSender(wire.Add);
        return (engine, handler, players, wire, commands, signal);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    [Fact]
    public void Relog_FromAuthorisedSender_SendsConfiguredExitCommand()
    {
        var (engine, _, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("=x\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Relog_RespectsCustomExitCommand()
    {
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = "bye";

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));

        Assert.Equal("bye\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void Relog_FromUnauthorisedSender_DoesNothing()
    {
        // Sender has every permission EXCEPT HangupDisconnect — engine
        // denies, handler never fires.
        var (engine, _, players, wire, _, _) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@relog"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Relog_FromUnknownSender_DoesNothing()
    {
        // No customization at all → default deny.
        var (engine, _, _, wire, _, _) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Drive-by", "@relog"));
        Assert.Empty(wire);
    }

    [Fact]
    public void Relog_BlankExitCommand_SendsNothing()
    {
        // Defensive — a misconfigured-to-blank exit command shouldn't
        // produce a lone CR on the wire.
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = string.Empty;

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));

        Assert.Empty(wire);
    }

    // The hang-up penalty line is asked for once per @relog actually sent, and
    // not for one with no exit command to send.
    [Fact]
    public void Relog_AsksForThePenaltyLine_OnlyWhenTheExitWentOut()
    {
        var (engine, handler, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        int asked = 0;
        handler.SetHangupPenaltyLog(() => { Assert.Single(wire); asked++; });

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));
        Assert.Equal(1, asked);

        commands.ExitCommand = "";
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));
        Assert.Equal(1, asked);
        Assert.Single(wire);
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        handler.Dispose();
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));

        Assert.Empty(wire);
    }

    // ===== Relog-intent signalling ======================================

    [Fact]
    public void Relog_RaisesRelogSignal()
    {
        // MainWindowVM consumes the relog-intent flag in its Disconnected
        // handler to force the unconditional dial-back. Must be set
        // before the wire send so a fast server-side carrier drop can't
        // beat the classification.
        var (engine, _, players, _, _, signal) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));

        Assert.True(signal.ConsumeRelogIntent());
    }

    [Fact]
    public void Relog_DeniedByPermission_DoesNotRaiseSignal()
    {
        // Engine denies before the handler runs — no wire, no signal.
        // A stranger spamming @relog must not be able to force a
        // reconnect cycle.
        var (engine, _, _, _, _, signal) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@relog"));

        Assert.False(signal.ConsumeRelogIntent());
    }

    [Fact]
    public void Relog_SignalRaisedEvenWithoutWireSender()
    {
        // Before the wire-sender binds, the intent is still recorded so a
        // racing Disconnected event classifies the drop correctly.
        var (engine, _, players, wire, _, signal) = Setup(bindWire: false);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@relog"));
        Assert.Empty(wire);
        Assert.True(signal.ConsumeRelogIntent());
    }
}
