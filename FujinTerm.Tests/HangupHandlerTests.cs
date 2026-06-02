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

    private static (RemoteCommandManager engine, HangupHandler handler, PlayerDatabase players, List<byte[]> wire, GameCommands commands) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        GameCommands commands = new();   // defaults: "E" / "=x"
        RemoteCommandManager engine = new(chat, party, players);
        HangupHandler handler = new(engine, commands);
        List<byte[]> wire = new();
        handler.SetWireSender(wire.Add);
        return (engine, handler, players, wire, commands);
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
        var (engine, _, players, wire, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("=x\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Hangup_RespectsCustomExitCommand()
    {
        var (engine, _, players, wire, commands) = Setup();
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
        var (engine, _, players, wire, _) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.HangupDisconnect);

        engine.DispatchForTests(Telepath("Stranger", "@hangup"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Hangup_FromUnknownSender_DoesNothing()
    {
        // No customization at all → default deny.
        var (engine, _, _, wire, _) = Setup();
        engine.DispatchForTests(Telepath("Drive-by", "@hangup"));
        Assert.Empty(wire);
    }

    [Fact]
    public void Hangup_BlankExitCommand_SendsNothing()
    {
        // Defensive — a misconfigured-to-blank exit command shouldn't
        // produce a lone CR on the wire.
        var (engine, _, players, wire, commands) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);
        commands.ExitCommand = string.Empty;

        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.HangupDisconnect);

        handler.Dispose();
        engine.DispatchForTests(Telepath("Trusted", "@hangup"));

        Assert.Empty(wire);
    }
}
