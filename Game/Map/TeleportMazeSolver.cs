using System.Collections.Generic;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// The minimal surface the walker needs to hand a maze destination off to the
// solver. Kept tiny so AutoWalkManager doesn't take a hard dependency on the
// solver's internals (and so its own tests can inject a fake).
public interface IMazeSolver
{
    // True when this destination is a teleport-maze room the solver can drive
    // to (Stock realm, wire bound, not already mid-solve).
    bool CanSolve(RoomKey destination);

    // Take over navigation to destination. Returns true when the solver
    // accepted the job (it will surface the outcome through the walker's Event).
    bool TryBegin(RoomKey destination);
}

// Drives navigation INTO / WITHIN a random-teleport maze pocket (the Warped
// Asylum), the case normal routing can't handle: every room shares a name and a
// plain-exit fingerprint with its siblings, so a teleport landing collapses the
// tracker to Lost and BfsMapper refuses to plan through the cast-teleport exits.
//
// Strategy, per the pocket's structure captured by TeleportMazeIndex:
//   1. Get inside — if the player is outside, route to the one-way cast mouth
//      and cross it (the crossing fires the random teleport that drops us in).
//   2. Relocalize — after any teleport the tracker is Lost, so identify the
//      landing from its live "1x2 signature": the room's own obvious-exits mask
//      plus each neighbour's mask read via `look <dir>` (a passive peek that
//      renders the neighbour without moving or casting). The index maps that
//      signature back to an exact RoomKey; we hard-locate the tracker there.
//   3. Route or reshuffle — once located, if a plain (teleport-free) route to
//      the goal exists, hand the final leg to the walker. If not (the goal sits
//      in a different plain-connected component), walk a cast-teleport exit to
//      re-teleport ("reshuffle") and retry from the new landing.
//
// All timing is single-threaded on the UI dispatcher, like every other engine
// send. Two short timers absorb the game's asynchrony: a "settle" window that
// waits out the double room-display a teleport can emit before relocalizing off
// the LAST one, and a per-look timeout that fails the solve if a peek never
// renders.
public sealed class TeleportMazeSolver : IMazeSolver, IDisposable
{
    private const string LogSource = "TeleportMaze";

    // A teleport can echo two room displays back-to-back; wait this long with no
    // fresh display before treating the last one as the settled landing room.
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(700);

    // A `look <dir>` peek that never renders a room within this window fails the
    // relocalization rather than hanging the solve.
    private static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(3);

    // Safety bound on the probabilistic reshuffle search — each attempt is one
    // move plus a settle. A genuine pocket is small; this caps runaway churn if
    // the goal's component is never randomly hit (mis-flagged topology).
    private const int MaxReshuffleAttempts = 80;

    // Paradigm-only: a dropped `rm` reply is re-sent up to this many times before
    // the solve gives up. `rm` is a reliable server query, so this only covers a
    // rare lost packet — not a fallback to the look sweep (Paradigm never looks).
    private const int MaxRmRetries = 2;

    private enum Phase { Idle, RoutingToEntrance, Settling, Looking, Resyncing, Walking }

    private readonly TeleportMazeIndex _index;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly AutoWalkManager _walker;
    private readonly LogService? _log;

    // True on a ParaMud (Paradigm) realm, where the solver relocalizes by `rm`
    // instead of the look-sweep. Reads GameData.ActiveRealm live per-call so a
    // mid-session set swap is honoured. Defaults to stock (always false).
    private readonly Func<bool> _isParadigm;

    // Paradigm authoritative-position source. Its PositionResolved event fires on
    // (and only on) a parsed `rm` reply, so the solver keys its relocalization off
    // the true answer — never a same-second move-confirm that would race it. Null
    // on stock / in tests, where the sweep or a test seam drives relocalization.
    private readonly ParadigmPositionResolver? _paradigmResolver;

    // Defers the initial Start() (and the walker delegation) off the current
    // call stack so TryBegin, invoked from INSIDE the walker's own WalkTo, never
    // re-enters the walker synchronously. Production posts to the UI dispatcher;
    // tests run it inline.
    private readonly Action<Action> _post;

