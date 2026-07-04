using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Game;

// One axis of regen tracking — e.g. "HP regen while standing", "MA regen
// while meditating". Holds the seed interval, the running EWMA estimates for
// interval + amount per tick, and the observed sample count.
public sealed partial class RegenStat : ObservableObject
{
    // Seed interval used until observation refines the estimate. Re-set on a
    // realm switch via Reseed.
    public TimeSpan SeedInterval { get; private set; }

    // Current best estimate of the regen tick interval (seconds-precision).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private TimeSpan _estimatedInterval;

    // Current best estimate of the per-tick amount (HP / MA points).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private double _estimatedAmount;

    // Number of observation samples folded into the running average.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Confidence))]
    private int _sampleCount;

    // Trust-level the UI / automation should place in the estimates.
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

    // Fold a fresh observation into the running average. Only the per-tick
    // AMOUNT gets refined — the cycle interval is a realm constant (once
    // observed, the server cycle keeps firing every N seconds like
    // clockwork), so we keep EstimatedInterval locked to SeedInterval.
    //
    // Cycle detection: a sample's interval might cover multiple ticks (e.g.
    // one tick landed silently while HP was at max). We divide the observed
    // amount by the rounded cycle count to get an estimated per-tick amount.
    // Samples that don't look like 1–3 cycles are dropped — too much could be
    // a position change / AFK / activity artefact rather than a normal tick.
    public void AddSample(TimeSpan interval, double amount)
    {
        double seedSeconds = SeedInterval.TotalSeconds;
        double observedSeconds = interval.TotalSeconds;
        if (seedSeconds <= 0) return;

        int cycles = (int)Math.Round(observedSeconds / seedSeconds);
        if (cycles < 1 || cycles > 3) return;

        double perTickAmount = amount / cycles;
        double alpha = RegenConstants.EwmaAlpha;
        EstimatedAmount = alpha * perTickAmount + (1 - alpha) * EstimatedAmount;
        SampleCount++;
    }

    // Reset the amount estimate + sample count back to seed.
    public void Reset()
    {
        EstimatedAmount = 0;
        SampleCount = 0;
    }

    // Re-seed the interval for a new realm cadence (see RealmRegenProfile)
    // and clear the amount estimate — the old per-tick average was learned
    // under a different divisor / cadence, so it's stale. AddSample's
    // cycle-count math keys off SeedInterval, so it has to move with the realm.
    public void Reseed(TimeSpan seedInterval)
    {
        SeedInterval = seedInterval;
        EstimatedInterval = seedInterval;
        EstimatedAmount = 0;
        SampleCount = 0;
    }
}
