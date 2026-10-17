using MudPlay.Game;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

public sealed class EncumbranceGateTests
{
    private static readonly EncumbranceReading Enc = new(0, 3840, 0, EncumbranceLevel.None);

    [Fact]
    public void Past90_CapsAtNinetyPercentOfMax()
    {
        Assert.Equal(3456, EncumbranceGate.ComputeCapWeight(false, false, false, Enc, skipPast90: true));
    }

    [Fact]
    public void NoGates_CapIsMax()
    {
        Assert.Equal(3840, EncumbranceGate.ComputeCapWeight(false, false, false, Enc));
    }

    [Fact]
    public void HeavyGate_StaysTighterThanNinety()
    {
        long heavy = EncumbranceGate.ComputeCapWeight(false, false, true, Enc, skipPast90: true);
        Assert.Equal(2572, heavy);
        Assert.True(heavy < 3456);   // 90% of 3840
    }
}
