using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the @hangup handler's wire behaviour: only fires for a sender
// with the HangupDisconnect permission, sends the configured
// GameCommands.ExitCommand verbatim + CR. The default exit command
// is "=x" (MajorMUD main-menu logoff). Full cleanup-warning + first-
// session-login automation flows ship separately.
public sealed class HangupHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, HangupHandler handler, PlayerDatabase players, List<byte[]> wire, GameCommands commands, HangupSignal signal) Setup(bool bindWire = true)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        GameCommands commands = new();   // defaults: "E" / "=x"
        HangupSignal signal = new();
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        HangupHandler handler = new(engine, commands, signal);
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
    public void Hangup_FromAuthorisedSender_SendsConfiguredExitCommand()
    {
        var (engine, _, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("=x\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Hangup_RespectsCustomExitCommand()
    {
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = "bye";

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));

        Assert.Equal("bye\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void Hangup_FromUnauthorisedSender_DoesNothing()
    {
        // Sender has every permission EXCEPT HangupDisconnect — engine
        // denies, handler never fires.
        var (engine, _, players, wire, _, _) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@hangup"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Hangup_FromUnknownSender_DoesNothing()
    {
        // No customization at all → default deny.
        var (engine, _, _, wire, _, _) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Drive-by", "@hangup"));
        Assert.Empty(wire);
    }

    [Fact]
    public void Hangup_BlankExitCommand_SendsNothing()
    {
        // Defensive — a misconfigured-to-blank exit command shouldn't
        // produce a lone CR on the wire.
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = string.Empty;

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
    }

    // The hang-up penalty line is asked for once per @hangup actually sent, and
    // not for one with no exit command to send.
    [Fact]
    public void Hangup_AsksForThePenaltyLine_OnlyWhenTheExitWentOut()
    {
        var (engine, handler, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        int asked = 0;
        handler.SetHangupPenaltyLog(() => { Assert.Single(wire); asked++; });

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));
        Assert.Equal(1, asked);

        commands.ExitCommand = "";
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));
        Assert.Equal(1, asked);
        Assert.Single(wire);
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        handler.Dispose();
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
    }

    // ===== Hangup-intent signalling =====================================

    [Fact]
    public void Hangup_RaisesHangupSignalBothFlags()
    {
        // MainWindowVM consumes the disconnect-intent flag in its
        // Disconnected handler; MainMenuEntryAutomation consumes the
        // suppress-entry flag on the next Arm(). Both must be set
        // before the wire send so a fast server-side carrier drop
        // can't beat the classification.
        var (engine, _, players, _, _, signal) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));

        bool disc = signal.ConsumeDisconnectIntent();

        bool supp = signal.ConsumeSuppressEntry();
        Assert.True(disc);
        Assert.True(supp);
    }

    [Fact]
    public void Hangup_DeniedByPermission_DoesNotRaiseSignal()
    {
        // Engine denies before the handler runs — no wire, no signal.
        // Critical: a stranger spamming @hangup must not be able to
        // poison the suppress-entry flag for the next legitimate
        // login.
        var (engine, _, _, _, _, signal) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@hangup"));

        bool disc = signal.ConsumeDisconnectIntent();

        bool supp = signal.ConsumeSuppressEntry();
        Assert.False(disc);
        Assert.False(supp);
    }

    [Fact]
    public void Hangup_SignalRaisedEvenWithoutWireSender()
    {
        // Before the wire-sender binds, the intent is still recorded so a
        // racing Disconnected event classifies the drop correctly.
        var (engine, _, players, wire, _, signal) = Setup(bindWire: false);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@hangup"));
        bool disc = signal.ConsumeDisconnectIntent();
        bool supp = signal.ConsumeSuppressEntry();
        Assert.Empty(wire);
        Assert.True(disc);
        Assert.True(supp);
    }
}
