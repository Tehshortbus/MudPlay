using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Game;

/// <summary>
/// One axis of regen tracking — e.g. "HP regen while standing", "MA regen
/// while meditating". Holds the seed interval, the running EWMA estimates
/// for interval + amount per tick, and the observed sample count.
/// </summary>
public sealed partial class RegenStat : ObservableObject
{
    /// <summary>Seed interval used until observation refines the estimate.</summary>
    public TimeSpan SeedInterval { get; }

    /// <summary>Current best estimate of the regen tick interval (seconds-precision).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private TimeSpan _estimatedInterval;

    /// <summary>Current best estimate of the per-tick amount (HP / MA points).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private double _estimatedAmount;

    /// <summary>Number of observation samples folded into the running average.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private int _sampleCount;

    /// <summary>Trust-level the UI / automation should place in the estimates.</summary>
    public RegenConfidence Confidence => SampleCount switch
    {
        < 3  => RegenConfidence.Low,
        < 10 => RegenConfidence.Medium,
        _    => RegenConfidence.High,
    };

    public RegenStat(TimeSpan seedInterval)
    {
        SeedInterval = seedInterval;
        EstimatedInterval = seedInterval;
    }

    /// <summary>
    /// Fold a fresh observation into the EWMA. Interval is clamped to
    /// reasonable bounds — wildly long gaps (e.g. user was AFK across
    /// many ticks) are dropped rather than poisoning the running average.
    /// </summary>
    public void AddSample(TimeSpan interval, double amount)
    {
        // Drop samples that don't look like a single tick. The seed
        // interval gives us a "reasonable" zone — accept 0.3×–2.5× of it,
        // dropping outliers from AFK gaps or multi-tick coalesced samples.
        double seedSeconds = SeedInterval.TotalSeconds;
        double observedSeconds = interval.TotalSeconds;
        if (observedSeconds < seedSeconds * 0.3 || observedSeconds > seedSeconds * 2.5) return;

        double alpha = RegenConstants.EwmaAlpha;
        EstimatedInterval = TimeSpan.FromSeconds(
            alpha * observedSeconds + (1 - alpha) * EstimatedInterval.TotalSeconds);
        EstimatedAmount = alpha * amount + (1 - alpha) * EstimatedAmount;
        SampleCount++;
    }

    /// <summary>Reset everything back to the seed values + zero samples.</summary>
    public void Reset()
    {
        EstimatedInterval = SeedInterval;
        EstimatedAmount = 0;
        SampleCount = 0;
    }
}
