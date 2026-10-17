using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Auth + channel-aware-denial coverage for TrapHandler. The
// search → disarm state machine is in TrapDisarmManagerTests; these
// tests drive the handler via the engine's chat-entry pipeline and
// verify the right replies / wire-sends fire for the various
// permission + skill + channel combos.
public sealed class TrapHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    // Isolated empty game-data root — no SwitchSet, so CanDisarm is driven
    // purely by the parsed Traps stat (class/race are left empty here, so the
    // inference path never fires and never reads the cache).
    private static readonly string GameDataRoot =
        Path.Combine(Path.GetTempPath(), "mudplay-traphandler-tests");

    private (RemoteCommandManager engine, TrapHandler handler, TrapDisarmManager mgr, PlayerDatabase players, List<byte[]> wire) Setup(int traps = 50)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        PlayerStats stats = new() { Traps = traps };
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        TrapDisarmManager mgr = new(router, stats, new GameDataCache(GameDataRoot));
        TrapHandler handler = new(engine, mgr);
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        return (engine, handler, mgr, players, wire);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static ChatLine Say(string sender, string msg) =>
        new(ChatChannel.Local, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private string LastReply() =>
        Encoding.Latin1.GetString(_replies[^1]);

    // ===== Happy path =====

    [Fact]
    public void Trap_FromAuthorisedSenderWithSkill_QueuesAndSendsDisarm()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));

        Assert.Equal("disarm trap n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    // The acceptance goes back before the disarm starts, so the asker knows someone
    // has the trap.
    [Fact]
    public void Trap_Accepted_SaysSoFirst()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));

        Assert.Equal("/Raijin {Attempting to disarm trap n.}\r",
            Encoding.Latin1.GetString(_replies[0]));
    }

    [Fact]
    public void Trap_LongFormDirection_NormalisedToShort()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap northeast"));

        Assert.Equal("disarm trap ne\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    // ===== Missing / bad direction =====

    [Fact]
    public void Trap_NoDirection_RepliesMissing()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap"));

        Assert.Contains("missing direction", LastReply());
    }

    [Fact]
    public void Trap_UnknownDirection_RepliesUnknown()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap middle"));

        Assert.Contains("unknown direction", LastReply());
    }

    [Fact]
    public void Trap_MissingDirection_WhenWarnOnDenialOff_NoReply()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap"));

        Assert.Empty(_replies);
    }

    // ===== Skill gate (channel-aware) =====

    [Fact]
    public void Trap_NoSkill_FromTelepath_RepliesCantDisarm()
    {
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));

        Assert.Contains("can't disarm", LastReply());
    }

    [Fact]
    public void Trap_NoSkill_FromSay_SilentlyIgnored()
    {
        // Broadcast channel — only trap-skilled players should answer.
        // A chorus of "{can't disarm}" replies from everyone in the
        // room would be noise.
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Say("Raijin", "@trap n"));

        Assert.Empty(_replies);
    }

    [Fact]
    public void Trap_NoSkill_WhenWarnOnDenialOff_NoReply()
    {
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));

        Assert.Empty(_replies);
    }

    // ===== @trap stop =====

    [Fact]
    public void TrapStop_AbortsCurrentAndAcksWithOk()
    {
        var (engine, _, mgr, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));
        _replies.Clear();

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap stop"));

        // ok ack on telepath wire: `/Raijin {ok}\r`
        Assert.Equal("/Raijin {ok}\r", LastReply());
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void TrapStop_AnyExecuteCommandsGrant_CanStop()
    {
        var (engine, _, mgr, players, _) = Setup();
        SeedPlayer(players, "Initiator", PlayerRemoteControls.ExecuteCommands);
        SeedPlayer(players, "Stopper",   PlayerRemoteControls.ExecuteCommands);

        // Initiator opens the flow.
        ChatLines.Hear(_routers[engine], Telepath("Initiator", "@trap n"));
        // Stopper (different player) cancels — per user spec.
        ChatLines.Hear(_routers[engine], Telepath("Stopper", "@trap stop"));

        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // ===== Authorisation gate (engine-side, applies BEFORE handler) =====

    [Fact]
    public void Trap_FromUnauthorisedSender_DoesNothing()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@trap n"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Trap_FromUnknownSender_DoesNothing()
    {
        var (engine, _, _, _, wire) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Drive-by", "@trap n"));
        Assert.Empty(wire);
    }

    // ===== Lifecycle =====

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        handler.Dispose();
        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@trap n"));

        Assert.Empty(wire);
    }
}
