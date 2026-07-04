using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the @hangup handler's wire behaviour: only fires for a sender
/// with the HangupDisconnect permission, sends the configured
/// GameCommands.ExitCommand verbatim + CR. The default exit command
/// is "=x" (MajorMUD main-menu logoff). Full cleanup-warning + first-
/// session-login automation flows ship separately.
/// </summary>
public sealed class HangupHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, HangupHandler handler, PlayerDatabase players, List<byte[]> wire, GameCommands commands, HangupSignal signal) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        GameCommands commands = new();   // defaults: "E" / "=x"
        HangupSignal signal = new();
        RemoteCommandManager engine = new(chat, party, players);
        HangupHandler handler = new(engine, commands, signal);
        List<byte[]> wire = new();
        handler.SetWireSender(wire.Add);
        return (engine, handler, players, wire, commands, signal);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

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

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("=x\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Hangup_RespectsCustomExitCommand()
    {
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = "bye";

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

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

        engine.DispatchForTests(Telepath("Stranger", "@hangup"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Hangup_FromUnknownSender_DoesNothing()
    {
        // No customization at all → default deny.
        var (engine, _, _, wire, _, _) = Setup();
        engine.DispatchForTests(Telepath("Drive-by", "@hangup"));
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

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        handler.Dispose();
        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

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

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        var (disc, supp) = signal.PeekForTests();
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
        engine.DispatchForTests(Telepath("Stranger", "@hangup"));

        var (disc, supp) = signal.PeekForTests();
        Assert.False(disc);
        Assert.False(supp);
    }

    [Fact]
    public void Hangup_SignalRaisedEvenWithoutWireSender()
    {
        // Pre-binding (or in tests where the wire-sender intentionally
        // isn't bound), the intent should still be recorded so a
        // racing Disconnected event still classifies correctly.
        var (engine, handler, players, _, _, signal) = Setup();
        handler.SetWireSender(_ => { });   // bind once...
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        engine.DispatchForTests(Telepath("Trusted", "@hangup"));
        var (disc, supp) = signal.PeekForTests();
        Assert.True(disc);
        Assert.True(supp);
    }

    // ===== Master "Disable hangups" kill-switch =========================

    [Fact]
    public void Hangup_WhenHangupsDisabled_SendsNothingAndRaisesNoSignal()
    {
        // Kill-switch on → an authorised @hangup is a silent no-op: no
        // wire write, and crucially no HangupSignal, so the next
        // reconnect/login classification stays untouched.
        var (engine, handler, players, wire, _, signal) = Setup();
        handler.SetHangupsDisabledCheck(() => true);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
        var (disc, supp) = signal.PeekForTests();
        Assert.False(disc);
        Assert.False(supp);
    }

    [Fact]
    public void Hangup_WhenCheckReturnsFalse_FiresNormally()
    {
        // Kill-switch off → behaves exactly as the unguarded path.
        var (engine, handler, players, wire, _, _) = Setup();
        handler.SetHangupsDisabledCheck(() => false);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        Assert.Equal("=x\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }
}
