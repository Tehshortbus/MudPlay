using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// @do is the highest-trust verb in the @-command catalogue —
/// passthrough that ships the sender's args on the wire as if the
/// local user had typed them. Engine-level hard-blocks (reroll,
/// suicide-lives) gate destructive verbs before this handler runs,
/// per-player ExecuteCommands grant gates everything else.
/// </summary>
public sealed class DoHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, DoHandler handler, PlayerDatabase players, List<byte[]> wire) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        DoHandler handler = new(engine);
        List<byte[]> wire = new();
        handler.SetWireSender(wire.Add);
        return (engine, handler, players, wire);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    // ===== Authorisation =====

    [Fact]
    public void Do_FromAuthorisedSender_ShipsCommandToWire()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Trusted", "@do par"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("par\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Do_AcknowledgesSenderWithOkReply()
    {
        // After the wire-send lands, the sender gets {ok} back on the
        // same channel they used — same curly-brace meta-line shape
        // every other handler reply uses. Lets the sender confirm the
        // command actually fired (not just that they were permitted).
        var (engine, _, players, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Trusted", "@do par"));

        // Engine wraps the reply payload in { } at SendReply time and
        // routes via telepath: `/Trusted {ok}\r`.
        Assert.Equal("/Trusted {ok}\r",
            Encoding.Latin1.GetString(engine.LastSentForTests[^1]));
    }

    [Fact]
    public void Do_NoArgs_DoesNotAck()
    {
        // No work was done — no {ok} either.
        var (engine, _, players, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Trusted", "@do"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Do_MultiArgCommand_RejoinedWithSingleSpaces()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Trusted", "@do cast major heal Helper"));

        Assert.Equal("cast major heal Helper\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void Do_NoArgs_NoOp()
    {
        // Defensive — a bare `@do` shouldn't drop a lone CR on the wire.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Trusted", "@do"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_FromUnauthorisedSender_DoesNothing()
    {
        // Sender has every permission EXCEPT ExecuteCommands.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Stranger", "@do par"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_FromUnknownSender_DoesNothing()
    {
        // No customisation = default deny.
        var (engine, _, _, wire) = Setup();
        engine.DispatchForTests(Telepath("Drive-by", "@do par"));
        Assert.Empty(wire);
    }

    // ===== Hard-blocks (engine-level, applies BEFORE this handler) =====

    [Fact]
    public void Do_Reroll_AlwaysBlocked_RegardlessOfGrants()
    {
        // Give EVERY permission — the reroll hard-block is unconditional.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        engine.DispatchForTests(Telepath("Trusted", "@do reroll"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_Suicide_AlwaysBlocked_RegardlessOfLives()
    {
        // Per user direction: forcible-death verbs route exclusively
        // through @suicide (which has its own elevated-permission
        // gate + lives-threshold + stored-password contract). @do
        // suicide is unconditionally redirected — even at high lives
        // and permissive threshold, the @do handler never runs.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 99;
        engine.MaxSuicideLivesThreshold = 0;

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.Empty(wire);  // never reaches the @do handler's wire-sender
    }

    // ===== Channel filters / master kill-switch =====

    [Fact]
    public void Do_MasterDisable_DropsEverything()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        engine.MasterDisable = true;

        engine.DispatchForTests(Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_DisabledChannel_DropsCommand()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        engine.DisableTelepathChannel = true;

        engine.DispatchForTests(Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }

    // ===== Lifecycle =====

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        handler.Dispose();
        engine.DispatchForTests(Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }
}
