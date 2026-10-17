using MudPlay.Game.Light;
using Xunit;

namespace MudPlay.Tests;

// The room-visibility model ported from MME's threshold table
// (frmMain.frm:33293-33300): visibility is a function of
// V = charIllu + roomLight, with strict-< band boundaries.
public sealed class LightModelTests
{
    [Theory]
    [InlineData(-201, LightVisibility.PitchBlack)]
    [InlineData(-200, LightVisibility.VeryDark)]      // boundary: -200 is NOT pitch black
    [InlineData(-151, LightVisibility.VeryDark)]
    [InlineData(-150, LightVisibility.BarelyVisible)] // boundary: -150 is seeable
    [InlineData(-101, LightVisibility.BarelyVisible)]
    [InlineData(-100, LightVisibility.DimlyLit)]      // boundary: -100 is dimly lit
    [InlineData(-1,   LightVisibility.DimlyLit)]
    [InlineData(0,    LightVisibility.Normal)]
    [InlineData(50,   LightVisibility.Normal)]
    public void Classify_BandsOnCombinedValue(int roomLight, LightVisibility expected)
        => Assert.Equal(expected, LightModel.Classify(charIllu: 0, roomLight));

    [Fact]
    public void Classify_AddsCharIlluToRoomLight()
    {
        // A -300 room is pitch black unlit, but a lantern (175) lifts V to -125
        // → barely visible (seeable).
        Assert.Equal(LightVisibility.PitchBlack, LightModel.Classify(0, -300));
        Assert.Equal(LightVisibility.BarelyVisible, LightModel.Classify(175, -300));
    }

    [Theory]
    [InlineData(0, -150, 0)]      // already visible → no gap
    [InlineData(0, -100, 0)]
    [InlineData(0, -200, 50)]     // need +50 illu to reach -150
    [InlineData(0, -300, 150)]    // dark room needs a 150-strength light
    [InlineData(50, -300, 100)]   // worn +50 illu shrinks the gap to 100
    public void IlluGapToSee_IsTheStrengthNeededToJustSee(int charIllu, int roomLight, int expected)
        => Assert.Equal(expected, LightModel.IlluGapToSee(charIllu, roomLight));

    [Theory]
    [InlineData(LightVisibility.PitchBlack, "The room is pitch black")]
    [InlineData(LightVisibility.VeryDark, "The room is very dark — you can't see anything")]
    [InlineData(LightVisibility.BarelyVisible, "The room is barely visible")]
    [InlineData(LightVisibility.DimlyLit, "The room is dimly lit")]
    [InlineData(LightVisibility.Normal, "")]
    public void Describe_MatchesServerPhrasing(LightVisibility visibility, string expected)
        => Assert.Equal(expected, LightModel.Describe(visibility));
}
