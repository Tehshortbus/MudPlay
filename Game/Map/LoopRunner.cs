using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Executes a saved Loop against the wire. Sibling of AutoWalkManager — shares the
// same MovementCoordinator for pause gates, the same RoomTracker for move
// confirmation, and the same EngineRecoveryGate for tier-1/2/3 location recovery.
// Operates on LoopSteps (which include CommandLoopStep.DelayMs pauses the walker
// doesn't need) and supports circular loops that restart at the top after the
// last step.
public sealed class LoopRunner : IRecoverableEngine
{
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly WirePromptScanner? _promptScanner;
    private readonly LogService? _log;

    // Marshals a resume-triggered step dispatch onto the next UI tick. The
    // coordinator fires PauseStateChanged synchronously mid server-line burst
    // (a Combat gate clearing on a room re-display); a LATER line in the SAME
    // burst can assert another gate — a party @wait — that must re-pause us
    // before the next move leaves. Posting past the burst lets that @wait land
    // first (see DeferResumeDispatch). Defaults to Dispatcher.UIThread.Post.
    private readonly Action<Action> _postToUi;
    private readonly EngineRecoveryGate? _recovery;
    private readonly BfsMapper? _bfs;
    private readonly AutoWalkManager? _walker;
    // Path filter used by the runner's BFS calls (rotation + closest-waypoint
    // pick). When set this is typically AppServices.Movement; changes to its
    // avoided-rooms list arrive via NotifyAvoidedChanged. Once the run has given up
    // on a door it is that filter with the door refused on top
    // (AbandonedDoorsFilter), and the bare one again from the next run.
    private IRoomFilter? _filter;
    private readonly IRoomFilter? _runFilter;
    // Doors this run tried and couldn't open, by the room each leaves and the room
    // it leads to: the walker's rule (AutoWalkManager.TryGoRoundDoor) for a circuit.
    private readonly HashSet<(RoomKey From, RoomKey To)> _abandonedDoors = new();
    public IReadOnlyCollection<(RoomKey From, RoomKey To)> AbandonedDoors => _abandonedDoors;
    private Action<byte[]>? _wireSender;
    private Action? _preMoveHook;
    // StealthManager.ReadyToMoveSneaking — false = hold the step (a sneak is settling).
    private Func<bool>? _moveReadyCheck;
    public void SetMoveReadyCheck(Func<bool> check) => _moveReadyCheck = check;
    private Action<RoomKey>? _approachRoomHook;

    // (source, dest) → teleport keyword resolver, mirroring the walker's
    // AutoWalkManager.SetTeleportResolver. Lets the circuit cross a
    // RoomExitHint.Teleport exit with the same keyword the walker would use. Null
    // until wired.
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;

    // True when the local character should relay a teleport keyword to party
    // followers (`.@party <kw>`). Mirrors the walker's
    // AutoWalkManager.SetPartyLeaderCheck. Null until wired.
    private Func<bool>? _isLeaderWithFollowers;

    // True while the character is Confused (ConditionTracker.IsConfused). Read
    // by EnterRecovery — see MaxRecoverAttempts below. Null until wired.
    private Func<bool>? _isConfused;

    // Fired after a leading character crosses a party-splitting CMD teleport so
    // the party engine reforms the dissolved group. Mirrors the walker's
    // AutoWalkManager.SetPartySplitHandler. Null until wired.
    private Action? _onLeaderPartySplit;

    // Door-open enqueuer — the runner calls this when a circuit step crosses a
    // closed Door / KeyLocked exit, mirroring the walker's DoorOpenManager
    // integration so a loop can bash/pick/key its way through instead of failing
    // the lap. Null until wired (unit harnesses leave it unbound and keep the
    // fail-loudly path). _doorStopAll drains the FSM when a run is torn down
    // mid-open; _awaitingDoorOpen gates the tracker handler so the FSM's own
    // bash/pick re-observations don't get mistaken for a landing.
    private Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? _doorEnqueuer;
    private Action? _doorStopAll;
    private bool _awaitingDoorOpen;

    // Hidden-exit reveal enqueuer — mirrors the door integration above for a
    // SearchableHidden exit crossed mid-circuit: fire the shared sea <dir> retry
    // loop and cross from OnHiddenRevealReply once the exit appears, rather than
    // failing the whole lap. Null until wired (unit harnesses leave it unbound and
    // keep the fail-loudly path). _hiddenSearchStopAll drains the FSM on teardown;
    // _awaitingHiddenReveal gates the tracker handler so the sea re-observations
    // of the source room aren't mistaken for a landing.
    private Action<Direction, string, Action<HiddenSearchResult>>? _hiddenSearchEnqueuer;
    private Action? _hiddenSearchStopAll;
    private bool _awaitingHiddenReveal;

    // Winch enqueuer — mirrors the door/hidden integration for a MultiActionHidden
    // winch exit crossed mid-circuit: pull the winch, wait for it to turn + the gate
    // to open, then cross from OnWinchReply. Null until wired (unit harnesses leave it
    // unbound and keep the synchronous dispatch).
    private Action<Direction, string, bool, string, Action<WinchResult>>? _winchEnqueuer;
    private Action? _winchStopAll;
    private bool _awaitingWinch;

    // Trapped exits — mirrors the walker's trap integration: a trapped exit crossed
    // mid-circuit is disarmed first (by us, or by a capable party member on @trap)
    // instead of walking into the trap. Null until wired: unit harnesses walk through.
    // _trapClearedFor marks the step whose trap was just disarmed, so re-driving it
    // through the ordinary step path (a disarm ends a sneak, so the ready check gets
    // its re-sneak) crosses instead of disarming again.
    private Action<string, string, Action<string>>? _trapEnqueuer;
    private Func<bool>? _shouldDisarmTrap;
    private Action<string, Action<string>>? _trapDelegator;
    private Func<bool>? _canDelegateTrap;
    private Action? _trapDelegateStopAll;
    private bool _awaitingTrapDisarm;
    private (int Index, RoomKey Room, Direction Direction)? _trapClearedFor;

    private Loop? _loop;
    private int _index;

    // Runtime expansion of _loop's waypoints into the flat LoopStep sequence the
    // runner executes. Recomputed in Start after the rotation is committed; rebuilt
    // by NotifyAvoidedChanged when the filter changes. Always non-null while a loop
    // is active.
    private List<LoopStep> _expandedSteps = new();

    // Set by ReconcileExpandedSteps when a live per-room edit on the running-loop
    // rail adds or removes a command (changing the expanded step count): the runner
    // can't safely swap the step list mid-flight, so it re-expands at the next lap
    // wrap, where _index is 0 and the player is back at the entry room. Delay /
    // command-text edits that keep the step count are applied in place at once and
    // don't set this.
    private bool _reExpandAtLapEnd;
    private bool _stepInFlight;
    // A no-delay command block is on the wire and the game hasn't answered all of it.
    private bool _awaitingCommandReplies;
    public bool AwaitingCommandReplies => _awaitingCommandReplies;
    // What the wait still needs: one prompt for each command in the block, and, when
    // the block holds a bare Enter, the room shown again.
    private int _commandPromptsOwed;
    private bool _commandRedisplayAwaited;
    private bool _combatHoldLogged;
    private Func<bool>? _inCombat;
    // Bounds one wait for a command's replies (an echo knocked off the prompt row by
    // another line is never read). Re-armed while a fight the command started runs.
    private static readonly TimeSpan CommandReplyWait = TimeSpan.FromSeconds(3);

    // Whether the character is in combat. A waypoint command that starts a fight keeps
    // the loop in the room until it is over.
    public void SetInCombatProbe(Func<bool> inCombat) => _inCombat = inCombat;

    // True while a waypoint's command block is being written to the wire. The send is
    // synchronous through the outbound observers, which read it to tell the loop's
    // commands from the user's typing.
    public bool SendingOwnCommand { get; private set; }
    private RoomKey? _expectedMoveTarget;
    // The room the in-flight move was sent FROM. OnTrackerStateChanged ignores
    // tracker transitions while State != Running (a paused loop doesn't react
    // to events in real time), so a MoveRefusal that resolves mid-pause — the
    // tracker reverts Pending → Confirmed at this same room, but the running
    // handler never sees it — leaves this the only way for the resume path to
    // tell "refused, still here" apart from "still Pending, awaiting the
    // reply" or "arrived at target". See OnPauseChanged's resume branches.
    private RoomKey? _expectedMoveSource;

    // True when the runner flipped to LoopState.Paused while still in the approach
    // phase (the walker was driving us toward the loop's entry waypoint). Tells the
    // resume handler to transition back to LoopState.Approaching instead of trying
    // to send the loop's first step before the walker has actually finished.
    private bool _pausedFromApproach;

    // Set when the walker fires Finished for the approach while the runner is still
    // parked in the paused-from-approach window (its own resume handler ran before
    // ours in the coordinator's subscriber list, so it completed the walk and reset
    // to Idle before we could hand off). Buffers the arrival so the resume path
    // enters the circle instead of restoring Approaching and waiting for a Finished
    // that will never re-fire. (Live bug: loop "walks to the first room then just
    // sits there" until a second Run click.)
    private bool _approachFinishedWhilePaused;

    // Bounded auto-recovery counter. When a mid-circuit step blocks at its source
    // room, or the recovery gate hands back a room that isn't the step's expected
    // target, the runner re-determines its position and reroutes onto the nearest
    // loop segment (see EnterRecovery) instead of failing straight to Idle. This
    // caps how many consecutive recoveries we attempt before giving up; it resets
    // to 0 on any forward progress (AdvanceStep) and on a fresh (non-recovery)
    // Start, so a healthy loop always has the full budget. Not charged for an
    // attempt taken while the character is Confused — see EnterRecovery.
    private int _recoverAttempts;
    private const int MaxRecoverAttempts = 3;

    // Minimum spacing between recovery attempts. Without it the budget could be
    // spent in a single millisecond: a reroute from a room the tracker has wrong
    // re-blocks immediately, re-enters recovery, and repeats — three "attempts"
    // inside one second, none of which could have gone differently because nothing
    // about the world changed between them (report stock-20260904-143436). An
    // attempt is only a real chance if something has had time to change.
    private static readonly TimeSpan RecoveryAttemptSpacing = TimeSpan.FromSeconds(2);
    private DateTimeOffset _lastRecoveryAttemptAt = DateTimeOffset.MinValue;

    // Waypoint the walker is currently approaching during LoopState.Approaching.
    // Null when not approaching.
    private RoomKey? _approachTarget;

    // Room the rotated circle begins (and ends) at. Set when the runner picks the
    // entry waypoint — either immediately in Start for player-already-at-waypoint /
    // approach cases, or after the legacy / no-waypoints branch leaves it null.
    // Used by the Navigation overlay as the source for rendering the full cycle so
    // the visible polyline stays anchored to the cycle itself instead of shifting
    // under the player as they walk.
    private RoomKey? _circleStartRoom;

    // Set true the first time we begin the circle in a given Start session so
    // LoopEventKind.ReachedFirstWaypoint only fires once per session (not on every
    // wrap).
    private bool _firstWaypointReached;

    // One-shot, set by ResumeAfterDetour. An auto-deposit / bank detour fully
    // Stop()s the loop (clearing _firstWaypointReached) then re-Starts it to walk
    // the same circuit — but that is a continuation of the same hunting session,
    // not a new one, so BeginCircle must NOT re-fire ReachedFirstWaypoint (whose
    // side effect is a session-stats reset + a party @reset broadcast). Consumed
    // in BeginCircle, cleared on Reset.
    private bool _suppressFirstWaypointEvent;

    // Wall-clock anchor for the current lap. Set when BeginCircle first reaches the
    // first waypoint and refreshed on every wrap so CurrentLapTime reads correctly.
    private DateTimeOffset _lapStartedAt;

    private readonly List<TimeSpan> _lapDurations = new();
    private const int MaxLapHistory = 10;

    // Total laps completed this run. Distinct from _lapDurations.Count, which is
    // capped at MaxLapHistory — the UI's lap counter needs the true running total
    // so it keeps climbing past the 10th lap instead of freezing.
    private int _completedLaps;

    // Custom-command delay timer state. _delayRemaining tracks the time left when
    // the timer is stopped by a pause so resume continues from where it left off
    // rather than restarting the full duration.
    private IDisposable? _delayTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private TimeSpan _delayRemaining;
    private long _delayStartTimestamp;
    // A room command's own delay runs in real time from the command (Stopwatch
    // timestamp it ends at); 0 for the reply wait, which pauses with the loop.
    private long _delayDeadline;
    // The clocks the loop's times are read on: lap times on the wall clock, a room
    // command's delay on the Stopwatch.
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _timestamp;
    // A DispatcherTimer can't be set past ~24 days, and a delay may be far longer:
    // a long one is waited out in slices, each re-reading the deadline.
    private static readonly TimeSpan MaxDelaySlice = TimeSpan.FromHours(1);

    // Time left on a room command's delay, or null when none is running.
    public TimeSpan? CommandDelayRemaining => _delayDeadline == 0 ? null : DeadlineRemaining();

