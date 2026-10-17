using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// @do is the highest-trust verb in the @-command catalogue —
// passthrough that ships the sender's args on the wire as if the
// local user had typed them. Engine-level hard-blocks (reroll,
// suicide-lives) gate destructive verbs before this handler runs,
// per-player ExecuteCommands grant gates everything else.
public sealed class DoHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private (RemoteCommandManager engine, DoHandler handler, PlayerDatabase players, List<byte[]> wire) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        DoHandler handler = new(engine);
        List<byte[]> wire = new();
        handler.SetWireSender(wire.Add);
        return (engine, handler, players, wire);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par"));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("par\r", Encoding.Latin1.GetString(sent));
    }

    // While the line is going out the handler names who asked for it, which is how
    // the wire tells a party member's `@do break` from an engine's; before and after
    // the send it names nobody.
    [Fact]
    public void Do_NamesTheSender_OnlyWhileItsLineIsBeingSent()
    {
        var (engine, handler, players, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        List<string?> duringSend = new();
        handler.SetWireSender(_ => duringSend.Add(handler.SendingFor));

        Assert.Null(handler.SendingFor);
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do break"));

        Assert.Equal(new string?[] { "Trusted" }, duringSend);
        Assert.Null(handler.SendingFor);
    }

    // With the master switch off no remote command is followed, so a `@do break`
    // never reaches the wire and there is nothing for it to hold.
    [Fact]
    public void Do_WithTheMasterSwitchOff_SendsNothing()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        engine.BlockedByMasterSwitch = _ => true;

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do break"));

        Assert.Empty(wire);
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

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par"));

        // Engine wraps the reply payload in { } at SendReply time and
        // routes via telepath: `/Trusted {ok}\r`.
        Assert.Equal("/Trusted {ok}\r",
            Encoding.Latin1.GetString(_replies[^1]));
    }

    [Fact]
    public void Do_NoArgs_DoesNotAck()
    {
        // No work was done — no {ok} either.
        var (engine, _, players, _) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do"));

        Assert.Empty(_replies);
    }

    [Fact]
    public void Do_MultiArgCommand_RejoinedWithSingleSpaces()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do cast major heal Helper"));

        Assert.Equal("cast major heal Helper\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    // The relayed words get the clean-up every reply gets: a character outside
    // plain ASCII (here the CP437 é and ═ as the screen decodes them) goes out as
    // `?`.
    [Fact]
    public void Do_TextOutsideAscii_GoesOutAsQuestionMarks()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do say café ══"));

        Assert.Equal("say caf? ??\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void Do_PlainAsciiCommand_GoesOutByteForByte()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        const string command = "say Hi, \"there\" ~ {x} [1+2=3] 50% @home!";

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do " + command));

        Assert.Equal(Encoding.ASCII.GetBytes(command + "\r"), Assert.Single(wire));
    }

    // A letter outside ASCII would leave a `suicide` beside it as `suicide?`, which
    // the game may take as the command: the rule reads the word as it goes out.
    [Fact]
    public void Do_SuicideNextToALetterOutsideAscii_IsRefused()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do suicideé"));

        Assert.Empty(wire);
        Assert.Equal("/Trusted {@do suicide is not allowed, use @suicide}\r",
            Encoding.Latin1.GetString(_replies[^1]));
    }

    [Fact]
    public void Do_NoArgs_NoOp()
    {
        // Defensive — a bare `@do` shouldn't drop a lone CR on the wire.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_FromUnauthorisedSender_DoesNothing()
    {
        // Sender has every permission EXCEPT ExecuteCommands.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@do par"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_FromUnknownSender_DoesNothing()
    {
        // No customisation = default deny.
        var (engine, _, _, wire) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Drive-by", "@do par"));
        Assert.Empty(wire);
    }

    // ===== Hard-blocks (engine-level, applies BEFORE this handler) =====

    [Fact]
    public void Do_Reroll_AlwaysBlocked_RegardlessOfGrants()
    {
        // Give EVERY permission — the reroll hard-block is unconditional.
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do reroll"));

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

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do suicide"));

        Assert.Empty(wire);  // never reaches the @do handler's wire-sender
    }

    // ===== Channel filters / master kill-switch =====

    [Fact]
    public void Do_MasterDisable_DropsEverything()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        engine.MasterDisable = true;

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Do_DisabledChannel_DropsCommand()
    {
        var (engine, _, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);
        engine.DisableTelepathChannel = true;

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }

    // ===== Lifecycle =====

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, players, wire) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.ExecuteCommands);

        handler.Dispose();
        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par"));

        Assert.Empty(wire);
    }
}
