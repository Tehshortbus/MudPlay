using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Holds the movement stack for a brief settle window after each dead-reckoned
// advance into a too-dark room, giving the game engine a beat to reveal the
// room's occupant before the loop fires the next move.
//
// The race this closes: a too-dark room prints no name / no exits / no "Also
// here:" line, so RoomTracker CONFIRMS the move by graph dead-reckoning
// (NoteDarkRoomEntered) and fires StateChanged SYNCHRONOUSLY. The active
// movement engine advances its step on that same synchronous edge and sends the
// next move — all BEFORE the mob's reveal (its "strides in" arrival or first
// dark-cyan attack line) lands a beat later. The loop marches past the fight and
// the phantom monster injects into a room we've already left.
//
// The fix is timing, not roster surgery: on each confirmed dark advance we
// assert MovementCoordinator.DarkRoomSettleGate, which flips the engines to
// Paused before their synchronous SendNextStep runs (this watcher subscribes to
// StateChanged BEFORE the engines — see AppServices construction order). During
// the window one of two things happens: a hostile reveals and CombatGate asserts
// to take over the hold (the loop resumes into the fight via its
// arrived-while-paused guard once the room clears), OR the room really is empty
// and the settle timer clears the gate so the loop steps on. Either way we react
// to the room we're standing in instead of the one we were about to leave.
//
// The gate lives in the engine-wait tier (like AbandonedCombatGate), not the
// manual-override tier, so it never flips the toolbar Start/Pause/Stop button.
public sealed class DarkRoomMovementSettle : IDisposable
{
    public const string LogCategory = "DarkRoomSettle";

    // A dark reveal — the pursuing monster's "strides in" arrival or its first
    // dark-cyan attack line — lands a beat after entry, up to about a game round.
    // The window must OUTLAST that beat: if it expires first, the loop takes its
    // next step before the Combat gate can catch the monster, so we walk past the
    // fight and our attack targets a mob that's no longer here ("Your command had
    // no effect") while it chases us onward. 1s holds long enough for the reveal
    // without stalling an empty corridor for long (a shorter 900ms let pursuers
    // land after we'd already stepped on). Each fresh dark advance restarts the
    // window, so a run of empty dark rooms settles one short beat per room, not a
    // single long freeze.
    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromSeconds(1);

    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly TimeSpan _window;

    // When set, the settle window is scheduled through this callback instead of a
    // real DispatcherTimer (which needs a live Avalonia dispatcher).
    private readonly Action<TimeSpan, Action>? _scheduleOverride;

    private DispatcherTimer? _timer;
    private bool _asserted;
    private bool _disposed;

    // Reveal-lag instrumentation. _settleStartedAt stamps the latest dark
    // advance; _revealObservedAt records when the Combat gate took over the
    // window (a hostile surfaced). On elapse we log which outcome happened and
    // the lag — so a long capture's Gate log shows the reveal-lag distribution
    // (is 1s generous or tight?) and how much a truly-empty dark room costs.
    private DateTimeOffset _settleStartedAt;
    private DateTimeOffset? _revealObservedAt;

    public DarkRoomMovementSettle(
        RoomTracker tracker,
        MovementCoordinator coordinator,
        LogService? log = null,
        TimeSpan? window = null,
        Action<TimeSpan, Action>? scheduleOverride = null)
    {
        _tracker = tracker;
        _coordinator = coordinator;
        _log = log;
        _window = window ?? DefaultSettleWindow;
        _scheduleOverride = scheduleOverride;
        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.GatesChanged += OnGatesChanged;
    }

    // Watch for the Combat gate taking over an active settle window — the moment
    // a pursuing hostile reveals. Recording it (once per window) lets OnSettleElapsed
    // report the reveal-lag instead of mislabelling the room empty. Pure
    // instrumentation: it changes no gate state, only measures.
    private void OnGatesChanged()
    {
        if (_disposed) return;
        if (!_asserted) return;
        if (_revealObservedAt is not null) return;
        if (!_coordinator.IsGateAsserted(MovementCoordinator.CombatGate)) return;
        _revealObservedAt = DateTimeOffset.Now;
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (_disposed) return;

        // Only dark-room advances carry the reveal-lag we're guarding against. A
        // lit-room arrival clears IsInDarkRoom before firing (RoomTracker), so a
        // normal move never trips this.
        if (!_tracker.IsInDarkRoom) return;

        // Only a true location change is a move worth settling — mirror the
        // occupant classifier's guard so confidence-only flips and same-room
        // re-renders don't fire the hold.
        if (transition.PreviousRoom is null) return;
        if (transition.NewRoom is null) return;
        if (ReferenceEquals(transition.PreviousRoom, transition.NewRoom)) return;
        if (transition.PreviousRoom.Key.Equals(transition.NewRoom.Key)) return;

        // Each fresh dark advance restarts the window, so re-stamp the start and
        // clear the prior window's reveal mark before measuring this one.
        _settleStartedAt = DateTimeOffset.Now;
        _revealObservedAt = null;

        if (!_asserted)
        {
            _asserted = true;
            _coordinator.AssertGate(
                MovementCoordinator.DarkRoomSettleGate, LogCategory,
                $"dark advance to {transition.NewRoom.Key} — settling {_window.TotalMilliseconds:F0}ms for reveal");
        }

        RestartTimer();
    }

    private void RestartTimer()
    {
        if (_scheduleOverride is not null)
        {
            _scheduleOverride(_window, OnSettleElapsed);
            return;
        }

        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Interval = _window;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        OnSettleElapsed();
    }

    private void OnSettleElapsed()
    {
        if (!_asserted) return;
        _asserted = false;

        // Report the outcome so a capture shows reveal-lag vs. empty-room cost:
        // "reveal engaged Δms in" means a pursuer surfaced and Combat holds the
        // loop from here; "empty after Nms" means the whole window was spent on a
        // room nothing followed us into (the overhead we pay per empty dark room).
        double heldMs = (DateTimeOffset.Now - _settleStartedAt).TotalMilliseconds;
        string reason = _revealObservedAt is { } revealedAt
            ? $"settle elapsed — reveal engaged {(revealedAt - _settleStartedAt).TotalMilliseconds:F0}ms in, Combat holds"
            : $"settle elapsed empty after {heldMs:F0}ms — no reveal";
        _coordinator.ClearGate(
            MovementCoordinator.DarkRoomSettleGate, LogCategory, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _coordinator.GatesChanged -= OnGatesChanged;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
            _timer = null;
        }
        if (_asserted)
        {
            _asserted = false;
            _coordinator.ClearGate(
                MovementCoordinator.DarkRoomSettleGate, LogCategory, "disposed");
        }
    }
}
