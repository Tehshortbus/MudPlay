using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Deterministic Auto-Lair session controller. Holds the marked-rooms
// set + per-marker respawn overrides, then drives the walker through
// the Approaching → Waiting → Entering → Engaging cycle picked by
// AutoLairScheduler.
//
// The picker is timer-aware: it tries to step into each marked lair as
// close to its respawn ready-at as possible while never entering early
// (an early entry triggers the spawn check and burns the timer
// immediately).
//
// State machine:
//   Idle        — not running; no scheduler tick; walker untouched.
//   Approaching — walking to the chosen lair's WAIT-ROOM (one hop short
//                 of the lair). Mid-route flip allowed: if the next
//                 tick's pick changes, the walker redirects to the new
//                 wait-room.
//   Waiting     — parked in the wait-room counting down to the entry
//                 arrival instant. Re-evaluates on every tick; can flip
//                 targets and walk to a different wait-room.
//   Entering    — single walker leg from wait-room into the lair,
//                 dispatched on the entry tick.
//   Engaging    — player is in the lair fighting. Timer-bound
//                 (EngageTimeoutSeconds) before returning to Approaching
//                 for the next pick. A future combat-ended signal will
//                 replace the timeout.
//
// Why no built-in pause: the walker honours its own MovementCoordinator
// pause gates. When the walker pauses (HP threshold, encumbrance,
// party-wait, user-pause), our Approaching state effectively pauses too
// — we don't pump moves while it's blocked, and the scheduler ticks
// keep re-evaluating but won't dispatch until the walker frees up.
public sealed class AutoLairManager : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly RoomTracker _tracker;
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LairTimerStore _timers;
    private readonly LogService? _log;
    private readonly MovementCoordinator? _coordinator;

    // Marker set + per-marker user override (seconds; null = use game-data default).
    private readonly Dictionary<RoomKey, int?> _markers = new();

    // Runtime state.
    private readonly DispatcherTimer _schedulerTick;
    private readonly DispatcherTimer _entryTimer;
    private readonly DispatcherTimer _engageTimer;
    private readonly System.Timers.Timer _retryTimer;

    // Travel-cost model — flat default until the encumbrance-gated table
    // from AutoLairSettings is wired in.
    public ITravelCostModel TravelCostModel { get; set; } = new FlatTravelCostModel();

    // How long to stay in Engaging after entering a lair before the
    // scheduler picks the next target. Stop-gap until a combat "ended"
    // signal is available to subscribe to. Default 30 s.
    public int EngageTimeoutSeconds { get; set; } = 30;

    // Scoring heuristic. Default = idle-penalised; Throughput = wasted-only.
    public AutoLairHeuristic Heuristic { get; set; } = AutoLairHeuristic.Default;

    // Idle-wait penalty under the Default heuristic.
    public double IdlePenalty { get; set; } = 1.0;

    // Current state machine phase. Observable for the bottom-strip status.
    public AutoLairPhase Phase { get; private set; } = AutoLairPhase.Idle;

    // Current target lair, when running. Null in Idle.
    public RoomKey? CurrentTarget { get; private set; }

    // Current wait-room, when running. Null in Idle / Entering / Engaging.
    public RoomKey? CurrentWaitRoom { get; private set; }

    // Latest scheduler decision the controller is acting on. Null in Idle.
    public LairDecision? LastDecision { get; private set; }

    // Latched entry-arrival instant for the current Waiting cycle. Set
    // once on Approaching→Waiting (and on the "already at wait-room"
    // short-circuit) and NOT recomputed on every scheduler tick — doing
    // so left the player parked in the wait-room forever, with the 1 s
    // tick resetting the entry timer to now + entryHopDuration before it
    // could fire. Cleared on Entering / Stop / target-change.
    public DateTimeOffset? CurrentEntryArrivalAt { get; private set; }

    public bool IsActive => Phase != AutoLairPhase.Idle;

    // True when Pause has been called and Resume hasn't yet — the
    // scheduler suspends new dispatches, all timers are halted, and the
    // walker is gated via MovementCoordinator.UserGate. Cleared implicitly
    // when Stop tears the session down.
    public bool IsPaused { get; private set; }

    // Fires when IsPaused flips. Carries the new value.
    public event Action<bool>? PausedChanged;

    public IReadOnlyCollection<RoomKey> Marked => _markers.Keys.ToArray();

    // Per-marker override snapshot — null value means "use game-data default".
    public IReadOnlyDictionary<RoomKey, int?> Overrides =>
        new Dictionary<RoomKey, int?>(_markers);

    // Fires on every mutation to the marker set or its overrides.
    public event Action? MarkedChanged;

    // Fires when IsActive flips. Carries the new value.
    public event Action<bool>? ActiveChanged;

    // Fires when Phase changes; carries the new phase.
    public event Action<AutoLairPhase>? PhaseChanged;

    public AutoLairManager(
        AutoWalkManager walker,
        RoomTracker tracker,
        RoomGraphManager graph,
        BfsMapper bfs,
        LairTimerStore timers,
        LogService? log = null,
        MovementCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(timers);
        _walker = walker;
        _tracker = tracker;
        _graph = graph;
        _bfs = bfs;
        _timers = timers;
        _log = log;
        _coordinator = coordinator;

        _schedulerTick = new DispatcherTimer(TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal, (_, _) => OnSchedulerTick());
        _schedulerTick.Stop();

        _entryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _entryTimer.Tick += (_, _) => OnEntryTimerFired();
        _entryTimer.Stop();

        _engageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _engageTimer.Tick += (_, _) => OnEngageTimerFired();
        _engageTimer.Stop();

        _retryTimer = new System.Timers.Timer(2000) { AutoReset = false };
        _retryTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(EvaluateAndDispatch);

        _walker.Event += OnWalkerEvent;
        _tracker.StateChanged += OnTrackerTransition;
    }

    public void Dispose()
    {
        _schedulerTick.Stop();
        _entryTimer.Stop();
        _engageTimer.Stop();
        _retryTimer.Stop();
        _retryTimer.Dispose();
        _walker.Event -= OnWalkerEvent;
        _tracker.StateChanged -= OnTrackerTransition;
    }

    // ----- marker CRUD ---------------------------------------------

    public bool IsMarked(RoomKey key) => _markers.ContainsKey(key);

    public void Mark(RoomKey key, int? overrideRespawnSeconds = null)
    {
        if (_markers.TryGetValue(key, out int? current) && current == overrideRespawnSeconds) return;
        _markers[key] = overrideRespawnSeconds;
        _log?.Info("AutoLair",
            overrideRespawnSeconds is int s
                ? $"marked {key} (override {s}s)"
                : $"marked {key}");
        MarkedChanged?.Invoke();
    }

    public void Unmark(RoomKey key)
    {
        if (!_markers.Remove(key)) return;
        _log?.Info("AutoLair", $"unmarked {key}");
        MarkedChanged?.Invoke();
    }

    public void Toggle(RoomKey key)
    {
        if (_markers.ContainsKey(key)) Unmark(key);
        else Mark(key);
    }

    public void SetOverride(RoomKey key, int? overrideRespawnSeconds)
    {
        if (!_markers.ContainsKey(key)) return;     // ignore — only edits affect marked rooms
        if (_markers[key] == overrideRespawnSeconds) return;
        _markers[key] = overrideRespawnSeconds;
        _log?.Info("AutoLair", $"override for {key} = " +
            (overrideRespawnSeconds is int s ? $"{s}s" : "game-data default"));
        MarkedChanged?.Invoke();
    }

    public int? GetOverride(RoomKey key) =>
        _markers.TryGetValue(key, out int? v) ? v : null;

    public void Clear()
    {
        if (_markers.Count == 0) return;
        _markers.Clear();
        MarkedChanged?.Invoke();
    }

    // ----- session control -----------------------------------------

    // Begin scheduling. No-op when already active, when fewer than
    // 2 markers are set, or when the tracker has no current room for
    // the walker to start from.
    public bool Start()
    {
        if (IsActive) return true;
        if (_markers.Count < 2)
        {
            _log?.Warn("AutoLair", $"need at least 2 markers; have {_markers.Count}.");
            return false;
        }
        if (_tracker.State.CurrentRoom is null)
        {
            _log?.Warn("AutoLair", "no current room — locate before starting Auto-Lair.");
            return false;
        }

        // Treat every marked room as "ready now" at Start, ignoring
        // any stale LastEntered the store picked up from passive walk-
        // throughs earlier in the session. Per user direction:
        // respawn timers should only tick from the moment the
        // scheduler ENTERS each lair as part of this run, not from
        // some wall-clock anchor the player set incidentally.
        _timers.ResetArrivalsFor(_markers.Keys);

        SetPhase(AutoLairPhase.Approaching);
        ActiveChanged?.Invoke(true);
        _log?.Info("AutoLair", $"start ({_markers.Count} markers)");
        _schedulerTick.Start();
        EvaluateAndDispatch();
        return true;
    }

    public void Stop(string reason = "user stop")
    {
        if (!IsActive) return;
        _schedulerTick.Stop();
        _entryTimer.Stop();
        _engageTimer.Stop();
        _retryTimer.Stop();
        // Clear the pause gate before tearing the session down so the
        // walker isn't stuck behind a stale UserGate after Stop fires.
        if (IsPaused)
        {
            IsPaused = false;
            _coordinator?.ClearGate(MovementCoordinator.UserGate);
            PausedChanged?.Invoke(false);
        }
        CurrentTarget = null;
        CurrentWaitRoom = null;
        CurrentEntryArrivalAt = null;
        LastDecision = null;
        SetPhase(AutoLairPhase.Idle);
        ActiveChanged?.Invoke(false);
        _log?.Info("AutoLair", $"stop: {reason}");

        if (_walker.State != WalkState.Idle) _walker.Stop("auto-lair stop");
    }

    // Suspend scheduling without tearing down state. All timers stop;
    // the user gate on MovementCoordinator is asserted so any in-flight
    // walk halts at its next step. Markers, current target, and the
    // entry latch are preserved — Resume picks back up from where it
    // left off.
    public void Pause()
    {
        if (!IsActive || IsPaused) return;
        IsPaused = true;
        _schedulerTick.Stop();
        _entryTimer.Stop();
        _engageTimer.Stop();
        _retryTimer.Stop();
        _coordinator?.AssertGate(MovementCoordinator.UserGate);
        PausedChanged?.Invoke(true);
        _log?.Info("AutoLair", "paused.");
    }

    // Inverse of Pause — clears the user gate, restarts the scheduler
    // tick, and forces an immediate re-evaluation so the run picks up
    // from the player's current position.
    //
    // In-game respawn timers don't pause when WE pause — they're
    // wall-clock anchored on the player's last arrival at each lair. So
    // Resume drops the latched leg state (LastDecision, CurrentTarget,
    // CurrentWaitRoom, CurrentEntryArrivalAt) and snaps Phase back to
    // Approaching before re-evaluating. The next pick is computed against
    // the NEW timer landscape — the lair we were heading to before the
    // pause may have over-spawned while paused, and a different marker
    // may now be the better target.
    public void Resume()
    {
        if (!IsActive || !IsPaused) return;
        IsPaused = false;
        _coordinator?.ClearGate(MovementCoordinator.UserGate);

        // Drop the stale leg — respawn timers ticked while paused.
        LastDecision = null;
        CurrentTarget = null;
        CurrentWaitRoom = null;
        CurrentEntryArrivalAt = null;
        _entryTimer.Stop();
        _engageTimer.Stop();
        SetPhase(AutoLairPhase.Approaching);

        _schedulerTick.Start();
        PausedChanged?.Invoke(false);
        _log?.Info("AutoLair", "resumed; re-evaluating against current timer state.");
        EvaluateAndDispatch();
    }

    // ----- scheduler tick + dispatch -------------------------------

    private void OnSchedulerTick()
    {
        if (!IsActive || IsPaused) return;
        // Engaging is its own phase — don't churn picks during combat.
        if (Phase == AutoLairPhase.Engaging) return;
        EvaluateAndDispatch();
    }

    private void EvaluateAndDispatch()
    {
        if (!IsActive) return;
        if (_tracker.State.CurrentRoom is not { } current)
        {
            // Locator dropped to Lost mid-run — wait for it to recover.
            _log?.Debug("AutoLair", "no current room; waiting.");
            return;
        }

        List<LairCandidate> candidates = BuildCandidates(current.Key);
        if (candidates.Count == 0)
        {
            _log?.Warn("AutoLair", "no schedulable candidates this tick; idling.");
            return;
        }

        LairDecision? pick = AutoLairScheduler.PickNext(
            candidates, TravelCostModel, Heuristic, IdlePenalty, DateTimeOffset.UtcNow);
        if (pick is null) return;

        LairDecision? prev = LastDecision;
        // Same-target tick during Waiting: the entry timer is already
        // running against a latched CurrentEntryArrivalAt. Don't touch
        // LastDecision (would keep racing pick.EntryArrival forward),
        // don't redispatch, just let the timer fire. This was the
        // entry-timer-never-fires bug.
        bool targetUnchanged = prev is not null
            && prev.Lair.Equals(pick.Lair)
            && prev.WaitRoom.Equals(pick.WaitRoom);
        if (Phase == AutoLairPhase.Waiting && targetUnchanged) return;

        // Same-target tick during Approaching with the walker already
        // headed for our wait-room — let it keep walking. Re-issuing
        // WalkTo would call walker.Stop("superseded") + restart, which
        // fires Stopped → our handler clears CurrentTarget mid-walk →
        // RefreshAutoLairApproachPath nulls the orange line for a
        // frame, producing the user-visible blink between every
        // scheduler tick. This guard makes the same-target case a
        // no-op so the walk runs uninterrupted.
        if (Phase == AutoLairPhase.Approaching
            && targetUnchanged
            && _walker.State == WalkState.Walking
            && _walker.Destination is { } currentDest
            && currentDest.Equals(pick.WaitRoom))
        {
            return;
        }

        LastDecision = pick;

        // Phase-specific dispatch.
        if (Phase is AutoLairPhase.Approaching or AutoLairPhase.Waiting)
        {
            // Different target reached us here — wipe any in-flight
            // entry latch from the previous target so the next arrival
            // gets a fresh ScheduleEntryAt.
            CurrentEntryArrivalAt = null;
            _entryTimer.Stop();

            // Dispatch walker to the new wait-room.
            CurrentTarget = pick.Lair;
            CurrentWaitRoom = pick.WaitRoom;

            if (current.Key.Equals(pick.WaitRoom))
            {
                // Already at the wait-room — skip the walk, latch the
                // entry-arrival once, jump straight to Waiting.
                SetPhase(AutoLairPhase.Waiting);
                _log?.Info("AutoLair",
                    $"already at wait-room {pick.WaitRoom}; waiting {FormatSlack(pick.SlackAtEntry)} to enter {pick.Lair}.");
                LatchAndScheduleEntry(pick.EntryArrival);
                return;
            }

            SetPhase(AutoLairPhase.Approaching);
            _log?.Info("AutoLair",
                $"approaching {pick.Lair} via wait-room {pick.WaitRoom} ({FormatSlack(pick.SlackAtEntry)} at entry).");

            if (!_walker.WalkTo(pick.WaitRoom))
            {
                _log?.Warn("AutoLair", $"walker rejected path to {pick.WaitRoom}; retrying in 2s.");
                _retryTimer.Stop();
                _retryTimer.Start();
            }
        }
    }

    private List<LairCandidate> BuildCandidates(RoomKey current)
    {
        List<LairCandidate> cands = new(_markers.Count);

        foreach ((RoomKey lair, int? overrideSec) in _markers)
        {
            // The room we're already in is INCLUDED as a candidate:
            // PickWaitRoom resolves to a one-hop non-marker neighbour
            // when current == lair, so a self-cycle is "step out, wait,
            // step back in" — much cheaper than routing to a different
            // lair when the current room is the soonest-ready one.
            DateTimeOffset? readyAt = _timers.NextReadyAt(lair, overrideSec);
            // readyAt = null means either:
            //   (a) we've never entered this lair (no anchor); or
            //   (b) no respawn timer could be resolved (game data + override both empty).
            // In both cases the scheduler treats it as "ready now" — see
            // LairCandidate.ReadyAt contract.

            (RoomKey? waitRoom, int? hops) = PickWaitRoom(current, lair);
            cands.Add(new LairCandidate(lair, readyAt, hops, waitRoom));
        }
        return cands;
    }

    // Choose the wait-room for lair from current. Preferred shape: the
    // room immediately before lair on the shortest BFS path. Falls back
    // to the closest non-marked neighbour when the preferred wait-room is
    // itself a marked lair (would trigger a stray spawn check). Returns
    // (null, null) when no eligible wait-room exists, signalling the
    // candidate is unschedulable.
    private (RoomKey? waitRoom, int? hops) PickWaitRoom(RoomKey current, RoomKey lair)
    {
        // Self-lair cycle: we're already standing in a marked lair
        // and want to re-trigger its respawn. MajorMUD only checks
        // the respawn on entry, so we have to leave + come back. Pick
        // a one-hop non-marker neighbour as the wait-room; the walker
        // will step out, wait for ReadyAt, then step in. Cheaper than
        // routing to a different lair and back when this one is the
        // soonest-ready candidate.
        if (current.Equals(lair))
        {
            (RoomKey? alt, _) = NearestNonMarkedNeighbour(current, lair);
            return alt is null ? (null, null) : (alt, 1);
        }

        IReadOnlyList<Direction>? path = _bfs.FindPath(current, lair);
        if (path is null || path.Count == 0) return (null, null);

        // Walk the path to recover the room sequence and locate the
        // hop-before-lair as the natural wait-room.
        IReadOnlyList<RoomKey> roomSeq = RoomsAlongPath(current, path);
        if (roomSeq.Count == 0) return (null, null);

        RoomKey natural = roomSeq.Count == 1 ? current : roomSeq[^2];
        int hops = roomSeq.Count - 1; // hops from current to natural (excludes lair entry)

        // Natural wait-room is itself a marker → would trigger that
        // lair's spawn check when we waited there. Fall back to the
        // closest non-marker neighbour of `lair`.
        if (_markers.ContainsKey(natural))
        {
            (RoomKey? alt, int? altHops) = NearestNonMarkedNeighbour(current, lair);
            return (alt, altHops);
        }

        return (natural, hops);
    }

    // BFS-shortest non-marker neighbour of lair from current. Used when
    // the natural wait-room (the BFS path's second-to-last room) is a
    // marker we don't want to disturb.
    private (RoomKey? key, int? hops) NearestNonMarkedNeighbour(RoomKey current, RoomKey lair)
    {
        if (_graph.GetRoom(lair) is not Room room) return (null, null);

        RoomKey? bestKey = null;
        int bestDist = int.MaxValue;
        foreach (RoomExit exit in room.Exits.Values)
        {
            RoomKey n = exit.Target;
            if (n.Equals(lair)) continue;
            if (_markers.ContainsKey(n)) continue;
            int? d = _bfs.DistanceBetween(current, n);
            if (d is not int dist) continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestKey = n;
            }
        }
        return bestKey is null ? (null, null) : (bestKey, bestDist);
    }

    // Replay a list of directions from source through the graph and
    // return the room keys touched (excluding source). Stops early if a
    // step lands outside the graph — defensive against a graph reload
    // mid-tick.
    private IReadOnlyList<RoomKey> RoomsAlongPath(RoomKey source, IReadOnlyList<Direction> dirs)
    {
        List<RoomKey> rooms = new(dirs.Count);
        RoomKey cur = source;
        foreach (Direction d in dirs)
        {
            if (_graph.GetRoom(cur) is not Room room) break;
            if (!room.Exits.TryGetValue(d, out RoomExit exit)) break;
            cur = exit.Target;
            rooms.Add(cur);
        }
        return rooms;
    }

    // ----- entry timer ---------------------------------------------

    // Latch CurrentEntryArrivalAt and start the entry timer once per
    // Waiting cycle. Subsequent scheduler ticks that resolve to the same
    // target take the early-return in EvaluateAndDispatch and don't touch
    // this — the running timer is allowed to fire on time.
    private void LatchAndScheduleEntry(DateTimeOffset entryArrival)
    {
        if (Phase != AutoLairPhase.Waiting) return;
        CurrentEntryArrivalAt = entryArrival;

        TimeSpan wait = entryArrival - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            // Past entry-time on arrival → step in immediately.
            EnterLairNow();
            return;
        }
        _entryTimer.Stop();
        _entryTimer.Interval = wait;
        _entryTimer.Start();
    }

    private void OnEntryTimerFired()
    {
        _entryTimer.Stop();
        if (!IsActive || IsPaused) return;
        EnterLairNow();
    }

    private void EnterLairNow()
    {
        if (CurrentTarget is not { } lair) return;
        SetPhase(AutoLairPhase.Entering);
        CurrentEntryArrivalAt = null; // we're stepping in; the latch served its purpose
        _log?.Info("AutoLair", $"entering {lair}.");
        if (!_walker.WalkTo(lair))
        {
            _log?.Warn("AutoLair", $"walker rejected entry to {lair}; retrying in 2s.");
            _retryTimer.Stop();
            _retryTimer.Start();
        }
    }

    // ----- engage timer --------------------------------------------

    private void StartEngagement()
    {
        SetPhase(AutoLairPhase.Engaging);
        _engageTimer.Stop();
        _engageTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, EngageTimeoutSeconds));
        _engageTimer.Start();
        _log?.Info("AutoLair",
            $"engaging — re-evaluating in {EngageTimeoutSeconds}s.");
        // The engage window is a fixed wall-clock timeout
        // (EngageTimeoutSeconds, default 30 s) because we don't yet know
        // whether combat is actually in progress. A combat signal could
        // drive the Engaging phase directly instead:
        //   - on first damage line / attack send in this room →
        //     SetPhase(Engaging) + stop the engage timer (we know we're
        //     actually fighting, no timeout needed)
        //   - on "room cleared" (no live targets remaining in the current
        //     room's Also-Here list, OR the auto-combat engine reports
        //     done) → SetPhase(Approaching) + EvaluateAndDispatch() to
        //     pick the next lair.
        // The timeout would stay as the FALLBACK upper bound so the
        // scheduler never permanently parks on a dead lair if combat
        // detection misses a clear-signal.
    }

    private void OnEngageTimerFired()
    {
        _engageTimer.Stop();
        if (!IsActive || IsPaused) return;
        SetPhase(AutoLairPhase.Approaching);
        EvaluateAndDispatch();
    }

    // ----- walker / tracker event hooks ----------------------------

    private void OnWalkerEvent(WalkEvent evt)
    {
        if (!IsActive) return;
        // When paused, ignore walker events so the scheduler doesn't
        // auto-transition or auto-redispatch. The user gate keeps the
        // walker stalled until Resume.
        if (IsPaused) return;

        switch (evt.Kind)
        {
            case WalkEventKind.Finished:
                // The walker landed on its destination. Branch on phase.
                if (Phase == AutoLairPhase.Approaching && CurrentTarget is { } target)
                {
                    SetPhase(AutoLairPhase.Waiting);
                    // If the lair's respawn timer has elapsed (or it's
                    // never been entered this run), step in immediately
                    // — no artificial wait. Only when the timer is
                    // genuinely in the future do we park at the
                    // wait-room and let it tick down before entering.
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    DateTimeOffset entryAt = now;
                    int? overrideSec = _markers.TryGetValue(target, out int? o) ? o : null;
                    if (_timers.NextReadyAt(target, overrideSec) is { } readyAt
                        && readyAt > now)
                        entryAt = readyAt;
                    LatchAndScheduleEntry(entryAt);
                }
                else if (Phase == AutoLairPhase.Entering)
                {
                    StartEngagement();
                }
                break;

            case WalkEventKind.Failed:
                _log?.Warn("AutoLair", $"walker failed: {evt.Detail}");
                _retryTimer.Stop();
                _retryTimer.Start();
                break;

            case WalkEventKind.Stopped:
                // External walker stops (user-typed move displaced us,
                // another engine grabbed the walker, etc) used to kill
                // the whole session. That was too aggressive — typing
                // a single move while the scheduler was running
                // permanently stranded the run after the first lair.
                // Now we just reschedule: drop the in-flight target,
                // kick the retry timer, let the next tick re-evaluate
                // from wherever the player ended up. Explicit user
                // stops still go through AutoLair.Stop directly (chip /
                // mode toggle), so the session can only end by the
                // user actually saying so.
                if (Phase is AutoLairPhase.Approaching
                          or AutoLairPhase.Waiting
                          or AutoLairPhase.Entering)
                {
                    _log?.Info("AutoLair",
                        $"walker stopped ({evt.Detail}); rescheduling.");
                    _entryTimer.Stop();
                    CurrentEntryArrivalAt = null;
                    LastDecision = null;
                    CurrentTarget = null;
                    CurrentWaitRoom = null;
                    SetPhase(AutoLairPhase.Approaching);
                    _retryTimer.Stop();
                    _retryTimer.Start();
                }
                break;
        }
    }

    private void OnTrackerTransition(RoomTransition t)
    {
        if (!IsActive) return;
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is not { } room) return;

        // If we entered the target lair without going through the
        // walker (e.g. user typed the move manually), short-circuit
        // into Engaging. Catches the "I'm already moving through —
        // just adopt the schedule" case the user is likely to hit.
        if (Phase is AutoLairPhase.Approaching or AutoLairPhase.Waiting or AutoLairPhase.Entering
            && CurrentTarget is { } target
            && room.Key.Equals(target))
        {
            _entryTimer.Stop();
            StartEngagement();
        }
    }

    // ----- helpers --------------------------------------------------

    private void SetPhase(AutoLairPhase next)
    {
        if (Phase == next) return;
        Phase = next;
        PhaseChanged?.Invoke(next);
    }

    private static string FormatSlack(TimeSpan slack)
    {
        StringBuilder sb = new();
        if (slack.TotalSeconds > 0)
            sb.Append(slack.TotalSeconds.ToString("F1")).Append("s wasted respawn");
        else if (slack.TotalSeconds < 0)
            sb.Append((-slack.TotalSeconds).ToString("F1")).Append("s idle wait");
        else
            sb.Append("perfect timing");
        return sb.ToString();
    }
}

// Phases of the Auto-Lair state machine. See AutoLairManager for the
// per-phase semantics.
public enum AutoLairPhase
{
    Idle,
    Approaching,
    Waiting,
    Entering,
    Engaging,
}
