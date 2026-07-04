using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the @relog handler's wire behaviour: only fires for a sender
/// with the HangupDisconnect permission, sends the configured
/// GameCommands.ExitCommand verbatim + CR (graceful main-menu logoff),
/// and arms RelogSignal so MainWindowVM forces a reconnect-and-login.
/// Sibling of HangupHandler — same wire shape, opposite reconnect
/// intent (relog does NOT suppress the entry automation).
/// </summary>
public sealed class RelogHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, RelogHandler handler, PlayerDatabase players, List<byte[]> wire, GameCommands commands, RelogSignal signal) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        GameCommands commands = new();   // defaults: "E" / "=x"
        RelogSignal signal = new();
        RemoteCommandManager engine = new(chat, party, players);
        RelogHandler handler = new(engine, commands, signal);
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
    public void Relog_FromAuthorisedSender_SendsConfiguredExitCommand()
    {
        var (engine, _, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("=x\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Relog_RespectsCustomExitCommand()
    {
        var (engine, _, players, wire, commands, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = "bye";

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

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

        engine.DispatchForTests(Telepath("Stranger", "@relog"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Relog_FromUnknownSender_DoesNothing()
    {
        // No customization at all → default deny.
        var (engine, _, _, wire, _, _) = Setup();
        engine.DispatchForTests(Telepath("Drive-by", "@relog"));
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

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire, _, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        handler.Dispose();
        engine.DispatchForTests(Telepath("Trusted", "@relog"));

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

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

        Assert.True(signal.PeekForTests());
    }

    [Fact]
    public void Relog_DeniedByPermission_DoesNotRaiseSignal()
    {
        // Engine denies before the handler runs — no wire, no signal.
        // A stranger spamming @relog must not be able to force a
        // reconnect cycle.
        var (engine, _, _, _, _, signal) = Setup();
        engine.DispatchForTests(Telepath("Stranger", "@relog"));

        Assert.False(signal.PeekForTests());
    }

    [Fact]
    public void Relog_SignalRaisedEvenWithoutWireSender()
    {
        // Pre-binding (or in tests where the wire-sender intentionally
        // isn't bound), the intent should still be recorded so a racing
        // Disconnected event still classifies correctly.
        var (engine, handler, players, _, _, signal) = Setup();
        handler.SetWireSender(_ => { });   // bind once...
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        engine.DispatchForTests(Telepath("Trusted", "@relog"));
        Assert.True(signal.PeekForTests());
    }

    // ===== Master "Disable hangups" kill-switch =========================

    [Fact]
    public void Relog_WhenHangupsDisabled_SendsNothingAndRaisesNoSignal()
    {
        // Kill-switch on → an authorised @relog is a silent no-op: a relog
        // is a drop-then-redial, so it counts as an automatic hangup the
        // user opted out of. No wire, no RelogSignal.
        var (engine, handler, players, wire, _, signal) = Setup();
        handler.SetHangupsDisabledCheck(() => true);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

        Assert.Empty(wire);
        Assert.False(signal.PeekForTests());
    }

    [Fact]
    public void Relog_WhenCheckReturnsFalse_FiresNormally()
    {
        // Kill-switch off → behaves exactly as the unguarded path.
        var (engine, handler, players, wire, _, _) = Setup();
        handler.SetHangupsDisabledCheck(() => false);
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@relog"));

        Assert.Equal("=x\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }
}
