using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// AutoLairSettings DTO defaults and the
// EncumbranceHopTimes lookup. Plus the EncumbranceGatedTravelCostModel
// hop → duration math.
public sealed class AutoLairSettingsTests
{
    // ----- defaults -------------------------------------------------

    [Fact]
    public void Defaults_AreTheShippedOnes()
    {
        // What a profile with no Auto-Lair entry runs on (the hop bands are pinned by
        // HopTimes_For_ReturnsBucketDefault).
        AutoLairSettings dto = new();
        Assert.Equal(AutoLairHeuristic.Default, dto.Heuristic);
        Assert.Equal(30, dto.EngageTimeoutSeconds);
        // Auto is the default — realm-aware model selection out of the box.
        Assert.Equal(AutoLairTravelCostMode.Auto, dto.TravelCostMode);
        Assert.Equal(1.5, dto.FlatSecondsPerHop);
    }

    // ----- EncumbranceHopTimes.For ----------------------------------

    [Theory]
    [InlineData(EncumbranceLevel.None,       0.7)]
    [InlineData(EncumbranceLevel.Light,      0.7)]
    [InlineData(EncumbranceLevel.Medium,     0.7)]
    [InlineData(EncumbranceLevel.Heavy,      1.7)]
    [InlineData(EncumbranceLevel.Encumbered, 1.7)]
    public void HopTimes_For_ReturnsBucketDefault(EncumbranceLevel level, double expected)
    {
        EncumbranceHopTimes t = new();
        Assert.Equal(expected, t.For(level));
    }

    [Fact]
    public void HopTimes_For_Unknown_FallsBackToLight()
    {
        EncumbranceHopTimes t = new();
        // Unknown means we haven't yet observed an `enc` line. Returning
        // the Light bucket beats Unknown=0 (would crash the scheduler
        // with TimeSpan.Zero estimates) and beats Heavy (overly cautious
        // pre-observation).
        Assert.Equal(t.Light, t.For(EncumbranceLevel.Unknown));
    }

    // ----- EncumbranceGatedTravelCostModel --------------------------

    [Fact]
    public void EncumbranceGated_UsesLiveStateBucket()
    {
        PlayerState state = new();
        EncumbranceHopTimes times = new() { None = 1.0, Heavy = 4.5 };
        EncumbranceGatedTravelCostModel model = new(state, times);

        state.Encumbrance = EncumbranceLevel.Heavy;
        Assert.Equal(TimeSpan.FromSeconds(13.5), model.EstimateTravel(3));

        state.Encumbrance = EncumbranceLevel.None;
        Assert.Equal(TimeSpan.FromSeconds(3), model.EstimateTravel(3));
    }

    [Fact]
    public void EncumbranceGated_ZeroHops_ReturnsZero()
    {
        PlayerState state = new();
        EncumbranceGatedTravelCostModel model = new(state, new EncumbranceHopTimes());
        Assert.Equal(TimeSpan.Zero, model.EstimateTravel(0));
        Assert.Equal(TimeSpan.Zero, model.EstimateTravel(-1));
    }

    [Fact]
    public void EncumbranceGated_ZeroEntryClampedToFloor()
    {
        // Bucket of 0 (or negative) would zero out scheduler costs and
        // collapse the scoring; the model floors at 0.1 s/hop.
        PlayerState state = new();
        EncumbranceHopTimes times = new() { None = 0, Light = -1 };
        EncumbranceGatedTravelCostModel model = new(state, times);

        state.Encumbrance = EncumbranceLevel.None;
        Assert.Equal(TimeSpan.FromSeconds(0.1), model.EstimateTravel(1));

        state.Encumbrance = EncumbranceLevel.Light;
        Assert.Equal(TimeSpan.FromSeconds(0.1), model.EstimateTravel(1));
    }

}
