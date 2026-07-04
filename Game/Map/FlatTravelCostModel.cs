namespace FujinTerm.Game.Map;

// Default ITravelCostModel — multiplies hop count by a configurable
// seconds-per-hop constant. Used until the user feeds real timings into
// the AutoLairSettings encumbrance-gated table, at which point
// EncumbranceGatedTravelCostModel takes over.
//
// The 1.5 s default is a single-character running-without-encumbrance
// observation — close enough to ship before the calibration loop
// (HopTimingCalibrator) produces realm-tuned values. Setting fewer than
// 0.1 s clamps to 0.1 s; the scheduler asserts a positive estimate.
public sealed class FlatTravelCostModel : ITravelCostModel
{
    private readonly double _secondsPerHop;

    public FlatTravelCostModel(double secondsPerHop = 1.5)
    {
        _secondsPerHop = Math.Max(0.1, secondsPerHop);
    }

    public TimeSpan EstimateTravel(int hopCount)
    {
        if (hopCount <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(hopCount * _secondsPerHop);
    }
}
