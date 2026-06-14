using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Always-alive, headless control surface over the three movement
/// engines (<see cref="AutoWalkManager"/>, <see cref="LoopRunner"/>,
/// <see cref="AutoLairManager"/>) and their shared
/// <see cref="MovementCoordinator"/>. Exposes a single coalesced
/// run-state (<see cref="State"/>) plus Pause / Resume / Stop actions
/// that pick the right engine automatically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The toolbar's Start / Pause / Stop buttons need
/// an engine-control target that outlives the Navigation window —
/// <c>NavigationViewModel</c> is window-scoped (created on open, disposed
/// on close), so the toolbar can't delegate to it. This controller lives
/// in <c>AppServices</c> for the whole app lifetime.
/// </para>
/// <para>
/// <b>Two-way sync is free.</b> Both this controller and
/// <c>NavigationViewModel</c> act on the same engine primitives and the
/// same <see cref="MovementCoordinator"/> gate, and both subscribe to the
/// engines' events. Whoever acts (toolbar or nav window), the engines
/// fire their events, every subscriber recomputes, and the two surfaces
/// stay in lock-step. No direct controller↔window wiring needed.
/// </para>
/// <para>
/// <b>Pause routing.</b> Auto-Lair owns its own pause (it halts internal
/// scheduler timers as well as gating the walker), so we call
/// <see cref="AutoLairManager.Pause"/> / <see cref="AutoLairManager.Resume"/>
/// for it. The walker and loop runner both pause purely via the
/// <see cref="MovementCoordinator.UserGate"/>, so for those we assert /
/// clear that gate directly.
/// </para>
/// </remarks>
public sealed class MovementController : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _autoLair;
    private readonly MovementCoordinator _coordinator;
    private bool _disposed;

    /// <summary>Fires whenever <see cref="State"/> may have changed.</summary>
    public event Action? StateChanged;

    public MovementController(
        AutoWalkManager walker,
        LoopRunner loops,
        AutoLairManager autoLair,
        MovementCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        _walker = walker;
        _loops = loops;
        _autoLair = autoLair;
        _coordinator = coordinator;

        _walker.Event += OnWalkerEvent;
        _loops.Event += OnLoopEvent;
        _autoLair.ActiveChanged += OnAutoLairBool;
        _autoLair.PausedChanged += OnAutoLairBool;
        _coordinator.PauseStateChanged += OnCoordinatorPauseChanged;
    }

    /// <summary>
    /// Coalesced run-state across all three engines. Priority mirrors
    /// <c>NavigationViewModel.RefreshEngineActionLabel</c>: Auto-Lair
    /// (drives the walker internally) → Loop → Walker → Idle.
    /// </summary>
    public MovementEngineState State
    {
        get
        {
            if (_autoLair.IsActive)
                return _autoLair.IsPaused ? MovementEngineState.Paused : MovementEngineState.Running;
            if (_loops.State == LoopState.Paused) return MovementEngineState.Paused;
            if (_loops.State is LoopState.Running or LoopState.Approaching)
                return MovementEngineState.Running;
            if (_walker.State == WalkState.Paused) return MovementEngineState.Paused;
            if (_walker.State == WalkState.Walking) return MovementEngineState.Running;
            return MovementEngineState.Idle;
        }
    }

    /// <summary>True when no engine is driving — toolbar shows Start.</summary>
    public bool IsIdle => State == MovementEngineState.Idle;

    /// <summary>True when an engine is driving (running or paused).</summary>
    public bool IsActive => State != MovementEngineState.Idle;

    /// <summary>True when the active engine is paused.</summary>
    public bool IsPaused => State == MovementEngineState.Paused;

    /// <summary>
    /// Suspend the active engine without tearing it down. Auto-Lair
    /// pauses itself (also halting its scheduler); walker + loop pause
    /// via the shared user gate. No-op when idle or already paused.
    /// </summary>
    public void Pause()
    {
        if (State != MovementEngineState.Running) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Pause();
            return;
        }
        _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    /// <summary>Inverse of <see cref="Pause"/>. No-op when not paused.</summary>
    public void Resume()
    {
        if (State != MovementEngineState.Paused) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Resume();
            return;
        }
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    /// <summary>
    /// Convenience: pause when running, resume when paused. Backs the
    /// single toolbar Pause/Resume button.
    /// </summary>
    public void TogglePause()
    {
        if (IsPaused) Resume();
        else if (State == MovementEngineState.Running) Pause();
    }

    /// <summary>
    /// Fully back out of whichever engine is running — same intent as the
    /// per-mode Stop buttons in the Navigation window. Clears the user
    /// gate afterwards so a stale pause can't strand the next run.
    /// </summary>
    public void Stop()
    {
        if (_autoLair.IsActive) _autoLair.Stop("user stop from toolbar");
        if (_loops.State != LoopState.Idle) _loops.Stop("user stop from toolbar");
        if (_walker.State is WalkState.Walking or WalkState.Paused)
            _walker.Stop("user stop from toolbar");
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    private void OnWalkerEvent(WalkEvent _) => StateChanged?.Invoke();
    private void OnLoopEvent(LoopEvent _) => StateChanged?.Invoke();
    private void OnAutoLairBool(bool _) => StateChanged?.Invoke();
    private void OnCoordinatorPauseChanged(bool _) => StateChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkerEvent;
        _loops.Event -= OnLoopEvent;
        _autoLair.ActiveChanged -= OnAutoLairBool;
        _autoLair.PausedChanged -= OnAutoLairBool;
        _coordinator.PauseStateChanged -= OnCoordinatorPauseChanged;
    }
}

/// <summary>Coalesced run-state across the movement engines.</summary>
public enum MovementEngineState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
}
