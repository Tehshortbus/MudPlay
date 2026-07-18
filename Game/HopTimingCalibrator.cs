using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Debug instrumentation that records the wall-clock duration between "movement
// command sent" and "next room display Confirmed", tagged with the player's
// current EncumbranceLevel. The point is to give the user a stream of real
// per-hop times during a regular session so the Settings → Auto-Lair tab's
// per-encumbrance defaults can be calibrated against in-game truth instead of
// guessed.
//
// Data source: RoomTracker.StateChanged. A move command pushes the tracker from
// Confirmed → Pending; the arrival observation pushes it back from Pending →
// Confirmed. The timestamps on those two transitions bracket the hop. Pipelined
// moves (multiple sends before any reply) collapse into a single timing
// measurement on the LAST send — that's noisy but the user will typically
// calibrate by typing single moves anyway.
//
// Off by default. Toggle on via the Program Log window's "Hop timing" checkbox
// (LogDiagnosticState.HopTiming); logs at Info severity so the Log pane's
// default filters surface it without DBG being checked.
public sealed class HopTimingCalibrator : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly PlayerState _state;
    private readonly LogService _log;

    private DateTimeOffset? _lastSentAt;
    private RoomKey? _lastSentFrom;

    // When true, every hop gets one Info log line. Flip via the Settings tab.
    // Cheap to leave off — the calibrator stays subscribed but the handler
    // returns immediately.
    public bool Enabled { get; set; }

    public HopTimingCalibrator(RoomTracker tracker, PlayerState state, LogService log)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        _tracker = tracker;
        _state = state;
        _log = log;
        _tracker.StateChanged += OnTransition;
    }

    public void Dispose() => _tracker.StateChanged -= OnTransition;

    private void OnTransition(RoomTransition t)
    {
        // Confirmed → Pending: move was sent. Stamp the source +
        // moment so the matching Pending → Confirmed can compute the
        // delta.
        if (t.PreviousConfidence == RoomConfidence.Confirmed
            && t.NewConfidence == RoomConfidence.Pending)
        {
            _lastSentAt = t.At;
            _lastSentFrom = t.PreviousRoom?.Key;
            return;
        }

        // Pending → Confirmed: hop landed.
        if (t.NewConfidence == RoomConfidence.Confirmed
            && t.PreviousConfidence == RoomConfidence.Pending
            && _lastSentAt is { } sentAt)
        {
            TimeSpan elapsed = t.At - sentAt;
            _lastSentAt = null;

            if (!Enabled) return;
            if (elapsed < TimeSpan.Zero) return;       // clock skew guard
            if (elapsed > TimeSpan.FromSeconds(30)) return; // outliers (rest mid-walk, fights, etc.)

            string from = _lastSentFrom is { } f ? FormatRoom(f) : "?";
            string to   = t.NewRoom is { } n ? FormatRoom(n.Key, n.Name) : "?";
            // Log both the numeric level and its word (e.g. "2/Light") so the
            // trace reads at a glance and still sorts by severity band.
            _log.Info("HopCalibration",
                $"{elapsed.TotalSeconds:F2}s @ encumbrance={(int)_state.Encumbrance}/{_state.Encumbrance} ({from} → {to})");
        }
    }

    private string FormatRoom(RoomKey key, string? name = null)
        => name is { Length: > 0 } ? $"{key} '{name}'" : key.ToString();
}
