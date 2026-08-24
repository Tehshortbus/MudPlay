using MudPlay.Services;

namespace MudPlay.Game.Map;

// Always-alive, headless control surface over the three movement engines
// (AutoWalkManager, LoopRunner, AutoLairManager) and their shared
// MovementCoordinator. Exposes a single coalesced run-state (State) plus
// Pause / Resume / Stop actions that pick the right engine automatically.
//
// Why it exists. The toolbar's Start / Pause / Stop buttons need an
// engine-control target that outlives the Navigation window —
// NavigationViewModel is window-scoped (created on open, disposed on close),
// so the toolbar can't delegate to it. This controller lives in AppServices
// for the whole app lifetime.
//
// Two-way sync is free. Both this controller and NavigationViewModel act on
// the same engine primitives and the same MovementCoordinator gate, and both
// subscribe to the engines' events. Whoever acts (toolbar or nav window), the
// engines fire their events, every subscriber recomputes, and the two
// surfaces stay in lock-step. No direct controller↔window wiring needed.
//
// Pause routing. Auto-Lair owns its own pause (it halts internal scheduler
// timers as well as gating the walker), so we call its Pause / Resume. The
// walker and loop runner both pause purely via the UserGate, so for those we
// assert / clear that gate directly.
public sealed class MovementController : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _autoLair;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _disposed;

    // True while the Auto-All kill switch paused a running engine — so a later
    // release only resumes navigation WE suspended, never a nav the user had
    // already paused (or a fresh one they started while the switch was engaged).
    private bool _autoAllSuspended;

    // Surfaced in the bug report so a "nav didn't resume after Auto-All" report
    // shows whether the switch is holding the pause (vs the user's own).
    public bool IsAutoAllSuspended => _autoAllSuspended;

    // Fires whenever State may have changed.
    public event Action? StateChanged;

    public MovementController(
        AutoWalkManager walker,
        LoopRunner loops,
        AutoLairManager autoLair,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        _walker = walker;
        _loops = loops;
        _autoLair = autoLair;
        _coordinator = coordinator;
        _log = log;

        _walker.Event += OnWalkerEvent;
        _loops.Event += OnLoopEvent;
        _autoLair.ActiveChanged += OnAutoLairBool;
        _autoLair.PausedChanged += OnAutoLairBool;
        // GatesChanged, not PauseStateChanged: the coarse signal stays silent
        // when a gate stacks on an already-paused engine (e.g. the user pressing
        // Pause during a fight asserts UserGate while CombatGate holds), so
        // IsUserPaused flips true with no notification and the toolbar's
        // Resume button never re-enables. The fine-grained signal fires on
        // every assert/clear and is a superset of PauseStateChanged.
        _coordinator.GatesChanged += OnCoordinatorGatesChanged;
    }

    // Coalesced run-state across all three engines. Priority mirrors
    // NavigationViewModel.RefreshEngineActionKind: Auto-Lair (drives the
    // walker internally) → Loop → Walker → Idle.
    public MovementEngineState State
    {
        get
        {
            if (_autoLair.IsActive)
                return _autoLair.IsPaused ? MovementEngineState.Paused : MovementEngineState.Running;
            if (_loops.State == LoopState.Paused) return MovementEngineState.Paused;
            if (_loops.State is LoopState.Running or LoopState.Approaching
                             or LoopState.Recovering)
                return MovementEngineState.Running;
            if (_walker.State == WalkState.Paused) return MovementEngineState.Paused;
            if (_walker.State == WalkState.Walking) return MovementEngineState.Running;
            return MovementEngineState.Idle;
        }
    }

    // True when no engine is driving — toolbar shows Start.
    public bool IsIdle => State == MovementEngineState.Idle;

    // True when an engine is driving (running or paused).
    public bool IsActive => State != MovementEngineState.Idle;

    // True when the active engine is paused for ANY reason — a user pause OR an
    // engine wait (combat, resting, abandoned-combat, party hold, …). This is
    // the coalesced run-state, not the toolbar's pause signal.
    public bool IsPaused => State == MovementEngineState.Paused;

    // True only when the pause is the USER's manual override — the highest-level
    // pause tier. Engine waits (Combat / HealthRecovery / AbandonedCombat / …)
    // are excluded: those hold the engines transparently and auto-clear, so the
    // toolbar's Start/Pause buttons must NOT flip on them (that was the bug —
    // every mid-walk fight showed "Resume"). Auto-Lair owns its own user pause
    // flag; walker + loop user-pause ride the UserGate (also asserted by the
    // death halt and remote @pause, both of which are legitimately "resume is
    // now the user's call"). This is what the toolbar keys off.
    public bool IsUserPaused =>
        _autoLair.IsActive
            ? _autoLair.IsPaused
            : _coordinator.AssertedGates.Contains(MovementCoordinator.UserGate);

    // Suspend the active engine as a user override. This is the highest-level
    // pause: it stacks on top of any engine wait, so a walk paused mid-combat
    // stays paused after the fight clears until the user resumes. Auto-Lair
    // pauses itself (also halting its scheduler); walker + loop pause via the
    // shared user gate. No-op when idle or already user-paused.
    public void Pause()
    {
        if (IsIdle || IsUserPaused) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Pause();
            return;
        }
        _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Inverse of Pause — lifts the user override only. Any engine wait still
    // asserted (an active fight, a rest) keeps the engine paused on its own
    // gate; we just clear the user's hold. No-op when not user-paused.
    public void Resume()
    {
        if (!IsUserPaused) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Resume();
            return;
        }
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Toggle the user override. Backs the single toolbar Pause/Resume button.
    public void TogglePause()
    {
        if (IsUserPaused) Resume();
        else if (IsActive) Pause();
    }

    // Fully back out of whichever engine is running — same intent as the
    // per-mode Stop buttons in the Navigation window. Clears the user gate
    // afterwards so a stale pause can't strand the next run.
    public void Stop()
    {
        if (_autoLair.IsActive) _autoLair.Stop("user stop from toolbar");
        if (_loops.State != LoopState.Idle) _loops.Stop("user stop from toolbar");
        if (_walker.State is WalkState.Walking or WalkState.Paused)
            _walker.Stop("user stop from toolbar");
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
        // Unconditional, not routed through the three guards above: this is
        // the toolbar's full-stop button, and it must disarm
        // PassiveRelocalizer's automation-intent latch even when none of the
        // three engines is currently active — exactly the shape a genuinely
        // Lost tracker with no attached engine is in, which is the whole
        // scenario that latch exists to gate.
        _coordinator.DisengageAutomation();
    }

    // Auto-All kill switch engaged: suspend an in-flight navigation the same way a
    // user Pause does — retaining the loop's step / walk destination / lair target
    // so ReleaseFromAutoAll picks it up exactly where it left off. Remembered only
    // when we actually paused a running, not-already-user-paused engine, so the
    // release can't resume a nav the user paused themselves or one they never had
    // running. No-op (and nothing to resume later) when idle or already paused.
    public void SuspendForAutoAll()
    {
        if (_autoAllSuspended) return;
        _autoAllSuspended = true;
        // Assert the engine-wait AutoAllGate UNCONDITIONALLY — even when idle — so a
        // walk / loop / auto-lair / right-click Queue-walk-to STARTED while Auto-All is
        // off plans but holds (every engine already respects the coordinator), not just
        // one that was already moving. It's not UserGate, so the toolbar Pause face is
        // untouched; ReleaseFromAutoAll clears it and the engines auto-resume.
        _coordinator.AssertGate(MovementCoordinator.AutoAllGate, nameof(MovementController),
            "Auto-All engaged — movement frozen");
        _log?.Info(nameof(MovementController),
            "Auto-All engaged — navigation frozen (resumes when Auto-All is restored).");
    }

    // Auto-All kill switch restored: resume the navigation we suspended, unless the
    // user has since taken it over (manually resumed, stopped, or started a new one)
    // — Resume itself is a no-op when the engine isn't user-paused, so a stale flag
    // can't force an unwanted resume.
    public void ReleaseFromAutoAll()
    {
        if (!_autoAllSuspended) return;
        _autoAllSuspended = false;
        // Drop the gate — the coordinator fires PauseStateChanged(false) when no other
        // gate remains, and the walker / loop resume from where they held. A gate the
        // user asserted themselves (UserGate) survives, so their own pause persists.
        _coordinator.ClearGate(MovementCoordinator.AutoAllGate, nameof(MovementController),
            "Auto-All restored");
        _log?.Info(nameof(MovementController),
            "Auto-All restored — navigation resumed.");
    }

    private void OnWalkerEvent(WalkEvent _) => StateChanged?.Invoke();
    private void OnLoopEvent(LoopEvent _) => StateChanged?.Invoke();
    private void OnAutoLairBool(bool _) => StateChanged?.Invoke();
    private void OnCoordinatorGatesChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkerEvent;
        _loops.Event -= OnLoopEvent;
        _autoLair.ActiveChanged -= OnAutoLairBool;
        _autoLair.PausedChanged -= OnAutoLairBool;
        _coordinator.GatesChanged -= OnCoordinatorGatesChanged;
    }
}

// Coalesced run-state across the movement engines.
public enum MovementEngineState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
}
