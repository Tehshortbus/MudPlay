using MudPlay.Services;

namespace MudPlay.Game.Map;

// Always-alive, headless control surface over the three movement engines
// (AutoWalkManager, LoopRunner, AutoLairManager), the puzzle solvers the walker
// hands off to, and their shared MovementCoordinator. Exposes a single coalesced run-state (State) plus
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

    // ----- Errand held by Stop -----------------------------------------
    // A money or training errand (a train trip, a stash transfer, a bank or sell
    // trip) drives the walker for many legs, and a Stop used to end it where it
    // stood: the coin already fetched stayed in the purse, the scrolls unbought.
    // Stop now holds it instead — the user gate, the same hold Pause uses, so Resume
    // carries it on — and the next walk, loop or Auto-Lair the user starts asks
    // whether to finish the errand first (user, 2026-10-02).
    private Func<string?>? _activeErrand;
    private Action<string>? _abandonErrand;
    private Func<string, System.Threading.Tasks.Task<bool>>? _askResumeErrand;
    private Action<Action> _post = static run => run();
    private Action? _afterErrand;
    // The start the open "Resume first?" question is being asked for.
    private Action? _awaitingAnswer;
    // Bumped when the queued run is dropped, so one already posted stands down.
    private int _queuedRun;
    // We asserted the user gate for the errand (it wasn't already paused by hand).
    private bool _gateHeldForErrand;
    private static readonly TimeSpan DoubleStopGuard = TimeSpan.FromSeconds(1);
    private Func<DateTime> _utcNow = static () => DateTime.UtcNow;
    private DateTime _heldAt;

    // The errand Stop is holding, as it reads in a sentence ("the stash transfer").
    public string? SuspendedErrand { get; private set; }

    // Raised when an errand is held or let go.
    public event Action? SuspendedErrandChanged;

    // activeErrand names the suspendable errand under way, or null. abandonErrand
    // ends it for good. askResume shows "Resume <errand> first?" and answers.
    // post defers a call until the current one has unwound: what the user queued
    // must start after the errand's own finishing moves, not in the middle of them.
    public void SetErrandHooks(
        Func<string?> activeErrand, Action<string> abandonErrand,
        Func<string, System.Threading.Tasks.Task<bool>> askResume, Action<Action>? post = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(activeErrand);
        ArgumentNullException.ThrowIfNull(abandonErrand);
        ArgumentNullException.ThrowIfNull(askResume);
        _activeErrand = activeErrand;
        _abandonErrand = abandonErrand;
        _askResumeErrand = askResume;
        if (post is not null) _post = post;
        if (utcNow is not null) _utcNow = utcNow;
    }

    // The user's Stop. The first one with an errand under way holds it and returns
    // true: the caller stops nothing. Stop is the only stop control there is, so a
    // second one while it is still held ends the errand and returns false, and the
    // caller's own stop runs (user, 2026-10-02).
    public bool HoldErrandOnStop()
    {
        if (_activeErrand?.Invoke() is not { } errand) return false;
        if (SuspendedErrand is not null)
        {
            // A double-click on Stop is one decision, not two.
            if (_utcNow() - _heldAt < DoubleStopGuard) return true;
            _log?.Info("Movement", $"Second Stop ended {errand}.");
            _abandonErrand?.Invoke("stopped by the user");
            Stop();
            return false;
        }
        _gateHeldForErrand = !_coordinator.AssertedGates.Contains(MovementCoordinator.UserGate);
        _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovementController), $"Stop held {errand}");
        SuspendedErrand = errand;
        _heldAt = _utcNow();
        _log?.Info("Movement", $"Stop held {errand} — Resume carries it on; Stop again ends it; starting something else asks first.");
        SuspendedErrandChanged?.Invoke();
        return true;
    }

    // The user's Stop, the toolbar's and the Navigation window's alike. The first
    // press with an errand under way only holds it and returns false; otherwise the
    // walk, loop or Auto-Lair, the solves, the errand and any run queued behind it
    // all stop, and it returns true.
    public bool StopByUser(string reason = "user stop from toolbar")
    {
        if (HoldErrandOnStop()) return false;
        Stop(reason);
        return true;
    }

    // A walk, loop or Auto-Lair the user starts. Runs straight away unless Stop is
    // holding an errand; then the user is asked. Yes carries the errand on and
    // starts this once it is done; No ends the errand and starts this now.
    public void StartUserRun(Action start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (SuspendedErrand is null || _activeErrand?.Invoke() is not { } errand)
        {
            LetErrandGo();
            start();
            return;
        }
        // The question is modeless, so a second start can arrive while it stands.
        // One question, and the latest start is the one it answers for.
        bool asking = _awaitingAnswer is not null;
        _awaitingAnswer = start;
        if (!asking) _ = AskThenStartAsync(errand);
    }

    private async System.Threading.Tasks.Task AskThenStartAsync(string errand)
    {
        bool resume = _askResumeErrand is null || await _askResumeErrand(errand);
        if (_awaitingAnswer is not { } start) return;
        _awaitingAnswer = null;
        // The errand may have ended while the question stood.
        if (_activeErrand?.Invoke() is null)
        {
            LetErrandGo();
            start();
            return;
        }
        if (resume)
        {
            _log?.Info("Movement", $"Resuming {errand} first; what was queued starts when it is done.");
            _afterErrand = start;
            ReleaseErrandHold(always: true);
            return;
        }
        _log?.Info("Movement", $"{errand} abandoned for a new run.");
        _afterErrand = null;
        _abandonErrand?.Invoke("the user started something else");
        Stop();
        start();
    }

    // An errand started or ended. Wired to each suspendable errand's state event.
    public void NoteErrandStateChanged()
    {
        if (_activeErrand?.Invoke() is not null) return;
        // Ended while held (finished its last step, or was cancelled elsewhere): a
        // hold we placed for it must not strand the next run.
        if (SuspendedErrand is not null) ReleaseErrandHold(always: false);
        if (_afterErrand is not { } next) return;
        _afterErrand = null;
        int queued = _queuedRun;
        _post(() => { if (queued == _queuedRun) next(); });
    }

    // A full stop from outside (a death, a reset) ends the errand without finishing
    // it. What was queued behind it must not start on its own afterwards.
    public void DropQueuedRun()
    {
        _afterErrand = null;
        _queuedRun++;
    }

    // always: the user asked for the errand to go on, so a pause of their own from
    // before the Stop goes too. Otherwise only the hold Stop placed is lifted.
    private void ReleaseErrandHold(bool always)
    {
        bool lift = always || _gateHeldForErrand;
        LetErrandGo();
        if (lift) _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController), "errand hold released");
    }

    private void LetErrandGo()
    {
        _gateHeldForErrand = false;
        if (SuspendedErrand is null) return;
        SuspendedErrand = null;
        SuspendedErrandChanged?.Invoke();
    }

    // ----- Paused by a typed move --------------------------------------
    // A move the user types pauses navigation on the same hold the Pause button
    // uses, but nobody pressed Pause. Unless the status says what did it, a slipped
    // key reads as a loop that simply stopped, with nothing holding it (report
    // paradigm-20261003-162514). The command that caused the pause, until the pause
    // lifts.
    public string? PausedByTypedMove { get; private set; }

    // Raised when that note is set or dropped.
    public event Action? PausedByTypedMoveChanged;

    public void PauseForTypedMove(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (IsIdle || IsUserPaused) return;
        Pause();
        if (!IsUserPaused) return;
        PausedByTypedMove = command;
        PausedByTypedMoveChanged?.Invoke();
    }

    // A walk reached the room before a boss room marked "stop before entering" and
    // waits there on the user's own gate, so the Play button (Resume) sends it on
    // through. False when nothing is running to pause.
    public bool PauseBeforeBossRoom()
    {
        if (IsIdle) return false;
        if (!IsUserPaused) Pause();
        return IsUserPaused;
    }

    private void ForgetTypedMoveOnceResumed()
    {
        if (PausedByTypedMove is null || IsUserPaused) return;
        PausedByTypedMove = null;
        PausedByTypedMoveChanged?.Invoke();
    }

    // ----- Puzzle solvers ----------------------------------------------
    // The Great Pyramid climb and the asylum maze solve take a walk-to over from the
    // walker, which goes idle for them. Without these the toolbar read a solve as
    // nothing running: Stop and Pause did nothing, and gear, party and combat rules
    // keyed to "navigation is running" stood down for the whole of it.
    private readonly List<(Func<bool> Active, Func<bool> Held, Action<string> Stop)> _solvers = new();

    public void AddSolver(Func<bool> active, Func<bool> held, Action<string> stop)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(stop);
        _solvers.Add((active, held, stop));
    }

    // ----- Starts held elsewhere ---------------------------------------
    // What would start a run by itself later and is no engine here to stop: the
    // loop, Auto-Lair or walk a fight with a player stood down to put back at its
    // end, the default task waiting out the party-rebuild window. Every engine is
    // idle while one is held, so a stop that looks at the engines finds nothing.
    // A change of game data asks each (StopForGameDataChange): what it holds is of
    // the old data's rooms, and is let go and named with the rest.
    //
    // held answers what it holds as it reads in the notice ("loop 'Ring', held for
    // a fight,"), or null for nothing; drop lets it go, with the reason.
    private readonly List<(Func<string?> Held, Action<string> Drop)> _heldStarts = new();

    public void AddHeldStart(Func<string?> held, Action<string> drop)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(drop);
        _heldStarts.Add((held, drop));
    }

    // A solver started, ended, or went into or out of a hold.
    public void NoteSolverStateChanged() => StateChanged?.Invoke();

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
            foreach ((Func<bool> active, Func<bool> held, _) in _solvers)
                if (active())
                    return held() ? MovementEngineState.Paused : MovementEngineState.Running;
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

    // Asked as the user resumes a paused run: true means it was refused (the
    // master switch is off) and the pause stays. Lifting it would only leave the
    // run frozen on the switch with nothing said.
    public Func<bool>? RefuseResume { get; set; }

    // Inverse of Pause — lifts the user override only. Any engine wait still
    // asserted (an active fight, a rest) keeps the engine paused on its own
    // gate; we just clear the user's hold. No-op when not user-paused.
    public void Resume()
    {
        if (IsUserPaused && RefuseResume?.Invoke() == true) return;
        LetErrandGo();
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
    // Raised as Stop begins, so a run the engines here don't own (a picked token
    // route between walks) stops with them.
    public event Action? Stopping;

    public void Stop(string reason = "user stop from toolbar")
    {
        LetErrandGo();
        DropQueuedRun();
        Stopping?.Invoke();
        if (_autoLair.IsActive) _autoLair.Stop(reason);
        if (_loops.State != LoopState.Idle) _loops.Stop(reason);
        _loops.DropResumeHeldForMasterSwitch(reason);
        // Idle too: a journey can stand between two of its legs with no walk running
        // (waiting at a giver for the hand-over), and Stop is the end of it.
        _walker.Stop(reason);
        foreach ((Func<bool> active, _, Action<string> stop) in _solvers)
            if (active()) stop(reason);
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Why the stop under way was made, while it is one for a change of game data
    // (StopForGameDataChange); null for any other. Stopping carries no detail, so a
    // subscriber that logs why it stood down reads this while it handles the event.
    public string? GameDataChangeStop { get; private set; }

    // Raised by StopForGameDataChange ahead of the stop, with the reason, for
    // whatever holds a run of the old data's rooms that Stop doesn't end by itself
    // (an event standing in a wait, a party pick-up waiting for its member, a sell
    // tour at a shop). Each subscriber ends its own and adds what it ended to the
    // list, as it reads inside a sentence ("the party pick-up").
    public event Action<string, List<string>>? EndingForGameDataChange;

    // The game data under the engines was read in afresh: another set became the
    // active one, or the active one was read in again (imported again, rewritten in
    // Manage Sets; readInAgain). Everything here walks by room numbers, which under
    // another set are other rooms and after a re-import may be: a loop, Auto-Lair, a
    // walk-to, a puzzle solve or an errand in progress is stopped, the way Stop
    // stops it, and whatever would start one again by itself is dropped: a loop held
    // for a reconnect or for the master switch, a loop loaded to start, a run queued
    // behind an errand, the "Resume first?" answer still owed, the starts held
    // elsewhere (AddHeldStart), and what the subscribers of EndingForGameDataChange
    // hold (owner, 2026-10-11). The stop is
    // made whether or not anything is running, as the Stop button's is. Returns what
    // ended, as the start of the notice ("Loop 'Ring' stopped"), or null when
    // nothing was running or held.
    //
    // because: what brought the change about when the user did not change the game
    // data: a profile closed or loaded whose realm runs on another set. The reason
    // then leads with it, so the log and every cancel say what the user did.
    public string? StopForGameDataChange(string? was, string? now, bool readInAgain = false, string? because = null)
    {
        bool sameSet = string.Equals(was, now, StringComparison.OrdinalIgnoreCase);
        if (sameSet && !readInAgain) return null;
        string reason = sameSet
            ? $"game data '{now ?? "(none)"}' was read in again"
            : $"game data changed from '{was ?? "(none)"}' to '{now ?? "(none)"}'";
        if (because is not null) reason = $"{because}, and with it {reason}";

        List<string> stopped = new();
        // The topmost engine is the run: a loop or Auto-Lair drives the walker.
        if (_autoLair.IsActive) stopped.Add("Auto-Lair");
        else if (_loops.State != LoopState.Idle) stopped.Add($"loop '{_loops.CurrentLoop?.Name ?? "?"}'");
        else if (_walker.State != WalkState.Idle || _walker.Journey is not null)
            stopped.Add((_walker.Journey?.Destination ?? _walker.Destination) is { } to ? $"walk to {to}" : "walk");
        if (_solvers.Any(s => s.Active())) stopped.Add("puzzle solve");
        string? errand = _activeErrand?.Invoke();
        if (errand is not null) stopped.Add(errand);

        List<string> dropped = HeldLoops();
        foreach ((Func<string?> held, Action<string> drop) in _heldStarts)
        {
            // One holder failing must not keep the stop from being made, nor the
            // others from being let go.
            try
            {
                if (held() is not { } heldStart) continue;
                drop(reason);
                dropped.Add(heldStart);
            }
            catch (Exception ex)
            {
                _log?.Warn("Movement", $"a held start could not be let go ({ex.GetType().Name}: {ex.Message}): {reason}");
            }
        }

        // A start waiting on the "Resume first?" answer is a run of the last set's too.
        _awaitingAnswer = null;
        if (errand is not null) _abandonErrand?.Invoke(reason);
        EndingForGameDataChange?.Invoke(reason, stopped);
        GameDataChangeStop = reason;
        try { Stop(reason); }
        finally { GameDataChangeStop = null; }
        _loops.ClearPendingReconnectResume();
        _loops.Unstage();

        if (stopped.Count == 0 && dropped.Count == 0) return null;
        string what = Sentence(string.Join("; ", new[]
        {
            stopped.Count == 0 ? null : string.Join(" and ", stopped) + " stopped",
            dropped.Count == 0 ? null : string.Join(" and ", dropped) + " dropped",
        }.Where(part => part is not null)));
        _log?.Info("Movement", $"{what}: {reason}");
        return what;
    }

    // Another character was loaded, or the loaded one closed with none loaded after
    // it (HeldLoopsOnProfileSwap). A swap is made with the link down, so nothing is
    // running, but the loop set aside for the reconnect's restart, and the one loaded
    // with Load, would start by themselves under the next character (user,
    // 2026-10-12: "yes it should be dropped on a profile swap"). Returns what was
    // dropped, as the start of the notice, or null when nothing was held.
    public string? DropHeldLoopsForProfileChange(string why)
    {
        List<string> dropped = HeldLoops();
        _loops.ClearPendingReconnectResume();
        _loops.Unstage();
        if (dropped.Count == 0) return null;
        string what = Sentence(string.Join(" and ", dropped) + " dropped");
        _log?.Info("Movement", $"{what}: {why}");
        return what;
    }

    // The loops waiting to start by themselves, as they read in a notice.
    private List<string> HeldLoops()
    {
        List<string> held = new();
        if (_loops.PendingReconnectResumeName is { } resume) held.Add($"loop '{resume}', held for a reconnect,");
        if (_loops.StagedLoop is { } staged) held.Add($"loop '{staged.Name}', loaded to start,");
        return held;
    }

    private static string Sentence(string what) => char.ToUpperInvariant(what[0]) + what[1..];

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
        // After the freeze is up, so releasing the holds moves nothing.
        IReadOnlyList<string> released = _coordinator.ParkHoldsForMasterSwitch();
        _log?.Info(nameof(MovementController),
            "Auto-All engaged — navigation frozen (resumes when Auto-All is restored)."
            + (released.Count > 0 ? $" Holds released: {string.Join(", ", released)}." : string.Empty));
    }

    // Re-assert the holds still owed, ahead of ReleaseFromAutoAll. Separate from
    // it so the engines that derive a hold from current state can be re-run in
    // between, with every hold up before the freeze lifts.
    public void RestoreHoldsBeforeAutoAllRelease() => _coordinator.RestoreHoldsAfterMasterSwitch();

    // Auto-All kill switch restored: resume the navigation we suspended, unless the
    // user has since taken it over (manually resumed, stopped, or started a new one)
    // — Resume itself is a no-op when the engine isn't user-paused, so a stale flag
    // can't force an unwanted resume.
    public void ReleaseFromAutoAll()
    {
        if (!_autoAllSuspended) return;
        _autoAllSuspended = false;
        // A no-op when the caller already restored them; never lift the freeze
        // with a hold still parked.
        _coordinator.RestoreHoldsAfterMasterSwitch();
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
    private void OnAutoLairBool(bool _)
    {
        ForgetTypedMoveOnceResumed();
        StateChanged?.Invoke();
    }
    private void OnCoordinatorGatesChanged()
    {
        // The user gate has other hands on it (the Navigation window's own Resume,
        // a remote resume). Whoever lifts it has carried the errand on.
        if (SuspendedErrand is not null
            && !_coordinator.AssertedGates.Contains(MovementCoordinator.UserGate))
            LetErrandGo();
        ForgetTypedMoveOnceResumed();
        StateChanged?.Invoke();
    }

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
