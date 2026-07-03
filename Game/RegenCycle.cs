using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Game;

/// <summary>
/// One regen cycle — anchored at a moment in wall-clock time and firing
/// at fixed <see cref="Interval"/> steps thereafter, until stopped. The
/// natural HP / MP cycles (30 s) anchor when the first matching
/// observation arrives and stay running forever; the rest (20 s) and
/// meditate (10 s) bonus cycles anchor when the user enters the
/// matching position and stop when they leave.
/// </summary>
public sealed partial class RegenCycle : ObservableObject
{
    /// <summary>Human-readable tag for diagnostics ("HP natural", "HP rest", "MP medi", etc.).</summary>
    public string Name { get; }

    /// <summary>Cycle length for the active realm — a realm constant, not
    /// refined by observation, but re-seeded when the realm changes via
    /// <see cref="Reseed"/> (see <see cref="RealmRegenProfile"/>).</summary>
    public TimeSpan Interval { get; private set; }

    /// <summary>Running-average per-tick amount (HP / MP delta). Refined via observation.</summary>
    public RegenStat Stat { get; }

    /// <summary>
    /// Wall-clock anchor for the most recently fired tick (real or
    /// lazily-projected). <c>null</c> when the cycle isn't running.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private DateTimeOffset? _anchor;

    /// <summary>True when the cycle is running.</summary>
    public bool IsActive => Anchor.HasValue;

    public RegenCycle(string name, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Name = name;
        Interval = interval;
        Stat = new RegenStat(interval);
    }

    /// <summary>
    /// Re-seed the cycle length for a new realm cadence. Keeps the current
    /// <see cref="Anchor"/> (the countdown re-phases to the new interval from
    /// wherever it sits) and resets the amount estimate, which was learned
    /// under the old realm's cadence.
    /// </summary>
    public void Reseed(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
        Stat.Reseed(interval);
    }

    /// <summary>(Re)start the cycle, anchoring at <paramref name="at"/>.</summary>
    public void Start(DateTimeOffset at) => Anchor = at;

    /// <summary>Stop the cycle. <see cref="GetTimeToNext"/> returns <c>null</c> after.</summary>
    public void Stop() => Anchor = null;

    /// <summary>
    /// Time until the next projected tick boundary. <c>null</c> if the
    /// cycle isn't running. Lazily rolls the anchor forward in exact
    /// <see cref="Interval"/> steps so the projection stays phase-locked
    /// with the original observation even through silent ticks (max HP
    /// → no observable delta) and system sleep gaps.
    /// </summary>
    public TimeSpan? GetTimeToNext(DateTimeOffset now)
    {
        if (Anchor is not { } a) return null;
        while (now - a >= Interval) a += Interval;
        Anchor = a;
        return a + Interval - now;
    }

    /// <summary>
    /// Record an observed tick at <paramref name="at"/> with the given
    /// amount. Anchors the cycle if not yet started, advances the EWMA
    /// for the per-tick amount.
    /// </summary>
    public void RecordObservation(DateTimeOffset at, double amount)
    {
        TimeSpan interval = Anchor is { } prev ? at - prev : Interval;
        Anchor = at;
        Stat.AddSample(interval, amount);
    }
}
