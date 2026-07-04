using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Bridges inbound @wait / .@held signals from other party members onto the
// MovementCoordinator.PartyWaitGate. PartyEssentialHandlers records each member
// asking us to hold (its WaitingMembers set) and raises PauseGateChanged on the
// 0↔1 transition; while at least one member is waiting this asserts the gate so
// the active movement engine (loop / Auto-Lair / walk-to) holds. The @wait is a
// real pause flag with two release paths: the same member sends @ok (cleared in
// PartyEssentialHandlers.OnOk), OR the leader's wait timer expires here — after
// WaitWindow with no @ok we give up and force-release so a dropped / AFK member
// can't strand the party forever. Without this bridge the WaitingMembers set
// only drove the PartyWindow's per-row WAIT chip and the @wait never actually
// paused our own automation.
//
// The leader-side opt-out (PartySettings.IgnoreWaitWhenLeading) is honoured
// upstream in PartyEssentialHandlers.NotePause, which drops a follower's @wait
// before it reaches WaitingMembers when we're leading and the user opted out —
// so IsPaused already reflects that choice and this bridge needs no settings
// read of its own beyond the timer duration.
//
// Read-only on party state (mirrors PartyFollowerMovementGate / PartyVitalsWatcher):
// it only subscribes to PartyEssentialHandlers.PauseGateChanged and never writes
// party state directly. Because it rides the shared MovementCoordinator it
// composes with every other pause source — clearing this gate leaves movement
// held if anything else (combat, health, follower, user pause) is still asserting.
public sealed class PartyWaitMovementGate : IDisposable
{
    // Identifier surfaced in MovementCoordinator.History when the gate flips.
    public const string AsserterName = "PartyWaitMovementGate";

    private readonly PartyEssentialHandlers _essentials;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _gateAsserted;

    // Overridable clock so tests drive the wait-timer deterministically.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Leader-side cap on how long a single @wait holds our movement before we
    // give up and resume (Settings → Party "If leading, wait only", mirrored
    // from PartySettings.IfLeadingWaitTotalSec by AppServices). Zero or negative
    // disables the timeout — the wait then persists until every waiting member
    // sends @ok. Measured from the moment the wait state began (0→1), not reset
    // by later @waits: "wait only N seconds" total.
    public TimeSpan WaitWindow { get; set; } = TimeSpan.FromSeconds(90);

    private DateTime _waitStarted;
    private Avalonia.Threading.DispatcherTimer? _timer;

    public PartyWaitMovementGate(
        PartyEssentialHandlers essentials,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(essentials);
        ArgumentNullException.ThrowIfNull(coordinator);
        _essentials = essentials;
        _coordinator = coordinator;
        _log = log;

        _essentials.PauseGateChanged += OnPauseGateChanged;
        Evaluate();
    }

    private void OnPauseGateChanged(bool _) => Evaluate();

    // Assert / clear PartyWaitGate from the inbound-@wait set. Idempotent — only
    // touches the coordinator when the held state actually flips.
    public void Evaluate()
    {
        bool shouldHold = _essentials.IsPaused;
        if (shouldHold == _gateAsserted) return;
        _gateAsserted = shouldHold;

        if (shouldHold)
        {
            _waitStarted = NowProvider();
            string who = string.Join(", ", _essentials.WaitingMembers);
            string reason = who.Length > 0 ? $"members={who}" : "party @wait";
            _coordinator.AssertGate(MovementCoordinator.PartyWaitGate, AsserterName, reason);
            _log?.Info("Party", "Holding — a party member asked us to @wait.");
            StartTimer();
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.PartyWaitGate, AsserterName, "all @ok");
            _log?.Info("Party", "Party @wait cleared — resuming.");
            StopTimer();
        }
    }

    // Lazily spin up the expiry tick. 1 s cadence — the timeout is
    // second-resolution. Only runs while a wait is actually held.
    private void StartTimer()
    {
        if (WaitWindow <= TimeSpan.Zero) return;
        if (_timer is not null) return;
        _timer = new Avalonia.Threading.DispatcherTimer(
            interval: TimeSpan.FromSeconds(1),
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => Tick());
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    // Test seam — runs one expiry check without a real timer.
    internal void TickForTests() => Tick();

    private void Tick()
    {
        if (!_gateAsserted || WaitWindow <= TimeSpan.Zero) { StopTimer(); return; }
        if (NowProvider() - _waitStarted < WaitWindow) return;
        _log?.Info("Party",
            $"Party @wait timer expired after {WaitWindow.TotalSeconds:0}s — resuming.");
        // Clears WaitingMembers + roster chips and fires PauseGateChanged(false),
        // which routes back through Evaluate to clear the gate and stop the timer.
        _essentials.ClearAllWaits();
    }

    public void Dispose()
    {
        _essentials.PauseGateChanged -= OnPauseGateChanged;
        StopTimer();
    }
}