    private TimeSpan DeadlineRemaining()
    {
        TimeSpan left = TimeSpan.FromSeconds((double)(_delayDeadline - _timestamp()) / Stopwatch.Frequency);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    // In-flight stall watchdog. A move can be sent, go Pending, then be interrupted
    // by a combat gate before it confirms; on resume the runner keeps the step in
    // flight "awaiting confirmation, not re-sending" (see OnPauseChanged resume),
    // which is correct WHEN the confirmation is merely delayed — but when the move
    // was swallowed by the interrupting combat (the player never left the room) that
    // confirmation never arrives, and in a same-named-room zone no Confirmed-at-
    // source/target transition ever fires to break the wait, so the loop hangs
    // forever (report paradigm-20260807-133143: 5½ min standing in "Cleared Fields").
    // This timer bounds that wait: armed when a resume leaves the step in flight,
    // disarmed on advance / pause / recovery / stop. On expiry — still Running, still
    // Pending — it escalates to the recovery gate, which re-establishes position
    // (Paradigm `rm` resync / stock footprint backtrack) and reroutes, re-sending the
    // interrupted move without risking an overshoot.
    private IDisposable? _stallWatchdog;
    internal static readonly TimeSpan StallWatchdogInterval = TimeSpan.FromSeconds(10);

    public LoopState State { get; private set; } = LoopState.Idle;

    // True while the runner is still getting TO the loop's start waypoint and hasn't yet
    // entered the circle — the live Approaching walk, OR a pause that fired DURING that
    // approach (a combat pause, a coordinator hold). Distinct from a pause mid-circuit,
    // which leaves _pausedFromApproach false. The map keys the red approach-preview line off
    // this (rather than State==Approaching alone) so a combat pause during the walk-to
    // doesn't briefly expose the green running-loop line underneath.
    public bool IsApproachInFlight =>
        State == LoopState.Approaching
        || (State == LoopState.Paused && _pausedFromApproach);

    public Loop? CurrentLoop => _loop;
    public int CurrentIndex => _index;

    // The room an in-flight move is heading INTO (null when no move is pending). While
    // a loop move is unconfirmed the RoomTracker still reports the room we're leaving, so
    // combat-suppression checks read this to judge against the room we're entering — see
    // AppServices.CombatSuppressedInCurrentRoom (report paradigm-20260915-122832).
    public RoomKey? ExpectedMoveTarget => _expectedMoveTarget;

    // True while a loop step is on the wire awaiting its landing — including while the
    // loop is paused mid-step (see LoopCombatSuppression.JudgeEnteringRoom).
    // What the loop is standing on right now, for a bug report: each answer it has
    // sent for and not had.
    public IReadOnlyList<string> DescribeWaits()
    {
        List<string> waits = new();
        if (_stepInFlight)
            waits.Add("a step is in flight" + (_expectedMoveTarget is { } to ? $" (expects {to})" : string.Empty));
        if (_awaitingCommandReplies)
            waits.Add($"a command's replies ({_commandPromptsOwed} prompt(s) owed"
                + (_commandRedisplayAwaited ? ", a room re-display awaited" : string.Empty) + ")");
        if (_awaitingDoorOpen) waits.Add("the door handler");
        if (_awaitingHiddenReveal) waits.Add("the hidden-exit search");
        if (_awaitingWinch) waits.Add("the winch handler");
        if (_awaitingTrapDisarm) waits.Add("the trap disarm");
        if (_emptyRoom.Active) waits.Add("an empty room for a waypoint command");
        if (_fleeHolding) waits.Add("held for a flee");
        if (_recoverAttempts > 0)
            waits.Add($"{_recoverAttempts} recovery attempt(s), the last at {_lastRecoveryAttemptAt.ToLocalTime():HH:mm:ss}");
        return waits;
    }

    public bool IsStepInFlight => _stepInFlight;
    public bool IsAwaitingTrapDisarm => _awaitingTrapDisarm;

    // Name of the most recently RUN loop, retained after the run stops (unlike
    // CurrentLoop, which nulls on Stop/Reset). Set when a loop starts and only
    // overwritten by the next loop — so after a death or manual stop, @path can
    // still tell a party member which loop the player was on, to help them
    // resume. Null until the first loop of the session runs. LastRunLoopAt lets
    // MovementStatus pick the more recent of loop vs auto-lair for @path.
    public string? LastRunLoopName { get; private set; }
    public DateTimeOffset LastRunLoopAt { get; private set; } = DateTimeOffset.MinValue;

    // A snapshot of the most recently RUN loop, retained past Stop/Reset like
    // LastRunLoopName — but the whole Loop (waypoints + notes + settings), so it can
    // be re-RUN, not just named. This is what lets "@loop last" and the Loop-mode
    // chip re-run / re-open the last loop even when it was an ad-hoc one that was
    // never saved (LastRunLoopName alone can't be resolved back to a Loop for an
    // unsaved run). A distinct copy from the running _loop, so later edits to the
    // running loop never reach the retained snapshot.
    public Loop? LastRunLoop { get; private set; }

    // The game-data set LastRunLoop was run on: its rooms are that set's. It is kept
    // through a switch of game data like the snapshot itself, so whoever would put
    // the snapshot where it can be saved (the Loop-mode chip's pre-load) can tell it
    // is another set's.
    public string? LastRunLoopGameData { get; private set; }

    // Loop the user has "loaded" (staged) but not yet started — the Manage dialog's
    // Load action records it here. Distinct from CurrentLoop (which is only set
    // while a run is live): a staged loop sits idle until something begins it. The
    // toolbar Start button reads this to run the staged loop without reopening the
    // Manage window. Null until the user stages one.
    public Loop? StagedLoop { get; private set; }

    // Every loop this runner holds on to: the one running, the snapshot of the last
    // run, the staged one and the one to restart after a reconnect. Any of them can
    // be a copy the catalogue no longer holds, so the character's settings for a loop
    // are brought to them here when they change (AppServices, on
    // LoopManager.SettingsChanged).
    public IEnumerable<Loop> HeldLoops
    {
        get
        {
            if (_loop is { } running) yield return running;
            if (LastRunLoop is { } last) yield return last;
            if (StagedLoop is { } staged) yield return staged;
            if (_pendingReconnectResume is { } resume) yield return resume;
        }
    }

    // Remember loop as the staged loop (see StagedLoop) without starting movement.
    // Idempotent — re-staging simply replaces the remembered loop.
    public void Stage(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        StagedLoop = loop;
    }

    // Forget the staged loop: the game data changed to another set, and its rooms
    // are the last one's.
    public void Unstage() => StagedLoop = null;

    // Rename the loop currently in flight in place, keeping the lap / step
    // position intact. The Save-current chip persists a rename without restarting
    // the runner (a rename doesn't change the path), but the live status readout
    // reads CurrentLoop.Name — so the editor pushes the new name here and we raise
    // a benign Renamed event, giving the nav header + status chip an immediate
    // re-read instead of holding the old (often builder-generated timestamp) name
    // until the next step ticks. No-op when nothing is running or the name is
    // unchanged.
    public void RenameCurrentLoop(string newName)
    {
        if (_loop is null || string.IsNullOrWhiteSpace(newName)) return;
        if (string.Equals(_loop.Name, newName, StringComparison.Ordinal)) return;
        // The live-vs-simulated check follows a running loop through the program log
        // by name; without this line a loop renamed mid-run never closes its session.
        _log?.Info("LoopRunner", $"Renamed: loop='{_loop.Name}' → '{newName}'");
        _loop.Name = newName;
        Raise(new LoopEvent(LoopEventKind.Renamed, newName));
    }

    // Waypoint the walker is approaching, or null when not in LoopState.Approaching.
    public RoomKey? ApproachTarget => _approachTarget;

    // Walking back into the circuit after an errand (ResumeAfterDetour) — a bank,
    // sell or training trip — until the lap picks up again. The Navigation top bar
    // reads it as "Back to Loop".
    private bool _returningFromDetour;
    public bool ReturningFromDetour => _returningFromDetour && State is LoopState.Approaching or LoopState.Paused;

    // Room the running cycle begins + ends at (the entry the run picked). Stable from
    // the moment Start picks it until the runner resets. Null for legacy v1 loops,
    // which load with no waypoints and so have no canonical start anchor.
    public RoomKey? CircleStartRoom => _circleStartRoom;

    // Total steps in the rotated circle. 0 when no loop is active.
    public int StepCount => _expandedSteps.Count;

    // Time elapsed in the current lap. Zero when no loop is running or the first
    // waypoint hasn't been reached. Computed on each read so VM bindings can poll via
    // a periodic tick. A fight, a rest or a recovery pauses the loop mid-lap without
    // restarting the lap, so the clock keeps counting through it (the lap's recorded
    // time includes it too); showing zero there blanked the readout on every fight
    // (report paradigm-20261002-170259).
    public TimeSpan CurrentLapTime
    {
        get
        {
            if (State == LoopState.Idle) return TimeSpan.Zero;
            if (_lapStartedAt == default) return TimeSpan.Zero;
            return _now() - _lapStartedAt;
        }
    }

    // Mean of the last MaxLapHistory completed laps. TimeSpan.Zero when no lap has
    // completed yet.
    public TimeSpan AverageLapTime
    {
        get
        {
            if (_lapDurations.Count == 0) return TimeSpan.Zero;
            long totalTicks = 0;
            foreach (TimeSpan t in _lapDurations) totalTicks += t.Ticks;
            return TimeSpan.FromTicks(totalTicks / _lapDurations.Count);
        }
    }

    // Read-only window onto the rolling lap-time history (oldest first).
    public IReadOnlyList<TimeSpan> LapHistory => _lapDurations;

    // Laps completed this run — the true running total (unlike LapHistory.Count,
    // which caps at MaxLapHistory). The lap the walker is currently on is this + 1.
    public int CompletedLaps => _completedLaps;

    private readonly RoomGraphManager? _graph;

    // ----- IRecoverableEngine ----------------------------------------

    public string Name => "LoopRunner";

    // A flee retreats toward the room the circuit began at. Null for legacy v1
    // loops with no canonical circle anchor — flee then inverts the last move.
    public RoomKey? JourneyOrigin => _circleStartRoom;

    public Direction? PeekNextPlannedDirection()
    {
        if (_loop is null || _index >= _expandedSteps.Count) return null;
        return _expandedSteps[_index] is MoveLoopStep move ? move.Direction : (Direction?)null;
    }

    public Direction? PlannedDirectionFrom(RoomKey room)
    {
        if (_graph is null || _circleStartRoom is not { } here) return null;
        foreach (LoopStep step in _expandedSteps)
        {
            if (step is not MoveLoopStep move) continue;
            if (here.Equals(room)) return move.Direction;
            if (_graph.GetRoom(here) is not { } r || !r.Exits.TryGetValue(move.Direction, out RoomExit exit)) return null;
            here = exit.Target;
        }
        return null;
    }

    // Every exit the circuit itself takes, with the room it is taken from, in lap
    // order; a room the lap passes twice is there twice. The walk to the circuit's
    // first room is not part of it. Empty with no loop running, or where the map
    // can't follow the lap.
    public IReadOnlyList<(RoomKey From, RoomExit Exit)> CircuitExits()
    {
        var exits = new List<(RoomKey, RoomExit)>();
        if (_graph is null || _circleStartRoom is not { } here) return exits;
        foreach (LoopStep step in _expandedSteps)
        {
            if (step is not MoveLoopStep move) continue;
            if (_graph.GetRoom(here) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            exits.Add((here, exit));
            here = exit.Target;
        }
        return exits;
    }

    public IReadOnlyList<Direction> PeekPlannedDirections(int count)
    {
        int n = _expandedSteps.Count;
        if (count < 1 || _loop is null || n == 0) return Array.Empty<Direction>();
        var dirs = new List<Direction>(count);
        // The step at _index has already been walked when its move landed while we
        // were paused: the pause (a fight, or the flee asking this) keeps the arrival
        // from advancing the index. Counting it sent a forward flee off with the
        // direction it had just come in by.
        int from = _index + (InFlightStepHasLanded() ? 1 : 0);
        // A flee already holds the loop: this is a later leg of its run, and its
        // earlier legs have walked steps of the lap the index knows nothing of. The
        // lap goes on from the step that leads into the room we stand in.
        if (_fleeHolding && _tracker.State.CurrentRoom?.Key is { } here
            && FirstStepLeadingTo(here, _index, n, out _) is >= 0 and int walked)
            from = walked + 1;
        // Loops are circular — wrap around the circuit to fill the count. Stop at
        // the first command / delay step: a forward flee sends plain cardinals
        // only and can't run a custom-command step mid-escape.
        for (int k = 0; k < n && dirs.Count < count; k++)
        {
            // A teleport step counts as a custom command too — LoopExpander turns a
            // BFS path straight into MoveLoopSteps, so a circuit that crosses a CMD
            // teleport carries one, and it can't go out as a bare direction.
            if (_expandedSteps[(from + k) % n] is not MoveLoopStep move
                || !move.Direction.IsCardinal()) break;
            dirs.Add(move.Direction);
        }
        return dirs;
    }

    // The move of the step at _index has been sent and the tracker stands in the
    // room it was headed for, though the index has not been advanced yet.
    private bool InFlightStepHasLanded() =>
        _stepInFlight
        && _index < _expandedSteps.Count
        && _expandedSteps[_index] is MoveLoopStep
        && _expectedMoveTarget is { } target
        && _tracker.State.CurrentRoom?.Key.Equals(target) == true;

    public void SendBacktrackMove(Direction direction)
    {
        // Tier-3 backtrack: send a single direction without advancing
        // our own loop index. The tracker still records the move so its
        // FSM stays in sync with the observation it'll receive.
        // Cardinals only, same as the walker's — callers must keep
        // Direction.Teleport out rather than have this swallow it.
        // A stopped loop moves nobody: a flee or recovery that outlived it (a death,
        // a drop of the line) must not walk the character on through it.
        // HealthManager ends a flee whose engine was stopped at the next room change,
        // so for a flee this is a backstop.
        if (State == LoopState.Idle)
        {
            _log?.Info("LoopRunner", $"backtrack move {direction} not sent: the loop is stopped");
            return;
        }
        (byte[] bytes, string what) = SpecialExitDispatch.EncodeBacktrack(_tracker, direction);
        _preMoveHook?.Invoke();
        Write(bytes, what);
    }

    public void PauseForRecovery(string reason)
    {
        if (State != LoopState.Running) return;
        _log?.Warn("LoopRunner",
            $"PauseForRecovery: gate took over at step {_index + 1}; reason={reason}");
        State = LoopState.Paused;
        Raise(new LoopEvent(LoopEventKind.Paused, $"recovery: {reason}"));
    }

    // A flee (HealthManager) retreated on purpose — often right back out of the room
    // the step was headed for, and hit-and-run does it lap after lap before a step can
    // complete. Resume the same way, but don't charge the reroute against the
    // recovery budget (report paradigm-20260927-000542: three hit-and-runs "exhausted"
    // recovery and failed the loop).
    // Report paradigm-20260927-023516: a flee started while combat already had the loop
    // paused; PauseForRecovery no-ops unless Running, so when combat cleared the loop
    // resumed, advanced its step on top of the flee's in-flight move, and later tried
    // that step from the room the flee landed in ("no exit U").
    public void PauseForFlee(string reason)
    {
        if (_loop is null) return;
        _fleeHolding = true;
        if (State == LoopState.Running) PauseForRecovery(reason);
        else _log?.Info("LoopRunner", $"flee takes over while paused at step {_index + 1}; reason={reason}");
    }

    private bool _fleeHolding;

    public void ResumeAfterFlee(RoomKey landedAt, bool carryOnFromHere = false)
    {
        _fleeHolding = false;
        _resumingAfterFlee = true;
        // Set before the adoption: a run that did not stop ahead on the lap is
        // still not walked back, it is re-planned from where it stopped.
        _carryOnAfterFlee = carryOnFromHere;
        if (carryOnFromHere) TakeStepsRunForwardAsWalked(landedAt);
        try { ResumeAfterRecovery(landedAt); }
        finally
        {
            _resumingAfterFlee = false;
            _carryOnAfterFlee = false;
        }
    }

    private bool _resumingAfterFlee;
    private bool _carryOnAfterFlee;

    // A forward flee walked the lap's own next steps (PeekPlannedDirections). Take
    // them as walked: the last becomes the step in flight with its move landed, so
    // the resume advances past it and the lap goes on from the room the run stopped
    // in (user, 2026-10-10), where walking back would only meet again what was run
    // from. Nothing is adopted when that room is not ahead on the lap, this one or
    // the start of the next: the run was turned onto another exit.
    private void TakeStepsRunForwardAsWalked(RoomKey landedAt)
    {
        if (State != LoopState.Paused) return;
        int was = _index;
        int k = FirstStepLeadingTo(landedAt, _index, _expandedSteps.Count, out RoomKey from);
        // Not ahead on this lap, with nothing but moves left in it: the run may have
        // walked over the lap's end. Then the lap is done, and is counted and
        // announced as any other, before the steps of the next one are taken.
        if (k < 0 && RestOfLapIsMoves()
            && FirstStepLeadingTo(landedAt, 0, _index, out _) >= 0)
        {
            RollLapOver();
            // A wrap's listener may stop the loop.
            if (_loop is null || State != LoopState.Paused) return;
            k = FirstStepLeadingTo(landedAt, 0, _expandedSteps.Count, out from);
        }
        if (k < 0) return;

        _log?.Info("LoopRunner",
            $"ResumeAfterFlee: the run went forward to {landedAt}, the room step {k + 1} leads to; carrying on from step {k + 2} (was at step {was + 1})");
        _index = k;
        _stepInFlight = true;
        _expectedMoveSource = from;
        _expectedMoveTarget = landedAt;
    }

    private bool RestOfLapIsMoves()
    {
        for (int k = _index; k < _expandedSteps.Count; k++)
            if (_expandedSteps[k] is not MoveLoopStep) return false;
        return true;
    }

    // The first step in [first, end) whose move leads into this room, found by
    // walking the lap from its start, and the room that step leaves from; -1 when
    // there is none. It stops at the first step in that range that is not a move: a
    // flee sends moves only, so it cannot have crossed one.
    private int FirstStepLeadingTo(RoomKey room, int first, int end, out RoomKey from)
    {
        from = default;
        if (_graph is null || _circleStartRoom is not { } here) return -1;
        for (int k = 0; k < end && k < _expandedSteps.Count; k++)
        {
            if (_expandedSteps[k] is not MoveLoopStep move)
            {
                if (k >= first) return -1;
                continue;
            }
            if (_graph.GetRoom(here) is not { } at || !at.Exits.TryGetValue(move.Direction, out RoomExit exit)) return -1;
            from = here;
            here = exit.Target;
            if (k >= first && here.Equals(room)) return k;
        }
        return -1;
    }

    // The room the loop stood in, with nothing in flight, when the recovery gate
    // took it to find out where we are (a resume with the tracker unsure). Null
    // otherwise, and when the tracker held no room.
    private RoomKey? _heldRoomAskedAbout;

    public void ResumeAfterRecovery(RoomKey recoveredAnchor)
    {
        if (_loop is null) return;
        RoomKey? heldRoom = _heldRoomAskedAbout;
        _heldRoomAskedAbout = null;

        // Normally the gate paused us and we're Paused here. But the gate's pause
        // and the MovementCoordinator's are separate: the coordinator can clear on
        // its own while the gate is still awaiting an authoritative position, which
        // puts us back in Running with the step HELD — SendNextStep declines on
        // MayProceedWithPlannedStep and returns having sent nothing and armed
        // nothing. Recovery finishing is the only thing left that can re-drive it,
        // so bailing on `State != Paused` here stranded the loop idle forever
        // (a Roomba sweep sat still after a sysop locate resolved correctly).
        // A step already on the wire needs no push — its own confirmation advances us.
        if (State == LoopState.Running)
        {
            if (_stepInFlight) return;
            _log?.Info("LoopRunner",
                $"ResumeAfterRecovery: recovered at {recoveredAnchor} while already Running "
                + $"(step {_index + 1} was held by the gate); re-driving it");
            SendNextStep();
            return;
        }

        if (State != LoopState.Paused) return;

        // Nothing was in flight when the gate took us, and we turn out to be in the
        // room we stood in: the tracker's doubt was unfounded and the step waiting
        // there was never sent, so it goes out now. A reroute would spend one of
        // the three recovery attempts on nothing and walk an out-and-back spur
        // over again. With another gate still up, its clearing sends the step.
        if (heldRoom is { } held && recoveredAnchor.Equals(held))
        {
            if (_coordinator.IsPaused)
            {
                _log?.Info("LoopRunner",
                    $"ResumeAfterRecovery: still in {held} with nothing in flight, but coordinator still paused (gates={string.Join(",", _coordinator.AssertedGates)}); step {_index + 1} goes out when it clears");
                return;
            }
            _log?.Info("LoopRunner",
                $"ResumeAfterRecovery: still in {held} with nothing in flight; sending step {_index + 1}");
            State = LoopState.Running;
            Raise(new LoopEvent(LoopEventKind.Resumed, $"still in {held}"));
            SendNextStep();
            return;
        }

        // Engine policy for loops: if the recovered anchor matches the
        // step's expected target, advance. Otherwise the loop is
        // desynced — fail rather than blindly continuing.
        if (_expectedMoveTarget is { } expected && recoveredAnchor.Equals(expected))
        {
            // State==Paused here is ambiguous: EngineRecoveryGate's own
            // PauseForRecovery set it, OR an unrelated MovementCoordinator gate
            // (Search, GhSort, ...) asserted while recovery was already paused
            // and OnPauseChanged(true) found State==Running-turned-Paused too.
            // Resolving THIS recovery doesn't mean the coordinator agrees we
            // should move — if some other gate is still up, sending the next
            // step here races that gate's eventual clear and can double-send
            // (OnPauseChanged's own "coordinator resumed" path would ALSO
            // advance once the other gate clears, since _stepInFlight /
            // _expectedMoveTarget are unchanged and still describe a completed
            // step). Defer entirely to that already re-pause-safe, deferred
            // path instead of advancing here: leave State Paused and just
            // record that the move landed. This is the fix for the "loop
            // double-sends a move and desyncs its step counter" bug (a GhSort
            // gate clearing in the same burst as an authoritative rm resync
            // sent the SAME cardinal twice, one lap re-entering a room from a
            // step that no longer matched its real exits — "no exit S" crash).
            if (_coordinator.IsPaused)
            {
                _log?.Info("LoopRunner",
                    $"ResumeAfterRecovery: recovered at expected target {recoveredAnchor}, but coordinator still paused (gates={string.Join(",", _coordinator.AssertedGates)}); deferring advance to gate clear");
                return;
            }

            _log?.Info("LoopRunner",
                $"ResumeAfterRecovery: recovered at expected target {recoveredAnchor}; resuming step {_index + 1}");
            State = LoopState.Running;
            _stepInFlight = false;
            Raise(new LoopEvent(LoopEventKind.Resumed,
                $"recovered at expected target {recoveredAnchor}"));
            AdvanceStep();
            return;
        }

        // A flee pulled us back from the step's room: walk back to it and carry on
        // from there, rather than re-planning the lap from wherever the flee
        // stopped. Re-planning restarted the loop at the nearest waypoint — for a
        // hit-and-run that walked away from the monster it had just backstabbed
        // instead of re-sneaking back in (report paradigm-20260929-221352).
        // Not after a forward run that is to carry on: it is re-planned from here.
        if (_resumingAfterFlee && !_carryOnAfterFlee
            && _expectedMoveTarget is { } fledFrom && StartFleeReturn(fledFrom, recoveredAnchor))
            return;

        // Desync: the gate recovered us to a real room that isn't the step's
        // expected target. Rather than fail to Idle, reroute the loop from where we
        // actually ended up (the gate call is terminal — FinishTier3Success does
        // nothing after this, so detaching + re-planning here is safe).
        if (_resumingAfterFlee)
            _log?.Info("LoopRunner",
                $"ResumeAfterFlee: landed at {recoveredAnchor} (step {_index + 1} was headed for {_expectedMoveTarget}); re-planning from here");
        else if (_expectedMoveTarget is null)
            _log?.Warn("LoopRunner",
                $"ResumeAfterRecovery: recovered at {recoveredAnchor} with nothing in flight (stood in {heldRoom?.ToString() ?? "an unknown room"} before step {_index + 1}); rerouting from re-determined room");
        else
            _log?.Warn("LoopRunner",
                $"ResumeAfterRecovery: desync at step {_index + 1} — recovered at {recoveredAnchor} but expected {_expectedMoveTarget}; rerouting from re-determined room");
        EnterRecovery($"step {_index + 1} desynced (recovered at {recoveredAnchor})");
    }

    // The room a flee is walking us back to, so the step it interrupted can count as
    // arrived and the lap carries on from there. Null when no flee return is walking.
    private RoomKey? _fleeReturnTarget;

    // Walk back to the room the interrupted step was headed for. The walk goes
    // through the walker like an approach — sneaking before each move, pausing for
    // combat — and OnWalkerEvent hands off to ContinueAfterFleeReturn. False when
    // there's no walker or no path back, leaving the caller to re-plan instead.
    private bool StartFleeReturn(RoomKey target, RoomKey from)
    {
        if (_walker is null || _bfs is null || _bfs.FindPath(from, target, _filter) is null) return false;
        _log?.Info("LoopRunner",
            $"ResumeAfterFlee: landed at {from}; walking back to {target} to carry on at step {_index + 1}");
        _recovery?.Detach();
        _stepInFlight = false;
        _fleeReturnTarget = target;
        _approachTarget = target;
        State = LoopState.Approaching;
        Raise(new LoopEvent(LoopEventKind.Resumed, $"walking back to {target} after a flee"));
        CarryAbandonedDoorsToWalker();
        if (_walker.WalkTo(target, preferTeleportFree: ApproachTeleportPreference)) return true;
        _fleeReturnTarget = null;
        _approachTarget = null;
        State = LoopState.Paused;
        return false;
    }

    // Back in the room the fled-from step was headed for: that step has landed, so
    // advance from it exactly as a recovery landing on target does.
    private void ContinueAfterFleeReturn()
    {
        RoomKey? target = _fleeReturnTarget;
        _fleeReturnTarget = null;
        State = LoopState.Running;
        _recovery?.Attach(this);
        _log?.Info("LoopRunner", $"flee return reached {target}; carrying on from step {_index + 1}");
        if (_coordinator.IsPaused)
        {
            // Hold for the gate (a fight in the room we came back to). The step reads
            // as in flight to its target, so the resume's arrived-during-pause check
            // advances past it instead of re-sending it.
            _stepInFlight = true;
            _expectedMoveTarget = target;
            State = LoopState.Paused;
            Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            return;
        }
        AdvanceStep();
    }

    // The approach walk's end: a flee return carries on mid-lap; a real approach
    // starts the lap.
    private void EnterCircleAfterApproach()
    {
        if (_fleeReturnTarget is not null) ContinueAfterFleeReturn();
        else BeginCircle();
    }

    public void AbortFromRecoveryFailure(string detail)
    {
        _log?.Warn("LoopRunner",
            $"AbortFromRecoveryFailure: loop='{_loop?.Name ?? "?"}' at step {_index + 1}; {detail}");
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, $"tier3 recovery failed: {detail}"));
    }

    // ----- public surface --------------------------------------------

    // Every room of the active loop's full cycle, replaying the expanded moves from
    // the room they begin at — the circle start (waypoint 0 when no entry was
    // picked). Takes no start room on purpose: replaying from anywhere else (the
    // current room mid-lap, or an approach room off the cycle) walks the moves off
    // the real path. Returns empty when no loop is active.
    public IReadOnlyList<RoomKey> ResolveLoopRoomKeys()
    {
        if (_loop is null || _graph is null) return Array.Empty<RoomKey>();
        RoomKey? start = _circleStartRoom ?? (_loop.Waypoints.Count > 0 ? _loop.Waypoints[0].Key : null);
        if (start is not { } source) return Array.Empty<RoomKey>();
        var keys = new List<RoomKey> { source };
        RoomKey here = source;
        foreach (LoopStep step in _expandedSteps)
        {
            if (step is not MoveLoopStep move) continue;
            Room? room = _graph.GetRoom(here);
            if (room is null) break;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            here = exit.Target;
            keys.Add(here);
        }
        return keys;
    }


    public event Action<LoopEvent>? Event;

    public LoopRunner(RoomTracker tracker, MovementCoordinator coordinator,
        WirePromptScanner? promptScanner = null, LogService? log = null,
        RoomGraphManager? graph = null, EngineRecoveryGate? recovery = null,
        BfsMapper? bfs = null, AutoWalkManager? walker = null,
        IRoomFilter? filter = null, Action<Action>? postToUi = null,
        Func<DateTimeOffset>? now = null, Func<long>? timestamp = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);
        _schedule = schedule ?? DispatcherTicks.Once;
        _tracker = tracker;
        _coordinator = coordinator;
        _promptScanner = promptScanner;
        _log = log;
        _graph = graph;
        _recovery = recovery;
        _bfs = bfs;
        _walker = walker;
        _runFilter = filter;
        _filter = filter;
        _postToUi = postToUi ?? (a => Dispatcher.UIThread.Post(a));
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;

