using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The green / amber / red band mapping the route Details window colours monster names
// with — both boundaries inclusive on the lower side, an unknown hit% muted.
public sealed class MonsterHitBandTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]   // at the green boundary → still green (inclusive)
    public void AtOrBelowGreenMax_IsGreen(int pct)
        => Assert.Same(MonsterHitBand.Green, MonsterHitBand.BrushFor(pct, 15, 45));

    [Theory]
    [InlineData(16)]   // one past green → amber
    [InlineData(45)]   // at the amber boundary → still amber (inclusive)
    public void BetweenBoundaries_IsAmber(int pct)
        => Assert.Same(MonsterHitBand.Yellow, MonsterHitBand.BrushFor(pct, 15, 45));

    [Theory]
    [InlineData(46)]   // one past amber → red
    [InlineData(100)]
    public void AboveYellowMax_IsRed(int pct)
        => Assert.Same(MonsterHitBand.Red, MonsterHitBand.BrushFor(pct, 15, 45));

    [Fact]
    public void UnknownHitPercent_IsMuted()
        => Assert.Same(MonsterHitBand.Muted, MonsterHitBand.BrushFor(null, 15, 45));
}
