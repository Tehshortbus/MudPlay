using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.Game;

// One regen cycle — anchored at a moment in wall-clock time and firing at
// fixed Interval steps thereafter, until stopped. The natural HP / MP cycles
// (30 s) anchor when the first matching observation arrives and stay running
// forever; the rest and meditate bonus cycles anchor when the
// user enters the matching position and stop when they leave.
public sealed partial class RegenCycle : ObservableObject
{
    // Human-readable tag for diagnostics ("HP natural", "HP rest", "MP medi", etc.).
    public string Name { get; }

    // Cycle length for the active realm — a realm constant, not refined by
    // observation, but re-seeded when the realm changes via Reseed (see
    // RealmRegenProfile).
    public TimeSpan Interval { get; private set; }

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
    }

    // Re-seed the cycle length for a new realm cadence. Keeps the current
    // Anchor (the countdown re-phases to the new interval from wherever it
    // sits).
    public void Reseed(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
    }

    // (Re)start the cycle, anchoring at at: a tick seen, or where its count begins.
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

    // Slide the anchor onto a grid of `step` through gridPoint, when it is already
    // within tolerance of one. A regen pass falls on a combat round, so a round seen
    // on the wire says exactly where the next tick will be; between sightings the
    // anchor only coasts on nominal seconds.
    public void AlignTo(DateTimeOffset gridPoint, TimeSpan step, TimeSpan tolerance)
    {
        if (Anchor is not { } a || step <= TimeSpan.Zero) return;
        double stepMs = step.TotalMilliseconds;
        double off = (a - gridPoint).TotalMilliseconds % stepMs;
        if (off > stepMs / 2) off -= stepMs;
        else if (off < -stepMs / 2) off += stepMs;
        if (off != 0 && Math.Abs(off) <= tolerance.TotalMilliseconds)
            Anchor = a - TimeSpan.FromMilliseconds(off);
    }
}
