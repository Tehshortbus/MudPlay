using System.Text;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="EngineSendGate"/> named-hold composition — more than one flow
/// (suicide-password entry, mortally-wounded drop) can gate engine sends at
/// once, so the gate stays locked until EVERY hold releases.
/// </summary>
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
}
