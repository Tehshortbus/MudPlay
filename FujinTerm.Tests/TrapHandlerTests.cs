using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Auth + channel-aware-denial coverage for TrapHandler. The
/// search → disarm state machine is in TrapDisarmManagerTests; these
/// tests drive the handler via the engine's chat-entry pipeline and
/// verify the right replies / wire-sends fire for the various
/// permission + skill + channel combos.
/// </summary>
public sealed class TrapHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, TrapHandler handler, TrapDisarmManager mgr, PlayerDatabase players, List<byte[]> wire) Setup(int traps = 50)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerStats stats = new() { Traps = traps };
        RemoteCommandManager engine = new(chat, party, players);
        TrapDisarmManager mgr = new(router, stats);
        TrapHandler handler = new(engine, mgr);
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        return (engine, handler, mgr, players, wire);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static ChatLogEntry Say(string sender, string msg) =>
        new(Now, ChatChannel.Local, sender, msg, $"{sender} says: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static string LastReply(RemoteCommandManager e) =>
        Encoding.Latin1.GetString(e.LastSentForTests[^1]);

    // ===== Happy path =====

    [Fact]
    public void Trap_FromAuthorisedSenderWithSkill_QueuesAndSendsSearch()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Raijin", "@trap n"));

        Assert.Equal("sea n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void Trap_LongFormDirection_NormalisedToShort()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Raijin", "@trap northeast"));

        Assert.Equal("sea ne\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    // ===== Missing / bad direction =====

    [Fact]
    public void Trap_NoDirection_RepliesMissing()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Raijin", "@trap"));

        Assert.Contains("missing direction", LastReply(engine));
    }

    [Fact]
    public void Trap_UnknownDirection_RepliesUnknown()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Raijin", "@trap middle"));

        Assert.Contains("unknown direction", LastReply(engine));
    }

    [Fact]
    public void Trap_MissingDirection_WhenWarnOnDenialOff_NoReply()
    {
        var (engine, _, _, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("Raijin", "@trap"));

        Assert.Empty(engine.LastSentForTests);
    }

    // ===== Skill gate (channel-aware) =====

    [Fact]
    public void Trap_NoSkill_FromTelepath_RepliesCantDisarm()
    {
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Raijin", "@trap n"));

        Assert.Contains("can't disarm", LastReply(engine));
    }

    [Fact]
    public void Trap_NoSkill_FromSay_SilentlyIgnored()
    {
        // Broadcast channel — only trap-skilled players should answer.
        // A chorus of "{can't disarm}" replies from everyone in the
        // room would be noise.
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Say("Raijin", "@trap n"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Trap_NoSkill_WhenWarnOnDenialOff_NoReply()
    {
        var (engine, _, _, players, _) = Setup(traps: 0);
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("Raijin", "@trap n"));

        Assert.Empty(engine.LastSentForTests);
    }

    // ===== @trap stop =====

    [Fact]
    public void TrapStop_AbortsCurrentAndAcksWithOk()
    {
        var (engine, _, mgr, players, _) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);
        engine.DispatchForTests(Telepath("Raijin", "@trap n"));
        engine.LastSentForTests.Clear();

        engine.DispatchForTests(Telepath("Raijin", "@trap stop"));

        // ok ack on telepath wire: `/Raijin {ok}\r`
        Assert.Equal("/Raijin {ok}\r", LastReply(engine));
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void TrapStop_AnyExecuteCommandsGrant_CanStop()
    {
        var (engine, _, mgr, players, _) = Setup();
        SeedPlayer(players, "Initiator", PlayerRemoteControls.ExecuteCommands);
        SeedPlayer(players, "Stopper",   PlayerRemoteControls.ExecuteCommands);

        // Initiator opens the flow.
        engine.DispatchForTests(Telepath("Initiator", "@trap n"));
        // Stopper (different player) cancels — per user spec.
        engine.DispatchForTests(Telepath("Stopper", "@trap stop"));

        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // ===== Authorisation gate (engine-side, applies BEFORE handler) =====

    [Fact]
    public void Trap_FromUnauthorisedSender_DoesNothing()
    {
        var (engine, _, _, players, wire) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);

        engine.DispatchForTests(Telepath("Stranger", "@trap n"));

        Assert.Empty(wire);
    }

    [Fact]
    public void Trap_FromUnknownSender_DoesNothing()
    {
        var (engine, _, _, _, wire) = Setup();
        engine.DispatchForTests(Telepath("Drive-by", "@trap n"));
        Assert.Empty(wire);
    }

    // ===== Lifecycle =====

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (engine, handler, _, players, wire) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.ExecuteCommands);

        handler.Dispose();
        engine.DispatchForTests(Telepath("Raijin", "@trap n"));

        Assert.Empty(wire);
    }
}
