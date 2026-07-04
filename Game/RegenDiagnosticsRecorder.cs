using FujinTerm.Services;

namespace FujinTerm.Game;

// Debug-channel instrument that traces every observed HP / MA regen uptick
// to the program log — the gap since the previous tick of the same stream,
// the amount gained, the player's position, and the running total. Emission
// is gated at LogService.Debug, so it stays silent unless the Log pane's
// Debug-diagnostics toggle is on.
//
// Motivation: some realms (Paradigm is the known case) vary the per-tick
// amount within a cycle instead of the interval — e.g. splitting a natural /
// rest tick into thirds where the first ticks pay a fraction and a later one
// pays the balance. A single editable interval can't capture that shape.
// Standing still (natural) or resting / meditating with this on produces a
// raw tick-by-tick trace the amount pattern can be reverse-engineered from
// before it's modelled.
//
// This is the first producer on the otherwise-empty Debug channel. It reads
// live values off PlayerState at event time — the tracker fires after the
// state property has already advanced, so the reported total is post-tick.
public sealed class RegenDiagnosticsRecorder : IDisposable
{
    private const string Source = "Regen";

    private readonly RegenTracker _tracker;
    private readonly PlayerState _state;
    private readonly LogService _log;
    private bool _disposed;

    public RegenDiagnosticsRecorder(RegenTracker tracker, PlayerState state, LogService log)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        _tracker = tracker;
        _state = state;
        _log = log;
        _tracker.HpTickObserved += OnHpTick;
        _tracker.MaTickObserved += OnMaTick;
    }

    private void OnHpTick(RegenSample sample)
    {
        // Guard before building the interpolated line so the disabled path
        // stays allocation-free (per LogService's Debug-channel contract).
        if (!_log.IsDebugEnabled) return;
        _log.Debug(Source, Format("HP", sample, _state.Hp, _state.MaxHp));
    }

    private void OnMaTick(RegenSample sample)
    {
        if (!_log.IsDebugEnabled) return;
        _log.Debug(Source, Format("MP", sample, _state.Ma, _state.MaxMa));
    }

    private static string Format(string stream, RegenSample sample, int current, int max)
    {
        string gap = sample.IntervalSinceLast > TimeSpan.Zero
            ? $"after {sample.IntervalSinceLast.TotalSeconds:0.0}s"
            : "first";
        int previous = current - sample.Delta;
        return $"{stream} +{sample.Delta} {gap} [{Describe(sample.Position)}] {previous}→{current}/{max}";
    }

    private static string Describe(PlayerPosition position) => position switch
    {
        PlayerPosition.Resting    => "resting",
        PlayerPosition.Meditating => "meditating",
        _                         => "standing",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.HpTickObserved -= OnHpTick;
        _tracker.MaTickObserved -= OnMaTick;
    }
}