    private readonly DispatcherTimer? _settleTimer;
    private readonly DispatcherTimer? _lookTimeout;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    private Phase _phase = Phase.Idle;
    private RoomKey _goal;
    private int _attempts;

    // Paradigm rm-relocalize scratch: how many times the current `rm` has been
    // re-sent after a dropped reply, and the room a plain step departed from so a
    // step that `rm` shows didn't move us (a blocked door/gate) is caught instead
    // of re-planning the same step forever.
    private int _rmRetries;
    private RoomKey? _paradigmStepFrom;

    // Relocalization scratch — snapshot the landing room's own exit mask when a
    // look sweep begins, then fill one neighbour mask per `look <dir>` reply.
    private RoomObservation? _lastObserved;
    private uint _ownMask;
    private readonly Dictionary<Direction, uint> _neighbourMasks = new();
    private readonly Queue<Direction> _lookQueue = new();
    private Direction _currentLookDir;

    // Enter-from-outside crossing target.
    private RoomKey _entranceSource;
    private Direction _entranceDir;

    // Self-driven final walk — the plain (teleport-free) route to the goal, each
    // step paired with the room the graph says it lands in. The solver drives
    // these moves itself (ungated, like a reshuffle) and verifies each landing by
    // name + exit-mask, instead of handing the leg to AutoWalkManager, whose
    // heuristic tracker can't confirm same-named maze steps and whose moves stall
    // on the combat gate.
    private readonly Queue<(Direction Dir, RoomKey Expected)> _finalRoute = new();
    private RoomKey _stepExpected;

    // Memoized "can a plain route reach the goal from here?" — a spell's landing
    // pool (up to ~24 rooms) is re-scored on every reshuffle, and the goal is
    // fixed for the whole solve, so cache the BFS verdict per room. Cleared at
    // TryBegin.
    private readonly Dictionary<RoomKey, bool> _reachGoalCache = new();

    // ----- bug-report surface ----------------------------------------
    public bool Active { get; private set; }
    public RoomKey? Goal => Active ? _goal : (RoomKey?)null;
    public int Attempts => _attempts;
    public string PhaseName => _phase.ToString();

    // Runs on every realm, so the solver is not gated by realm. The relocalization
    // METHOD differs by realm, though: on stock the only tool is the realm-agnostic
    // 1x2 look-sweep. On Paradigm the game answers `rm` with the player's own
    // authoritative (map,room) (see GAME_MECHANICS.md) — and that room number is
    // distinct even though every asylum room shares one NAME, so `rm` pinpoints the
    // exact landing, including the dead-end Padded Cells the look-sweep can't
    // relocalize in. So on Paradigm the solver relocalizes with `rm` alone and never
    // looks: every landing AND every plain step re-locates by `rm`, and a dropped
    // reply is re-sent rather than falling back to a look. On stock it uses the
    // sweep directly. (The Paradigm asylum's pull-lever escape is neutralised at
    // graph-build in RoomGraphManager so the pocket detects there the same as stock.)
    public bool Enabled => true;

    public TeleportMazeSolver(
        TeleportMazeIndex index,
        RoomGraphManager graph,
        RoomTracker tracker,
        BfsMapper bfs,
        AutoWalkManager walker,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        ParadigmPositionResolver? paradigmResolver = null)
        : this(index, graph, tracker, bfs, walker, log, useTimer: true, post: null, isParadigm, paradigmResolver) { }

