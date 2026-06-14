using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the single-flag one-shot semantics. ConsumeRelogIntent returns
/// true exactly once per SignalRelog and resets to false, so the signal
/// can be raised on subsequent relogs without leaking state.
/// </summary>
public sealed class RelogSignalTests
{
    [Fact]
    public void Initial_FlagFalse()
    {
        RelogSignal s = new();
        Assert.False(s.ConsumeRelogIntent());
    }

    [Fact]
    public void SignalRelog_ArmsFlag()
    {
        RelogSignal s = new();
        s.SignalRelog();
        Assert.True(s.PeekForTests());
    }

    [Fact]
    public void ConsumeRelogIntent_OneShot()
    {
        RelogSignal s = new();
        s.SignalRelog();
        Assert.True(s.ConsumeRelogIntent());
        Assert.False(s.ConsumeRelogIntent());
    }

    [Fact]
    public void SignalRelog_AfterConsume_ReArms()
    {
        // A second relog later in the session re-arms cleanly.
        RelogSignal s = new();
        s.SignalRelog();
        Assert.True(s.ConsumeRelogIntent());
        s.SignalRelog();
        Assert.True(s.ConsumeRelogIntent());
    }
}
