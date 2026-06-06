using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.24 — AutoLairSettings DTO defaults, JSON round-trip, and the
/// EncumbranceHopTimes lookup. Plus the
/// <see cref="EncumbranceGatedTravelCostModel"/> hop → duration math.
/// </summary>
public sealed class AutoLairSettingsTests
{
    // ----- defaults -------------------------------------------------

    [Fact]
    public void Defaults_AreSane()
    {
        AutoLairSettings dto = new();
        Assert.Equal(AutoLairHeuristic.Default, dto.Heuristic);
        Assert.Equal(1.0, dto.IdlePenalty);
        Assert.Equal(30, dto.EngageTimeoutSeconds);
        Assert.Equal(AutoLairTravelCostMode.Flat, dto.TravelCostMode);
        Assert.Equal(1.5, dto.FlatSecondsPerHop);
        Assert.NotNull(dto.HopTimesByEncumbrance);
        // Per-bucket defaults scale up monotonically with encumbrance.
        Assert.True(dto.HopTimesByEncumbrance.None       < dto.HopTimesByEncumbrance.Light);
        Assert.True(dto.HopTimesByEncumbrance.Light      < dto.HopTimesByEncumbrance.Medium);
        Assert.True(dto.HopTimesByEncumbrance.Medium     < dto.HopTimesByEncumbrance.Heavy);
        Assert.True(dto.HopTimesByEncumbrance.Heavy      < dto.HopTimesByEncumbrance.Encumbered);
    }

    // ----- JSON round-trip ------------------------------------------

    [Fact]
    public void JsonRoundTrip_PreservesEveryField()
    {
        AutoLairSettings dto = new()
        {
            Heuristic = AutoLairHeuristic.Throughput,
            IdlePenalty = 2.5,
            EngageTimeoutSeconds = 45,
            TravelCostMode = AutoLairTravelCostMode.EncumbranceGated,
            FlatSecondsPerHop = 1.7,
            HopTimesByEncumbrance = new EncumbranceHopTimes
            {
                None = 0.8, Light = 1.2, Medium = 2.1, Heavy = 3.5, Encumbered = 5.5,
            },
        };

        string json = JsonSerializer.Serialize(dto);
        AutoLairSettings? roundTrip = JsonSerializer.Deserialize<AutoLairSettings>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(AutoLairHeuristic.Throughput, roundTrip!.Heuristic);
        Assert.Equal(2.5, roundTrip.IdlePenalty);
        Assert.Equal(45, roundTrip.EngageTimeoutSeconds);
        Assert.Equal(AutoLairTravelCostMode.EncumbranceGated, roundTrip.TravelCostMode);
        Assert.Equal(1.7, roundTrip.FlatSecondsPerHop);
        Assert.Equal(0.8, roundTrip.HopTimesByEncumbrance.None);
        Assert.Equal(1.2, roundTrip.HopTimesByEncumbrance.Light);
        Assert.Equal(2.1, roundTrip.HopTimesByEncumbrance.Medium);
        Assert.Equal(3.5, roundTrip.HopTimesByEncumbrance.Heavy);
        Assert.Equal(5.5, roundTrip.HopTimesByEncumbrance.Encumbered);
    }

    // ----- EncumbranceHopTimes.For ----------------------------------

    [Theory]
    [InlineData(EncumbranceLevel.None,       1.0)]
    [InlineData(EncumbranceLevel.Light,      1.5)]
    [InlineData(EncumbranceLevel.Medium,     2.5)]
    [InlineData(EncumbranceLevel.Heavy,      4.0)]
    [InlineData(EncumbranceLevel.Encumbered, 6.0)]
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
        EncumbranceHopTimes times = new() { Heavy = 4.5 };
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

    [Fact]
    public void EncumbranceGated_TimesProperty_LiveSwap()
    {
        // The Settings tab's Apply replaces Times in place — the next
        // EstimateTravel call must see the new table without a model
        // re-construct.
        PlayerState state = new() { Encumbrance = EncumbranceLevel.Medium };
        EncumbranceGatedTravelCostModel model = new(state,
            new EncumbranceHopTimes { Medium = 2.0 });
        Assert.Equal(TimeSpan.FromSeconds(4), model.EstimateTravel(2));

        model.Times = new EncumbranceHopTimes { Medium = 3.0 };
        Assert.Equal(TimeSpan.FromSeconds(6), model.EstimateTravel(2));
    }
}
