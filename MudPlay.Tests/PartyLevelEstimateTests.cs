using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// PartyLevelEstimate resolves a member's (Low, High) from an exact level and a
// title band. Exact wins — UNLESS the band's floor has risen above the exact
// reading (the player trained since we last asked), in which case the band wins
// until we re-learn an exact at or above the floor.
public sealed class PartyLevelEstimateTests
{
    [Fact]
    public void ExactOnly_CollapsesToExact()
    {
        var e = new PartyLevelEstimate(12, null);
        Assert.Equal(12, e.Low);
        Assert.Equal(12, e.High);
    }

    [Fact]
    public void TitleOnly_UsesBand()
    {
        var e = new PartyLevelEstimate(null, (10, 14));
        Assert.Equal(10, e.Low);
        Assert.Equal(14, e.High);
    }

    [Fact]
    public void Neither_IsNull()
    {
        var e = new PartyLevelEstimate(null, null);
        Assert.Null(e.Low);
        Assert.Null(e.High);
    }

    [Fact]
    public void ExactWithinBand_ExactWins()
    {
        var e = new PartyLevelEstimate(12, (10, 14));
        Assert.Equal(12, e.Low);
        Assert.Equal(12, e.High);
    }

    [Fact]
    public void ExactBelowBandFloor_TitleWins()
    {
        // Recorded level 9, but the title band now starts at 10 — they trained
        // past our stale exact, so the band takes over.
        var e = new PartyLevelEstimate(9, (10, 14));
        Assert.Equal(10, e.Low);
        Assert.Equal(14, e.High);
    }

    [Fact]
    public void ExactAtBandFloor_ExactWins()
    {
        // exact == floor is not "below", so the exact still wins.
        var e = new PartyLevelEstimate(10, (10, 14));
        Assert.Equal(10, e.Low);
        Assert.Equal(10, e.High);
    }

    [Fact]
    public void ExactAboveBand_ExactWins()
    {
        // A lower band never overrides a valid exact.
        var e = new PartyLevelEstimate(30, (10, 14));
        Assert.Equal(30, e.Low);
        Assert.Equal(30, e.High);
    }
}
