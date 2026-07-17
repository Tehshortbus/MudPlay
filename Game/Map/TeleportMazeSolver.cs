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

    private enum Phase { Idle, RoutingToEntrance, Settling, Looking, Delegated }

    private readonly TeleportMazeIndex _index;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly AutoWalkManager _walker;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;

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

    // ----- bug-report surface ----------------------------------------
    public bool Active { get; private set; }
    public RoomKey? Goal => Active ? _goal : (RoomKey?)null;
    public int Attempts => _attempts;
    public string PhaseName => _phase.ToString();

    // Stock-only: Paradigm has `rm` (ParadigmPositionResolver hard-locates from
    // the game's own answer), so it never needs the look-sweep relocalization.
    public bool Enabled => _gameData.ActiveRealm != RealmType.ParaMud;

    public TeleportMazeSolver(
        TeleportMazeIndex index,
        RoomTracker tracker,
        BfsMapper bfs,
        AutoWalkManager walker,
        GameDataCache gameData,
        LogService? log = null)
        : this(index, tracker, bfs, walker, gameData, log, useTimer: true, post: null) { }

    internal TeleportMazeSolver(
        TeleportMazeIndex index,
        RoomTracker tracker,
        BfsMapper bfs,
        AutoWalkManager walker,
        GameDataCache gameData,
        LogService? log,
        bool useTimer,
        Action<Action>? post)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(gameData);

        _index = index;
        _tracker = tracker;
        _bfs = bfs;
        _walker = walker;
        _gameData = gameData;
        _log = log;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));

        _walker.Event += OnWalkerEvent;

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
                // A fresh display inside the settle window — a teleport can echo
                // two, so keep the last and restart the timer.
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

        // Lost / Unknown / Suspect — the teleport collapsed the tracker. Identify
        // the landing from the last room display we saw.
        BeginRelocalize();
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
        _phase = Phase.Settling;
        SendMove(_entranceDir);
        RestartSettle();
    }

    private void ContinueFromLocated(RoomKey here)
    {
        if (here.Equals(_goal))
        {
            DelegateFinalWalk(here);   // walker raises Finished ("already at destination")
            return;
        }

        IReadOnlyList<Direction>? plain = _bfs.FindPath(here, _goal);
        if (plain is { Count: > 0 })
            DelegateFinalWalk(here);
        else
            Reshuffle(here);
    }

    private void DelegateFinalWalk(RoomKey here)
    {
        _phase = Phase.Delegated;
        _log?.Log(LogSeverity.Info, LogSource,
            $"located at {here}; delegating final walk to {_goal}");
        // Active stays true so the walker's re-entry hook (CanSolve → !Active)
        // won't re-trigger us if this delegated walk itself hits a no-path.
        _walker.WalkTo(_goal);
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

        Direction d = dirs[0];
        _log?.Log(LogSeverity.Info, LogSource,
            $"reshuffle #{_attempts}: walking {d.ToLongName()} from {here} to re-teleport");
        _phase = Phase.Settling;
        SendMove(d);
        RestartSettle();
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
            _phase = Phase.Settling;
            SendMove((Direction)d);
            RestartSettle();
            return;
        }

        FailSolve("blind reshuffle: landing room has no exit to walk");
    }

    // ----- walker delegation callbacks -------------------------------

    private void OnWalkerEvent(WalkEvent e)
    {
        if (!Active) return;

        switch (_phase)
        {
            case Phase.RoutingToEntrance:
                if (e.Kind == WalkEventKind.Finished) CrossEntrance();
                else if (e.Kind == WalkEventKind.Failed) FailSolve($"could not reach maze entrance: {e.Detail}");
                else if (e.Kind == WalkEventKind.Stopped) Abandon("route to entrance superseded");
                break;

            case Phase.Delegated:
                if (e.Kind == WalkEventKind.Finished) Finish();
                else if (e.Kind == WalkEventKind.Failed) OnDelegatedWalkFailed(e.Detail);
                else if (e.Kind == WalkEventKind.Stopped) Abandon("final walk superseded");
                break;
        }
    }

    private void OnDelegatedWalkFailed(string detail)
    {
        // A delegated walk fails either because we drifted into a component with
        // no plain route to the goal (reshuffle to jump components) or because a
        // gate/blocker stopped an otherwise-plain route (surface, don't loop).
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is { } cur
            && _index.IsMazeRoom(cur.Key)
            && _bfs.FindPath(cur.Key, _goal) is not { Count: > 0 })
        {
            Reshuffle(cur.Key);
        }
        else
        {
            FailSolve($"delegated walk failed: {detail}");
        }
    }

    // ----- terminal transitions --------------------------------------

    private void Finish()
    {
        _log?.Log(LogSeverity.Info, LogSource, $"maze solve complete → {_goal}");
        StopTimers();
        _phase = Phase.Idle;
        Active = false;   // the walker already raised Finished for the UI
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
        if (!Active || _phase != Phase.Settling) return;
        BeginRelocalize();
    }

    private void RestartLookTimeout()
    {
        _lookTimeout?.Stop();
        _lookTimeout?.Start();
    }

    private void OnLookTimeout()
    {
        _lookTimeout?.Stop();
        if (!Active || _phase != Phase.Looking) return;
        FailSolve($"look {_currentLookDir.ToLongName()} did not render a room");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkerEvent;
        StopTimers();
    }
}
