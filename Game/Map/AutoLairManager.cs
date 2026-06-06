using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Deterministic Auto-Lair session controller. Holds the marked-rooms
/// set + per-marker respawn overrides, then drives the walker through
/// the Approaching → Waiting → Entering → Engaging cycle picked by
/// <see cref="AutoLairScheduler"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces the previous random-walk Auto-Roam stub</b>. The picker is
/// now timer-aware: it tries to step into each marked lair as close to
/// its respawn ready-at as possible while never entering early (an
/// early entry triggers the spawn check and burns the timer immediately).
/// </para>
/// <para>
/// <b>State machine</b>:
/// <list type="bullet">
///   <item><b>Idle</b> — not running; no scheduler tick; walker untouched.</item>
///   <item><b>Approaching</b> — walking to the chosen lair's WAIT-ROOM
///   (one hop short of the lair). Mid-route flip allowed: if the next
///   tick's pick changes, the walker redirects to the new wait-room.</item>
///   <item><b>Waiting</b> — parked in the wait-room counting down to
///   <see cref="LairDecision.EntryArrival"/>. Re-evaluates on every
///   tick; can flip targets and walk to a different wait-room.</item>
///   <item><b>Entering</b> — single walker leg from wait-room into the
///   lair, dispatched on the entry tick.</item>
///   <item><b>Engaging</b> — player is in the lair fighting. Timer-bound
///   (<see cref="EngageTimeoutSeconds"/>) before returning to
///   Approaching for the next pick. Phase 13's CombatManager will
///   tighten this from a timeout into a "combat-ended" signal.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why no built-in pause</b>: the walker honours its own
/// <see cref="MovementCoordinator"/> pause gates. When the walker
/// pauses (HP threshold, encumbrance, party-wait, user-pause), our
/// Approaching state effectively pauses too — we don't pump moves
/// while it's blocked, and the scheduler ticks keep re-evaluating but
/// won't dispatch until the walker frees up.
/// </para>
/// </remarks>
public sealed class AutoLairManager : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly RoomTracker _tracker;
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LairTimerStore _timers;
    private readonly LogService? _log;

    // Marker set + per-marker user override (seconds; null = use game-data default).
    private readonly Dictionary<RoomKey, int?> _markers = new();

    // Runtime state.
    private readonly DispatcherTimer _schedulerTick;
    private readonly DispatcherTimer _entryTimer;
    private readonly DispatcherTimer _engageTimer;
    private readonly System.Timers.Timer _retryTimer;

    // Travel-cost model — flat default until commit 4 wires the
    // encumbrance-gated table from AutoLairSettings.
    public ITravelCostModel TravelCostModel { get; set; } = new FlatTravelCostModel();

    /// <summary>
    /// How long to stay in Engaging after entering a lair before the
    /// scheduler picks the next target. Stop-gap until Phase 13's
    /// CombatManager exposes a "combat ended" signal we can subscribe
    /// to. Default 30 s.
    /// </summary>
    public int EngageTimeoutSeconds { get; set; } = 30;

    /// <summary>Scoring heuristic. Default = idle-penalised; Throughput = wasted-only.</summary>
    public AutoLairHeuristic Heuristic { get; set; } = AutoLairHeuristic.Default;

    /// <summary>Idle-wait penalty under the Default heuristic. Default 1.0.</summary>
    public double IdlePenalty { get; set; } = 1.0;

    /// <summary>Current state machine phase. Observable for the bottom-strip status.</summary>
    public AutoLairPhase Phase { get; private set; } = AutoLairPhase.Idle;

    /// <summary>Current target lair, when running. Null in Idle.</summary>
    public RoomKey? CurrentTarget { get; private set; }

    /// <summary>Current wait-room, when running. Null in Idle / Entering / Engaging.</summary>
    public RoomKey? CurrentWaitRoom { get; private set; }

    /// <summary>Latest scheduler decision the controller is acting on. Null in Idle.</summary>
    public LairDecision? LastDecision { get; private set; }

    /// <summary>
    /// Latched entry-arrival instant for the current Waiting cycle. Set
    /// once on Approaching→Waiting (and on the "already at wait-room"
    /// short-circuit) and NOT recomputed on every scheduler tick — that
    /// was the bug that left the player parked in the wait-room
    /// forever, with the 1 s tick resetting the entry timer to
    /// <c>now + entryHopDuration</c> before it could fire. Cleared on
    /// Entering / Stop / target-change.
    /// </summary>
    public DateTimeOffset? CurrentEntryArrivalAt { get; private set; }

    public bool IsActive => Phase != AutoLairPhase.Idle;

    public IReadOnlyCollection<RoomKey> Marked => _markers.Keys.ToArray();

    /// <summary>Per-marker override snapshot — null value means "use game-data default".</summary>
    public IReadOnlyDictionary<RoomKey, int?> Overrides =>
        new Dictionary<RoomKey, int?>(_markers);

    /// <summary>Fires on every mutation to the marker set or its overrides.</summary>
    public event Action? MarkedChanged;

    /// <summary>Fires when <see cref="IsActive"/> flips. Carries the new value.</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>Fires when <see cref="Phase"/> changes; carries the new phase.</summary>
    public event Action<AutoLairPhase>? PhaseChanged;

    public AutoLairManager(
        AutoWalkManager walker,
        RoomTracker tracker,
        RoomGraphManager graph,
        BfsMapper bfs,
        LairTimerStore timers,
        LogService? log = null)
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

    /// <summary>
    /// Begin scheduling. No-op when already active, when fewer than
    /// 2 markers are set, or when the tracker has no current room
    /// for the walker to start from.
    /// </summary>
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
        CurrentTarget = null;
        CurrentWaitRoom = null;
        CurrentEntryArrivalAt = null;
        LastDecision = null;
        SetPhase(AutoLairPhase.Idle);
        ActiveChanged?.Invoke(false);
        _log?.Info("AutoLair", $"stop: {reason}");

        if (_walker.State != WalkState.Idle) _walker.Stop("auto-lair stop");
    }

    // ----- scheduler tick + dispatch -------------------------------

    private void OnSchedulerTick()
    {
        if (!IsActive) return;
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
            // The room we're already in can't trigger its own respawn —
            // MajorMUD only checks on entry. Skip it so the scheduler
            // routes us out and back; the timer keeps ticking.
            if (lair.Equals(current)) continue;

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

    /// <summary>
    /// Choose the wait-room for <paramref name="lair"/> from
    /// <paramref name="current"/>. Preferred shape: the room immediately
    /// before <paramref name="lair"/> on the shortest BFS path. Falls
    /// back to the closest non-marked neighbour when the preferred
    /// wait-room is itself a marked lair (would trigger a stray spawn
    /// check). Returns <c>(null, null)</c> when no eligible wait-room
    /// exists, signalling the candidate is unschedulable.
    /// </summary>
    private (RoomKey? waitRoom, int? hops) PickWaitRoom(RoomKey current, RoomKey lair)
    {
        // Already at the lair → we just arrived; treat current as
        // the wait-room (0 approach hops). The Approaching dispatch
        // will short-circuit to Waiting → Entering.
        if (current.Equals(lair)) return (lair, 0);

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

    /// <summary>
    /// BFS-shortest non-marker neighbour of <paramref name="lair"/>
    /// from <paramref name="current"/>. Used when the natural wait-room
    /// (the BFS path's second-to-last room) is a marker we don't want
    /// to disturb.
    /// </summary>
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

    /// <summary>
    /// Replay a list of directions from <paramref name="source"/> through
    /// the graph and return the room keys touched (excluding source).
    /// Stops early if a step lands outside the graph — defensive against
    /// a graph reload mid-tick.
    /// </summary>
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

    /// <summary>
    /// Latch <see cref="CurrentEntryArrivalAt"/> and start the entry
    /// timer once per Waiting cycle. Subsequent scheduler ticks that
    /// resolve to the same target take the early-return in
    /// <see cref="EvaluateAndDispatch"/> and don't touch this — the
    /// running timer is allowed to fire on time.
    /// </summary>
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
    }

    private void OnEngageTimerFired()
    {
        _engageTimer.Stop();
        if (!IsActive) return;
        SetPhase(AutoLairPhase.Approaching);
        EvaluateAndDispatch();
    }

    // ----- walker / tracker event hooks ----------------------------

    private void OnWalkerEvent(WalkEvent evt)
    {
        if (!IsActive) return;

        switch (evt.Kind)
        {
            case WalkEventKind.Finished:
                // The walker landed on its destination. Branch on phase.
                if (Phase == AutoLairPhase.Approaching && CurrentTarget is { } target)
                {
                    SetPhase(AutoLairPhase.Waiting);
                    // Latch entry-arrival to either "now + entry hop"
                    // (lair already ready, just step in) or the lair's
                    // ReadyAt (mob hasn't respawned yet — wait it out).
                    // Recomputing from "now" beats reusing
                    // LastDecision.EntryArrival, which was estimated at
                    // PickNext time and would be skewed if our travel
                    // estimate didn't match reality. Subsequent same-
                    // target scheduler ticks won't touch the latch (see
                    // EvaluateAndDispatch early-return).
                    DateTimeOffset entryFloor =
                        DateTimeOffset.UtcNow + TravelCostModel.EntryHopDuration;
                    DateTimeOffset entryAt = entryFloor;
                    int? overrideSec = _markers.TryGetValue(target, out int? o) ? o : null;
                    if (_timers.NextReadyAt(target, overrideSec) is { } readyAt
                        && readyAt > entryAt)
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
                // We sent the stop ourselves — don't recurse. External
                // stops bail the session so the user sees consistent
                // behaviour ("if I stop the walker, Auto-Lair stops too").
                if (Phase != AutoLairPhase.Idle && Phase != AutoLairPhase.Engaging)
                {
                    // Suppress when we're mid-redirect (we'll have queued
                    // a fresh WalkTo right after Stop). Walker-state-Idle
                    // is the distinguishing signal.
                    if (_walker.State == WalkState.Idle)
                        Stop("walker stopped externally");
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

/// <summary>
/// Phases of the Auto-Lair state machine. See <see cref="AutoLairManager"/>
/// for the per-phase semantics.
/// </summary>
public enum AutoLairPhase
{
    Idle,
    Approaching,
    Waiting,
    Entering,
    Engaging,
}
