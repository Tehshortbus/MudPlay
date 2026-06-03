using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the two-flag one-shot semantics. Each ConsumeXxx returns true
/// exactly once per SignalHangup and resets back to false, so the
/// signal can be raised on subsequent hangups without leaking state.
/// </summary>
public sealed class HangupSignalTests
{
    [Fact]
    public void Initial_BothFlagsFalse()
    {
        HangupSignal s = new();
        Assert.False(s.ConsumeDisconnectIntent());
        Assert.False(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void SignalHangup_ArmsBothFlags()
    {
        HangupSignal s = new();
        s.SignalHangup();
        var (disc, supp) = s.PeekForTests();
        Assert.True(disc);
        Assert.True(supp);
    }

    [Fact]
    public void ConsumeDisconnectIntent_OneShot()
    {
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.False(s.ConsumeDisconnectIntent());
    }

    [Fact]
    public void ConsumeSuppressEntry_OneShot()
    {
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeSuppressEntry());
        Assert.False(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void ConsumeMethods_Independent()
    {
        // Consuming one flag must NOT consume the other — they fire at
        // different moments (disconnect handler vs entry-arm time) so
        // each consumer reads only its own flag.
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.True(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void SignalHangup_AfterPartialConsume_ReArmsBoth()
    {
        // A second hangup later in the session re-arms cleanly.
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        // Disconnect-intent already consumed, suppress-entry still pending.
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.True(s.ConsumeSuppressEntry());
    }
}
