using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Game;

// One regen cycle — anchored at a moment in wall-clock time and firing at
// fixed Interval steps thereafter, until stopped. The natural HP / MP cycles
// (30 s) anchor when the first matching observation arrives and stay running
// forever; the rest (20 s) and meditate (10 s) bonus cycles anchor when the
// user enters the matching position and stop when they leave.
public sealed partial class RegenCycle : ObservableObject
{
    // Human-readable tag for diagnostics ("HP natural", "HP rest", "MP medi", etc.).
    public string Name { get; }

    // Cycle length for the active realm — a realm constant, not refined by
    // observation, but re-seeded when the realm changes via Reseed (see
    // RealmRegenProfile).
    public TimeSpan Interval { get; private set; }

    // Running-average per-tick amount (HP / MP delta). Refined via observation.
    public RegenStat Stat { get; }

    // Wall-clock anchor for the most recently fired tick (real or
    // lazily-projected). null when the cycle isn't running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private DateTimeOffset? _anchor;

    // True when the cycle is running.
    public bool IsActive => Anchor.HasValue;

    public RegenCycle(string name, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Name = name;
        Interval = interval;
        Stat = new RegenStat(interval);
    }

    // Re-seed the cycle length for a new realm cadence. Keeps the current
    // Anchor (the countdown re-phases to the new interval from wherever it
    // sits) and resets the amount estimate, which was learned under the old
    // realm's cadence.
    public void Reseed(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
        Stat.Reseed(interval);
    }

    // (Re)start the cycle, anchoring at at.
    public void Start(DateTimeOffset at) => Anchor = at;

    // Stop the cycle. GetTimeToNext returns null after.
    public void Stop() => Anchor = null;

    // Time until the next projected tick boundary. null if the cycle isn't
    // running. Lazily rolls the anchor forward in exact Interval steps so the
    // projection stays phase-locked with the original observation even
    // through silent ticks (max HP → no observable delta) and system sleep gaps.
    public TimeSpan? GetTimeToNext(DateTimeOffset now)
    {
        if (Anchor is not { } a) return null;
        while (now - a >= Interval) a += Interval;
        Anchor = a;
        return a + Interval - now;
    }

    // Record an observed tick at at with the given amount. Anchors the cycle
    // if not yet started, advances the EWMA for the per-tick amount.
    public void RecordObservation(DateTimeOffset at, double amount)
    {
        TimeSpan interval = Anchor is { } prev ? at - prev : Interval;
        Anchor = at;
        Stat.AddSample(interval, amount);
    }
}