        _tracker.StateChanged += OnTrackerStateChanged;
        // A death found out at the login (the hang-up penalty killed the character
        // after the link was gone) stops the engines, but the loop set aside when
        // the link dropped is not running yet: the first prompt would start it and
        // walk a stripped character out of the temple.
        _tracker.PlayerDeathInferred += ClearPendingReconnectResume;
        _tracker.CastCrossingStarted += OnCastCrossingStarted;
        _coordinator.PauseStateChanged += OnPauseChanged;
        if (_promptScanner is not null)
            _promptScanner.PromptObserved += OnPromptObserved;
        if (_walker is not null)
            _walker.Event += OnWalkerEvent;
    }

    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Pre-move stealth hook — invoked immediately before each loop move's bytes go
    // out so sn is the last command before the move and the circuit is walked under
    // sneak. Mirrors AutoWalkManager.SetPreMoveHook; AppServices binds both to
    // Game.Stealth.StealthManager.RequestPreMoveStealth.
    public void SetPreMoveHook(Action hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _preMoveHook = hook;
    }

    // Predictive approach hook — invoked the instant a circuit step commits, with
    // the room about to be entered, before any crossing bytes go out. Mirrors
    // AutoWalkManager.SetApproachRoomHook; AppServices binds both to the same
    // room-provisioners (auto-light + hazard-counter) so a loop lap readies a dark
    // room's light / raises a hazard buff ahead of the step exactly like a walk-to.
    public void SetApproachRoomHook(Action<RoomKey> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _approachRoomHook = hook;
    }

    // Wire the teleport-keyword resolver so circuit steps can cross
    // RoomExitHint.Teleport exits. Mirrors AutoWalkManager.SetTeleportResolver;
    // AppServices binds both to the same TBInfo-backed resolver.
    public void SetTeleportResolver(Func<RoomKey, RoomKey, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _teleportResolver = resolver;
    }

    // Wire the party-leader check so a leading character relays the teleport keyword
    // to followers before crossing. Mirrors AutoWalkManager.SetPartyLeaderCheck.
    public void SetPartyLeaderCheck(Func<bool> isLeaderWithFollowers)
    {
        ArgumentNullException.ThrowIfNull(isLeaderWithFollowers);
        _isLeaderWithFollowers = isLeaderWithFollowers;
    }

    // Item-name lookup for a leg the loop refuses for want of an item. Mirrors
    // AutoWalkManager.SetItemNameResolver.
    private Func<int, string?>? _itemNameResolver;

    public void SetItemNameResolver(Func<int, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _itemNameResolver = resolver;
    }

    // Wire the Confused check (AppServices binds this to Conditions.IsConfused) so
    // EnterRecovery can tell a confusion fumble apart from a genuine block.
    public void SetConfusedCheck(Func<bool> isConfused)
    {
        ArgumentNullException.ThrowIfNull(isConfused);
        _isConfused = isConfused;
    }

    // Wire the party-split-teleport handler so a leading character reforms the
    // party after a party-splitting CMD teleport. Mirrors
    // AutoWalkManager.SetPartySplitHandler.
    public void SetPartySplitHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onLeaderPartySplit = handler;
    }

    // Trapped-exit handling — the same five hooks the walker takes
    // (AutoWalkManager.SetTrapEnqueuer / SetTrapDisarmGate / SetTrapDelegator /
    // SetTrapDelegateGate / SetTrapDelegateStopper), bound to the same managers.
    public void SetTrapHandling(
        Action<string, string, Action<string>> enqueuer, Func<bool> disarmGate,
        Action<string, Action<string>> delegator, Func<bool> delegateGate, Action delegateStopper)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        ArgumentNullException.ThrowIfNull(disarmGate);
        ArgumentNullException.ThrowIfNull(delegator);
        ArgumentNullException.ThrowIfNull(delegateGate);
        ArgumentNullException.ThrowIfNull(delegateStopper);
        _trapEnqueuer = enqueuer;
        _shouldDisarmTrap = disarmGate;
        _trapDelegator = delegator;
        _canDelegateTrap = delegateGate;
        _trapDelegateStopAll = delegateStopper;
    }

    // Door-open enqueuer — mirrors AutoWalkManager.SetDoorEnqueuer. AppServices
    // binds both engines to the same DoorOpenManager.Enqueue so a loop crosses a
    // closed door with the same bash / pick / key flow the walker uses.
    public void SetDoorEnqueuer(Action<Direction, int, bool, int, string, Action<DoorOpenResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _doorEnqueuer = enqueuer;
    }

    // Door-FSM teardown — mirrors AutoWalkManager.SetDoorStopper. Called from
    // Reset / recovery when a loop is superseded mid-door-FSM so a stale queued
    // request can't fire a stray verb in a room we've since left.
    public void SetDoorStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _doorStopAll = stopAll;
    }

    // Hidden-exit reveal enqueuer — mirrors AutoWalkManager.SetHiddenSearchEnqueuer.
    // MainWindowVM binds both engines to the same HiddenExitRevealManager so a loop
    // uncovers a SearchableHidden exit with the same sea <dir> retry loop the walker
    // uses instead of failing the lap.
    public void SetHiddenSearchEnqueuer(Action<Direction, string, Action<HiddenSearchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _hiddenSearchEnqueuer = enqueuer;
    }

    // Hidden-search teardown — mirrors AutoWalkManager.SetHiddenSearchStopper.
    // Same stale-state cleanup rationale as SetDoorStopper.
    public void SetHiddenSearchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _hiddenSearchStopAll = stopAll;
    }

    // Winch enqueuer — mirrors AutoWalkManager.SetWinchEnqueuer. Both engines bind
    // to the same WinchManager so a loop crosses a winch gate the same way.
    public void SetWinchEnqueuer(Action<Direction, string, bool, string, Action<WinchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _winchEnqueuer = enqueuer;
    }

    // Winch teardown — mirrors AutoWalkManager.SetWinchStopper.
    public void SetWinchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _winchStopAll = stopAll;
    }

    // Start running loop. If a loop is already running, it is stopped first. Returns
    // false when the loop is empty.
    //
    // userStarted: the user pressed Start on this loop, against an event, a remote
    // command or a sweep starting it. A user's start from off the loop normally
    // arrives here already standing on it (LoopWalkHandoff walks there first, as a
    // walk-to); when the runner still has to walk, that walk is the user's and
    // keeps the shortest route. Started any other way the walk is automatic and
    // uses only the teleports allowed to those (AutoWalkManager.SetAutomaticWalkTeleports).
    public bool Start(Loop loop, bool userStarted = false)
    {
        _userApproach = userStarted;
        return StartInternal(loop, isRecovery: false, gateFallback: true);
    }

    // Why this loop can't be run at all, or null when it can: it has a waypoint in a
    // room no route enters. Asked before the walk to the loop too, so nobody is
    // walked to the lake to be told there.
    public string? RefusalFor(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return OtherGameDataRefusalFor(loop) ?? ClosedRoomRefusalFor(loop) ?? UnpaidRefusalFor(loop);
    }

    // The snapshot of the last run is kept through a switch of game data, and its
    // rooms are the set's it was run on: under another set it is not run again
    // ("@loop last", or anything else handing the snapshot back), which would also
    // make it this set's to save.
    private string? OtherGameDataRefusalFor(Loop loop) =>
        ReferenceEquals(loop, LastRunLoop)
        && !string.Equals(LastRunLoopGameData, _graph?.ActiveSet, StringComparison.OrdinalIgnoreCase)
            ? $"loop '{loop.Name}' was last run on game data '{LastRunLoopGameData ?? "(none)"}', and "
              + $"'{_graph?.ActiveSet ?? "(none)"}' is active now: its rooms are that game data's"
            : null;

    private string? ClosedRoomRefusalFor(Loop loop)
    {
        if (_filter is null) return null;
        List<RoomKey> closed = loop.Waypoints.Select(w => w.Key).Where(_filter.IsClosedToRoutes).Distinct().ToList();
        if (closed.Count == 0) return null;
        string first = _graph?.GetRoom(closed[0])?.Name is { Length: > 0 } name ? $"{closed[0]} ({name})" : closed[0].ToString();
        return $"loop '{loop.Name}' has {closed.Count} waypoint(s) in rooms that teleport at random and that nothing "
            + $"protects from, {first} the first: no loop or automatic walk enters them";
    }

    // A loop one of whose legs has no way but through a toll or fare the purse
    // can't pay, or a toll the party in tow can't all get through, is not run
    // (user, 2026-10-10): it would walk to the gate and be turned away, or leave
    // someone there, every lap. Asked at every start, a recovery's re-plan included,
    // so a loop refused at a toll mid-lap re-expands round it where a way round
    // exists and stops here, naming the crossing, where none does. The words for
    // the crossing are the walk-to's (PaidCrossingDescriber).
    private string? UnpaidRefusalFor(Loop loop)
    {
        if (_filter is null || _bfs is null || _graph is null || loop.Waypoints.Count < 2) return null;
        (_, IReadOnlyList<(RoomKey From, RoomKey To)> unreachable) = LoopExpander.Expand(loop.Waypoints, _bfs, _filter);
        foreach ((RoomKey from, RoomKey to) in unreachable)
        {
            // A leg something obtainable opens is not blocked by coin.
            IReadOnlyList<Direction>? probe;
            using (_filter.SuspendAcquirableGatesButUnprotectableHazards())
                probe = _bfs.FindPath(from, to, _filter);
            if (probe is { Count: > 0 }) continue;
            probe = _bfs.FindPath(from, to, _filter, ignoreExitGates: true);
            if (probe is null
                || PaidCrossingDescriber.FirstUnpaidOn(_graph, from, probe, _filter) is not { } gate)
                continue;
            RoomExit exit = gate.Exit;
            string crossing = PaidCrossingDescriber.DescribeUnpaid(gate.From, gate.Dir, in exit, _filter, RoomNameOf)
                ?? PaidCrossingDescriber.Describe(gate.From, gate.Dir, in exit, RoomNameOf) + " you can't pay";
            return $"loop '{loop.Name}': no way from {from} to {to} without {crossing}";
        }
        return null;
    }

    private string? RoomNameOf(RoomKey key) => _graph?.GetRoom(key)?.Name;

    // Told when a loop is refused or stopped for want of a toll or fare, with the
    // reason it failed with: the terminal notice a walk-to gives for the same thing.
    private Action<string>? _unpaidCrossingHandler;
    public void SetUnpaidCrossingHandler(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _unpaidCrossingHandler = handler;
    }

    // The walk to the loop is the user's: set by a user Start and dropped the moment
    // the loop is reached (BeginCircle). The user asked to go to the loop, not for
    // what the run does afterwards, so a walk back to it after a detour or a flee is
    // an automatic walk like any other (user, 2026-10-07).
    private bool _userApproach;

    // The teleport preference a walk to the loop states: none for an automatic one
    // (the walker then applies the allowed list), the shortest route for the user's.
    private bool? ApproachTeleportPreference => _userApproach ? false : null;

    // The room of the loop's cycle nearest to `from`, where a start from there would
    // join it: the destination of the walk-to a user's start makes first
    // (LoopWalkHandoff). Null when `from` is already on the cycle, or nothing of it
    // can be reached. Works nothing out about the run in progress.
    public RoomKey? NearestRoomOf(Loop loop, RoomKey from)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (_bfs is null || _graph is null || loop.Waypoints.Count < 2) return null;
        List<RoomKey> rooms = LoopExpander.ResolveCycleRoomKeys(loop.Waypoints, _bfs, _graph, _filter)
            .Concat(loop.Waypoints.Select(w => w.Key)).Distinct().ToList();
        if (rooms.Contains(from)) return null;

        RoomKey? Nearest()
        {
            IReadOnlyDictionary<RoomKey, int> steps = _bfs.ComputeDistancesTo(from, rooms, _filter);
            RoomKey? best = null;
            foreach (RoomKey room in rooms)
                if (steps.TryGetValue(room, out int n) && (best is not { } b || n < steps[b])) best = room;
            return best;
        }

        // The free way in first; failing that, through a gate the walk can open on
        // the way, as the runner's own start falls back.
        // Never across a room closed to routes: the walk there wouldn't cross either.
        if (Nearest() is { } free) return free;
        using (_filter?.SuspendAcquirableGatesButUnprotectableHazards())
            return Nearest();
    }

    // Resume a loop after an auto-deposit / bank / trainer detour that Stop()ed it
    // for its own walk. Re-plans from the current room exactly like a fresh Start, but
    // suppresses the one-shot ReachedFirstWaypoint event — the session began at
    // the user's original Start, so the session-stats reset and party @reset
    // broadcast wired to that event must not re-fire on a mid-session detour.
    //
    // throughGates: plan the re-approach through acquirable gates (key-doors, hidden
    // exits, summon-drop keys, multi-action gates). A detour walks OUT of a gated grind
    // area trivially but getting back IN needs the same acquirable-gate planning a user
    // GOTO uses — without it PickClosestWaypoint finds no reachable waypoint and the loop
    // never resumes, stranding the character where the detour ended (report
    // paradigm-20260913-022254).
    public bool ResumeAfterDetour(Loop loop, bool throughGates = false)
        => StartInternal(loop, isRecovery: false, suppressFirstWaypointEvent: true, throughGates: throughGates);

    // The room a run stopped for a trip (bank, sell, train) counted its steps from.
    // Its resume keeps that anchor, so the step shown after rejoining at another
    // room of the loop is the same number it was before the trip, not a count
    // restarted from the rejoin room.
    private (string Name, RoomKey[] Waypoints, RoomKey Anchor)? _resumeAnchor;

    private RoomKey? TakeResumeAnchor(Loop loop)
    {
        (string Name, RoomKey[] Waypoints, RoomKey Anchor)? kept = _resumeAnchor;
        _resumeAnchor = null;
        return kept is { } k && k.Name == loop.Name
            && k.Waypoints.SequenceEqual(loop.Waypoints.Select(w => w.Key))
            ? k.Anchor : null;
    }

    // Shared engine for both a fresh user Start and an auto-recovery reroute. On a
    // recovery reroute (isRecovery) we deliberately keep the session-scoped state —
    // the bounded _recoverAttempts budget and the lap history / first-waypoint flag
    // — so the reroute continues the same lap instead of re-arming ReachedFirstWaypoint
    // (which would re-fire the party @reset side effect on every recovery). EnterRecovery
    // has already detached the gate + cleared the in-flight step by the time we land here.
    //
    // gateFallback: a fresh Start that can reach nothing freely plans its approach
    // through gates instead of failing. A resume or a recovery reroute keeps the
    // planning its caller asked for.
    private bool StartInternal(Loop loop, bool isRecovery, bool suppressFirstWaypointEvent = false,
        bool throughGates = false, bool gateFallback = false)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Waypoints.Count < 2)
        {
            _log?.Warn("LoopRunner",
                $"Start refused: loop '{loop.Name}' has {loop.Waypoints.Count} waypoint(s); need ≥2 for a cycle");
            return false;
        }
        // Before anything of a run under way is touched: nothing starts, and what is
        // running is left as it is.
        if (!isRecovery && OtherGameDataRefusalFor(loop) is { } otherGameData)
        {
            _log?.Warn("LoopRunner", $"Start refused: {otherGameData}");
            return false;
        }
        // Handed back to run again, the snapshot keeps the stamp it has.
        bool keptSnapshot = ReferenceEquals(loop, LastRunLoop);
        RoomKey? keepAnchor = suppressFirstWaypointEvent ? TakeResumeAnchor(loop) : null;
        _resumeAnchor = null;

        if (isRecovery)
        {
            _log?.Info("LoopRunner",
                $"recovery reroute: re-planning loop '{loop.Name}' from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
        }
        else
        {
            _recoverAttempts = 0;
            _revealRetries = 0;
            _lastRecoveryAttemptAt = DateTimeOffset.MinValue;
            ForgetAbandonedDoors();
            if (State is LoopState.Running or LoopState.Paused
                       or LoopState.Approaching or LoopState.Recovering)
            {
                _log?.Info("LoopRunner",
                    $"Start: superseding active loop '{_loop?.Name ?? "?"}' (state={State}) with '{loop.Name}'");
                // Stopping ends a reconnect hold, but the run that replaces this one
                // must wait for the party reform just the same.
                bool heldForReform = ReconnectResumeHeldForReform;
                Stop("superseded by new loop");
                if (heldForReform && _reformAwaitsRoom?.Invoke() == true) BeginReconnectReformHold();
            }
            else
            {
                _log?.Info("LoopRunner",
                    $"Start: loop='{loop.Name}' waypoints={loop.Waypoints.Count} from={_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
            }
        }

        _loop = loop;
        LastRunLoopName = loop.Name;   // retained past Stop/Reset for @path recovery
        LastRunLoopAt = _now();
        // Retain a COPY so "@loop last" / the Loop-mode chip can re-run or re-open
        // this exact loop even if it was an ad-hoc, never-saved run. The two settings
        // are the ones this character runs the loop with (Loop.OwnSettings), not the
        // loop file's.
        LastRunLoop = new Loop(loop.Name, new List<LoopWaypoint>(loop.Waypoints))
        {
            Notes = loop.Notes,
            OwnSettings = loop.OwnSettings,
        };
        // The stamp is the run's, written by a fresh start only. A recovery reroute,
        // an avoid-list reroute and a resume after a detour (the two that arrive
        // with the first-waypoint event suppressed) are the same run re-planned: the
        // loop is still the set's it was started on, whatever is active by now.
        if (!isRecovery && !suppressFirstWaypointEvent && !keptSnapshot) LastRunLoopGameData = _graph?.ActiveSet;
        _index = 0;
        _stepInFlight = false;
        _awaitingCommandReplies = false;
        _expectedMoveTarget = null;
        _expectedMoveSource = null;
        _heldRoomAskedAbout = null;
        _approachTarget = null;
        _circleStartRoom = null;
        _expandedSteps = new List<LoopStep>();
        _reExpandAtLapEnd = false;
        _partialLap = false;
        if (!isRecovery)
        {
            _firstWaypointReached = false;
            _lapDurations.Clear();
            _completedLaps = 0;
        }
        // Set after any supersede-Stop above (which routes through Reset and would
        // otherwise clear it) so the one-shot survives to BeginCircle.
        _suppressFirstWaypointEvent = suppressFirstWaypointEvent;
        _returningFromDetour = suppressFirstWaypointEvent;

        // A waypoint in a room no route enters (Crystal Lake's teleporting sea
        // rooms) can't be walked to, and a loop that stood in one would be thrown
        // off it on most entries. Refused here, by name, rather than started with
        // a leg missing.
        string? unpaid = null;
        if ((ClosedRoomRefusalFor(loop) ?? (unpaid = UnpaidRefusalFor(loop))) is { } refusal)
        {
            _log?.Warn("LoopRunner", $"Start refused: {refusal}");
            if (unpaid is not null) _unpaidCrossingHandler?.Invoke(refusal);
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, refusal));
            return false;
        }

        RoomKey? currentKey = _tracker.State.CurrentRoom?.Key;

        // Decision: do we need an approach walk, or can we begin the
        // circle immediately?
        //   - Player already at a waypoint → rotate the loop so that
        //     waypoint is first, no approach.
        //   - Player elsewhere AND walker bound AND graph available →
        //     pick the closest waypoint, walker drives the approach,
        //     loop steps are rotated + expanded UP FRONT so the
        //     approach-preview overlay can render the upcoming cycle.
        //   - Walker missing or no graph → expand from
        //     waypoint 0 and let the runner fail-or-recover.

        // Started is raised AFTER each branch commits its state +
        // rotation + expansion + (where applicable) State transition.
        // Subscribers like NavigationViewModel.RefreshLoopOverlays read
        // runner.State / CurrentLoop / ResolveLoopRoomKeys() (built from
        // the start room and the expanded steps) in their handler; if we
        // raised before the commit they'd see the prior
        // (Idle) shape and the approach-phase preview overlay would
        // render empty.

        if (keepAnchor is null && currentKey is { } here && loop.Waypoints.Any(w => w.Key.Equals(here)))
        {
            _log?.Info("LoopRunner",
                $"Start branch=at-waypoint: player already at {here}; no approach needed");
            _circleStartRoom = here;
            ExpandSteps();
            // Ahead of Started: a loop with nothing to walk never started, and the
            // caller is told so.
            if (FailIfNothingToWalk()) return false;
            Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
            BeginCircle();
            return true;
        }

        if (_bfs is not null && currentKey is { } from
            && TryEnterAtNearestRoom(loop, from, throughGates, firstRun: !isRecovery, anchor: keepAnchor))
            return true;

        if (_walker is null || _bfs is null || currentKey is null)
        {
            _log?.Info("LoopRunner",
                $"Start branch=no-walker: walker={_walker is not null} bfs={_bfs is not null} currentKey={currentKey?.ToString() ?? "(null)"}; expanding from waypoint 0");
            ExpandSteps();
            if (FailIfNothingToWalk()) return false;
            Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
            BeginCircle();
            return true;
        }

        RoomKey? closest = PickClosestWaypoint(currentKey.Value, loop.Waypoints, throughGates);
        // Nothing can be reached as things stand, but the loop may sit behind a gate
        // the approach can open on the way: a door whose key an NPC hands over, a
        // hidden exit. Plan through those, as a go-to does. The free way in is still
        // preferred when there is one.
        if (closest is null && !throughGates && gateFallback)
        {
            if (TryEnterAtNearestRoom(loop, currentKey.Value, throughGates: true, firstRun: !isRecovery))
            {
                _log?.Info("LoopRunner",
                    "nothing on the loop can be reached freely; approaching through a gate it can open on the way");
                return true;
            }
            closest = PickClosestWaypoint(currentKey.Value, loop.Waypoints, throughGates: true);
            if (closest is not null)
            {
                throughGates = true;
                _log?.Info("LoopRunner",
                    "no waypoint can be reached freely; approaching through a gate it can open on the way");
            }
        }
        if (closest is null)
        {
            // No reachable waypoint — bail; gate would fail us anyway.
            _log?.Warn("LoopRunner",
                $"Start failed: no reachable waypoint from {currentKey} (graph disconnected, all behind avoided rooms, or filter excludes them)");
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                $"no reachable waypoint from {currentKey}"));
            return false;
        }

        // Commit the cycle's entry UP FRONT — the moment we pick the closest
        // waypoint. Setting _circleStartRoom before ExpandSteps means the runtime
        // traversal begins at the entry (and ResolveLoopRoomKeys() produces
        // the correct approach-preview cycle) WITHOUT reordering the authored
        // waypoint list — the entry is just a start offset into the fixed cycle.
        _circleStartRoom = closest;
        _approachTarget  = closest;
        ExpandSteps();
        if (FailIfNothingToWalk()) return false;
        State = LoopState.Approaching;
        Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
        _log?.Info("LoopRunner",
            $"approach: walking from {currentKey} → {closest} (closest of {loop.Waypoints.Count} waypoints)");
        JourneyFetch? fetch = throughGates ? _gatedApproachFetch?.Invoke(currentKey.Value, closest.Value) : null;
        CarryAbandonedDoorsToWalker();
        _walker.WalkTo(closest.Value, planThroughAcquirableGates: throughGates, preferTeleportFree: ApproachTeleportPreference,
            fetch: fetch);
        return true;
    }

    // Start the loop at whichever room of its cycle is nearest — a room partway along a
    // leg as readily as a waypoint — rather than walking on to the nearest waypoint
    // and back (user, 2026-09-30). Standing on the cycle, it starts right there with
    // no approach. The cycle stays anchored at the first authored waypoint; the entry
    // is just the step index the run begins at. False when the cycle can't be
    // resolved or none of it can be reached, leaving the caller the waypoint pick.
    private bool TryEnterAtNearestRoom(Loop loop, RoomKey from, bool throughGates, bool firstRun,
        RoomKey? anchor = null)
    {
        _circleStartRoom = anchor ?? loop.Waypoints[0].Key;
        ExpandSteps();
        Dictionary<RoomKey, int> entryIndex = CycleEntryIndices();
        if (entryIndex.Count == 0) return false;

        IReadOnlyDictionary<RoomKey, int> steps;
        using (IDisposable? gateScope = throughGates ? _filter?.SuspendAcquirableGatesButUnprotectableHazards() : null)
            steps = _bfs!.ComputeDistancesTo(from, entryIndex.Keys, _filter);
        RoomKey? best = null;
        foreach ((RoomKey room, int index) in entryIndex)
        {
            if (!steps.TryGetValue(room, out int n)) continue;
            if (best is not { } b || n < steps[b] || (n == steps[b] && index < entryIndex[b])) best = room;
        }
        if (best is not { } entry) return false;
        if (!entry.Equals(from) && _walker is null) return false;

        if (anchor is null && loop.Waypoints.Any(w => w.Key.Equals(entry)))
        {
            // A waypoint entry anchors the cycle there, so every lap is whole — the
            // Roomba sweep, whose rooms are all waypoints, reads its first wrap as
            // the recon lap done.
            _circleStartRoom = entry;
            ExpandSteps();
            _index = 0;
        }
        else
        {
            _index = entryIndex[entry];
            // A first lap entered partway along a leg isn't a lap: it isn't counted,
            // timed or announced as a wrap.
            _partialLap = firstRun;
        }
        Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
        if (entry.Equals(from))
        {
            _log?.Info("LoopRunner",
                $"Start branch=on-cycle: player at {from}, step {_index + 1} of {_expandedSteps.Count}; no approach needed");
            BeginCircle();
            return true;
        }
        _approachTarget = entry;
        State = LoopState.Approaching;
        _log?.Info("LoopRunner",
            $"approach: walking from {from} → {entry} (nearest loop room, {steps[entry]} step(s); joins at step {_index + 1} of {_expandedSteps.Count})");
        JourneyFetch? fetch = throughGates ? _gatedApproachFetch?.Invoke(from, entry) : null;
        CarryAbandonedDoorsToWalker();
        _walker!.WalkTo(entry, planThroughAcquirableGates: throughGates, preferTeleportFree: ApproachTeleportPreference,
            fetch: fetch);
        return true;
    }

    // For each room on the cycle, the step index the run begins at when entering
    // there: the first step taken in that room (its room commands, then the move
    // out). A room the cycle passes twice keeps its first visit.
    private Dictionary<RoomKey, int> CycleEntryIndices()
    {
        Dictionary<RoomKey, int> entry = new();
        if (_graph is null || _circleStartRoom is not { } here) return entry;
        entry[here] = 0;
        for (int i = 0; i < _expandedSteps.Count; i++)
        {
            if (_expandedSteps[i] is not MoveLoopStep move) continue;
            if (_graph.GetRoom(here) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit))
                return new Dictionary<RoomKey, int>();
            here = exit.Target;
            if (i + 1 < _expandedSteps.Count) entry.TryAdd(here, i + 1);
        }
        return entry;
    }

    // True from a first run that joined the cycle partway round until that lap wraps.
    private bool _partialLap;

    // Pick the user-waypoint with the shortest BFS path from from. Returns null when
    // no waypoint is reachable (disconnected graph, all waypoints behind avoided
    // rooms, etc.).
    private RoomKey? PickClosestWaypoint(RoomKey from, IReadOnlyList<LoopWaypoint> waypoints, bool throughGates = false)
    {
        if (_bfs is null) return waypoints.Count > 0 ? waypoints[0].Key : null;
        // throughGates: suspend the acquirable gates (item / key-door / hidden exit /
        // hazard) for the reachability probe so a waypoint reachable only by acquiring
        // something en route (e.g. the key to re-enter a walled city after a detour)
        // still counts as reachable; the approach walk then plans + acquires through
        // them. Level / toll / class gates stay active regardless, and so do the
        // rooms closed to routes, which that walk would not cross.
        using IDisposable? gateScope = throughGates ? _filter?.SuspendAcquirableGatesButUnprotectableHazards() : null;
        RoomKey? best = null;
        int bestLen = int.MaxValue;
        foreach (LoopWaypoint w in waypoints)
        {
            RoomKey key = w.Key;
            if (key.Equals(from)) return key;
            IReadOnlyList<Direction>? path = _bfs.FindPath(from, key, _filter);
            if (path is null) continue;
            if (path.Count < bestLen) { best = key; bestLen = path.Count; }
        }
        return best;
    }

    // The authored waypoints, ordered so traversal BEGINS at the entry the run /
    // recovery picked (_circleStartRoom), WITHOUT ever reordering _loop.Waypoints.
    // The authored order is the single source of truth for the map/rail numbering,
    // which must stay fixed to what the user authored no matter where they enter or
    // recover — so the entry is only a start offset into the fixed cycle, computed as
    // a transient view here. Returns the authored list unchanged when there's no
    // anchor or the anchor is already waypoint 0.
    private IReadOnlyList<LoopWaypoint> RuntimeWaypointOrder()
    {
        IReadOnlyList<LoopWaypoint> wps =
            _loop?.Waypoints ?? (IReadOnlyList<LoopWaypoint>)System.Array.Empty<LoopWaypoint>();
        if (wps.Count == 0 || _circleStartRoom is not { } anchor) return wps;

        int k = -1;
        for (int i = 0; i < wps.Count; i++)
            if (wps[i].Key.Equals(anchor)) { k = i; break; }
        if (k <= 0) return wps;   // anchor not found, or already first — no offset needed

        var ordered = new List<LoopWaypoint>(wps.Count);
        for (int i = 0; i < wps.Count; i++)
            ordered.Add(wps[(k + i) % wps.Count]);
        return ordered;
    }

    // (Re)compute _expandedSteps from the runtime traversal order (authored order
    // starting at the entry) + the active filter. Called on start, recovery, and every
    // avoid-list change. Never mutates the authored waypoint list.
    private void ExpandSteps()
    {
        _unreachableLeg = null;
        if (_loop is null || _bfs is null)
        {
            _expandedSteps = new List<LoopStep>();
            return;
        }
        IReadOnlyList<LoopWaypoint> wps = RuntimeWaypointOrder();
        // Route-scoped @wealth warm-up: probe the party only when a leg of the
        // cycle actually crosses a toll. LoopExpander is a pure helper (no
        // side effects), so the probe lives here — one debounced round-trip
        // covers every toll leg in the expansion.
        if (_filter is not null)
        {
            for (int i = 0; i < wps.Count; i++)
                _filter.WarmForRoute(_bfs, wps[i].Key, wps[(i + 1) % wps.Count].Key);
        }

        (IReadOnlyList<LoopStep> steps,
         IReadOnlyList<(RoomKey From, RoomKey To)> unreachable)
                = LoopExpander.Expand(wps, _bfs, _filter);
        _expandedSteps = new List<LoopStep>(steps);
        _log?.Info("LoopRunner",
            $"expand: loop='{_loop.Name}' waypoints={wps.Count} → {steps.Count} step(s), {unreachable.Count} unreachable segment(s)");
        if (unreachable.Count > 0)
        {
            _unreachableLeg = unreachable[0];
            foreach ((RoomKey from, RoomKey to) in unreachable)
                _log?.Warn("LoopRunner", $"expand unreachable: {from} → {to} (BFS found no path)");
        }
    }

    // The first leg the last expansion found no route for, to name when it left
    // the loop with nothing to walk.
    private (RoomKey From, RoomKey To)? _unreachableLeg;

    // An expansion with no step in it (every leg unreachable: behind an avoided
    // room, a gate, a toll) can't be run, and indexing into it took the client
    // down from inside the line pump. The loop fails with the leg named instead.
    private bool FailIfNothingToWalk()
    {
        if (_loop is null || _expandedSteps.Count > 0) return false;
        string why = _unreachableLeg is { } leg ? $": no route from {leg.From} to {leg.To}" : string.Empty;
        _log?.Warn("LoopRunner", $"loop '{_loop.Name}' expanded to no steps{why}; failing it");
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, $"loop '{_loop.Name}' has no leg that can be walked{why}"));
        return true;
    }

    // Reconcile the live per-room edits the running-loop rail allows (command / delay)
    // into the already-expanded runtime steps WITHOUT restarting the loop or moving
    // _index. The rail forbids add / remove / reorder of rooms, so the waypoint path is
    // unchanged and the command-bearing waypoints map 1:1, in order, onto the
    // CommandLoopSteps in _expandedSteps (LoopExpander emits one per command-bearing
    // waypoint):
    //   - same command count → swap each changed CommandLoopStep in place; the next run
    //     of that step uses the new command / delay (effectively instant).
    //   - count changed (a command was added or removed → the step count shifts) → defer
    //     a full re-expand to the next lap wrap, the only safe point to swap the list
    //     (see _reExpandAtLapEnd in SendNextStep).
    // Do-not-rest / do-not-attack and the lair toggle need no reconcile — they're read
    // live off _loop each decision. Runs on the UI thread, same as the step pump.
    public void ReconcileExpandedSteps()
    {
        if (_loop is null) return;

        var cmdStepIdx = new List<int>();
        for (int i = 0; i < _expandedSteps.Count; i++)
            if (_expandedSteps[i] is CommandLoopStep) cmdStepIdx.Add(i);

        // Iterate the RUNTIME order (authored order from the entry) so the
        // command-bearing waypoints line up 1:1, in sequence, with the CommandLoopSteps
        // in _expandedSteps (which was expanded in that same order).
        var cmdWaypoints = new List<LoopWaypoint>();
        foreach (LoopWaypoint w in RuntimeWaypointOrder())
            if (!string.IsNullOrEmpty(w.Command)) cmdWaypoints.Add(w);

        if (cmdWaypoints.Count != cmdStepIdx.Count)
        {
            _reExpandAtLapEnd = true;
            _log?.Info("LoopRunner",
                $"live-edit changed the command set of loop '{_loop.Name}'; re-expanding at the next lap");
            return;
        }

        for (int k = 0; k < cmdWaypoints.Count; k++)
        {
            var cmd = (CommandLoopStep)_expandedSteps[cmdStepIdx[k]];
            LoopWaypoint wp = cmdWaypoints[k];
            if (cmd.Command != wp.Command || cmd.DelayMs != wp.DelayMs)
                _expandedSteps[cmdStepIdx[k]] = cmd with { Command = wp.Command!, DelayMs = wp.DelayMs };
        }
    }

    // Common entry into the circle phase — called either immediately from Start
    // (player already at waypoint / legacy loop) or after walker-driven approach
    // completes. Attaches the recovery gate, fires ReachedFirstWaypoint once per
    // session, anchors lap timing, and pushes the first step.
    private void BeginCircle()
    {
        if (_loop is null) return;
        // Before the first-waypoint event, which resets the session and the party.
        if (FailIfNothingToWalk()) return;
        _returningFromDetour = false;
        // The loop is reached: from here on the run's walks are its own.
        _userApproach = false;

        State = LoopState.Running;
        _recovery?.Attach(this);
        _log?.Info("LoopRunner",
            $"BeginCircle: loop='{_loop.Name}' start={_circleStartRoom?.ToString() ?? "(none)"} steps={_expandedSteps.Count}");

        if (!_firstWaypointReached)
        {
            _firstWaypointReached = true;
            _lapStartedAt = _now();
            if (_suppressFirstWaypointEvent)
                _suppressFirstWaypointEvent = false;   // consume the detour-resume suppression
            else
                Raise(new LoopEvent(LoopEventKind.ReachedFirstWaypoint, _loop.Name));
        }

        if (_coordinator.IsPaused)
        {
            State = LoopState.Paused;
            _log?.Info("LoopRunner", "BeginCircle: coordinator paused on entry; holding before first step");
            Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            return;
        }

        SendNextStep();
    }

    // The room a path-item detour walks to (an NPC that hands over a key the
    // approach needs). Its arrival there, or failing to get there, is not the
    // approach arriving or failing: the detour resumes the walk to the entry itself.
    private Func<RoomKey?>? _pathItemDetourRoom;
    public void SetPathItemDetourRoomProbe(Func<RoomKey?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _pathItemDetourRoom = probe;
    }

    // Asked with (from, entry) just before an approach walk that plans through
    // gates: what the way in needs fetched, for that walk to carry.
    private Func<RoomKey, RoomKey, JourneyFetch?>? _gatedApproachFetch;
    public void SetGatedApproachFetch(Func<RoomKey, RoomKey, JourneyFetch?> fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _gatedApproachFetch = fetch;
    }

    internal void OnWalkerEvent(WalkEvent e)
    {
        // The runner cares about walker events during two shapes of "approach in
        // flight": the live LoopState.Approaching, and the paused-from-approach
        // window where a gate flipped us to Paused while the walker was still
        // driving. In the latter, if the walker's own resume handler ran before
        // ours it completes the walk and fires Finished while we're still Paused —
        // dropping it here strands the run, so buffer it for the resume path.
        bool approaching = State == LoopState.Approaching;
        bool pausedMidApproach = State == LoopState.Paused && _pausedFromApproach;
        if (!approaching && !pausedMidApproach) return;
        if (_approachTarget is null) return;
        if (e.Destination is { } walked && !walked.Equals(_approachTarget.Value)
            && _pathItemDetourRoom?.Invoke() is { } detour && walked.Equals(detour))
            return;

        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                _approachTarget = null;
                if (pausedMidApproach)
                {
                    // Coordinator is already unpaused (the walker only finishes
                    // while Walking), but our OnPauseChanged resume hasn't run yet.
                    // Buffer the arrival; the resume branch enters the circle.
                    _log?.Info("LoopRunner",
                        "approach finished during pause window; deferring circle entry to resume");
                    _approachFinishedWhilePaused = true;
                    break;
                }
                // Walker arrived at the chosen waypoint. Rotation already happened
                // in Start — hand off into the circle. Marshalled past the current
                // dispatch instead of called directly: OnWalkerEvent runs
                // synchronously from inside RoomTracker.StateChanged (the walker's
                // own subscription fires first — it's constructed before us in
                // AppServices), so calling BeginCircle here would race the REST of
                // that same dispatch. BeginCircle's own SendNextStep flips us to
                // Running/step-in-flight immediately; by the time the tracker's
                // later subscribers — including our own OnTrackerStateChanged —
                // get their turn at the ORIGINAL arrival transition, our guard
                // (State==Running && step in flight) no longer filters it out, and
                // we misread the walker's own already-consumed arrival as a bad
                // landing of the step we hadn't even sent when the dispatch began
                // (report paradigm-20260901-090044). A pause that lands in the same
                // window before this runs falls back to the same deferred-resume
                // handoff the pausedMidApproach branch above uses.
                _log?.Info("LoopRunner", "approach finished; entering circle");
                _postToUi(() =>
                {
                    if (State == LoopState.Paused && _pausedFromApproach)
                    {
                        _approachFinishedWhilePaused = true;
                        return;
                    }
                    if (State != LoopState.Approaching) return;
                    EnterCircleAfterApproach();
                });
                break;
            case WalkEventKind.Failed:
                // A walk back after a flee that can't finish re-plans the lap from
                // where we are, as a flee did before the walk back existed.
                if (_fleeReturnTarget is not null)
                {
                    _log?.Info("LoopRunner", $"flee return failed ({e.Detail}); re-planning from here");
                    _fleeReturnTarget = null;
                    _approachTarget = null;
                    StartInternal(_loop!, isRecovery: true);
                    break;
                }
                // Walker gave up (tier-3 abort, blocked, no path, etc.).
                _log?.Warn("LoopRunner",
                    $"approach failed: {e.Detail}");
                RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                    $"approach failed: {e.Detail}"));
                break;
        }
    }

    // willResume: the caller stops the loop only to start it again itself (a bank or
    // sell trip, a flee, a reconnect), which the Stopped event says so a listener
    // can tell that from the loop being called off (an event run counting this
    // loop's laps must outlast the one and end on the other).
    public void Stop(string reason = "user stop", bool willResume = false)
    {
        if (State == LoopState.Idle) return;
        string? name = _loop?.Name;
        _resumeAnchor = willResume && _loop is { } stopped && _circleStartRoom is { } anchor
            ? (stopped.Name, stopped.Waypoints.Select(w => w.Key).ToArray(), anchor)
            : null;
        _log?.Info("LoopRunner",
            $"Stop: loop='{name ?? "?"}' state={State} reason={reason}");
        // If we're approaching, stop the walker too. The walker's own
        // Reset on stop detaches the recovery gate, so no gate cleanup
        // is needed on our side for the approach phase.
        if (State == LoopState.Approaching) _walker?.Stop("loop stopped");
        Reset();
        EndReconnectReformHold("the loop was stopped");
        Raise(new LoopEvent(LoopEventKind.Stopped, $"{name}: {reason}", willResume));
    }

    // Avoided-rooms list mutated mid-loop. Re-plan with the new filter so it
    // applies to every BFS call (closest-waypoint pick + rotation + walker
    // approach). The user effectively re-routes the loop without losing the
    // definition — and, crucially, without ending the SESSION: this is not a
    // fresh run, so the one-shot ReachedFirstWaypoint stays consumed. Re-firing
    // it (which the old Stop+Start path did, because Stop→Reset cleared the
    // latch) reset the session statistics and re-broadcast a party @reset on
    // every toggle — turning a route tweak into a "the loop restarted" event.
    //
    // No-op when the runner is idle. Loops without waypoints (legacy v1 loaded
    // from disk) can't be re-expanded, so this only re-routes a waypointed loop.
    public void NotifyAvoidedChanged()
    {
        if (State == LoopState.Idle) return;
        if (_loop is null) return;
        if (_loop.Waypoints.Count == 0) return;

        // Only a change that actually touches THIS loop's path matters. If none of
        // the rooms the loop currently traverses is avoided, the toggle is for a
        // room off the route — leave the running loop completely alone. Avoiding
        // an unrelated room used to Stop+Start the loop (re-approach + session
        // reset + party @reset), stranding it on the preview overlay.
        if (!LoopPathCrossesAvoided())
        {
            _log?.Info("LoopRunner",
                $"avoid-list changed but loop '{_loop.Name}' path is clear of avoided rooms; continuing uninterrupted");
            return;
        }

        Loop snapshot = _loop;
        _log?.Info("LoopRunner",
            $"avoid-list changed; loop '{snapshot.Name}' path crosses an avoided room — re-routing around it");
        // StartInternal supersede-stops the active run itself, so no explicit
        // Stop() first (that path clears the first-waypoint latch). suppress=true
        // keeps the same session across the re-route (no stats reset / @reset).
        StartInternal(snapshot, isRecovery: false, suppressFirstWaypointEvent: true);
    }

    // True when any room the loop currently traverses sits on the avoided list —
    // i.e. the avoid change actually blocks the live route. No filter means
    // nothing is avoided; an unresolvable path (no circle start yet) errs toward
    // re-routing so we never keep a stale route that walks into an avoid.
    private bool LoopPathCrossesAvoided()
    {
        if (_filter is null) return false;
        if (_circleStartRoom is null) return true;
        foreach (RoomKey key in ResolveLoopRoomKeys())
            if (_filter.IsAvoided(key)) return true;
        return false;
    }

    // ----- internals -------------------------------------------------

    // The lap's last step is behind us: count and time the lap, go back to step 0
    // and announce the wrap. Called where the steps run out, and by a forward flee's
    // resume when the run itself walked over the lap's end.
    private void RollLapOver()
    {
        if (_loop is null) return;
        // Record the just-completed lap's duration into the rolling
        // history (capped at MaxLapHistory) so AverageLapTime stays
        // bounded in memory across long-running sessions.
        DateTimeOffset now = _now();
        bool partLap = _partialLap;
        _partialLap = false;
        if (!partLap)
        {
            TimeSpan lapTime = now - _lapStartedAt;
            _lapDurations.Add(lapTime);
            if (_lapDurations.Count > MaxLapHistory) _lapDurations.RemoveAt(0);
            _completedLaps++;
        }
        _lapStartedAt = now;
        _index = 0;

        // A live per-room edit that added or removed a command deferred its
        // re-expansion to here — the one safe point to swap the step list, with
        // _index at 0 and the player back at the entry room after the closing leg.
        if (_reExpandAtLapEnd)
        {
            _reExpandAtLapEnd = false;
            ExpandSteps();
        }

        if (!partLap) Raise(new LoopEvent(LoopEventKind.RepeatStarted, _loop.Name));
    }

    private void SendNextStep()
    {
        if (_loop is null || State != LoopState.Running) return;
        if (_stepInFlight) return;

        // Tier-3 gate may have escalated; if so don't queue a new step.
        if (_recovery is not null && !_recovery.MayProceedWithPlannedStep()) return;

        if (FailIfNothingToWalk()) return;

        // All loops are circular by definition — every lap wraps back
        // to step 0. The runner has no "Finished" end-condition; it
        // runs until the user Stops or the recovery gate aborts it.
        if (_index >= _expandedSteps.Count)
        {
            RollLapOver();

            // A RepeatStarted subscriber can react synchronously — e.g. a
            // room-arrival dispatcher asserting a MovementCoordinator gate to
            // hold the room the loop just wrapped back into. That reaction
            // can change State (via OnPauseChanged) or stop the loop
            // entirely, several frames up the stack from here, but this
            // method already passed its own State/_stepInFlight guard at
            // entry and doesn't know to look again. Re-check before falling
            // through to send the next step: without this, a gate asserted
            // during the Raise() above is silently ignored for THIS send —
            // the loop ships the next move anyway, physically leaving the
            // room a reactor just started dispatching commands for, so
            // those commands resolve against the wrong room entirely.
            if (_loop is null || State != LoopState.Running || _stepInFlight) return;
            // The wrap can re-expand (a live edit), and that can come back empty.
            if (FailIfNothingToWalk()) return;
        }

        LoopStep step = _expandedSteps[_index];
        // A room command that only works in an empty room waits for the room to
        // clear instead of going out to be refused.
        if (HeldForEmptyRoom(step, alreadySent: false)) return;
        // Auto-sneak wants a sneak in place before we step; it holds the coordinator
        // meanwhile and the resume re-drives this step.
        if (step is MoveLoopStep && _moveReadyCheck?.Invoke() == false) return;
        // The ready check sends the pre-step gear, and a swap that asserts and clears
        // its gate in one go re-enters this method through the resume, which sends the
        // step. Sending it again here put the move on the wire twice.
        if (_loop is null || State != LoopState.Running || _stepInFlight) return;
        // The purses were read while a step was in flight or the loop was held.
        if (_tollCheckOwed)
        {
            _tollCheckOwed = false;
            if (ClosedTollAhead())
            {
                _log?.Info("LoopRunner", "a toll on the circuit isn't taken with the party; planning the loop again without it");
                PlanAgainRoundAToll(_loop);
                return;
            }
        }
        // Last, with nothing left that could keep the step back: a toll the party is
        // let through is taken as paid from that moment (PartyTollGate.BeforeTollStep).
        if (step is MoveLoopStep tollMove && TurnedAsideAtToll(tollMove)) return;
        switch (step)
        {
            case MoveLoopStep move:    SendMove(move);    break;
            case CommandLoopStep cmd:  SendCommand(cmd);  break;
        }
    }

    // Asked before a step through a toll exit (PartyTollGate.BeforeTollStep), as
    // the walker asks it (AutoWalkManager.TurnedAsideAtToll). Hold: a movement gate
    // is up and the resume re-drives the step. Closed: the party can't be taken
    // through, so the loop is planned again from here with that exit shut, round
    // it where a way exists and to a stop naming it where none does (UnpaidRefusalFor).
    // The last argument is what the rest of the lap will charge us ourselves
    // (OwnChargesOnTheRestOfTheLap).
    private Func<RoomKey, Direction, RoomExit, long, PartyTollGate.StepVerdict>? _tollStepCheck;
    public void SetTollStepCheck(Func<RoomKey, Direction, RoomExit, long, PartyTollGate.StepVerdict> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _tollStepCheck = check;
    }

    private bool _turningAtToll;

    private bool TurnedAsideAtToll(MoveLoopStep step)
    {
        if (_tollStepCheck is not { } check || _loop is not { } loop) return false;
        if (_tracker.State.CurrentRoom is not { } room
            || !room.Exits.TryGetValue(step.Direction, out RoomExit exit)
            || !PaidCrossingDescriber.Charges(in exit))
            return false;
        switch (check(room.Key, step.Direction, exit, OwnChargesOnTheRestOfTheLap(exit.Target)))
        {
            case PartyTollGate.StepVerdict.Go:
                return false;
            case PartyTollGate.StepVerdict.Hold:
                return true;
        }
        string toll = PaidCrossingDescriber.Describe(room.Key, step.Direction, in exit, RoomNameOf);
        // The plan made for it came straight back to the same toll: it would for ever.
        if (_turningAtToll)
        {
            string stopped = $"stopped at {toll}: the party can't all pay it";
            _unpaidCrossingHandler?.Invoke($"loop '{loop.Name}': {stopped}");
            FailStep(stopped);
            return true;
        }
        _log?.Info("LoopRunner",
            $"step {_index + 1}/{_expandedSteps.Count}: {toll} isn't taken with the party; planning the loop again without it");
        PlanAgainRoundAToll(loop);
        return true;
    }

    // What the lap's steps past the one due will charge us ourselves, in copper:
    // every toll and fare from the room the step due leads to on to the end of the
    // lap, read off the exits. The party's check keeps that back before it hands
    // coin over here (AutoWalkManager.OwnChargesPastTheStepDue is the twin). The
    // next lap is not counted: its tolls are judged when it comes to them.
    private long OwnChargesOnTheRestOfTheLap(RoomKey afterTheStepDue)
    {
        if (_graph is null) return 0;
        long copper = 0;
        RoomKey at = afterTheStepDue;
        for (int i = _index + 1; i < _expandedSteps.Count; i++)
        {
            if (_expandedSteps[i] is not MoveLoopStep move) continue;
            if (_graph.GetRoom(at) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            copper += PaidCrossingDescriber.CostOf(in exit);
            at = exit.Target;
        }
        return copper;
    }

    private void PlanAgainRoundAToll(Loop loop)
    {
        _turningAtToll = true;
        try { StartInternal(loop, isRecovery: true); }
        finally { _turningAtToll = false; }
    }

    // The party's purses, asked for as the loop was planned, have been read
    // (PartyWealthTracker.RoundSettled). If the circuit goes through a toll that is
    // now closed to the party, the loop is planned again from here rather than
    // walked up to the gate: at once between steps, and when the step in flight has
    // landed otherwise (AutoWalkManager.ReplanIfATollAheadIsClosed is the twin).
    public void ReplanIfATollAheadIsClosed()
    {
        if (_loop is not { } loop) return;
        if (State != LoopState.Running || _stepInFlight)
        {
            _tollCheckOwed = State is LoopState.Running or LoopState.Paused;
            return;
        }
        _tollCheckOwed = false;
        if (!ClosedTollAhead()) return;
        _log?.Info("LoopRunner", "a toll on the circuit isn't taken with the party; planning the loop again without it");
        PlanAgainRoundAToll(loop);
    }

    private bool _tollCheckOwed;

    // One lap on from the step due, read off the exits: whether it steps through a
    // toll the filter now turns the party away from.
    private bool ClosedTollAhead()
    {
        if (_filter is null || _graph is null || _expandedSteps.Count == 0
            || _tracker.State.CurrentRoom is not { } here)
            return false;
        RoomKey at = here.Key;
        for (int n = 0; n < _expandedSteps.Count; n++)
        {
            if (_expandedSteps[(_index + n) % _expandedSteps.Count] is not MoveLoopStep move) continue;
            if (_graph.GetRoom(at) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit)) return false;
            if (PaidCrossingDescriber.Charges(in exit)
                && (_filter.DescribeExitBlock(in exit) & (ExitBlockReason.Toll | ExitBlockReason.Fare)) != 0)
                return true;
            at = exit.Target;
        }
        return false;
    }

    private void SendMove(MoveLoopStep step)
    {
        // Predict the expected landing from the tracker's current room.
        if (_tracker.State.CurrentRoom is not { } current
            || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
        {
            _log?.Warn("LoopRunner",
                $"SendMove fail: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"} on step {_index + 1}/{_expandedSteps.Count}");
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                $"no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}"));
            return;
        }

        // An exit whose spell lands us on the loop's side only with an item in hand
        // isn't stepped through without it, however the loop came to hold a leg
        // through it (AutoWalkManager.SendMoveStep has the reasoning).
        if (exit.CastGateItemId > 0 && _tracker.HoldsItem(exit.CastGateItemId) == false)
        {
            FailStep("stopped at " + BlockedExitDescriber.Describe(current.Key, step.Direction, in exit,
                k => _graph?.GetRoom(k)?.Name, id => _itemNameResolver?.Invoke(id)));
            return;
        }

        // Set the landing prediction first so OnTrackerStateChanged
        // confirms the step regardless of HOW we cross the exit (plain
        // cardinal, text command, teleport keyword, or post-action
        // cardinal). _stepInFlight gates the confirmation handler. An exit whose
        // spell teleports us on ends in its landing, not in the room it names.
        _expectedMoveTarget = exit.Landing;
        _expectedMoveSource = current.Key;
        _stepInFlight = true;

        // Predictive room provisioning: light a carried light if the room this lap
        // step enters reads dark, and raise a checkspell hazard buff if it needs one,
        // before any crossing bytes (door / cardinal / special) go out — so the `use`
        // lands ahead of the move and the room is lit / survivable on arrival. No-op
        // for a benign / unmapped target.
        _approachRoomHook?.Invoke(exit.Target);

        // Trapped exit: disarm before crossing, the same way the walker does — by us
        // when we can, else by a capable party member — and cross from OnTrapReply.
        // A loop used to send the plain move and walk into the trap (report
        // paradigm-20260929-215833: 40 damage off an arrow trap).
        if (exit.Hint == RoomExitHint.Trap && _trapEnqueuer is not null)
        {
            bool alreadyCleared = _trapClearedFor == (_index, current.Key, step.Direction);
            _trapClearedFor = null;
            if (!alreadyCleared)
            {
                string dirWord = step.Display;
                if (_shouldDisarmTrap?.Invoke() ?? true)
                {
                    _awaitingTrapDisarm = true;
                    _log?.Info("LoopRunner", $"step {_index + 1}/{_expandedSteps.Count}: disarm trap {dirWord}");
                    _trapEnqueuer(dirWord, "loop", OnTrapReply);
                    return;
                }
                if (_trapDelegator is not null && (_canDelegateTrap?.Invoke() ?? false))
                {
                    _awaitingTrapDisarm = true;
                    _log?.Info("LoopRunner", $"step {_index + 1}/{_expandedSteps.Count}: delegate trap {dirWord} to party");
                    _trapDelegator(dirWord, OnTrapReply);
                    return;
                }
                _log?.Info("LoopRunner",
                    $"step {_index + 1}/{_expandedSteps.Count}: trap on {dirWord} — walking through (disarm disabled or unable)");
            }
        }

        // Door / KeyLocked: if the latest room observation already shows the
        // door open, cross with the plain cardinal. Otherwise route through the
        // same DoorOpenManager the walker uses (bash / pick / key) and cross
        // from OnDoorReply once it opens — a loop should traverse a closed door
        // mid-circuit rather than detach the whole lap. Only when an enqueuer is
        // bound; unit harnesses without one keep the fail-loudly path rather
        // than sending a cardinal into a shut door and desyncing.
        if (exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked)
        {
            if (_tracker.State.OpenDoorDirections is { } openDoors
                && openDoors.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "door pre-open");
                return;
            }
            if (_doorEnqueuer is not null)
            {
                _awaitingDoorOpen = true;
                _log?.Info("LoopRunner",
                    $"step {_index + 1}/{_expandedSteps.Count}: opening door {step.Direction}"
                    + (exit.StatRequirement > 0
                        ? $" (req {exit.StatRequirement}, canBash {exit.CanBash})"
                        : "")
                    + (exit.KeyItemId > 0 ? $" (key {exit.KeyItemId})" : ""));
                _doorEnqueuer(step.Direction, exit.StatRequirement, exit.CanBash, exit.KeyItemId, "loop", OnDoorReply);
                return;
            }
            FailStep($"closed door {step.Direction} mid-circuit — no door-open flow bound");
            return;
        }

        // SearchableHidden: route through the shared HiddenExitRevealManager —
        // the same sea <dir> reveal FSM the walker uses — and cross from
        // OnHiddenRevealReply once the exit appears, so a loop uncovers a hidden
        // exit mid-circuit rather than failing the lap. Pre-check the live room
        // first (a prior sea may have revealed it) to skip wasted round-trips,
        // mirroring the door pre-open check above. Only when an enqueuer is bound;
        // unit harnesses without one keep the fail-loudly path.
        if (exit.Hint == RoomExitHint.SearchableHidden)
        {
            if (_tracker.State.ObservedExitDirections is { } observedExits
                && observedExits.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "hidden already revealed");
                return;
            }
            if (_hiddenSearchEnqueuer is not null)
            {
                _awaitingHiddenReveal = true;
                _log?.Info("LoopRunner",
                    $"step {_index + 1}/{_expandedSteps.Count}: revealing hidden exit {step.Direction}");
                _hiddenSearchEnqueuer(step.Direction, "loop", OnHiddenRevealReply);
                return;
            }
            FailStep($"hidden exit {step.Direction} mid-circuit — no hidden-reveal flow bound");
            return;
        }

        // Winch MultiActionHidden: pull the winch, wait for it to turn AND the gate
        // to open, then cross from OnWinchReply — a winch gate opens on a delay, so
        // firing the move blindly (the synchronous path below) bonks "The gate is
        // closed!". Only when an enqueuer is bound; unwired harnesses fall through to
        // the synchronous dispatch (fire-and-forget pull + move) unchanged.
        if (_winchEnqueuer is not null && WinchManager.IsWinchExit(exit)
            && WinchManager.PullCommand(exit) is { } winchPull)
        {
            if (_tracker.State.OpenDoorDirections is { } openGate && openGate.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "gate pre-open");
                return;
            }
            _awaitingWinch = true;
            _log?.Info("LoopRunner",
                $"step {_index + 1}/{_expandedSteps.Count}: winching gate {step.Direction} ('{winchPull}').");
            _winchEnqueuer(step.Direction, winchPull, /*waitForGate:*/ true, "loop", OnWinchReply);
            return;
        }

        // Synchronous special exits (Text / Teleport / same-room
        // MultiActionHidden) share the walker's emission path so both
        // engines cross them identically — the fix that makes a circuit
        // send "borrow skiff" for a Text exit instead of the cardinal.
        SpecialExitSend sync = SpecialExitDispatch.TrySendSynchronous(
            exit, step.Direction, _tracker.State.CurrentRoom,
            _tracker, _recovery,
            emitMove: (b, msg) => { _preMoveHook?.Invoke(); Write(b, msg); },
            writeAux: Write,
            _teleportResolver, _isLeaderWithFollowers,
            out string? syncFail,
            onLeaderPartySplitTeleport: _onLeaderPartySplit);
        if (sync == SpecialExitSend.Sent) return;
        if (sync == SpecialExitSend.Failed)
        {
            _log?.Debug("LoopRunner",
                $"special-exit dispatch rejected step {_index + 1}/{_expandedSteps.Count} " +
                $"({step.Direction} {exit.Hint} -> {exit.Target}): {syncFail}");
            FailStep(syncFail!);
            return;
        }

        // Plain passage — the cardinal.
        EmitCardinal(step.Direction, exit.Target, exit.CastLandings is null
            ? null : $"→ {exit.Target}, whose spell teleports us on to {exit.Landing}");
    }

    // The loop's step in flight has gone through an exit whose spell teleports
    // everyone on by themselves, which drops the party apart on the way. Announced
    // once the crossing is seen rather than when the step is sent, and whatever
    // hold has landed on the loop since, as the walker does
    // (AutoWalkManager.OnCastCrossingStarted has the reasoning).
    private void OnCastCrossingStarted(RoomKey landing)
    {
        if (!_stepInFlight || _expectedMoveTarget is not { } expected || !expected.Equals(landing)) return;
        if (_isLeaderWithFollowers?.Invoke() != true) return;
        _log?.Info("LoopRunner", $"step {_index + 1}: the exit's spell is taking the party through one by one — re-forming it on landing");
        _onLeaderPartySplit?.Invoke();
    }

    // Emit a plain cardinal move for the circuit, notifying the tracker + recovery
    // gate and firing the pre-move stealth hook. note annotates the wire reason
    // (e.g. "door pre-open"); null for an ordinary passage.
    //
    // Arms the stall watchdog on every send, not just the resume-reconciliation
    // path (ArmStallWatchdog's other two call sites) — this is the ONE place every
    // plain cardinal actually goes on the wire, ordinary mid-loop sends included.
    // Without it, a move that goes Pending outside a pause/resume boundary and then
    // gets swallowed by an unrelated line (a debuff reapplying mid-move, say) had
    // no timeout at all: AdvanceStep only disarms on confirmation, so nothing was
    // ever watching for one that never arrives. Reports paradigm-20260831-091353
    // and -100557 ("the debuff wore off and it got stuck" / "movement stopped
    // again"): a rabid-dire-wolf convulsions tick landed the same instant a step's
    // move went out, the room confirmation never came, and the loop sat wedged —
    // once for 19s, once for over 4 minutes — because no watchdog had been armed.
    private void EmitCardinal(Direction direction, RoomKey target, string? note)
    {
        _tracker.NoteMoveSent(direction);
        _recovery?.NoteEngineStepSent(direction);
        byte[] bytes = AutoWalkManager.EncodeMove(direction);
        _preMoveHook?.Invoke();
        string reason = note is null
            ? $"move {direction} → {target}"
            : $"move {direction} ({note})";
        Write(bytes, reason);
        _cardinalsSent++;
        ArmStallWatchdog($"step {_index + 1} move sent");
    }

    // Counts the cardinals put on the wire, so the stall watchdog can tell whether
    // the move it asked about is still the one in flight when the answer comes
    // (a reroute can leave the step index where it was).
    private int _cardinalsSent;

    // The send (by _cardinalsSent) the stall watchdog has already taken its one
    // extra wait for.
    private int _stallRearmedForSend = -1;

    // Fail the active circuit with reason and reset.
    private void FailStep(string reason)
    {
        _log?.Warn("LoopRunner", $"SendMove fail at step {_index + 1}/{_expandedSteps.Count}: {reason}");
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, reason));
    }

    // Terminal callback from DoorOpenManager for a closed-door circuit step.
    // Mirrors AutoWalkManager.OnDoorReply: on success re-fetch the exit (the
    // step index hasn't advanced) and cross with the cardinal; on failure fail
    // the lap. The _awaitingDoorOpen flag is cleared here before EmitCardinal so
    // the arrival transition it triggers lands in OnTrackerStateChanged normally.
    private void OnDoorReply(DoorOpenResult result)
    {
        if (!_awaitingDoorOpen) return;
        _awaitingDoorOpen = false;

        switch (result)
        {
            case DoorOpenResult.Opened:
                if (State == LoopState.Paused && _index < _expandedSteps.Count
                    && _expandedSteps[_index] is MoveLoopStep parkedDoorStep)
                {
                    // Marked open so the re-driven step crosses it rather than
                    // opening it again.
                    _tracker.NoteNamedDoorOpened(parkedDoorStep.Direction);
                }
                if (ParkClearedStepWhilePaused("door open")) return;
                if (_loop is null || State != LoopState.Running
                    || _index >= _expandedSteps.Count
                    || _expandedSteps[_index] is not MoveLoopStep step)
                {
                    return;
                }
                if (_tracker.State.CurrentRoom is not { } current
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    FailStep($"post-door-open: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
                    return;
                }
                // Cross through the ordinary step path, not straight away: a bash or
                // open ends a sneak (GAME_MECHANICS "Sneaking — commands, equip order,
                // and the sneak state machine", its What ends a sneak list) and a
                // monster may have walked in meanwhile, so the ready check (re-sneak) and a
                // combat pause get their say first (report paradigm-20260928-125823).
                // The door is marked open so the re-driven step crosses it rather than
                // opening it again; if it can't be marked (a move still pending), cross
                // now as before.
                _tracker.NoteNamedDoorOpened(step.Direction);
                if (_tracker.State.OpenDoorDirections?.Contains(step.Direction) == true)
                {
                    _stepInFlight = false;
                    SendNextStep();
                    return;
                }
                _expectedMoveTarget = exit.Target;
                _expectedMoveSource = current.Key;
                _stepInFlight = true;
                EmitCardinal(step.Direction, exit.Target, "post-door");
                return;

            case DoorOpenResult.Failed failed:
                if (failed.Unopenable && TryGoRoundDoor(failed.Reason)) return;
                FailStep($"door open failed: {failed.Reason}");
                return;

            case DoorOpenResult.NotHere notHere:
                // Re-routing came straight back to the same door and the game said
                // the same thing: the map calls this exit a door and the game
                // doesn't. Recovery would take a second entry this soon for an echo
                // of the first and drop it, leaving the step with nothing on the
                // wire and nothing to wake it, so the step ends here instead.
                if (RecoveryWouldDeclineAsEcho())
                {
                    FailStep($"door isn't here ({notHere.Reason}), and re-routing came straight back to it");
                    return;
                }
                // The step was planned from a room we aren't standing in — re-sync
                // and re-route from where we actually are rather than failing the lap.
                EnterRecovery($"step {_index + 1} door isn't here ({notHere.Reason})");
                return;
        }
    }

    // The walker's rule for a door that beat the character
    // (AutoWalkManager.TryGoRoundDoor), for a circuit: the door is given up on for
    // the rest of the run and the loop re-planned from here with it refused, so the
    // legs it was on go round. Failing the lap there, as before, ended the run on a
    // door a few steps would have gone round. False when the step has to fail as
    // before; true when the loop went round, or failed here because a leg has no
    // other way.
    private bool TryGoRoundDoor(string reason)
    {
        if (_loop is null || _bfs is null || _index >= _expandedSteps.Count
            || _expandedSteps[_index] is not MoveLoopStep step
            || _tracker.State.CurrentRoom is not { } here
            || !here.Exits.TryGetValue(step.Direction, out RoomExit exit))
            return false;
        // Recovery would take an entry this soon after the last for its echo and
        // drop it, leaving the step with nothing on the wire: the OnDoorReply
        // NotHere case has the same guard.
        if (RecoveryWouldDeclineAsEcho()) return false;

        IReadOnlyList<LoopWaypoint> waypoints = RuntimeWaypointOrder();
        int unreachableBefore = LoopExpander.Expand(waypoints, _bfs, _filter).UnreachableSegments.Count;
        _abandonedDoors.Add((here.Key, exit.Target));
        _filter = new AbandonedDoorsFilter(_runFilter, _abandonedDoors);
        string door = $"the door {step.Direction.ToLongName()} from {here.Key} ({here.Name})";
        if (LoopExpander.Expand(waypoints, _bfs, _filter).UnreachableSegments.Count > unreachableBefore)
        {
            FailStep($"couldn't open {door} ({reason}), and the loop has no way round it");
            return true;
        }

        _log?.Info("LoopRunner",
            $"gave up on {door} ({reason}); routing the loop round it, and not planning through it again this run");
        EnterRecovery($"step {_index + 1} couldn't open {door}");
        return true;
    }

    private void ForgetAbandonedDoors()
    {
        _abandonedDoors.Clear();
        _filter = _runFilter;
    }

    // An approach walk is the walker's own trip, with its own list of doors given
    // up on: without the run's, it would try a door this run already gave up on.
    private void CarryAbandonedDoorsToWalker()
    {
        if (_abandonedDoors.Count > 0) _walker?.RefuseDoorsOnNextWalk(_abandonedDoors);
    }

    // The way was cleared (trap down, door open, winch turned, hidden exit found)
    // while something else holds the loop: a trap that fired first can drop HP under
    // the rest trigger, and the retry that disarms it then lands mid-rest (report
    // paradigm-20261003-111800). No move went out, so the step isn't in flight. Left
    // marked, the resume read "still at the source with a move in flight" as a
    // refused move and spent a recovery and an rm on it. Taking it out of flight
    // makes the resume re-drive the step, which crosses if the way is still clear
    // and clears it again if the wait outlasted it.
    private bool ParkClearedStepWhilePaused(string what)
    {
        if (State != LoopState.Paused) return false;
        _log?.Info("LoopRunner",
            $"step {_index + 1}/{_expandedSteps.Count}: {what} while paused — crossing once the loop resumes");
        _stepInFlight = false;
        _expectedMoveTarget = null;
        _expectedMoveSource = null;
        return true;
    }

    // Terminal reply from TrapDisarmManager (or a party member via
    // TrapDelegationManager) for a trapped circuit step. Mirrors
    // AutoWalkManager.OnTrapReply: disarmed or no trap there leaves the exit clear;
    // anything else fails the lap rather than walking into a trap that's there.
    private void OnTrapReply(string reply)
    {
        if (!_awaitingTrapDisarm) return;
        _awaitingTrapDisarm = false;

        TrapReplyOutcome? outcome = TrapReply.Read(reply);
        if (outcome == TrapReplyOutcome.Stopped)
        {
            FailStep("trap disarm cancelled");
            return;
        }
        // Asked of the party and nobody took it: as with nobody able, the exit is
        // walked through with the trap still set.
        if (outcome == TrapReplyOutcome.Unanswered)
        {
            _log?.Info("LoopRunner", "no party member took the trap: walking through it");
        }
        else if (outcome != TrapReplyOutcome.Clear)
        {
            FailStep($"trap disarm failed: {reply}");
            return;
        }
        // The disarm flow decides on the re-driven step whether that disarm still
        // stands or the trap has re-armed during the wait.
        if (ParkClearedStepWhilePaused("trap clear")) return;
        if (_loop is null || State != LoopState.Running
            || _index >= _expandedSteps.Count
            || _expandedSteps[_index] is not MoveLoopStep step
            || _tracker.State.CurrentRoom is not { } current)
        {
            return;
        }
        // Cross through the ordinary step path, like a door: the disarm ended a
        // sneak and a monster may have walked in, so the ready check and a combat
        // pause get their say first. The marker keeps the re-driven step from
        // disarming the same trap again.
        _log?.Info("LoopRunner", $"step {_index + 1}/{_expandedSteps.Count}: trap {step.Display} clear — crossing");
        _trapClearedFor = (_index, current.Key, step.Direction);
        _stepInFlight = false;
        SendNextStep();
    }

    // Terminal callback from WinchManager for a winch-gate circuit step. Mirrors
    // OnDoorReply: on Turned re-fetch the exit (the step index hasn't advanced) and
    // cross with the cardinal; on failure fail the lap.
    private void OnWinchReply(WinchResult result)
    {
        if (!_awaitingWinch) return;
        _awaitingWinch = false;

        switch (result)
        {
            case WinchResult.Turned:
                if (ParkClearedStepWhilePaused("winch turned")) return;
                if (_loop is null || State != LoopState.Running
                    || _index >= _expandedSteps.Count
                    || _expandedSteps[_index] is not MoveLoopStep step)
                {
                    return;
                }
                if (_tracker.State.CurrentRoom is not { } current
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    FailStep($"post-winch: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
                    return;
                }
                _expectedMoveTarget = exit.Target;
                _expectedMoveSource = current.Key;
                _stepInFlight = true;
                EmitCardinal(step.Direction, exit.Target, "post-winch");
                return;

            case WinchResult.RoomNotEmpty:
                // Back to an unsent step: the hold clears the room and the step is
                // driven again, winch and all.
                _stepInFlight = false;
                _expectedMoveTarget = null;
                _expectedMoveSource = null;
                if (_loop is not null && _index < _expandedSteps.Count
                    && HeldForEmptyRoom(_expandedSteps[_index], alreadySent: false))
                    return;
                FailStep("winch failed: it can't be pulled with a monster in the room, and none is listed here");
                return;

            case WinchResult.Failed failed:
                FailStep($"winch failed: {failed.Reason}");
                return;
        }
    }

    // ----- A room command that only works in an empty room -------------------
    //
    // The same wait the walker keeps (AutoWalkManager.HeldForEmptyRoom): a step
    // whose room command carries the game data's `nomonsters` condition is held
    // while a monster is in the room, the room is cleared, and the step goes out
    // once the roster shows none. A loop used to send it, take the refusal as a
    // blocked move, and spend its three recoveries on the same refusal.
    private readonly EmptyRoomCommandHold _emptyRoom = new();
    private Func<TimeSpan, Action, IDisposable>? _emptyRoomSchedule;

    // True while a room command waits for the room to be cleared. Combat reads it
    // as a force-clear, so the room is fought with Auto-Combat off.
    public bool AwaitingEmptyRoom => _emptyRoom.Active;

    // See EmptyRoomCommandHold.SetHooks. schedule runs the wait limit.
    public void SetRoomClearHooks(Func<bool> roomHasMonster, Action requestRoomClear, Action abortPartyReform,
        Func<Room, string, bool> commandNeedsEmptyRoom, Func<TimeSpan, Action, IDisposable> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _emptyRoom.SetHooks(roomHasMonster, requestRoomClear, abortPartyReform, commandNeedsEmptyRoom);
        _emptyRoomSchedule = schedule;
    }

    // The room commands a step sends from the room we stand in.
    private IEnumerable<string> RoomCommandsOf(LoopStep step)
    {
        switch (step)
        {
            case MoveLoopStep move
                when _tracker.State.CurrentRoom is { } room
                     && room.Exits.TryGetValue(move.Direction, out RoomExit exit):
                return SpecialExitDispatch.RoomCommandsFor(exit, move.Direction, room, _tracker, _teleportResolver);
            case CommandLoopStep command:
                return MacroStore.SplitCommandStepsKeepingEnters(command.Command).Where(static p => p.Length > 0);
            default:
                return [];
        }
    }

    // True when the step must wait: one of its commands needs an empty room and a
    // monster is here. Puts the hold up the first time. alreadySent marks a command
    // that went out and came back refused.
    private bool HeldForEmptyRoom(LoopStep step, bool alreadySent)
    {
        if (_emptyRoom.BlockedCommand(_tracker.State.CurrentRoom, RoomCommandsOf(step)) is not { } command)
        {
            _emptyRoom.End();
            return false;
        }
        if (_emptyRoom.Active && !alreadySent) return true;
        _log?.Info("LoopRunner", alreadySent
            ? $"step {_index + 1}: '{command}' was refused with a monster here — clearing the room, then trying it again"
            : $"step {_index + 1}: '{command}' only works in an empty room and a monster is here — clearing the room first");
        _emptyRoom.Begin(_emptyRoomSchedule, OnEmptyRoomWaitElapsed, alreadySent);
        return true;
    }

    // The room roster changed. Once no monster is left, the held command goes out.
    public void NoteRoomObserved()
    {
        if (!_emptyRoom.Active || _emptyRoom.RoomHasMonster) return;
        _emptyRoom.End();
        _log?.Info("LoopRunner", $"step {_index + 1}: the room is clear — sending the room command");
        SendNextStep();
    }

    private void OnEmptyRoomWaitElapsed()
    {
        if (!_emptyRoom.Active) return;
        // A fight or a rest is holding the loop and will re-drive the step when it
        // ends; the limit is for a monster nothing is doing anything about.
        if (_coordinator.IsPaused)
        {
            _emptyRoom.Begin(_emptyRoomSchedule, OnEmptyRoomWaitElapsed, alreadySent: false);
            return;
        }
        _emptyRoom.End();
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
            "a room command on the loop only works in an empty room, and a monster is still here"));
    }

    // Terminal callback from HiddenExitRevealManager for a searchable-hidden
    // circuit step. Mirrors AutoWalkManager.OnHiddenRevealReply and OnDoorReply
    // above: on reveal re-fetch the exit (the step index hasn't advanced) and
    // cross with the cardinal; on failure fail the lap. _awaitingHiddenReveal is
    // cleared before EmitCardinal so the arrival transition lands normally.
    private void OnHiddenRevealReply(HiddenSearchResult result)
    {
        if (!_awaitingHiddenReveal) return;
        _awaitingHiddenReveal = false;

        switch (result)
        {
            case HiddenSearchResult.Revealed:
                if (ParkClearedStepWhilePaused("hidden exit found")) return;
                if (_loop is null || State != LoopState.Running
                    || _index >= _expandedSteps.Count
                    || _expandedSteps[_index] is not MoveLoopStep step)
                {
                    return;
                }
                if (_tracker.State.CurrentRoom is not { } current
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    FailStep($"post-hidden-reveal: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
                    return;
                }
                _expectedMoveTarget = exit.Target;
                _expectedMoveSource = current.Key;
                _stepInFlight = true;
                EmitCardinal(step.Direction, exit.Target, "post-hidden-reveal");
                return;

            case HiddenSearchResult.Failed failed:
                FailStep($"hidden reveal failed: {failed.Reason}");
                return;

            case HiddenSearchResult.LeftRoom left:
                // A move already on the wire landed mid-search — re-sync and re-route
                // from where we actually are rather than failing the lap.
                _stepInFlight = false;
                EnterRecovery($"step {_index + 1} hidden-exit search interrupted — moved {left.SearchedIn} → {left.NowIn}");
                return;
        }
    }

    private void SendCommand(CommandLoopStep step)
    {
        // A waypoint command that is the text exit the next move takes anyway (a loop
        // written or imported before the map knew the passage) would cross twice: the
        // command goes through, then the move sends it again from the far side and
        // walks back out (report paradigm-20261005-201733, `go path` both ways).
        if (CrossingTheNextMoveTakes(step) is { } crossing)
        {
            _log?.Info("LoopRunner",
                $"step {_index + 1}: command '{crossing}' skipped — the next move takes that exit itself");
            AdvanceStep();
            return;
        }
        _stepInFlight = true;
        // A waypoint command may chain several commands with `;` or `^M` (the same
        // convention macros / triggers / pre-rest commands use) — send each fragment
        // as its own CR-terminated wire line. A command with no separator splits to a
        // single element, so the common case is unchanged. The step's delay /
        // prompt-advance below applies once, after the whole batch is on the wire.
        // A blank fragment between two separators (`pull book;^M`) is a bare Enter:
        // the room shown again, so whatever the command brought out is in "Also here".
        IReadOnlyList<string> parts = MacroStore.SplitCommandStepsKeepingEnters(step.Command);
        if (parts.Count == 0) parts = new[] { step.Command };   // defensive: Save trims/nulls empty

        // Ours, not typed: nothing that watches outbound input may read these as the
        // user taking over (a typed move pausing the loop, a typed cast or attack
        // claiming the round).
        SendingOwnCommand = true;
        try
        {
            foreach (string part in parts)
            {
                if (part.Length > 0) _tracker.NoteEngineCommandSent(part);
                Write(Encoding.Latin1.GetBytes(part + "\r"), part.Length > 0 ? $"command '{part}'" : "command: Enter");
            }
        }
        finally { SendingOwnCommand = false; }

        if (step.DelayMs > 0)
        {
            // Wait the user's delay before advancing. The command goes out once the
            // room is clear (a fight pauses the loop ahead of it), and the delay runs
            // in real time from there: a fight during it doesn't stop the clock, it
            // only holds the move until the room is clear again. Paused with the
            // loop instead, a delay in a busy room ran 3-9 times as long as set
            // (report paradigm-20261014-180148).
            _awaitingCommandReplies = false;
            _log?.Info("LoopRunner", $"command '{step.Command}': waiting {LoopDelay.Describe(step.DelayMs)} before moving on");
            StartDelay(TimeSpan.FromMilliseconds(step.DelayMs), runsInRealTime: true);
        }
        else
        {
            // No delay: hold the next move until the game has answered every command
            // in the block. "The next prompt" isn't that: a block sent on arrival is
            // followed first by the arrival's own prompt (report
            // paradigm-20261005-194751: `pull book` summoned a monster and the loop
            // had already walked out).
            // The game prints one prompt per command it answers, so the block is
            // answered when that many have come in. Prompts already read from the
            // data being handled now (the arrival's) fired before this send.
            _awaitingCommandReplies = true;
            _commandPromptsOwed = parts.Count;
            _commandRedisplayAwaited = parts.Any(static p => p.Length == 0);
            _combatHoldLogged = false;
            StartDelay(CommandReplyWait);
        }
    }

    // Everything the block needed is in: move on, unless it started a fight. Then the
    // loop stays put (the combat gate takes over once the monster is in the room list)
    // and the bound timer looks again.
    private void FinishCommandWaitIfAnswered()
    {
        if (!_awaitingCommandReplies || State != LoopState.Running) return;
        if (_commandPromptsOwed > 0 || _commandRedisplayAwaited) return;
        if (HoldForCombat()) return;
        StopDelayTimer();
        _awaitingCommandReplies = false;
        _stepInFlight = false;
        AdvanceStep();
    }

    private bool HoldForCombat()
    {
        if (_inCombat?.Invoke() != true) return false;
        if (!_combatHoldLogged)
        {
            _combatHoldLogged = true;
            _log?.Info("LoopRunner",
                $"step {_index + 1}: the command started a fight — holding the next move until it's over");
        }
        return true;
    }

    // The command, when it is one of the text-exit commands of the exit the next move
    // step leaves this room by. Null for anything else, a chained command included.
    private string? CrossingTheNextMoveTakes(CommandLoopStep step)
    {
        string command = step.Command.Trim();
        if (MacroStore.SplitCommandSteps(command).Count > 1) return null;
        if (_expandedSteps[(_index + 1) % _expandedSteps.Count] is not MoveLoopStep next) return null;
        if (_tracker.State.CurrentRoom is not { } here
            || !here.Exits.TryGetValue(next.Direction, out RoomExit exit)
            || exit.Hint != RoomExitHint.Text || exit.TextCommands is not { } crossings)
            return null;
        foreach (string crossing in crossings)
            if (string.Equals(crossing.Trim(), command, StringComparison.OrdinalIgnoreCase))
                return command;
        return null;
    }

    // ----- custom-command delay timer --------------------------------

    private void StartDelay(TimeSpan duration, bool runsInRealTime = false)
    {
        _delayRemaining = duration;
        _delayDeadline = runsInRealTime
            ? _timestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency)
            : 0;
        StartOrResumeDelayTimer();
    }

    private void StartOrResumeDelayTimer()
    {
        if (_delayDeadline != 0) _delayRemaining = DeadlineRemaining();
        if (_delayRemaining <= TimeSpan.Zero)
        {
            OnDelayElapsed();
            return;
        }
        _delayTimer?.Dispose();
        _delayStartTimestamp = _timestamp();
        _delayTimer = _schedule(_delayRemaining < MaxDelaySlice ? _delayRemaining : MaxDelaySlice, OnDelayTick);
    }

    private void CancelDelayTimer()
    {
        _delayTimer?.Dispose();
        _delayTimer = null;
    }

    private void PauseDelayTimer()
    {
        if (_delayTimer is null) return;
        CancelDelayTimer();
        if (_delayDeadline != 0)
        {
            // The clock keeps running; what's left is re-read on resume. Kept above
            // zero so the resume comes back here rather than re-sending the step.
            _delayRemaining = TimeSpan.FromTicks(1);
            return;
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_delayStartTimestamp, _timestamp());
        _delayRemaining -= elapsed;
        if (_delayRemaining < TimeSpan.Zero) _delayRemaining = TimeSpan.Zero;
    }

    private void StopDelayTimer()
    {
        _delayDeadline = 0;
        CancelDelayTimer();
        _delayRemaining = TimeSpan.Zero;
    }

    private void OnDelayTick()
    {
        _delayTimer = null;
        if (_delayDeadline != 0 && DeadlineRemaining() > TimeSpan.Zero)
        {
            StartOrResumeDelayTimer();   // the next slice of a long delay
            return;
        }
        OnDelayElapsed();
    }

    private void OnDelayElapsed()
    {
        CancelDelayTimer();
        _delayRemaining = TimeSpan.Zero;
        _delayDeadline = 0;
        if (_awaitingCommandReplies && State == LoopState.Running && HoldForCombat())
        {
            StartDelay(CommandReplyWait);
            return;
        }
        _awaitingCommandReplies = false;
        if (State != LoopState.Running) return;
        _stepInFlight = false;
        AdvanceStep();
    }

    private void ArmStallWatchdog(string why)
    {
        _stallWatchdog?.Dispose();
        _stallWatchdog = _schedule(StallWatchdogInterval, OnStallWatchdogElapsed);
        _log?.Debug("LoopRunner",
            $"stall watchdog armed ({StallWatchdogInterval.TotalSeconds:F0}s): {why}");
    }

    private void DisarmStallWatchdog()
    {
        _stallWatchdog?.Dispose();
        _stallWatchdog = null;
    }

    private void OnStallWatchdogElapsed()
    {
        _stallWatchdog = null;
        // Only act if we're genuinely still wedged: Running, a step in flight, and
        // the tracker still Pending. A move that confirmed normally already advanced
        // us (AdvanceStep disarms); a re-pause disarmed us too. Escalate to the
        // recovery gate — on Paradigm it fires `rm` for the authoritative position,
        // on stock it runs the footprint backtrack; either way ResumeAfterRecovery
        // then advances (if we really arrived) or reroutes and re-sends (if we're
        // still at the source), so the interrupted move resumes without an overshoot.
        //
        // Reported as stalled, not as a mismatch: tier 2 watches for a 1-of-1 over
        // the engine's NEXT few steps, and a wedged engine has none — reporting a
        // mismatch here parks us in tier 2 forever, because this watchdog has
        // already stopped itself and only a send or a resume re-arms it.
        if (State != LoopState.Running || !_stepInFlight) return;
        if (_tracker.State.Confidence == RoomConfidence.Pending)
        {
            _log?.Warn("LoopRunner",
                $"step {_index + 1} in-flight stall: move Pending, unconfirmed for {StallWatchdogInterval.TotalSeconds:F0}s — escalating to recovery");
            _recovery?.NoteEngineStalled(
                $"loop step {_index + 1} in-flight stall (move interrupted, never confirmed)");
            return;
        }

        // A move sent while the tracker was unsure of its room arms no Pending, so
        // when the game refuses it nothing changes that anyone is listening for and
        // the branch above never sees it (the walker's side of this is report
        // paradigm-20261009-082958). Ask the game where we are. Asked directly, not
        // through NoteEngineStalled: with nobody to ask that goes on to the
        // reverse-walk, and a timer is no ground for walking a stock realm's loop
        // backwards. With nobody to ask, everything stays as it was.
        if (!IsPlainMoveSentWhileUnsure(out EngineRecoveryGate? gate)) return;
        int askedForSend = _cardinalsSent;
        _log?.Warn("LoopRunner",
            $"step {_index + 1} in-flight stall: sent with the tracker {_tracker.State.Confidence}, nothing for {StallWatchdogInterval.TotalSeconds:F0}s — asking the game where we are");
        if (gate.TryResyncOnce?.Invoke(
                $"loop step {_index + 1} sent with the tracker {_tracker.State.Confidence} and unanswered",
                key => OnStallLocateAnswered(askedForSend, key),
                () => WaitOnceMoreForStalledMove(askedForSend, "the game didn't answer")) == true)
        {
            return;
        }
        WaitOnceMoreForStalledMove(askedForSend, "nobody to ask yet");
    }

    // The step in flight is a bare move (no door, hidden-exit, winch or trap task
    // and no command block owns it), the tracker isn't sure of its room, and the
    // recovery gate isn't already dealing with it.
    private bool IsPlainMoveSentWhileUnsure([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EngineRecoveryGate? gate)
    {
        gate = _recovery;
        return gate is { AwaitingAuthoritativeResync: false, CurrentTier: not TierLevel.Tier3 }
            && _tracker.State.Confidence is RoomConfidence.Suspect or RoomConfidence.Lost or RoomConfidence.Unknown
            && _index < _expandedSteps.Count && _expandedSteps[_index] is MoveLoopStep
            && !(_awaitingDoorOpen || _awaitingHiddenReveal || _awaitingWinch || _awaitingTrapDisarm || _awaitingCommandReplies);
    }

    // The game's answer to the stall watchdog's ask. It has already re-located the
    // tracker, and a landing on the step's target, or a refusal that left us in the
    // room it was sent from, has been dealt with on that transition. What is left
    // is the move still in flight with the game naming some other room, which
    // nothing else would act on.
    private void OnStallLocateAnswered(int askedForSend, RoomKey key)
    {
        if (State != LoopState.Running || !_stepInFlight || _cardinalsSent != askedForSend) return;
        if (_tracker.State.CurrentRoom?.Key.Equals(key) != true) return;   // a room the map doesn't have
        if (key.Equals(_expectedMoveTarget))
        {
            _stepInFlight = false;
            AdvanceStep();
            return;
        }
        EnterRecovery($"step {_index + 1} unanswered; the game has us in {key}");
    }

    // One more wait for the same move when the ask didn't come off: the resolver's
    // throttle, or a confusion fumble eating the `rm`, may be all that was in the way.
    private void WaitOnceMoreForStalledMove(int askedForSend, string why)
    {
        if (State != LoopState.Running || !_stepInFlight || _cardinalsSent != askedForSend) return;
        if (_stallRearmedForSend == askedForSend)
        {
            _log?.Info("LoopRunner", $"step {_index + 1}: {why}, again; leaving the step in flight");
            return;
        }
        _stallRearmedForSend = askedForSend;
        ArmStallWatchdog($"step {_index + 1}: {why}, one more wait");
    }

    private void Write(byte[] bytes, string reason)
    {
        if (_wireSender is null)
            _log?.Warn("LoopRunner", $"wire not bound; suppressed: {reason}");
        else
            _wireSender(bytes);
        _log?.Info("LoopRunner", $"step {_index + 1}: {reason}");
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        // While recovering we're waiting for the tracker to (re)confirm a room so
        // we can reroute onto the nearest loop segment. Handle that before the
        // normal running-step confirmation logic (which gates on Running).
        if (State == LoopState.Recovering)
        {
            OnRecoveringTransition(t);
            return;
        }
        if (State != LoopState.Running || !_stepInFlight) return;
        if (_loop is null || _index >= _expandedSteps.Count) return;
        if (_awaitingCommandReplies)
        {
            // The room shown again for the block's bare Enter.
            if (!_commandRedisplayAwaited || t.NewRoom is null
                || t.NewConfidence is not (RoomConfidence.Confirmed or RoomConfidence.Pending)) return;
            _commandRedisplayAwaited = false;
            FinishCommandWaitIfAnswered();
            return;
        }
        if (_expandedSteps[_index] is not MoveLoopStep) return;

        // A door, hidden-reveal, winch or trap sub-FSM owns this step until its reply
        // fires the cardinal. Its bash / pick / sea / disarm output re-observes the current (source)
        // room; acting on that transition here would treat the in-progress step as
        // blocked-at-source and spuriously enter recovery. The FSM clears its
        // await flag before emitting the real move, so the genuine arrival still
        // lands here.
        if (_awaitingDoorOpen || _awaitingHiddenReveal || _awaitingWinch || _awaitingTrapDisarm) return;

        // Suspect / Lost / Unknown are real confidence drops we forward
        // to the recovery gate. Pending is the normal Confirmed →
        // Pending transition that fires synchronously from our own
        // _tracker.NoteMoveSent inside SendMove — escalating on it
        // would spuriously bump every step into Tier2 because the
        // handler runs before the confirmation observation arrives.
        if (t.NewConfidence is RoomConfidence.Suspect
                            or RoomConfidence.Lost
                            or RoomConfidence.Unknown)
        {
            _log?.Info("LoopRunner",
                $"step {_index + 1}: tracker confidence={t.NewConfidence} mid-step; forwarding to recovery gate");
            _recovery?.NoteSuspectedMismatch(
                $"tracker {t.NewConfidence} mid-step {_index + 1}");
            return;
        }

        // Suspect / Lost / Unknown already returned above, so NewConfidence is
        // now Confirmed or Pending.
        if (t.NewRoom?.Key is not { } key) return;

        if (key.Equals(_expectedMoveTarget))
        {
            // Arrived at the step's target. Confirmed is the clean case;
            // Pending means we're physically at the target but the tracker's
            // pending queue still carries a stale entry (a phantom duplicate /
            // an unconsumed echo). The loop only ever has one move in flight,
            // so any queue residue at the target is spurious — the step
            // completed either way. Advance instead of hanging in Pending
            // forever waiting for a Confirmed the wedged queue will never
            // deliver (the multi-minute silent loop stall).
            _stepInFlight = false;
            AdvanceStep();
            return;
        }

        // Below only makes sense once the move has resolved to a Confirmed
        // room. A Pending transition that isn't at the target is just the
        // in-flight posture — the synchronous Confirmed → Pending fired by our
        // own NoteMoveSent inside SendMove (still at source), or a mid-flight
        // redisplay — so wait for the real confirmation rather than treating it
        // as a landing.
        if (t.NewConfidence != RoomConfidence.Confirmed) return;

        if (t.PreviousRoom is not null
            && key.Equals(t.PreviousRoom.Key))
        {
            // Blocked at source — the move didn't take (a mob in the way, lag, a
            // transient obstruction). Instead of failing straight to Idle, enter
            // bounded auto-recovery: re-determine where we are and reroute onto the
            // loop from there. Since we're confirmed back at the source (which is on
            // the loop), the reroute re-sends this step; a persistent block trips
            // the MaxRecoverAttempts cap and finally surfaces as Failed.
            // A monster came in after the step's empty-room command was judged clear
            // to send, and the command came back refused. That is a wait, not a
            // position to recover.
            if (_index < _expandedSteps.Count && HeldForEmptyRoom(_expandedSteps[_index], alreadySent: true))
            {
                _stepInFlight = false;
                _expectedMoveTarget = null;
                _expectedMoveSource = null;
                return;
            }
            if (TryResendRolledReveal(t.PreviousRoom)) return;
            _log?.Warn("LoopRunner",
                $"step {_index + 1} blocked at source {key}; expected {_expectedMoveTarget}; entering recovery");
            EnterRecovery($"step {_index + 1} blocked at {key}");
        }
        else
        {
            // Confirmed elsewhere — flag the mismatch to the gate. If
            // tier 2 is happy (1-of-1 anchor, etc.) keep going; if it
            // escalates to tier 3 the gate will pause us.
            _log?.Warn("LoopRunner",
                $"step {_index + 1} landed at {key} (expected {_expectedMoveTarget}); graph data may be stale on this exit");
            _recovery?.NoteSuspectedMismatch(
                $"step {_index + 1} landed at {key} (expected {_expectedMoveTarget})");
        }
    }

    // Set by NotifyDisconnected when a running/paused/recovering loop is torn down
    // because the connection dropped. Captures the loop definition so the first
    // real in-game prompt after reconnect can restart it from scratch via a
    // genuine Start() call — see NotifyDisconnected's rationale.
    private Loop? _pendingReconnectResume;

    // True while the master switch (Auto-All) is off. The restart after a
    // reconnect is an engine starting by itself, so it waits for the switch:
    // restarted then, the loop would raise its start events (the session reset,
    // the party's @reset, the base-modes settle) with the user's autos off.
    public Func<bool>? MasterSwitchOff { get; set; }

    // The first game prompt came with the switch off: the restart is owed, and
    // ResumeAfterMasterSwitch runs it.
    private bool _reconnectResumeHeldBySwitch;
    public bool ReconnectResumeHeldForMasterSwitch => _reconnectResumeHeldBySwitch;

    // Reset States, and a death found out at the login: don't restart the loop on
    // the next prompt after a reconnect.
    public void ClearPendingReconnectResume()
    {
        _pendingReconnectResume = null;
        _reconnectResumeHeldBySwitch = false;
    }

    // A restart kept waiting on the master switch is the user's to call off: a
    // Stop, or another character loaded. One still waiting for the reconnect's
    // first prompt is left as it always was.
    public void DropResumeHeldForMasterSwitch(string why)
    {
        if (!_reconnectResumeHeldBySwitch) return;
        _log?.Info("LoopRunner",
            $"reconnect: loop '{_pendingReconnectResume?.Name}' will not be restarted when the master switch is back on ({why})");
        ClearPendingReconnectResume();
    }

    // The master switch is back on, in the game: restart the loop the reconnect
    // set aside.
    public void ResumeAfterMasterSwitch()
    {
        if (!_reconnectResumeHeldBySwitch) return;
        _reconnectResumeHeldBySwitch = false;
        if (_pendingReconnectResume is not { } loop) return;
        _pendingReconnectResume = null;
        _log?.Info("LoopRunner",
            $"reconnect: resuming loop '{loop.Name}' now the master switch is back on");
        RestartAfterReconnect(loop);
    }

    // Torn down by a connection drop (wired from MainWindowViewModel's
    // client.Disconnected, mirroring every other subsystem's NotifyDisconnected).
    // Nothing in the recovery ladder (the gate's Tier2/Tier3/awaiting-rm wait, or
    // this runner's own local EnterRecovery) has any way to know the connection
    // died mid-wait — it just sits there forever, and when the wire comes back the
    // FIRST post-reconnect room render gets fed into that stale wait as if it were
    // the landing/reply it was expecting, producing a false "Lost" (report
    // paradigm-20260901-191945). Stop cleanly instead — identical teardown to a
    // user Stop, which already correctly unwinds every sub-state (Approaching's
    // walker, Recovering's local retry, an attached gate) — and remember the loop
    // so the first genuine in-game prompt after reconnect restarts it fresh, with
    // no stale recovery state left to misread.
    public void NotifyDisconnected()
    {
        // A restart held for the master switch goes back to waiting for the next
        // stay's first prompt, which asks the switch again.
        _reconnectResumeHeldBySwitch = false;
        if (State != LoopState.Idle)
        {
            _pendingReconnectResume = _loop;
            Stop("disconnected — will resume on reconnect", willResume: true);
        }
        // A reform hold belongs to the connection that just ended. Dropped after the
        // stop, which drops it too: lifting it first would let the held loop step.
        EndReconnectReformHold("disconnected");
    }

    // Reports whether a leader's party reform is still waiting for the first room
    // display after a reconnect (PartyReformCoordinator.PendingReform). Unset
    // before wiring → no reform, so the restarted loop steps at once.
    private Func<bool>? _reformAwaitsRoom;

    public void SetReconnectReformProbe(Func<bool> reformAwaitsRoom)
    {
        ArgumentNullException.ThrowIfNull(reformAwaitsRoom);
        _reformAwaitsRoom = reformAwaitsRoom;
    }

    // For the bug report: the loop waiting to restart after a reconnect, and whether
    // the restarted loop is being held until the party reform has seen the room.
    public string? PendingReconnectResumeName => _pendingReconnectResume?.Name;
    public bool ReconnectResumeHeldForReform { get; private set; }

    // The reform reads the first room display, which a lit room prints within the
    // second. A room too dark to display never gives it one, and the loop mustn't
    // wait on that for good.
    internal static readonly TimeSpan ReconnectReformHoldMax = TimeSpan.FromSeconds(5);
    private IDisposable? _reconnectHoldTimer;

    private void OnPromptObserved(PromptObservation _)
    {
        // The first genuine in-game prompt after a reconnect restarts the loop
        // NotifyDisconnected set aside.
        //
        // A leader's party reform fires off the first room display, and the prompt
        // that came with that display is read off the wire before its lines are
        // handled: started bare, the loop sent its first step before the reform's
        // hold went up and walked the leader out on the followers (report
        // paradigm-20260923-092317). So when a reform has yet to see the room, the
        // loop is started behind a hold of its own, dropped once the reform has
        // had its look, by which time the reform's own holds are up. Started and
        // held, rather than left waiting to start, the loop is a paused run like
        // any other: Stop stops it, and a member's @comeback finds an engine to
        // resume.
        if (_pendingReconnectResume is { } loop)
        {
            if (MasterSwitchOff?.Invoke() == true)
            {
                if (!_reconnectResumeHeldBySwitch)
                    _log?.Info("LoopRunner",
                        $"reconnect: loop '{loop.Name}' is not restarted with the master switch off; it restarts when the switch is back on");
                _reconnectResumeHeldBySwitch = true;
                return;
            }
            _pendingReconnectResume = null;
            _reconnectResumeHeldBySwitch = false;
            _log?.Info("LoopRunner",
                $"reconnect: resuming loop '{loop.Name}' on first in-game prompt");
            RestartAfterReconnect(loop);
            return;
        }
        ReleaseReconnectHoldOnceReformSawRoom(linesStillToCome: true);
        if (!_awaitingCommandReplies || _commandPromptsOwed == 0 || State != LoopState.Running) return;
        if (--_commandPromptsOwed > 0) return;
        // Prompts are read off the wire ahead of the lines that came with them, so
        // the last reply's own lines (`*Combat Engaged*`) haven't been handled yet.
        // Decide once they have.
        int step = _index;
        _postToUi(() => { if (_index == step) FinishCommandWaitIfAnswered(); });
    }

    private void RestartAfterReconnect(Loop loop)
    {
        if (_reformAwaitsRoom?.Invoke() == true) BeginReconnectReformHold();
        Start(loop);
        ReleaseReconnectHoldOnceReformSawRoom(linesStillToCome: true);
    }

    private void BeginReconnectReformHold()
    {
        if (ReconnectResumeHeldForReform) return;
        ReconnectResumeHeldForReform = true;
        _log?.Info("LoopRunner", "reconnect: holding movement until the party reform has seen the room");
        _coordinator.AssertGate(MovementCoordinator.ReconnectReformGate, nameof(LoopRunner),
            "the party reform has yet to see the room");
        _reconnectHoldTimer?.Dispose();
        _reconnectHoldTimer = _schedule(ReconnectReformHoldMax, OnReconnectHoldElapsed);
    }

    // linesStillToCome: called from a prompt, whose lines (the room display the
    // reform is waiting for among them) haven't been handled yet, so the hold is
    // looked at again once they have.
    private void ReleaseReconnectHoldOnceReformSawRoom(bool linesStillToCome)
    {
        if (!ReconnectResumeHeldForReform) return;
        if (_reformAwaitsRoom?.Invoke() != true)
        {
            EndReconnectReformHold("the party reform has seen the room");
            return;
        }
        if (linesStillToCome) _postToUi(() => ReleaseReconnectHoldOnceReformSawRoom(linesStillToCome: false));
    }

    private void OnReconnectHoldElapsed()
    {
        _reconnectHoldTimer = null;
        EndReconnectReformHold($"no room display for the party reform within {ReconnectReformHoldMax.TotalSeconds:0}s");
    }

    private void EndReconnectReformHold(string why)
    {
        if (!ReconnectResumeHeldForReform) return;
        ReconnectResumeHeldForReform = false;
        _reconnectHoldTimer?.Dispose();
        _reconnectHoldTimer = null;
        _log?.Info("LoopRunner", $"reconnect: movement hold for the party reform dropped ({why})");
        _coordinator.ClearGate(MovementCoordinator.ReconnectReformGate, nameof(LoopRunner), why);
    }

    // Auto-recovery entry: a mid-circuit step landed somewhere we didn't plan for
    // (blocked at the source room, or the recovery gate handed back a room that
    // isn't the step's expected target). Rather than fail to Idle, re-determine
    // where we actually are and reroute onto the nearest loop segment. When the
    // tracker already knows the room we reroute immediately; when it's unsure we
    // send a bare `look` and let the echo (re)confirm the room in
    // OnRecoveringTransition. Bounded by MaxRecoverAttempts so a persistent block
    // eventually surfaces as Failed instead of looping forever.
    // Whether EnterRecovery would drop an entry made now as the previous attempt
    // echoing, rather than act on it.
    private bool RecoveryWouldDeclineAsEcho() =>
        !_resumingAfterFlee && _recoverAttempts > 0
        && _now() - _lastRecoveryAttemptAt < RecoveryAttemptSpacing;

    private void EnterRecovery(string reason)
    {
        // Recovery owns position resolution now — the in-flight stall wait is over.
        DisarmStallWatchdog();
        if (_loop is null)
        {
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, reason));
            return;
        }

        // A block landing while the character is Confused is the movement-fumble
        // mechanic (GAME_MECHANICS "Confusion fumbles — actions fail and must be
        // re-sent") — the just-sent move was consumed by the fumble, not a
        // genuine mapping/graph problem, and the resync below confirms the
        // tracker's belief is correct every time. Confusion can fumble several
        // moves back-to-back well inside MaxRecoverAttempts' window; charging
        // those against the same budget used for real desyncs starves it in
        // seconds and fails the whole loop while the character is otherwise fine
        // and just waiting out a status effect (report paradigm-20260902-113201).
        // Don't count this attempt while it's active — the reroute below still
        // fires, so the step is retried the moment a move actually lands. A
        // genuine block hit right after confusion clears still gets the full
        // budget, since only attempts taken *while confused* are exempted.
        // Arriving again before the world could have changed isn't a new attempt,
        // it's the same one echoing. Drop it rather than spend budget on it — the
        // next block or mismatch re-enters, by which time a resync may have landed
        // or the character may actually have moved.
        DateTimeOffset now = _now();
        if (RecoveryWouldDeclineAsEcho())
        {
            _log?.Debug("LoopRunner",
                $"recovery re-entered {(now - _lastRecoveryAttemptAt).TotalMilliseconds:F0}ms after the "
                + $"last attempt; too soon to be a fresh chance — ignoring. reason={reason}");
            return;
        }

        bool confused = _isConfused?.Invoke() == true;
        if (_resumingAfterFlee)
        {
            _log?.Info("LoopRunner", $"rerouting after a flee — not a failed recovery ({reason})");
        }
        else if (!confused)
        {
            _lastRecoveryAttemptAt = now;
            _recoverAttempts++;
            if (_recoverAttempts > MaxRecoverAttempts)
            {
                _log?.Warn("LoopRunner",
                    $"recovery exhausted after {MaxRecoverAttempts} attempts; failing loop. last={reason}");
                RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                    $"recovery exhausted: {reason}"));
                return;
            }
        }

        // Drop the gate + any in-flight step; we're re-planning from scratch.
        _recovery?.Detach();
        StopDelayTimer();
        if (_awaitingDoorOpen) { _doorStopAll?.Invoke(); _awaitingDoorOpen = false; }
        if (_awaitingHiddenReveal) { _hiddenSearchStopAll?.Invoke(); _awaitingHiddenReveal = false; }
        if (_awaitingWinch) { _winchStopAll?.Invoke(); _awaitingWinch = false; }
        if (_awaitingTrapDisarm) { _trapDelegateStopAll?.Invoke(); _awaitingTrapDisarm = false; }
        _trapClearedFor = null;
        _stepInFlight = false;
        _awaitingCommandReplies = false;
        _expectedMoveTarget = null;
        _expectedMoveSource = null;
        _heldRoomAskedAbout = null;
        _approachTarget = null;
        _fleeReturnTarget = null;
        State = LoopState.Recovering;
        Raise(new LoopEvent(LoopEventKind.Paused, $"recovering: {reason}"));

        // Lean on Paradigm's authoritative `rm` before trusting ANY belief about
        // where we are. "Blocked at source" / a mid-step mismatch is exactly the
        // shape a name-ambiguous zone (many identically-named rooms sharing an
        // exit pattern) produces: the tracker's belief LOOKS Confirmed, but the
        // room that just refused a move disagrees — and a bare `look` below would
        // just re-run the same name+exit matching that produced the wrong belief
        // in the first place. `rm` hard-locates the tracker independent of that
        // matching, so it corrects a mis-anchor instead of rerouting from the same
        // wrong room cycle after cycle until the retry budget burns out (report
        // paradigm-20260901-100523). Both callbacks reroute — RerouteFromCurrentRoom
        // reads whatever room the tracker holds at that moment, corrected or not —
        // so a failed/unavailable resync (stock realm, no wire, throttled) falls
        // through to exactly the prior behavior.
        // A flee's own move just landed and confirmed the room — there's no doubt to
        // settle, so skip the `rm` (report paradigm-20260927-010144: every hit-and-run
        // lap paused to "check the room").
        if (_resumingAfterFlee && _tracker.State.Confidence == RoomConfidence.Confirmed)
        {
            RerouteFromCurrentRoom();
            return;
        }
        if (_recovery?.TryResyncOnce?.Invoke(reason, _ => RerouteFromCurrentRoom(), RerouteFromCurrentRoom) == true)
        {
            _log?.Warn("LoopRunner",
                $"recovery {_recoverAttempts}/{MaxRecoverAttempts}: {reason}; confirming via rm before rerouting");
            return;
        }

        // Tracker already sure of the room → reroute now. Issuing a `look` here
        // would race the reroute's first move: the echo re-prints the current room
        // and would trip the tracker into Suspect right after we send that move.
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is not null)
        {
            _log?.Warn("LoopRunner",
                $"recovery {_recoverAttempts}/{MaxRecoverAttempts}: {reason}; rerouting from {_tracker.State.CurrentRoom.Key}");
            RerouteFromCurrentRoom();
            return;
        }

        // Position unknown — ask the game to re-print the room and wait for the
        // tracker to (re)confirm before rerouting. Bare `look` has no target, so
        // the outbound peek-suppression pattern won't fire on it.
        _log?.Warn("LoopRunner",
            $"recovery {_recoverAttempts}/{MaxRecoverAttempts}: {reason}; issuing look to re-determine room");
        Write(Encoding.Latin1.GetBytes("look\r"), "recovery look");
    }

    // Reroute the active loop from wherever the tracker now says we are — picks the
    // closest waypoint, re-approaches if needed, and continues the circle. Reuses
    // Start's planning; isRecovery keeps the bounded budget + lap continuity.
    //
    // Guarded on State == Recovering so a TryResyncOnce success can't double-fire
    // this: SetLocated's own reentrant tracker transition already reroutes us via
    // OnRecoveringTransition below before our callback gets its turn, which leaves
    // State no longer Recovering by the time the callback runs.
    private void RerouteFromCurrentRoom()
    {
        if (_loop is null) return;
        if (State != LoopState.Recovering) return;
        // A refused toll has just asked for the inventory, and what the loop can
        // be re-planned over depends on the answer: ahead of it every toll is
        // closed, and a loop the purse could pay for would stop at its next one.
        // The walker holds its re-plan the same way (AutoWalkManager.TryReplanOrFail).
        if (_purseReadWait?.Invoke(RerouteOnceThePurseIsRead) == true)
        {
            _log?.Info("LoopRunner", "recovery: reroute waits for the inventory read the refused toll asked for");
            return;
        }
        StartInternal(_loop, isRecovery: true);
    }

    // The read answered, or its bound passed. Still the same recovery, or nothing:
    // a stop, or a reroute another road took meanwhile, has left that state.
    private void RerouteOnceThePurseIsRead()
    {
        if (_loop is null || State != LoopState.Recovering) return;
        StartInternal(_loop, isRecovery: true);
    }

    // Asked before a recovery's reroute: if an inventory read a refused toll asked
    // for is still unanswered, takes the reroute to run when it answers and returns
    // true (OwedPurseRead.WaitForAnswer). Unset, nothing waits.
    private Func<Action, bool>? _purseReadWait;
    public void SetPurseReadWait(Func<Action, bool> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        _purseReadWait = wait;
    }

    // Tracker transitions arriving while State == Recovering: once it firmly
    // (re)confirms a room, reroute onto the nearest loop segment from there.
    private void OnRecoveringTransition(RoomTransition t)
    {
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is null) return;
        _log?.Info("LoopRunner",
            $"recovery: re-determined room {t.NewRoom.Key}; rerouting");
        RerouteFromCurrentRoom();
    }

    // The walker's rule for a reveal that rolls (AutoWalkManager.TryResendRolledReveal
    // has the reasoning): the room command goes out again on its own budget before
    // the bonk is taken for a blocked step and spent against the three recoveries.
    private int _revealRetries;

    // Diagnostics: rolled-reveal re-sends spent on the step in flight.
    public int RolledRevealRetries => _revealRetries;

    private bool TryResendRolledReveal(Room source)
    {
        if (_index >= _expandedSteps.Count || _expandedSteps[_index] is not MoveLoopStep step) return false;
        if (!source.Exits.TryGetValue(step.Direction, out RoomExit exit)
            || SpecialExitDispatch.RolledRevealCommand(exit) is not { } reveal) return false;
        if (_revealRetries >= SpecialExitDispatch.RolledRevealRetryCap)
        {
            _log?.Info("LoopRunner",
                $"step {_index + 1}: '{reveal}' still hasn't opened the way {step.Direction} after "
                + $"{_revealRetries} more tries — treating the step as blocked");
            return false;
        }

        _revealRetries++;
        _stepInFlight = false;
        _expectedMoveTarget = null;
        _expectedMoveSource = null;
        _log?.Info("LoopRunner",
            $"step {_index + 1}: '{reveal}' didn't open the way {step.Direction} (its roll can miss) — "
            + $"sending it again, retry {_revealRetries} of {SpecialExitDispatch.RolledRevealRetryCap}");
        SendNextStep();
        return true;
    }

    private void AdvanceStep()
    {
        // Forward progress → refresh the recovery budget so an unrelated block
        // later in the lap gets the full retry allowance again.
        DisarmStallWatchdog();
        _recoverAttempts = 0;
        _revealRetries = 0;
        _lastRecoveryAttemptAt = DateTimeOffset.MinValue;   // reset the spacing clock with the budget
        _index++;
        Raise(new LoopEvent(LoopEventKind.StepCompleted, $"{_index}/{_expandedSteps.Count}"));
        SendNextStep();
    }

    private void OnPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            if (State == LoopState.Running)
            {
                _log?.Info("LoopRunner",
                    $"coordinator paused at step {_index + 1}/{_expandedSteps.Count}");
                State = LoopState.Paused;
                // A custom-command delay timer in flight pauses with
                // the coordinator — resume picks up from the remaining
                // time, not the full duration.
                PauseDelayTimer();
                // Don't count paused time against the in-flight stall watchdog — a
                // long combat legitimately holds the move. Resume re-arms it if the
                // step is still in flight.
                DisarmStallWatchdog();
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            }
            else if (State == LoopState.Approaching)
            {
                // Approach phase: the walker handles its own pause via
                // the coordinator gate, but the runner state has to
                // flip too — otherwise RunStopLabel keeps reporting
                // "Pause" instead of "Run" (since Approaching means
                // "in flight"), and the resume branch in RunStop
                // (which only fires for State==Paused) is unreachable.
                _log?.Info("LoopRunner",
                    $"coordinator paused during approach to {_approachTarget?.ToString() ?? "(?)"}");
                _pausedFromApproach = true;
                State = LoopState.Paused;
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused (approach)"));
            }
            return;
        }
        if (State == LoopState.Paused && _pausedFromApproach)
        {
            _pausedFromApproach = false;
            if (_approachFinishedWhilePaused)
            {
                // The walker completed the approach during the pause window (its
                // resume handler fired Finished before ours). Enter the circle now
                // instead of restoring Approaching — the walker is done and won't
                // re-fire Finished. This is the fix for the "loop walks to the
                // first room then sits idle until a second Run" bug.
                _approachFinishedWhilePaused = false;
                _log?.Info("LoopRunner",
                    "coordinator resumed; approach already finished, entering circle");
                EnterCircleAfterApproach();
                return;
            }
            // Walker is still mid-approach — put the runner back into Approaching
            // so OnWalkerEvent.Finished still hands off into BeginCircle correctly.
            // Don't send any loop steps; the walker owns the wire until it's done.
            _log?.Info("LoopRunner", "coordinator resumed during approach");
            State = LoopState.Approaching;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed (approach)"));
            return;
        }
        if (State == LoopState.Paused && _fleeHolding)
        {
            _log?.Info("LoopRunner", "coordinator resumed, but a flee holds the loop until it lands");
            return;
        }
        if (State == LoopState.Paused)
        {
            _log?.Info("LoopRunner",
                $"coordinator resumed at step {_index + 1}/{_expandedSteps.Count}");
            State = LoopState.Running;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed"));
            // If a delay was in flight, continue it from the remaining
            // time. Otherwise fall through to SendNextStep.
            if (_delayRemaining > TimeSpan.Zero)
            {
                StartOrResumeDelayTimer();
                return;
            }
            // Arrived-while-paused guard: while paused we ignore tracker events,
            // so an in-flight move whose arrival landed during the pause window
            // leaves _index stale. Detect it by position, not posture — if the
            // tracker's current room is already the step's expected target, the
            // move physically completed, so advance the index before SendNextStep
            // rather than re-sending the same direction and walking one extra room
            // (the user-reported "overshoot"). Accept Pending as well as Confirmed:
            // when the arrival confirmed with a stale entry still in the pending
            // queue (a leftover from a just-stopped loop / an unconsumed echo) the
            // tracker lands CurrentRoom at the target but holds Pending posture,
            // and the OnTrackerStateChanged advance that normally handles this was
            // skipped because it fired while we were Paused. Without accepting the
            // Pending-at-target case here the loop falls through to the "still in
            // flight, awaiting confirmation" return below and hangs in the cleared
            // room until a manual redisplay flushes the queue.
            if (_stepInFlight
                && _expectedMoveTarget is { } expected
                && _tracker.State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending
                && _tracker.State.CurrentRoom?.Key.Equals(expected) == true)
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} arrived during pause (tracker at {expected}, {_tracker.State.Confidence}); advancing");
                // Defer the advance+send so a same-burst re-pause aborts it. Keep
                // the in-flight flags set until the deferred body runs — if the
                // send is aborted, the overshoot guard must re-fire on the next
                // resume rather than falling through and re-sending the completed
                // move.
                //
                // Capture the step index and bail if it moved: the SAME server-line
                // burst that cleared the gate can also carry the room's forced
                // re-display (a combat "resync" \r after the final kill). That
                // re-display re-confirms the current room (Confirmed → Confirmed) and
                // OnTrackerStateChanged advances this very step before the deferred
                // body runs. Advancing again here would send the step AFTER next from
                // the pre-move room ("no exit …"), failing the whole lap to Idle
                // (the "moves a room or two then sits idle" report). Only advance if
                // the step is still in flight and un-advanced.
                int overshootIndex = _index;
                DeferResumeDispatch(() =>
                {
                    if (_index != overshootIndex || !_stepInFlight) return;
                    _stepInFlight = false;
                    _expectedMoveTarget = null;
                    _expectedMoveSource = null;
                    AdvanceStep();
                });
                return;
            }
            // A door / winch / hidden-exit sub-FSM was mid-flight when the pause
            // hit. Its OWN reply (not a tracker move) drives the step — it sets
            // _expectedMoveSource and EmitCardinals the move itself — so the tracker
            // legitimately still reads Confirmed at the source room. The refusal /
            // Suspect checks below would misread that as "blocked at source" and
            // spuriously abort the in-progress open (burning a recover attempt);
            // OnTrackerStateChanged guards the identical case at the top of its
            // real-time handler. Mirror it here: wait for the sub-FSM's reply,
            // bounded by the stall watchdog in case the interrupting combat swallowed
            // it, rather than recovering or resending.
            if (_stepInFlight
                && (_awaitingDoorOpen || _awaitingHiddenReveal || _awaitingWinch || _awaitingTrapDisarm))
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} has a door/winch/hidden/trap sub-FSM in flight; awaiting its reply, not recovering or resending");
                ArmStallWatchdog($"resume with step {_index + 1} sub-FSM in flight");
                return;
            }
            // A MoveRefusal ("There is no exit in that direction!", a shut
            // door, etc.) resolved WHILE paused. RoomTracker.NoteMoveBlocked
            // correctly reverted Pending → Confirmed at the source room and
            // fired StateChanged, but OnTrackerStateChanged ignores tracker
            // events while State != Running, so that recovery never happened
            // in real time — this step is still marked in flight even though
            // the move is long since dead. Falling through to the blind resend
            // below would re-issue the exact same doomed direction, get
            // refused again, and (with no combat gate this time to eventually
            // clear and retry) just sit there — the loop only recovers by
            // accident, whenever some unrelated event forces a fresh room
            // observation (paradigm-20260829-084558, paradigm-20260829-104437:
            // one stall ran for over an hour). Recognize "Confirmed, still at
            // the room we sent the move FROM" as the resume-time equivalent of
            // OnTrackerStateChanged's real-time "blocked at source" branch and
            // enter recovery immediately instead of resending.
            if (_stepInFlight
                && _expectedMoveSource is { } source
                && _tracker.State.Confidence == RoomConfidence.Confirmed
                && _tracker.State.CurrentRoom?.Key.Equals(source) == true)
            {
                _log?.Warn("LoopRunner",
                    $"resume: step {_index + 1} was refused while paused (still at {source}, expected {_expectedMoveTarget}); entering recovery");
                EnterRecovery($"step {_index + 1} refused while paused at {source}");
                return;
            }
            // Landed somewhere that's neither the step's expected target
            // (overshoot guard above) NOR its source (refused-while-paused
            // above) — a genuine desync: the exit's real destination doesn't
            // match what the graph says, or a name-ambiguous zone attributed
            // the landing to the wrong room entirely. OnTrackerStateChanged's
            // real-time "Confirmed elsewhere" branch handles the identical
            // shape by flagging the mismatch to the recovery gate instead of
            // trusting the stale plan; this resume path had no equivalent, so
            // it fell all the way through to the blind resend at the bottom.
            // That resend's fresh SendMove room-lookup can paper over ONE hop
            // by luck (it reads the real current room, not the stale target),
            // but _expandedSteps was drawn for a route that no longer matches
            // reality from here, and the very next step hard-fails with
            // "no exit" (report paradigm-20260902-072545: a combat pause
            // absorbed a step landing three rooms off-plan with no mismatch
            // ever raised, and the loop only noticed one hop later, too late
            // to recover from).
            if (_stepInFlight
                && _expectedMoveTarget is { } stillExpectedTarget
                && _tracker.State.Confidence == RoomConfidence.Confirmed
                && _tracker.State.CurrentRoom is { } landedRoom
                && !landedRoom.Key.Equals(stillExpectedTarget))
            {
                _log?.Warn("LoopRunner",
                    $"resume: step {_index + 1} landed at {landedRoom.Key} (expected {stillExpectedTarget}, source {_expectedMoveSource}); forwarding to recovery gate");
                _recovery?.NoteSuspectedMismatch(
                    $"step {_index + 1} landed at {landedRoom.Key} on resume (expected {stillExpectedTarget})");
                return;
            }
            // The tracker landed in Suspect/Lost/Unknown WHILE paused — an
            // ambiguous room observation it couldn't reconcile against the
            // pending queue (a combat redisplay, another player's arrival,
            // etc. mid-pause). OnTrackerStateChanged forwards this to the
            // recovery gate in real time; while paused it never got the
            // chance. Falling through to a blind resend here is worse than
            // useless: NoteMoveSentCore deliberately does NOT re-arm Pending
            // from Suspect/Lost/Unknown (no confirmed anchor to predict a
            // landing from), so a subsequent refusal is silently dropped too
            // — NoteMoveBlocked only acts when confidence is Pending —
            // stranding the loop in Suspect with no way back
            // (paradigm-20260829-111627; also the backstop for the bright-cyan
            // ability-line room misparse of paradigm-20260829-154032, whose
            // primary fix is RoomDisplayParser keeping the title nearest the
            // exits line). Forward to the recovery gate exactly like the
            // real-time branch instead of resending.
            if (_stepInFlight
                && _tracker.State.Confidence is RoomConfidence.Suspect or RoomConfidence.Lost or RoomConfidence.Unknown)
            {
                _log?.Warn("LoopRunner",
                    $"resume: step {_index + 1} tracker confidence={_tracker.State.Confidence} after pause; forwarding to recovery gate");
                _recovery?.NoteSuspectedMismatch(
                    $"tracker {_tracker.State.Confidence} on resume at step {_index + 1}");
                return;
            }
            // The same loss of place with nothing in flight (the pause landed
            // between steps): the send at the bottom would go out from a room we
            // may not be standing in, to the same silently dropped refusal (the
            // walker's side of this is report paradigm-20261009-082958). Tell the
            // gate here too. Where the game can be asked (rm, sys st) the gate
            // pauses us for the answer. The last step's target is dropped first,
            // because an answer naming it would otherwise count as a landing and
            // skip the step that was never sent; the room we stand in is kept
            // instead, so that an answer naming it sends that step
            // (ResumeAfterRecovery). Where the game can't be asked, the gate starts
            // watching and the send goes ahead as before, unless 15 or more steps
            // have gone by since its last sure room: then it takes the loop over
            // for its reverse-walk at once, as it does for a step in flight.
            if (_tracker.State.Confidence is RoomConfidence.Suspect or RoomConfidence.Lost or RoomConfidence.Unknown)
            {
                RoomKey? held = _tracker.State.CurrentRoom?.Key;
                // Another gate came and went while the recovery gate still had us
                // (the game's answer not yet in, or its reverse-walk under way).
                // Telling it again would leave us Running, and its result would
                // then re-drive the step from whichever room it found, with no
                // reroute. Wait for it paused.
                if (_recovery is { AwaitingAuthoritativeResync: true } or { CurrentTier: TierLevel.Tier3 })
                {
                    _expectedMoveTarget = null;
                    _expectedMoveSource = null;
                    _heldRoomAskedAbout = held;
                    PauseForRecovery("the recovery gate is still finding where we are");
                    return;
                }
                // Not when recovery would drop a reroute as an echo of its last
                // attempt: nothing would be left to wake the loop.
                if (!RecoveryWouldDeclineAsEcho())
                {
                    _log?.Warn("LoopRunner",
                        $"resume: tracker confidence={_tracker.State.Confidence} after pause with nothing in flight; forwarding to recovery gate before step {_index + 1}");
                    _expectedMoveTarget = null;
                    _expectedMoveSource = null;
                    _heldRoomAskedAbout = held;
                    _recovery?.NoteSuspectedMismatch(
                        $"tracker {_tracker.State.Confidence} on resume before step {_index + 1}");
                    if (State != LoopState.Running) return;
                    _heldRoomAskedAbout = null;
                }
            }
            // A move was already on the wire when the pause hit and its
            // confirmation hasn't landed yet (the overshoot guard above didn't
            // fire, so the tracker is still Pending on it). Re-sending it here
            // would put a second copy of the same move on the wire AND a phantom
            // duplicate in the tracker's pending queue — the queue never empties,
            // the tracker sticks in Pending-at-target, and the loop hangs on a
            // Confirmed it will never get. Keep the step in flight instead; now
            // that we're Running again the resumed tracker events confirm it and
            // advance us. A refusal doesn't reach this branch — the "refused
            // while paused" check above already caught it once NoteMoveBlocked
            // dropped the pending entry and re-Confirmed.
            if (_stepInFlight && _tracker.State.Confidence == RoomConfidence.Pending)
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} still in flight (tracker Pending); awaiting confirmation, not re-sending");
                // Bound the wait: if the interrupting combat swallowed the move, this
                // confirmation never arrives and the loop would hang forever. The
                // watchdog escalates to recovery once the wait exceeds the window.
                ArmStallWatchdog($"resume with step {_index + 1} still in flight (Pending)");
                return;
            }
            // Defer the send so a same-burst re-pause (a party @wait telepath
            // arriving after the Combat gate cleared in the same server-line
            // burst) lands first and aborts the leaked move.
            //
            // Re-check posture inside the deferred body, because the burst that
            // paused and resumed us can also carry the previous step's arrival —
            // and that arrival advances AND dispatches the next step before this
            // runs. Clearing _stepInFlight unconditionally then sent that same step
            // a second time: a gear swap straddling the arrival put "n n" on the
            // wire and walked the loop into a wall, desyncing it into a lap that
            // re-blocked every circuit (reports paradigm-20260914-054046 /
            // -054501). The synchronous guard above tests the same thing; it just
            // runs before the arrival lands. The overshoot dispatch below already
            // captures its index for this reason.
            int resumeIndex = _index;
            DeferResumeDispatch(() =>
            {
                if (_index != resumeIndex) return;
                if (_stepInFlight && _tracker.State.Confidence == RoomConfidence.Pending) return;
                _stepInFlight = false;
                _awaitingCommandReplies = false;
                SendNextStep();
            });
        }
    }

    // Marshal a resume-triggered step dispatch onto the next UI tick and re-check
    // posture before it fires. PauseStateChanged runs synchronously inside a
    // server-line burst; posting past the burst lets any later gate-assert in the
    // same burst re-pause us first. The State re-check makes that re-pause abort
    // the send rather than leak a move past the new gate.
    private void DeferResumeDispatch(Action send)
    {
        _postToUi(() =>
        {
            if (State != LoopState.Running) return;
            send();
        });
    }

    private void Reset()
    {
        _fleeHolding = false;
        _recovery?.Detach();
        StopDelayTimer();
        DisarmStallWatchdog();
        _emptyRoom.End();
        // Drain a door FSM that was opening on our behalf — otherwise its
        // internal state sticks and the next run's enqueue sits in the queue
        // forever (DoorOpenManager.TryStartNext bails on non-Idle state).
        // Clearing _awaitingDoorOpen also makes any late OnDoorReply a no-op.
        if (_awaitingDoorOpen) _doorStopAll?.Invoke();
        _awaitingDoorOpen = false;
        // Same for a hidden-reveal FSM opening on our behalf.
        if (_awaitingHiddenReveal) _hiddenSearchStopAll?.Invoke();
        _awaitingHiddenReveal = false;
        // Same for a winch FSM turning a gate on our behalf.
        if (_awaitingWinch) _winchStopAll?.Invoke();
        _awaitingWinch = false;
        // A delegated trap is cancelled; a local disarm finishes on its own and its
        // late reply is dropped (the walker does the same — stopping TrapDisarmManager
        // would also drop a party member's @trap request).
        if (_awaitingTrapDisarm) _trapDelegateStopAll?.Invoke();
        _awaitingTrapDisarm = false;
        _trapClearedFor = null;
        _loop = null;
        _index = 0;
        _partialLap = false;
        _expandedSteps = new List<LoopStep>();
        _reExpandAtLapEnd = false;
        _stepInFlight = false;
        _awaitingCommandReplies = false;
        _expectedMoveTarget = null;
        _expectedMoveSource = null;
        _heldRoomAskedAbout = null;
        _approachTarget = null;
        _fleeReturnTarget = null;
        _circleStartRoom = null;
        _firstWaypointReached = false;
        _suppressFirstWaypointEvent = false;
        _returningFromDetour = false;
        _pausedFromApproach = false;
        _approachFinishedWhilePaused = false;
        _recoverAttempts = 0;
        _lastRecoveryAttemptAt = DateTimeOffset.MinValue;   // reset the spacing clock with the budget
        ForgetAbandonedDoors();
        _lapDurations.Clear();
        _completedLaps = 0;
        _lapStartedAt = default;
        State = LoopState.Idle;
    }

    private void Raise(LoopEvent evt) => Event?.Invoke(evt);

    // Terminal-failure raise: Reset() to Idle FIRST, then raise. Consumers that
    // re-read runner state inside the event handler (NavigationViewModel does so
    // synchronously) must see the final Idle state — otherwise a Failed raised
    // while still Running pins the Nav "Looping/moving" chip and Reset() fires no
    // follow-up event to clear it. Mirrors Stop's reset-then-raise ordering.
    // Callers build the LoopEvent as the argument, so its Detail (which reads
    // live step state like _index) is frozen before Reset() wipes that state.
    // Every terminal failure passes here (Stop logs its own line), so this is the
    // program log's end-of-run marker for a loop that didn't stop cleanly.
    private void RaiseAfterReset(LoopEvent evt)
    {
        _log?.Info("LoopRunner", $"Ended: loop='{_loop?.Name ?? "?"}' reason={evt.Detail}");
        Reset();
        Raise(evt);
    }
}