    internal TeleportMazeSolver(
        TeleportMazeIndex index,
        RoomGraphManager graph,
        RoomTracker tracker,
        BfsMapper bfs,
        AutoWalkManager walker,
        LogService? log,
        bool useTimer,
        Action<Action>? post,
        Func<bool>? isParadigm = null,
        ParadigmPositionResolver? paradigmResolver = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(walker);

        _index = index;
        _graph = graph;
        _tracker = tracker;
        _bfs = bfs;
        _walker = walker;
        _log = log;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _isParadigm = isParadigm ?? (() => false);
        _paradigmResolver = paradigmResolver;

        _walker.Event += OnWalkerEvent;
        if (_paradigmResolver is not null)
            _paradigmResolver.PositionResolved += OnParadigmPositionResolved;

        if (useTimer)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SettleWindow };
            _settleTimer.Tick += (_, _) => OnSettleElapsed();
            _lookTimeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = LookTimeout };
            _lookTimeout.Tick += (_, _) => OnLookTimeout();
        }
    }

    // Main-window VM supplies the EngineSendGate-wrapped SendUserInput. Without
    // it the solver observes room displays but can't send looks / moves, so
    // CanSolve stays false.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public bool CanSolve(RoomKey destination)
        => Enabled && _wireSender is not null && !Active && _index.IsMazeRoom(destination);

    public bool TryBegin(RoomKey destination)
    {
        if (!CanSolve(destination)) return false;

        _goal = destination;
        _attempts = 0;
        _phase = Phase.Idle;
        _neighbourMasks.Clear();
        _lookQueue.Clear();
        _finalRoute.Clear();
        _reachGoalCache.Clear();
        Active = true;
        _log?.Log(LogSeverity.Info, LogSource, $"engaging maze solver for {destination}");
        // Defer off the walker's call stack — TryBegin runs inside WalkToImmediate.
        _post(Start);
        return true;
    }

    // Fed every parsed room display (RoomDisplayParser.RoomParsed, which fires
    // BEFORE the tracker's look-suppression drops the peek). Public so tests can
    // drive the look / settle sequence.
    public void OnRoomObserved(RoomObservation obs)
    {
        _lastObserved = obs;   // always freshest — the Lost-start relocalize reads it
        if (!Active) return;

        switch (_phase)
        {
            case Phase.Looking:
                _neighbourMasks[_currentLookDir] = MaskOf(obs.Exits);
                _lookTimeout?.Stop();
                SendNextLook();
                break;
            case Phase.Settling:
            case Phase.Walking:
                // A fresh display inside the settle window — a teleport (or a
                // driven step's self-look) can echo two, so keep the last and
                // restart the timer.
                RestartSettle();
                break;
        }
    }

    // ----- state machine ---------------------------------------------

    private void Start()
    {
        if (!Active) return;

        RoomState st = _tracker.State;
        if (st.Confidence == RoomConfidence.Confirmed && st.CurrentRoom is { } cur)
        {
            if (_index.IsMazeRoom(cur.Key))
                ContinueFromLocated(cur.Key);   // already inside; tracker knows the cell
            else
                EnterFromOutside(cur.Key);       // outside; route to the mouth and cross
            return;
        }

        // Lost / Unknown / Suspect — the teleport collapsed the tracker. Force a
        // fresh render of the room we're actually standing in and relocalize off
        // it (the last passive display can be a stale pre-teleport room).
        ObserveLandingAndRelocalize();
    }

    private void EnterFromOutside(RoomKey here)
    {
        if (!_index.TryGetEntrance(_goal, out RoomKey src, out Direction dir))
        {
            FailSolve("maze entrance unknown");
            return;
        }

        _entranceSource = src;
        _entranceDir = dir;

        if (here.Equals(src))
        {
            CrossEntrance();
            return;
        }

        _phase = Phase.RoutingToEntrance;
        _log?.Log(LogSeverity.Info, LogSource,
            $"routing to maze entrance {src} then crossing {dir.ToLongName()}");
        _walker.WalkTo(src);
    }

    private void CrossEntrance()
    {
        _log?.Log(LogSeverity.Info, LogSource,
            $"crossing maze entrance {_entranceDir.ToLongName()} (fires random teleport)");
        SendMove(_entranceDir);
        ObserveLandingAndRelocalize();
    }

    private void ContinueFromLocated(RoomKey here)
    {
        if (here.Equals(_goal))
        {
            Finish();   // relocalized straight onto the goal
            return;
        }

        IReadOnlyList<Direction>? plain = _bfs.FindPath(here, _goal);
        if (plain is { Count: > 0 })
        {
            if (_isParadigm())
                ParadigmStep(here, plain[0]);   // one step, then re-locate via `rm`
            else
                DriveFinalWalk(here, plain);     // stock: queued walk, name+mask verify
        }
        else
            Reshuffle(here);
    }

    // Paradigm plain-route step: walk ONE square toward the goal, let the landing
    // settle, then re-locate with `rm` and re-plan from the authoritative answer
    // (in OnParadigmPositionResolved → ContinueFromLocated). No self-look or
    // name+mask verify — `rm` names the exact room, so a surprise teleport or
    // blocked door is caught by re-planning rather than a signature check, and
    // arrival at the dead-end goal cell (which the look sweep can't relocalize) is
    // just `here == goal`. The settle before `rm` is what keeps a stuck-gate step
    // from racing the query (report 152718).
    private void ParadigmStep(RoomKey here, Direction d)
    {
        _paradigmStepFrom = here;
        _lastObserved = null;
        _log?.Debug(LogSource, $"plain step {d.ToLongName()} from {here}; settle then re-locate via `rm`");
        SendMove(d);
        BeginParadigmSettle();
    }

    // Walk the plain (teleport-free) route to the goal ourselves, one step at a
    // time. Handing the leg to AutoWalkManager fails inside the pocket: its
    // heuristic tracker can't confirm same-named landings without `rm` (which the
    // solve suppresses), and its moves stall on the combat gate that the asylum's
    // monsters keep asserting. Driving the moves directly (ungated, like a
    // reshuffle) and verifying each landing by the graph's name + exit-mask keeps
    // the final leg as robust as the reshuffle loop — and recognizes arrival at a
    // dead-end goal cell (the old man's Padded Cell) by name instead of trying to
    // signature-relocalize a room the index deliberately omits, which walked us
    // straight back out.
    private void DriveFinalWalk(RoomKey here, IReadOnlyList<Direction> route)
    {
        _finalRoute.Clear();
        RoomKey cur = here;
        foreach (Direction d in route)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(d, out RoomExit exit))
            {
                FailSolve("plain route step missing from graph");
                return;
            }
            _finalRoute.Enqueue((d, exit.Target));
            cur = exit.Target;
        }

        _log?.Log(LogSeverity.Info, LogSource,
            $"located at {here}; driving {_finalRoute.Count}-step plain route to {_goal}");
        StepFinalWalk();
    }

    private void StepFinalWalk()
    {
        if (_finalRoute.Count == 0)
        {
            Finish();   // defensive — nothing left to walk
            return;
        }

        (Direction dir, RoomKey expected) = _finalRoute.Dequeue();
        _stepExpected = expected;
        _phase = Phase.Walking;
        _log?.Debug(LogSource, $"final walk: step {dir.ToLongName()} → expect {expected}");
        SendMove(dir);
        // Brief mode renders only a NAME on entry — force a self-look so the
        // landing's full exits render for the name + mask verification.
        _lastObserved = null;
        SendSelfLook();
        RestartSettle();
        RestartLookTimeout();
    }

    private void VerifyStep()
    {
        if (_lastObserved is not { } obs)
        {
            FailSolve("walk step did not render");
            return;
        }

        Room? expected = _graph.GetRoom(_stepExpected);
        bool onRoute = expected is not null
            && string.Equals(obs.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
            && MaskOf(obs.Exits) == expected.ExitMask;

        if (onRoute)
        {
            _tracker.SetLocated(_stepExpected);
            if (_finalRoute.Count == 0)
                Finish();
            else
                StepFinalWalk();
            return;
        }

        // Landed off the planned plain route — a blocked move, or a surprise
        // teleport we mis-attributed. Re-relocalize from scratch and re-plan
        // rather than blindly firing the rest of a route from the wrong room.
        if (++_attempts > MaxReshuffleAttempts)
        {
            FailSolve($"exceeded {MaxReshuffleAttempts} reshuffle attempts");
            return;
        }
        _log?.Log(LogSeverity.Info, LogSource,
            $"walk step landed off-route (saw '{obs.Name}'); re-relocalizing");
        ObserveLandingAndRelocalize();
    }

    private void Reshuffle(RoomKey here)
    {
        if (++_attempts > MaxReshuffleAttempts)
        {
            FailSolve($"exceeded {MaxReshuffleAttempts} reshuffle attempts");
            return;
        }

        IReadOnlyList<Direction> dirs = _index.ReshuffleDirections(here);
        if (dirs.Count == 0)
        {
            FailSolve($"no reshuffle exit at {here}");
            return;
        }

        Direction d = ChooseReshuffleDirection(here, dirs);
        _log?.Log(LogSeverity.Info, LogSource,
            $"reshuffle #{_attempts}: walking {d.ToLongName()} from {here} to re-teleport");
        SendMove(d);
        ObserveLandingAndRelocalize();
    }

    // Pick the reshuffle exit whose random-teleport spell is most likely to drop
    // us somewhere we can act on. Each cast exit fires a DIFFERENT teleport spell
    // with a different landing pool, and the pools differ wildly: the Warped
    // Asylum's "asylum" spell lands in a relocatable corridor that carries a shot
    // at the goal's plain component, while its "cell" spells dump you into
    // dead-end Padded Cells you can't even relocalize in (so you blind-reshuffle
    // and the odds spiral). We score each exit's pool by the fraction of its rooms
    // that are BOTH uniquely relocatable AND plain-connected to the goal — a
    // direct win — tie-broken by the fraction merely relocatable, so a failed roll
    // still lands somewhere we can make the next choice from rather than a blind
    // dead-end. Falls back to the first exit when no pool data is available (a
    // single exit, or a build with no spell catalog).
    private Direction ChooseReshuffleDirection(RoomKey here, IReadOnlyList<Direction> dirs)
    {
        if (dirs.Count == 1 || _graph.GetRoom(here) is not { } room) return dirs[0];

        Direction best = dirs[0];
        double bestUseful = -1.0, bestReloc = -1.0;
        bool scored = false;
        foreach (Direction d in dirs)
        {
            if (!room.Exits.TryGetValue(d, out RoomExit exit)) continue;
            IReadOnlyList<RoomKey>? pool = exit.CastTeleportTargets;
            if (pool is null || pool.Count == 0) continue;

            int useful = 0, reloc = 0;
            foreach (RoomKey t in pool)
            {
                if (!_index.IsUniquelyRelocatable(t)) continue;
                reloc++;
                if (CanPlainReachGoal(t)) useful++;
            }
            double usefulFrac = (double)useful / pool.Count;
            double relocFrac = (double)reloc / pool.Count;
            scored = true;
            if (usefulFrac > bestUseful || (usefulFrac == bestUseful && relocFrac > bestReloc))
            {
                bestUseful = usefulFrac;
                bestReloc = relocFrac;
                best = d;
            }
        }

        if (scored)
            _log?.Debug(LogSource,
                $"reshuffle chooser at {here}: {best.ToLongName()} best "
                + $"(useful {bestUseful:P0}, relocatable {bestReloc:P0} of pool)");
        return scored ? best : dirs[0];
    }

    // A room from which a plain (teleport-free) route to the goal exists — the
    // "useful landing" test for the reshuffle chooser. Cached per solve because a
    // spell's pool is re-scored on every reshuffle. FindPath already refuses to
    // route through cast-teleport exits, so a non-empty path is a genuine walkable
    // route.
    private bool CanPlainReachGoal(RoomKey from)
    {
        if (from.Equals(_goal)) return true;
        if (_reachGoalCache.TryGetValue(from, out bool cached)) return cached;
        bool ok = _bfs.FindPath(from, _goal) is { Count: > 0 };
        _reachGoalCache[from] = ok;
        return ok;
    }

    // Relocalize after a teleport landing, by the realm's method.
    //
    // Paradigm: DON'T fire `rm` yet. A teleport redisplays the room on its own —
    // often twice — so wait out that redisplay (BeginParadigmSettle) before asking
    // a single `rm`. Firing `rm` the instant the move was sent raced the round-time
    // and piled move+`rm` pairs at the wire, so replies desynced from moves and the
    // walker bonked into non-existent exits (report 152718). `rm` reports the
    // player's own (map,room), distinct even though every asylum room shares a name,
    // so once the landing settles it pinpoints the exact room — including the
    // dead-end Padded Cells the look sweep can't relocalize in.
    //
    // Stock (no `rm`): force a fresh render with a self-`look`, then settle and run
    // the 1x2 look sweep off it. A maze move always fires a random teleport, and in
    // BRIEF mode (how ~all players run) the landing renders only a NAME line — no
    // exits — so it yields no RoomObservation; relocalizing off the stale last
    // display would key the sweep to the room we LEFT (the entrance-cross desync
    // from the 102748 report). The self-`look` always prints name+exits; clearing
    // _lastObserved first guarantees the settle can't fire off a pre-teleport room.
    private void ObserveLandingAndRelocalize()
    {
        _lastObserved = null;
        _paradigmStepFrom = null;   // this landing is a teleport, not a plain step
        if (_isParadigm())
        {
            BeginParadigmSettle();   // wait for the teleport's redisplay, then `rm`
            return;
        }
        _phase = Phase.Settling;
        SendSelfLook();
        RestartSettle();
        RestartLookTimeout();
    }

    // Paradigm pacing: after a move, let the game finish before asking `rm`. A
    // teleport (and, on this realm, even a plain step) redisplays the room on its
    // own — often twice — so wait out a quiet window with no fresh display, THEN
    // fire a single `rm` (BeginParadigmResync off the settle tick). Firing `rm`
    // the instant the move was sent raced the round-time so replies desynced from
    // moves and the walker bonked into non-existent exits (report 152718). A
    // blocked move renders no room: the settle window still elapses on its own and
    // fires `rm`, whose same-room answer is read as "didn't move".
    private void BeginParadigmSettle()
    {
        _phase = Phase.Settling;
        RestartSettle();
    }

    // Paradigm authoritative-position query, fired once the landing has settled.
    // ParadigmPositionResolver parses the `Location:` reply and raises its
    // PositionResolved event, which we continue off in OnParadigmPositionResolved.
    // A dropped reply is re-sent (OnLookTimeout) up to MaxRmRetries — never a
    // look-sweep fallback: on Paradigm the solver never looks.
    private void BeginParadigmResync()
    {
        _phase = Phase.Resyncing;
        _rmRetries = 0;
        _log?.Log(LogSeverity.Info, LogSource, "landing settled; querying authoritative position via `rm`");
        SendRm();
        RestartLookTimeout();
    }

    // The `rm` reply lands here — ParadigmPositionResolver fires PositionResolved
    // ONLY on a parsed `Location:` line, so this never races an ordinary move-
    // confirm or a blocked-move refusal (both of which drive the tracker's own
    // StateChanged and used to trip this handler early). Only act while awaiting
    // the reply; a stray manual `rm` outside the Resyncing phase is ignored.
    private void OnParadigmPositionResolved(RoomKey here)
    {
        if (!Active || _phase != Phase.Resyncing) return;
        _lookTimeout?.Stop();

        // A plain step `rm` shows didn't move us (blocked door/gate) would otherwise
        // re-plan the same step forever — escape via a reshuffle teleport instead.
        if (_paradigmStepFrom is { } from && from.Equals(here))
        {
            _paradigmStepFrom = null;
            _log?.Log(LogSeverity.Info, LogSource,
                $"plain step from {from} did not move (blocked); reshuffling");
            Reshuffle(here);
            return;
        }
        _paradigmStepFrom = null;

        _log?.Log(LogSeverity.Info, LogSource, $"relocalized to {here} via authoritative `rm`");
        ContinueFromLocated(here);
    }

    private void BeginRelocalize()
    {
        if (_lastObserved is not { } obs)
        {
            FailSolve("no room display to relocalize from");
            return;
        }

        _ownMask = MaskOf(obs.Exits);
        _neighbourMasks.Clear();
        _lookQueue.Clear();
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
            if ((_ownMask & (1u << d)) != 0)
                _lookQueue.Enqueue((Direction)d);

        if (_lookQueue.Count == 0)
        {
            FailSolve("landing room has no exits to peek through");
            return;
        }

        _phase = Phase.Looking;
        _log?.Debug(LogSource, $"relocalizing: own mask {_ownMask}, peeking {_lookQueue.Count} neighbour(s)");
        SendNextLook();
    }

    private void SendNextLook()
    {
        if (_lookQueue.Count == 0)
        {
            ResolveAfterLooks();
            return;
        }

        _currentLookDir = _lookQueue.Dequeue();
        SendLook(_currentLookDir);
        RestartLookTimeout();
    }

    private void ResolveAfterLooks()
    {
        if (_index.TryIdentify(_ownMask, _neighbourMasks, out RoomKey key))
        {
            _log?.Log(LogSeverity.Info, LogSource, $"relocalized to {key} via 1x2 signature");
            _tracker.SetLocated(key);
            ContinueFromLocated(key);
        }
        else
        {
            // Signature didn't resolve to a unique room (a peek came back
            // ambiguous / incomplete). We can't key the reshuffle exits without a
            // room, so walk any observed exit to move on and try again.
            _log?.Log(LogSeverity.Info, LogSource, "1x2 signature matched no unique room; blind reshuffle");
            BlindReshuffle();
        }
    }

    private void BlindReshuffle()
    {
        if (++_attempts > MaxReshuffleAttempts)
        {
            FailSolve($"exceeded {MaxReshuffleAttempts} reshuffle attempts");
            return;
        }

        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
        {
            if ((_ownMask & (1u << d)) == 0) continue;
            _log?.Log(LogSeverity.Info, LogSource,
                $"blind reshuffle #{_attempts}: walking {((Direction)d).ToLongName()} to re-observe");
            SendMove((Direction)d);
            ObserveLandingAndRelocalize();
            return;
        }

        FailSolve("blind reshuffle: landing room has no exit to walk");
    }

    // ----- walker delegation callbacks -------------------------------

    private void OnWalkerEvent(WalkEvent e)
    {
        if (!Active) return;

        // The walker only drives the leg TO the pocket mouth (RoutingToEntrance);
        // the final in-pocket walk is solver-driven, so no other phase reacts to
        // a walker event. Our own Finish / FailSolve raise walker events too, but
        // they flip Active false first, so the guard above short-circuits them.
        if (_phase != Phase.RoutingToEntrance) return;

        if (e.Kind == WalkEventKind.Finished) CrossEntrance();
        else if (e.Kind == WalkEventKind.Failed) FailSolve($"could not reach maze entrance: {e.Detail}");
        else if (e.Kind == WalkEventKind.Stopped) Abandon("route to entrance superseded");
    }

    // ----- terminal transitions --------------------------------------

    private void Finish()
    {
        _log?.Log(LogSeverity.Info, LogSource, $"maze solve complete → {_goal}");
        StopTimers();
        RoomKey dest = _goal;
        _phase = Phase.Idle;
        Active = false;
        // We drove the final leg ourselves, so the walker never raised its own
        // Finished — surface arrival through it so every WalkTo caller (and the
        // UI) sees the maze solve complete like any other route.
        _walker.ReportMazeSolveSucceeded(dest);
    }

    private void Abandon(string reason)
    {
        _log?.Log(LogSeverity.Info, LogSource, $"maze solve abandoned: {reason}");
        StopTimers();
        _phase = Phase.Idle;
        Active = false;   // the walker already raised Stopped for the UI
    }

    private void FailSolve(string reason)
    {
        _log?.Log(LogSeverity.Warn, LogSource, $"maze solve failed: {reason}");
        StopTimers();
        RoomKey dest = _goal;
        _phase = Phase.Idle;
        Active = false;
        _walker.ReportMazeSolveFailed(dest, reason);
    }

    // ----- timers ----------------------------------------------------

    private void RestartSettle()
    {
        _settleTimer?.Stop();
        _settleTimer?.Start();
    }

    private void OnSettleElapsed()
    {
        _settleTimer?.Stop();
        if (!Active) return;
        if (_phase != Phase.Settling && _phase != Phase.Walking) return;

        // Paradigm: the redisplay (if any) has gone quiet — ask `rm` now. Unlike
        // the stock sweep this doesn't need a rendered display to key off, so a
        // blocked move that renders nothing still advances to the query, whose
        // same-room answer is read as "didn't move".
        if (_isParadigm() && _phase == Phase.Settling)
        {
            BeginParadigmResync();
            return;
        }

        if (_lastObserved is null)
        {
            // The self-look landing render hasn't arrived yet — keep waiting; the
            // look timeout is the hard deadline for a landing that never renders.
            RestartSettle();
            return;
        }
        _lookTimeout?.Stop();
        if (_phase == Phase.Settling)
            BeginRelocalize();   // stock post-teleport: full 1x2 look sweep
        else
            VerifyStep();        // stock driven step: name + mask check
    }

    private void RestartLookTimeout()
    {
        _lookTimeout?.Stop();
        _lookTimeout?.Start();
    }

    private void OnLookTimeout()
    {
        _lookTimeout?.Stop();
        if (!Active) return;
        if (_phase == Phase.Looking)
            FailSolve($"look {_currentLookDir.ToLongName()} did not render a room");
        else if (_phase == Phase.Settling)
            FailSolve("teleport landing did not render");
        else if (_phase == Phase.Resyncing)
        {
            // No Location: reply to our `rm` — re-send it (a rare lost packet) rather
            // than falling back to the look sweep; on Paradigm the solver never looks.
            if (++_rmRetries <= MaxRmRetries)
            {
                _log?.Log(LogSeverity.Warn, LogSource,
                    $"`rm` got no Location: reply; retry {_rmRetries}/{MaxRmRetries}");
                SendRm();
                RestartLookTimeout();
            }
            else
                FailSolve("`rm` resync got no Location: reply");
        }
        else if (_phase == Phase.Walking)
            FailSolve("walk step landing did not render");
    }

    private void StopTimers()
    {
        _settleTimer?.Stop();
        _lookTimeout?.Stop();
    }

    // ----- wire ------------------------------------------------------

    private void SendMove(Direction d) => Send(AutoWalkManager.EncodeMove(d));

    private void SendLook(Direction d)
        => Send(Encoding.Latin1.GetBytes("look " + d.ToLongName() + "\r"));

    // Bare self-look forces a full name+exits render of the room we're standing
    // in. A bare `look` isn't a directional peek, so OutboundMovementObserver
    // doesn't arm the tracker's peek-suppression for it — that's fine: the solver
    // reads the render via OnRoomObserved (which fires before that suppression)
    // and hard-locates with SetLocated once the sweep resolves.
    private void SendSelfLook() => Send(Encoding.Latin1.GetBytes("look\r"));

    // Paradigm authoritative-position query. The reply's `Location:` line is
    // parsed by ParadigmPositionResolver, which hard-locates the tracker.
    private void SendRm() => Send(Encoding.Latin1.GetBytes("rm\r"));

    private void Send(byte[] bytes) => _wireSender?.Invoke(bytes);

    private static uint MaskOf(IReadOnlySet<Direction> exits)
    {
        uint m = 0;
        foreach (Direction d in exits)
            if ((int)d >= (int)Direction.N && (int)d <= (int)Direction.D)
                m |= 1u << (int)d;
        return m;
    }

    // ----- test seams ------------------------------------------------
    internal void FireSettleForTests() => OnSettleElapsed();
    internal void FireLookTimeoutForTests() => OnLookTimeout();
    internal void FireParadigmPositionResolvedForTests(RoomKey here) => OnParadigmPositionResolved(here);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkerEvent;
        if (_paradigmResolver is not null)
            _paradigmResolver.PositionResolved -= OnParadigmPositionResolved;
        StopTimers();
    }
}
