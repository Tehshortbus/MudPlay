using System.Collections.Generic;
using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// EngineSendGate named-hold composition — more than one flow
// (suicide-password entry, mortally-wounded drop) can gate engine sends at
// once, so the gate stays locked until EVERY hold releases.
public sealed class EngineSendGateTests
{
    [Fact]
    public void NoHolds_IsUnlocked()
    {
        EngineSendGate gate = new();
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void SingleHold_LocksUntilReleased()
    {
        EngineSendGate gate = new();
        gate.Hold("SuicidePassword");
        Assert.True(gate.IsLocked);
        gate.Release("SuicidePassword");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void TwoHolds_StayLockedUntilBothRelease()
    {
        // The R7 case: a drop raises "MortallyWounded" while a suicide-password
        // flow already holds "SuicidePassword". Releasing one must NOT unlock —
        // the other flow still needs the gate down.
        EngineSendGate gate = new();
        gate.Hold("SuicidePassword");
        gate.Hold("MortallyWounded");
        Assert.True(gate.IsLocked);

        gate.Release("SuicidePassword");
        Assert.True(gate.IsLocked);   // MortallyWounded still holds

        gate.Release("MortallyWounded");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void Hold_IsIdempotentPerReason()
    {
        // Re-asserting the same reason must not require a matching double-release.
        EngineSendGate gate = new();
        gate.Hold("MortallyWounded");
        gate.Hold("MortallyWounded");
        gate.Release("MortallyWounded");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void Release_UnknownReason_IsNoOp()
    {
        EngineSendGate gate = new();
        gate.Hold("MortallyWounded");
        gate.Release("NeverHeld");
        Assert.True(gate.IsLocked);
    }

    [Fact]
    public void Wrapper_ShortCircuitsWhileAnyHold()
    {
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent);

        gate.Hold("MortallyWounded");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent); // dropped on the floor

        gate.Release("MortallyWounded");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Equal(2, sent.Count);
    }

    // ----- confusion-fumble replay of the last CLIENT command ----------

    private static string Last(List<byte[]> sent) => Encoding.Latin1.GetString(sent[^1]);

    [Fact]
    public void Replay_ResendsLastClientCommand()
    {
        // A fumble ate the last client command; re-sending it performs the action.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("cast mist dragon\r"));
        Assert.Single(sent);

        gate.ReplayLastClientCommand();
        Assert.Equal(2, sent.Count);
        Assert.Equal("cast mist dragon\r", Last(sent));
    }

    [Fact]
    public void Replay_ResendsItemUse()
    {
        // Item-use is a multi-token command, so it re-fires (unlike a bare move).
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("use waterskin\r"));
        gate.ReplayLastClientCommand();

        Assert.Equal(2, sent.Count);
        Assert.Equal("use waterskin\r", Last(sent));
    }

    [Theory]
    [InlineData("n\r")]
    [InlineData("ne\r")]
    [InlineData("up\r")]
    [InlineData("South\r")]
    public void Replay_SkipsBareMovement(string move)
    {
        // A fumbled move is recovered by the move-revert + walker re-send, so the
        // gate must NOT also re-fire it — a second step would desync position.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes(move));
        gate.ReplayLastClientCommand();

        Assert.Single(sent);   // not re-fired
    }

    [Fact]
    public void Replay_NoOpWhileHeld()
    {
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);
        wrapped(Encoding.Latin1.GetBytes("a orc\r"));

        gate.Hold("MortallyWounded");
        gate.ReplayLastClientCommand();
        Assert.Single(sent);   // held → no replay
    }

    // The fumble coordinator reads the last client command before asking for the
    // replay, to leave out an attack the game has already engaged.
    [Fact]
    public void LastClientCommandText_IsTheLastWrappedSend()
    {
        EngineSendGate gate = new();
        Assert.Null(gate.LastClientCommandText);

        Action<byte[]> wrapped = gate.WrapEngineSender(_ => { });
        wrapped(Encoding.Latin1.GetBytes("a orc\r"));
        wrapped(Encoding.Latin1.GetBytes("mmis orc\r"));

        Assert.Equal("mmis orc", gate.LastClientCommandText);
    }

    [Fact]
    public void Replay_NoOpBeforeAnyClientSend()
    {
        // Nothing sent yet (e.g. the only traffic was user-typed, which bypasses the
        // gate) → nothing to replay.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        gate.WrapEngineSender(sent.Add);

        gate.ReplayLastClientCommand();

        Assert.Empty(sent);
    }

    // Report paradigm-20261007-225617: sneak keeping took the walker's `say
    // gazmuldduhaz` (a spoken password that opens an exit) for later, as if it were
    // say-channel chatter, and the move behind it found no exit.
    [Fact]
    public void RouteStepSender_IsNeverTakenForLater_ButIsStillReported()
    {
        EngineSendGate gate = new();
        List<string> reported = new();
        gate.SetSneakHooks(takeForLater: _ => true, sent: reported.Add);
        List<string> wire = new();
        void Raw(byte[] b) => wire.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r'));

        gate.WrapEngineSender(Raw)(System.Text.Encoding.Latin1.GetBytes("say hello\r"));
        Assert.Empty(wire);                                     // chatter waits

        gate.WrapEngineSender(Raw, routeSteps: true)(System.Text.Encoding.Latin1.GetBytes("say gazmuldduhaz\r"));

        Assert.Equal(new[] { "say gazmuldduhaz" }, wire);
        Assert.Equal(new[] { "say gazmuldduhaz" }, reported);   // so the sneak is taken again
    }

    [Fact]
    public void RouteStepSender_StillStopsForAHold()
    {
        EngineSendGate gate = new();
        gate.Hold("password prompt");
        List<byte[]> wire = new();

        gate.WrapEngineSender(wire.Add, routeSteps: true)(System.Text.Encoding.Latin1.GetBytes("n\r"));

        Assert.Empty(wire);
    }

    // The gate is the one place a client command is reported from: once after the
    // send, and once more for a replay, which is the send that actually runs. While
    // the raw sender has the bytes, SendingClientCommand tells it the line is the
    // gate's to report.
    [Fact]
    public void ClientCommand_IsReportedOncePerSend_AndAgainForItsReplay()
    {
        EngineSendGate gate = new();
        List<string> reported = new();
        gate.SetSneakHooks(takeForLater: _ => false, sent: reported.Add);
        List<bool> sendingAsClient = new();

        gate.WrapEngineSender(_ => sendingAsClient.Add(gate.SendingClientCommand))(
            System.Text.Encoding.Latin1.GetBytes("use waterskin\r"));
        Assert.Equal(new[] { "use waterskin" }, reported);

        gate.ReplayLastClientCommand();
        Assert.Equal(new[] { "use waterskin", "use waterskin" }, reported);
        Assert.Equal(new[] { true, true }, sendingAsClient);
        Assert.False(gate.SendingClientCommand);
    }
}