public enum LoopState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    // Walker is driving the player from their current room to the loop's chosen
    // starting waypoint. Loop runner has nothing on the wire yet; transitions to
    // Running when the walker fires Finished.
    Approaching = 3,
    // Transient auto-recovery: a mid-circuit step didn't land where planned, so
    // the runner is re-determining its position (immediately from a confirmed
    // room, or after a bare `look`) before rerouting onto the nearest loop segment.
    // Treated as an active, in-flight state everywhere (never Idle); resolves back
    // into Approaching / Running via StartInternal, or fails after MaxRecoverAttempts.
    Recovering = 4,
}

public enum LoopEventKind
{
    Started = 0,
    StepCompleted = 1,
    Paused = 2,
    Resumed = 3,
    RepeatStarted = 4,
    Stopped = 5,
    Failed = 7,
    // 6 (Finished) retired in schema v2 — loops are circular by
    // definition and never end on their own; only Stop / Failed
    // remove them from running state.
    // Fired once per loop session at the moment the runner begins the circle
    // (either immediately on Start if the player is already at a waypoint, or after
    // the walker-driven approach completes). Consumers anchor lap stats, fire
    // @reset to the party, etc. on this event rather than on Started so the timing
    // reflects the actual loop start, not the approach walk.
    ReachedFirstWaypoint = 8,

    // The live loop was renamed in place (Save-current on a still-running loop)
    // without restarting the cycle — no lap/step change, only the display name.
    // Purely a nudge for the nav header / status chip to re-read CurrentLoop.Name;
    // engine-state consumers can ignore it.
    Renamed = 9,
}

// WillResume is set on a Stopped whose caller restarts the loop itself afterwards.
public readonly record struct LoopEvent(LoopEventKind Kind, string Detail, bool WillResume = false);
