using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Deterministic Auto-Lair session controller. Holds the marked-rooms
// set + per-marker respawn overrides, then drives the walker through
// the Approaching → Waiting → Entering → Engaging cycle picked by
// AutoLairScheduler.
//
// The picker is timer-aware: it tries to step into each marked lair as
// close to its respawn ready-at as possible while never entering early
// (an early entry finds the room empty, and on Paradigm — timed from the
// entry — restarts the countdown; Stock's clock runs from the last kill,
// see LairTimerStore.ClockStart).
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
//   Engaging    — stepped into the lair. Two exits, both prompt: if a fight
//                 starts, leave once it's over and its loot is in hand (the
//                 Combat then Acquisition gates clearing — OnGatesChanged),
//                 bounded by EngageTimeoutSeconds for a fight that never
//                 resolves; if no fight starts within EmptyLairGrace, the lair
//                 hadn't respawned, so leave and pick another.
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
    // The scheduler re-picks every SchedulerInterval while running; the engage
    // window ticks every _engageInterval until the engagement finishes. The entry
    // and retry timers are one-shot.
    private IDisposable? _schedulerTick;
    private IDisposable? _entryTimer;
    private IDisposable? _engageTimer;
    private TimeSpan _engageInterval;
    private IDisposable? _retryTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    internal static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(1);

    // Set while we're inside our own _walker.WalkTo. A WalkTo that supersedes
    // our previous walk raises Stopped("superseded by new walk") synchronously,
    // re-entering OnWalkerEvent before WalkTo returns; without this flag that
    // self-inflicted stop is read as an external displacement, nulls the target
    // we set one line earlier, and the next 1 s tick re-dispatches → supersede →
    // Stopped → reschedule, a ~1/sec loop that never enters the lair. The flag
    // lets the Stopped handler ignore our own supersede while still rescheduling
    // on a genuine external stop (user move / another engine grabbing the walker).
    private bool _issuingWalk;

    // Whether a fight has actually started during the current Engaging phase.
    // Gates the early exit: see OnGatesChanged.
    private bool _engageSawCombat;

    // Travel-cost model — flat default until the encumbrance-gated table
    // from AutoLairSettings is wired in.
    public ITravelCostModel TravelCostModel { get; set; } = new FlatTravelCostModel();

    // Upper bound on a single engagement: how long to stay in a lair when the
    // fight never resolves on its own. The normal exit is the combat-ended
    // signal (OnGatesChanged), so this only catches a monster we can't kill, one
    // that ran, or a missed clear-signal.
    public int EngageTimeoutSeconds { get; set; } = 30;

    // How long to wait, after stepping in, for a fight to start before deciding
    // the lair hadn't respawned. Short because the answer arrives with the room
    // display that the entering move itself returns — this is settle time, not a
    // guess at the respawn. Deliberately not the engage timeout: waiting 30 s in
    // an empty room was most of the idle time in report stock-20260908-192900,
    // and entering has already burned the lair's timer, so there is nothing to
    // gain by standing in it.
    internal static readonly TimeSpan EmptyLairGrace = TimeSpan.FromSeconds(3);

    // Scoring heuristic. Default = closest ready-by-arrival; Throughput = wasted-only.
    public AutoLairHeuristic Heuristic { get; set; } = AutoLairHeuristic.Default;

    // Current state machine phase. Observable for the bottom-strip status.
    public AutoLairPhase Phase { get; private set; } = AutoLairPhase.Idle;

    // Current target lair, when running. Null in Idle.
    public RoomKey? CurrentTarget { get; private set; }

    // Current wait-room, when running. Null in Idle / Entering / Engaging.
    public RoomKey? CurrentWaitRoom { get; private set; }

    // Latest scheduler decision the controller is acting on. Null in Idle.
    public LairDecision? LastDecision { get; private set; }

    // The most recent walker failure while approaching a lair, or null once the
    // approach makes progress again. Auto-Lair retries a failed approach on its
    // timer indefinitely (the lair may just be temporarily walled by a fight or a
    // hazard), so this lets the nav status say WHY it's stuck retrying instead of
    // sitting on a silent "Approaching". Surfaced via the VM's AutoLairStatusText,
    // which its 1 s tick refreshes.
    public string? LastWalkerFailure { get; private set; }

    // Doors a walk of this run tried and couldn't open, each by the room it leaves
    // and the room it leads to, and that this character cannot realistically open:
    // its pick and bash are impossible, or a pick is under DoorPolicy.PoorPickChancePercent
    // a try (KeepsDoorForTheRun). Every walk the run issues afterwards refuses them
    // and every wait room is chosen round them: each walk is a trip of its own to
    // the walker, which would otherwise spend its picks on the same door at every
    // visit, and with no other way in would be sent back to it every two seconds
    // by the retry below. A lair only such a door leads to is left out of the run.
    // A door that failed at fair odds is not kept: bad luck, tried again on the
    // lair's next visit.
    private readonly HashSet<(RoomKey From, RoomKey To)> _abandonedDoors = new();
    public IReadOnlyCollection<(RoomKey From, RoomKey To)> AbandonedDoors => _abandonedDoors;

    // Bumped whenever _abandonedDoors changes, so a remembered "no route" can tell.
    private int _abandonedDoorsVersion;

    // Lairs the run is leaving out because nothing routes to them, so each is said
    // once in the log and again only if it comes back and drops out a second time.
    private readonly HashSet<RoomKey> _lairsLeftOut = new();
    public IReadOnlyCollection<RoomKey> LairsLeftOut => _lairsLeftOut;
    private const string NoLairInReach = "no marked lair can be reached from here";

    // The "no route to this lair" verdicts. Finding none floods the whole graph (over
    // 100 ms on a big realm), and the scheduler asks once a second for every marker,
    // so a verdict stands until something that could change it does: the room we
    // are in, the doors given up on, the stats the door rule reads, the marker set.
    // Whatever else could change it (inventory, a key) is caught by the re-check.
    private readonly Dictionary<RoomKey, NoRouteVerdict> _noRoute = new();
    private static readonly TimeSpan NoRouteRecheck = TimeSpan.FromSeconds(30);
    private readonly record struct NoRouteVerdict(RoomKey From, int DoorsVersion, int RuleStamp, DateTimeOffset At);

    // A failed walk into a lair is retried on a timer, starting at RetryBase and
    // doubling for each further failed entry to the same lair up to RetryCap, so a
    // lair that keeps refusing isn't re-sent whatever the entry sends every 2 s.
    internal static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryCap = TimeSpan.FromSeconds(60);
    private RoomKey? _failedEntryLair;
    private int _failedEntries;

    // Latched entry-arrival instant for the current Waiting cycle. Set
    // once on Approaching→Waiting (and on the "already at wait-room"
    // short-circuit) and NOT recomputed on every scheduler tick — doing
    // so left the player parked in the wait-room forever, with the 1 s
    // tick resetting the entry timer to now + entryHopDuration before it
    // could fire. Cleared on Entering / Stop / target-change.
    public DateTimeOffset? CurrentEntryArrivalAt { get; private set; }

    public bool IsActive => Phase != AutoLairPhase.Idle;

    // Most recently run auto-lair, retained past Stop (like
    // LoopRunner.LastRunLoopName) so @path can name it for a dead player's
    // recovery. Name is the setup name (or an ad-hoc descriptor) passed to Start;
    // a label-less resume keeps the prior name but re-stamps the time. LastRunLairAt
    // lets MovementStatus pick the more recent of loop vs auto-lair. Default
    // (name null, time MinValue) until the first labelled run.
    public string? LastRunLairName { get; private set; }
    public DateTimeOffset LastRunLairAt { get; private set; } = DateTimeOffset.MinValue;

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

    // The last Stop was by a caller that restarts the run itself (see Stop).
    public bool StoppedForResume { get; private set; }

    // Fires when Phase changes; carries the new phase.
    public event Action<AutoLairPhase>? PhaseChanged;

    public AutoLairManager(
        AutoWalkManager walker,
        RoomTracker tracker,
        RoomGraphManager graph,
        BfsMapper bfs,
        LairTimerStore timers,
        LogService? log = null,
        MovementCoordinator? coordinator = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        _schedule = schedule ?? DispatcherTicks.Once;
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

        _walker.Event += OnWalkerEvent;
        _tracker.StateChanged += OnTrackerTransition;
        if (_coordinator is not null) _coordinator.GatesChanged += OnGatesChanged;
    }

    public void Dispose()
    {
        Cancel(ref _schedulerTick);
        Cancel(ref _entryTimer);
        Cancel(ref _engageTimer);
        Cancel(ref _retryTimer);
        _walker.Event -= OnWalkerEvent;
        _tracker.StateChanged -= OnTrackerTransition;
        if (_coordinator is not null) _coordinator.GatesChanged -= OnGatesChanged;
    }

    // ----- marker CRUD ---------------------------------------------

    public bool IsMarked(RoomKey key) => _markers.ContainsKey(key);

    // The game-data set the marks are rooms of: the one active when the first of
    // them was placed, so whoever would save them as a setup can tell whose they
    // are. A set of marks is one game data's, never mixed. Means nothing while
    // nothing is marked.
    public string? MarksGameData { get; private set; }

    // Marks were dropped for being another game data's rooms than the active one's:
    // the set they were of, and the set active now.
    public event Action<string?, string?>? MarksOfOtherGameDataDropped;

    // The active game data changed. Under another set the marked rooms are other
    // rooms, and saved as a setup they would land in that set's Loops folder, a file
    // every character and running client on that set reads: the marks are dropped.
    // The same set read in again keeps them. True when marks were dropped.
    public bool DropMarksOfOtherGameData(string? active)
    {
        if (_markers.Count == 0) return false;
        if (string.Equals(MarksGameData, active, StringComparison.OrdinalIgnoreCase)) return false;
        string? markedOn = MarksGameData;
        _log?.Info("AutoLair",
            $"{_markers.Count} lair mark(s) dropped: their rooms were game data '{markedOn ?? "(none)"}''s and '{active ?? "(none)"}' is active now");
        Clear();
        MarksOfOtherGameDataDropped?.Invoke(markedOn, active);
        return true;
    }

    // Put back the marks a run had when something took over from it (an event's
    // snapshot of what it interrupted), with the stamp they had. False, with
    // nothing put back, when they are another game data's than the active one's:
    // a set change in between doesn't make them this set's, and a run isn't
    // started on another set's rooms.
    public bool RestoreMarks(IReadOnlyDictionary<RoomKey, int?> marks, string? markedOn)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (!string.Equals(markedOn, _graph.ActiveSet, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info("AutoLair",
                $"{marks.Count} lair mark(s) not put back: their rooms were game data '{markedOn ?? "(none)"}''s and '{_graph.ActiveSet ?? "(none)"}' is active now");
            return false;
        }
        _markers.Clear();
        foreach ((RoomKey key, int? overrideRespawnSeconds) in marks) _markers[key] = overrideRespawnSeconds;
        MarksGameData = markedOn;
        _noRoute.Clear();
        _log?.Info("AutoLair", $"restored {_markers.Count} mark(s) of game data '{markedOn ?? "(none)"}'");
        MarkedChanged?.Invoke();
        return true;
    }

    public void Mark(RoomKey key, int? overrideRespawnSeconds = null)
    {
        // A set of marks is one game data's: any held from another than the active
        // one go before a room of this one joins them.
        DropMarksOfOtherGameData(_graph.ActiveSet);
        if (_markers.TryGetValue(key, out int? current) && current == overrideRespawnSeconds) return;
        if (_markers.Count == 0) MarksGameData = _graph.ActiveSet;
        _markers[key] = overrideRespawnSeconds;
        _noRoute.Clear();
        _log?.Info("AutoLair",
            overrideRespawnSeconds is int s
                ? $"marked {key} (override {s}s)"
                : $"marked {key}");
        MarkedChanged?.Invoke();
    }

    public void Unmark(RoomKey key)
    {
        if (!_markers.Remove(key)) return;
        _noRoute.Clear();
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
        _noRoute.Clear();
        MarkedChanged?.Invoke();
    }

    // ----- session control -----------------------------------------

    // Begin scheduling. No-op when already active, when fewer than
    // 2 markers are set, or when the tracker has no current room for
    // the walker to start from.
    // label names the run for @path recovery (the setup name, or an ad-hoc
    // descriptor for a coord-list run). A label-less call — a resume of the same
    // run from Trainer / comeback paths — keeps the prior name but re-stamps the
    // time so the last-run ordering stays fresh.
    public bool Start(string? label = null)
    {
        if (IsActive) return true;
        StoppedForResume = false;
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

        LastRunLairAt = DateTimeOffset.UtcNow;
        if (label is { Length: > 0 }) LastRunLairName = label;

        // Treat every marked room as "ready now" at Start, ignoring
        // any stale LastEntered the store picked up from passive walk-
        // throughs earlier in the session. Per user direction:
        // respawn timers should only tick from the moment the
        // scheduler ENTERS each lair as part of this run, not from
        // some wall-clock anchor the player set incidentally.
        _timers.ResetArrivalsFor(_markers.Keys);
        _abandonedDoors.Clear();
        _abandonedDoorsVersion++;
        _lairsLeftOut.Clear();
        _noRoute.Clear();
        _failedEntryLair = null;
        _failedEntries = 0;

        SetPhase(AutoLairPhase.Approaching);
        ActiveChanged?.Invoke(true);
        _log?.Info("AutoLair", $"start ({_markers.Count} markers)");
        StartSchedulerTick();
        EvaluateAndDispatch();
        return true;
    }

    // willResume: the caller stops the run only to start it again itself (a bank or
    // sell trip, a flee). ActiveChanged carries no detail, so a listener reads
    // StoppedForResume while it handles the change.
    public void Stop(string reason = "user stop", bool willResume = false)
    {
        if (!IsActive) return;
        StoppedForResume = willResume;
        Cancel(ref _schedulerTick);
        Cancel(ref _entryTimer);
        Cancel(ref _engageTimer);
        Cancel(ref _retryTimer);
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
        Cancel(ref _schedulerTick);
        Cancel(ref _entryTimer);
        Cancel(ref _engageTimer);
        Cancel(ref _retryTimer);
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
        Cancel(ref _entryTimer);
        Cancel(ref _engageTimer);
        SetPhase(AutoLairPhase.Approaching);

        StartSchedulerTick();
        PausedChanged?.Invoke(false);
        _log?.Info("AutoLair", "resumed; re-evaluating against current timer state.");
        EvaluateAndDispatch();
    }

    // ----- scheduler tick + dispatch -------------------------------

    // The master switch froze the run. Movement is held by the coordinator, but
    // this scheduler would go on deciding by itself: re-planning walks on its
    // retry timer, calling a lair empty when its engage window runs out, and
    // reading the Combat gate (parked while the switch is off) as the fight being
    // over. Each of its timers and its gate handler stands down while frozen; the
    // timers keep ticking, so it picks up where it was when the switch is back on.
    private bool FrozenByMasterSwitch =>
        _coordinator?.IsGateAsserted(MovementCoordinator.AutoAllGate) == true;

    private void OnSchedulerTick()
    {
        if (!IsActive || IsPaused || FrozenByMasterSwitch) return;
        // Engaging is its own phase — don't churn picks during combat.
        if (Phase == AutoLairPhase.Engaging) return;
        EvaluateAndDispatch();
    }

    private void EvaluateAndDispatch()
    {
        if (!IsActive) return;
        // The retry timer lands here too. Outside a fight the scheduler tick asks
        // again each second once the switch is back on; from inside one the gate
        // handler does, when the freeze lifts.
        if (FrozenByMasterSwitch) return;
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
            candidates, TravelCostModel, Heuristic, DateTimeOffset.UtcNow);
        if (pick is null)
        {
            // No marked lair has a route from here (a door that beat the character,
            // a gate it can't pass). The run stays up, since that can change, but it
            // says so on the nav status and once in the log instead of standing in
            // silence.
            if (LastWalkerFailure != NoLairInReach)
            {
                LastWalkerFailure = NoLairInReach;
                _log?.Warn("AutoLair", $"{NoLairInReach}; waiting for that to change.");
            }
            return;
        }

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
            // gets a fresh LatchAndScheduleEntry.
            CurrentEntryArrivalAt = null;
            Cancel(ref _entryTimer);

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

            if (!IssueWalk(pick.WaitRoom))
            {
                _log?.Warn("AutoLair", $"walker rejected path to {pick.WaitRoom}; retrying in 2s.");
                ArmRetry();
            }
        }
    }

    // Dispatch the walker to target, flagging the call so a synchronous
    // supersede-Stopped of our previous walk is recognised as our own and not
    // read as an external displacement by OnWalkerEvent.
    private bool IssueWalk(RoomKey target)
    {
        _issuingWalk = true;
        try
        {
            if (_abandonedDoors.Count > 0) _walker.RefuseDoorsOnNextWalk(_abandonedDoors);
            return _walker.WalkTo(target);
        }
        finally { _issuingWalk = false; }
    }

    private void ArmRetry(TimeSpan? delay = null)
    {
        Cancel(ref _retryTimer);
        _retryTimer = _schedule(delay ?? RetryBase, OnRetryDue);
    }

    private void OnRetryDue()
    {
        _retryTimer = null;
        EvaluateAndDispatch();
    }

    private void StartSchedulerTick()
    {
        Cancel(ref _schedulerTick);
        _schedulerTick = _schedule(SchedulerInterval, OnSchedulerTickDue);
    }

    private void OnSchedulerTickDue()
    {
        _schedulerTick = _schedule(SchedulerInterval, OnSchedulerTickDue);
        OnSchedulerTick();
    }

    private void StartEngageTimer(TimeSpan window)
    {
        Cancel(ref _engageTimer);
        _engageInterval = window;
        _engageTimer = _schedule(window, OnEngageTick);
    }

    // The window keeps ticking until the engagement finishes: a tick while frozen
    // by the master switch is passed over, and the next one is acted on.
    private void OnEngageTick()
    {
        _engageTimer = _schedule(_engageInterval, OnEngageTick);
        OnEngageTimerFired();
    }

    private static void Cancel(ref IDisposable? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private static TimeSpan RetryDelayAfter(int failedEntries) =>
        TimeSpan.FromMilliseconds(Math.Min(
            RetryBase.TotalMilliseconds * Math.Pow(2, failedEntries - 1), RetryCap.TotalMilliseconds));

    // Counts a failed entry and returns how long to wait before the next try. Said
    // at Warn on the first failure and again when the wait first reaches the cap,
    // not on every cycle in between.
    private TimeSpan NoteFailedEntry(string detail)
    {
        _failedEntries = CurrentTarget is { } lair && _failedEntryLair == lair ? _failedEntries + 1 : 1;
        _failedEntryLair = CurrentTarget;
        TimeSpan wait = RetryDelayAfter(_failedEntries);
        string said = $"walker failed: {detail} (entry {_failedEntries} to {CurrentTarget}; retrying in {wait.TotalSeconds:0}s)";
        if (_failedEntries == 1 || (wait == RetryCap && RetryDelayAfter(_failedEntries - 1) < RetryCap))
            _log?.Warn("AutoLair", said);
        else
            _log?.Debug("AutoLair", said);
        return wait;
    }

    // Take over the doors the walk that just ended gave up on that this character
    // can't realistically open. One it gave up on at fair odds is bad luck and is
    // tried again the next time the run plans a walk to its lair.
    private void NoteDoorsTheWalkGaveUpOn()
    {
        if (_walker.AbandonedDoors.Count == 0) return;
        IRoomFilter? filter = _walker.AutomaticWalkFilter();
        foreach ((RoomKey From, RoomKey To) door in _walker.AbandonedDoors)
        {
            if (!KeepsDoorForTheRun(door, filter))
            {
                _log?.Info("AutoLair",
                    $"the door from {door.From} to {door.To} didn't open at fair odds; it is tried again on the next visit");
                continue;
            }
            if (_abandonedDoors.Add(door))
            {
                _abandonedDoorsVersion++;
                _log?.Info("AutoLair",
                    $"the door from {door.From} to {door.To} couldn't be opened; walks and wait rooms go round it for the rest of this run");
            }
        }
    }

    // A door stays out for the whole run only when the character can't realistically
    // open it: neither its pick nor its bash can work, or the pick is under
    // DoorPolicy.PoorPickChancePercent a try (the filter's own door rule, so the
    // threshold is the one routes go round). With the stats unread there is no
    // chance to compute, and the filter says neither: fair odds, tried again.
    private bool KeepsDoorForTheRun((RoomKey From, RoomKey To) door, IRoomFilter? filter)
    {
        if (filter is null || _graph.GetRoom(door.From) is not Room room) return false;
        foreach (RoomExit exit in room.Exits.Values)
        {
            if (!exit.Target.Equals(door.To) || exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) continue;
            if ((filter.DescribeExitBlock(in exit) & (ExitBlockReason.Door | ExitBlockReason.LockedDoor)) != 0
                || filter.IsPoorOddsDoor(in exit))
                return true;
        }
        return false;
    }

    // What this run's walks are planned with: the walker's own filter for a walk
    // nobody chose a route for, with the doors the run gave up on refused.
    private IRoomFilter? RouteFilter()
    {
        IRoomFilter? automatic = _walker.AutomaticWalkFilter();
        return _abandonedDoors.Count == 0 ? automatic : new AbandonedDoorsFilter(automatic, _abandonedDoors);
    }

    private List<LairCandidate> BuildCandidates(RoomKey current)
    {
        List<LairCandidate> cands = new(_markers.Count);
        IRoomFilter? filter = RouteFilter();

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

            (RoomKey? waitRoom, int? hops) = PickWaitRoomUnlessKnownUnreachable(current, lair, filter);
            if (waitRoom is not null) _lairsLeftOut.Remove(lair);
            else if (_lairsLeftOut.Add(lair))
                _log?.Info("AutoLair", $"lair {lair} can't be scheduled from {current} as things stand (no route, or no room to wait in beside it); leaving it out until it can");
            cands.Add(new LairCandidate(lair, readyAt, hops, waitRoom));
        }
        return cands;
    }

    private (RoomKey? waitRoom, int? hops) PickWaitRoomUnlessKnownUnreachable(
        RoomKey current, RoomKey lair, IRoomFilter? filter)
    {
        int ruleStamp = filter?.DoorRuleStamp ?? 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_noRoute.TryGetValue(lair, out NoRouteVerdict verdict)
            && verdict.From.Equals(current)
            && verdict.DoorsVersion == _abandonedDoorsVersion
            && verdict.RuleStamp == ruleStamp
            && now - verdict.At < NoRouteRecheck)
            return (null, null);

        (RoomKey? waitRoom, int? hops) = PickWaitRoom(current, lair, filter);
        if (waitRoom is null) _noRoute[lair] = new NoRouteVerdict(current, _abandonedDoorsVersion, ruleStamp, now);
        else _noRoute.Remove(lair);
        return (waitRoom, hops);
    }

    // Choose the wait-room for lair from current. Preferred shape: the
    // room immediately before lair on the route the walk will take, so the
    // step in from it is one move (user, 2026-10-10: a wait room can be any
    // room that isn't the lair, ideally the one a step away). The route is
    // planned as the walker plans it: a search with no filter put the wait
    // room behind a door the character can't open (the Ancient Coliseum's
    // Viewing Stands, one locked step above the Arena), and the walk in from
    // there then went the long way round through two other lairs. When the
    // preferred wait-room is itself a marked lair (a stray spawn check), falls
    // back along that same route to the nearest room before it that isn't one,
    // since the lair's own neighbours can lie behind the door the route avoids;
    // and to the closest non-marked neighbour of the lair only when the route
    // has no such room. Returns (null, null) when no eligible wait-room exists,
    // signalling the candidate is unschedulable: a lair this character has no
    // route to is one of those.
    internal (RoomKey? waitRoom, int? hops) PickWaitRoom(RoomKey current, RoomKey lair, IRoomFilter? filter)
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
            (RoomKey? alt, _) = NearestNonMarkedNeighbour(current, lair, filter);
            return alt is null ? (null, null) : (alt, 1);
        }

        IReadOnlyList<Direction>? path = _bfs.FindPath(current, lair, filter);
        if (path is null || path.Count == 0)
        {
            _log?.Debug("AutoLair", $"searched the map for a route from {current} to lair {lair}: none");
            return (null, null);
        }

        // Walk the path to recover the room sequence and locate the
        // hop-before-lair as the natural wait-room.
        IReadOnlyList<RoomKey> roomSeq = RoomsAlongPath(current, path);
        if (roomSeq.Count == 0) return (null, null);

        RoomKey natural = roomSeq.Count == 1 ? current : roomSeq[^2];
        int hops = roomSeq.Count - 1; // hops from current to natural (excludes lair entry)

        // Natural wait-room is itself a marker → would trigger that lair's spawn
        // check when we waited there. Back along the route for the nearest room that
        // isn't one: the route is the one the walk takes, so a room on it is
        // reached without the door the route goes round. Only a route made of
        // lairs falls back to a neighbour of `lair`.
        if (_markers.ContainsKey(natural))
        {
            for (int i = roomSeq.Count - 3; i >= -1; i--)
            {
                RoomKey candidate = i < 0 ? current : roomSeq[i];
                if (!_markers.ContainsKey(candidate)) return (candidate, i + 1);
            }
            (RoomKey? alt, int? altHops) = NearestNonMarkedNeighbour(current, lair, filter);
            return (alt, altHops);
        }

        return (natural, hops);
    }

    // BFS-shortest non-marker neighbour of lair from current. Used when
    // the natural wait-room (the BFS path's second-to-last room) is a
    // marker we don't want to disturb.
    private (RoomKey? key, int? hops) NearestNonMarkedNeighbour(RoomKey current, RoomKey lair, IRoomFilter? filter)
    {
        if (_graph.GetRoom(lair) is not Room room) return (null, null);

        RoomKey? bestKey = null;
        int bestDist = int.MaxValue;
        foreach (RoomExit exit in room.Exits.Values)
        {
            RoomKey n = exit.Target;
            if (n.Equals(lair)) continue;
            if (_markers.ContainsKey(n)) continue;
            int? d = _bfs.DistanceBetween(current, n, filter);
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
        _entryOwedAfterFreeze = false;
        CurrentEntryArrivalAt = entryArrival;

        TimeSpan wait = entryArrival - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            // Past entry-time on arrival → step in immediately.
            EnterLairNow();
            return;
        }
        Cancel(ref _entryTimer);
        _entryTimer = _schedule(wait, OnEntryTimerFired);
    }

    // The entry time came while the master switch had the run frozen. The timer's
    // interval is the whole wait, so left running it would enter one more full
    // wait after the switch came back on: the entry is owed instead, and made as
    // the freeze lifts (OnGatesChanged).
    private bool _entryOwedAfterFreeze;

    private void OnEntryTimerFired()
    {
        _entryTimer = null;
        if (FrozenByMasterSwitch)
        {
            _entryOwedAfterFreeze = true;
            return;
        }
        if (!IsActive || IsPaused) return;
        EnterLairNow();
    }

    private void EnterLairNow()
    {
        if (CurrentTarget is not { } lair) return;
        SetPhase(AutoLairPhase.Entering);
        CurrentEntryArrivalAt = null; // we're stepping in; the latch served its purpose
        _log?.Info("AutoLair", $"entering {lair}.");
        if (!IssueWalk(lair))
        {
            _log?.Warn("AutoLair", $"walker rejected entry to {lair}; retrying in 2s.");
            ArmRetry();
        }
    }

    // ----- engage timer --------------------------------------------

    private void StartEngagement()
    {
        _failedEntryLair = null;
        _failedEntries = 0;
        SetPhase(AutoLairPhase.Engaging);
        _engageSawCombat = false;
        // Arm the SHORT window first. Until a fight starts we're only waiting to
        // find out whether there's anything here at all, and a lair that hasn't
        // respawned should cost seconds, not the full engage timeout — that was
        // most of the idle time in report stock-20260908-192900. The window
        // becomes EngageTimeoutSeconds the moment combat actually starts.
        StartEngageTimer(EmptyLairGrace);
        _log?.Info("AutoLair",
            $"engaging — leaving when the fight ends, "
            + $"or in {EmptyLairGrace.TotalSeconds:0}s if the lair is empty.");
    }

    // Combat gate asserted/cleared, or loot finished — see OnGatesChanged for why
    // both matter. The timer is now only the upper bound.
    private void OnGatesChanged()
    {
        if (_entryOwedAfterFreeze && !FrozenByMasterSwitch)
        {
            _entryOwedAfterFreeze = false;
            if (Phase == AutoLairPhase.Waiting && IsActive && !IsPaused) EnterLairNow();
            // No return: the user may have walked into the lair by hand while
            // frozen, and then this change is also the fight's to read.
        }
        if (Phase != AutoLairPhase.Engaging) return;
        if (!IsActive || IsPaused) return;
        if (_coordinator is null) return;
        if (FrozenByMasterSwitch) return;

        // CombatStateTracker owns this gate and clears it authoritatively when a
        // room re-display shows no engageable monster left — the reliable
        // end-of-combat signal on stock. The raw `*Combat Off*` line is not: the
        // server emits one every time we cast, and once per strike for
        // non-sustaining attacks, so a Warlock nuking mid-fight would look done
        // several times per kill (GAME_MECHANICS "Kill detection and monster-kill
        // message order").
        if (_coordinator.IsGateAsserted(MovementCoordinator.CombatGate))
        {
            if (!_engageSawCombat)
            {
                // A fight started, so the short empty-lair window no longer
                // applies — give the kill the full engage budget.
                _engageSawCombat = true;
                StartEngageTimer(TimeSpan.FromSeconds(Math.Max(1, EngageTimeoutSeconds)));
            }
            return;
        }

        // Entering a lair asserts nothing until a monster is actually seen, so an
        // un-asserted gate right after entry means "combat hasn't started", not
        // "combat is over". Leaving on that would walk straight back out. The
        // empty-lair window below is what handles "nothing ever showed up".
        if (!_engageSawCombat) return;

        // The kill's drops are still being picked up — auto-get/cash asserts this
        // while gets are outstanding. Walking off now would abandon the loot we
        // just fought for.
        if (_coordinator.IsGateAsserted(MovementCoordinator.AcquisitionGate)) return;

        FinishEngagement("fight over and loot collected");
    }

    private void OnEngageTimerFired()
    {
        if (FrozenByMasterSwitch) return;
        // Which window just expired depends on whether a fight ever started.
        FinishEngagement(_engageSawCombat
            // A fight that never resolves — unkillable, fled, or a missed
            // clear-signal — must not park the scheduler here forever.
            ? $"engage timeout ({EngageTimeoutSeconds}s)"
            // Nothing turned up in the grace window, so the lair hadn't respawned;
            // go find one that's ready.
            : $"lair empty after {EmptyLairGrace.TotalSeconds:0}s — nothing to fight");
    }


    private void FinishEngagement(string why)
    {
        Cancel(ref _engageTimer);
        if (!IsActive || IsPaused) return;
        string readyAgain = CurrentTarget is { } lair && _timers.NextReadyAt(lair, GetOverride(lair)) is { } at
            ? $" ({lair} ready again {at.ToLocalTime():HH:mm:ss})"
            : "";
        _log?.Info("AutoLair", $"engagement done — {why}{readyAgain}; picking the next lair.");
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
                LastWalkerFailure = null;   // arrived → approach is no longer stuck
                NoteDoorsTheWalkGaveUpOn();
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
                LastWalkerFailure = evt.Detail;   // surfaced on the nav status while we retry
                NoteDoorsTheWalkGaveUpOn();
                TimeSpan retryIn = RetryBase;
                // The walk into the lair failed. Left in Entering, the retry below
                // re-evaluates and dispatches nothing, since only Approaching and
                // Waiting dispatch: the run stood in the wait room for good (user,
                // 2026-10-10: the character stood at the door after its picks
                // failed). Back to Approaching with the leg dropped, as for a
                // stopped walk, so the retry picks again from where we stand.
                if (Phase == AutoLairPhase.Entering)
                {
                    retryIn = NoteFailedEntry(evt.Detail);
                    LastDecision = null;
                    CurrentTarget = null;
                    CurrentWaitRoom = null;
                    SetPhase(AutoLairPhase.Approaching);
                }
                else
                {
                    _log?.Warn("AutoLair", $"walker failed: {evt.Detail}");
                }
                ArmRetry(retryIn);
                break;

            case WalkEventKind.StepCompleted:
                LastWalkerFailure = null;   // the approach is moving again
                break;

            case WalkEventKind.Stopped:
                // A Stopped that fired synchronously inside our own IssueWalk is
                // our previous walk being superseded by the new one — expected,
                // not an external displacement. Swallow it: reacting would null
                // the target we just set and re-dispatch on the next tick, which
                // supersedes again, a ~1/sec loop that never enters the lair. A
                // genuine external stop arrives with _issuingWalk false.
                if (_issuingWalk) break;

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
                    Cancel(ref _entryTimer);
                    CurrentEntryArrivalAt = null;
                    LastDecision = null;
                    CurrentTarget = null;
                    CurrentWaitRoom = null;
                    SetPhase(AutoLairPhase.Approaching);
                    ArmRetry();
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
            Cancel(ref _entryTimer);
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
