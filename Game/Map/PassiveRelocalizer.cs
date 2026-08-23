using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Recovers a room fix when the tracker goes Suspect/Lost with NO engine
// attached — the shape a dragged party follower ends up in, since
// AutoWalkManager and LoopRunner both refuse to attach when CurrentRoom is
// null (RoomTracker's Lost path sets it to null). Without a driver in that
// state the client just sits there waiting for the user to click the map —
// the reported "when lost, it sits there and does nothing" bug.
//
// Two tiers, cheapest first:
//   Stage 1 — replay RecentSteps (which already carries follow-drags; see
//     RoomTracker.NoteFollowMove) as blind topological hops through a
//     FootprintMatcher seeded from the last accepted observation. No
//     per-step render is available for a step already taken, so this narrows
//     by graph structure alone (StepBlind), never by matching a display —
//     zero bytes sent either way.
//   Stage 2 — if replay alone leaves more than one candidate standing, drive
//     a LocatorWalk to walk it out live. Gated behind AllowWalking (off by
//     default — the settings surface lands in a later task) and, on every
//     single send, behind MovementCoordinator's follower gate: marching a
//     party follower out of the leader's drag is the one failure mode this
//     driver must never cause.
public sealed class PassiveRelocalizer : IDisposable
{
    private const string LogSource = "PassiveRelocalizer";

    private readonly RoomTracker _tracker;
    private readonly RoomLocator _locator;
    private readonly RoomGraphManager _graph;
    private readonly EngineRecoveryGate _gate;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly FootprintMatcher _matcher;

    private Action<byte[]>? _wireSender;
    private LocatorWalk? _walk;

    // Stage 2 (walking) only runs when true. Off by default until the
    // settings surface that drives it lands. Stage 1 (pure replay) always
    // runs regardless — it sends nothing, so there's nothing to opt into.
    public bool AllowWalking { get; set; }

    public PassiveRelocalizer(
        RoomTracker tracker,
        RoomLocator locator,
        RoomGraphManager graph,
        EngineRecoveryGate gate,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _locator = locator;
        _graph = graph;
        _gate = gate;
        _coordinator = coordinator;
        _log = log;
        _matcher = new FootprintMatcher(ProbeHop, KeyMatchesObservation, log);
        _tracker.StateChanged += OnTrackerStateChanged;
    }

    // Wire sender for Stage 2 — bound per-session once the telnet client is
    // up, the same EngineSendGate-wrapped sender RecoveryLookSweep and every
    // other engine ride (MainWindowViewModel's engineSend).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Fed every parsed room display (RoomDisplayParser.RoomParsed), same feed
    // EngineRecoveryGate.OnRoomObserved rides. Only meaningful while a Stage
    // 2 walk has a move outstanding; otherwise a no-op.
    public void OnRoomObserved(RoomObservation obs)
    {
        if (_walk is not { IsActive: true } walk) return;
        LocateOutcome? outcome = walk.OnLanding(obs);
        if (outcome is { } result) HandleOutcome(result);
    }

    public void Dispose() => _tracker.StateChanged -= OnTrackerStateChanged;

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (_gate.AttachedEngine is not null) return;   // never fight an attached engine
        if (t.NewConfidence is not (RoomConfidence.Suspect or RoomConfidence.Lost)) return;
        if (_walk is { IsActive: true }) return;         // already mid-walk from an earlier escalation

        // CurrentRoom is exactly what's null in the Lost case this driver
        // exists for — LastAcceptedObservation is the last render the
        // tracker accepted as ours regardless.
        if (_tracker.LastAcceptedObservation is not { } obs)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                "no cached observation to relocalize from — staying put.");
            return;
        }

        _matcher.Reset(_locator.Seed(obs));
        ReplayRecentSteps();

        if (_matcher.IsConverged)
        {
            RoomKey found = _matcher.Candidates.Single();
            _log?.Log(LogSeverity.Info, LogSource,
                $"footstep replay converged -> {found} with zero bytes sent.");
            _tracker.SetLocated(found);
            return;
        }

        if (_matcher.Candidates.Count == 0)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                "footstep replay exhausted every candidate against the graph; nothing more to try without walking.");
            return;
        }

        if (!AllowWalking)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"footstep replay narrowed to {_matcher.Candidates.Count} candidate(s); walking is off, staying put.");
            return;
        }

        BeginWalk(new List<RoomKey>(_matcher.Candidates));
    }

    // Pure topological dead-reckoning: hop every surviving candidate through
    // each recorded step with nothing to check the landing against
    // (StepBlind, not Step) — a follow-drag leaves no per-step render behind,
    // only the fact that the move happened (see RoomTracker.NoteFollowMove).
    // A step with no cardinal (an arbitrary text exit) can't be blind-hopped
    // through a Direction-only probe, so replay stops there rather than
    // guess which of several candidates' text exits it might have taken.
    private void ReplayRecentSteps()
    {
        foreach (DirectionDto step in _tracker.RecentSteps)
        {
            if (_matcher.Candidates.Count <= 1) return;
            if (step.Cardinal is not { } dir) return;
            _matcher.StepBlind(dir);
        }
    }

    private void BeginWalk(IReadOnlyCollection<RoomKey> candidates)
    {
        if (_wireSender is null)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                "no wire sender bound yet — can't walk a locate (headless / not connected).");
            return;
        }

        RoomObservation syntheticHere = SyntheticExitUnion(candidates);
        _walk = new LocatorWalk(_locator, _matcher, Send);
        LocateOutcome? outcome = _walk.BeginFrom(syntheticHere, candidates);
        if (outcome is { } result) HandleOutcome(result);
    }

    // The one send choke-point for Stage 2. Checked here, not at BeginWalk,
    // so there's a single authority to reason about: the party role can flip
    // mid-walk, and marching a follower out of the leader's drag is the one
    // failure mode this driver must never cause.
    private void Send(Direction direction)
    {
        if (_coordinator.IsGateAsserted(MovementCoordinator.FollowerGate))
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                "follower gate asserted — abandoning the locating walk rather than fighting the leader's drag.");
            _walk = null;
            return;
        }
        _wireSender?.Invoke(AutoWalkManager.EncodeMove(direction));
    }

    private void HandleOutcome(LocateOutcome outcome)
    {
        _walk = null;
        if (outcome.Kind == LocateOutcomeKind.Converged)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"locating walk converged -> {outcome.Room} after {outcome.Steps} step(s).");
            _tracker.SetLocated(outcome.Room);
            return;
        }
        _log?.Log(LogSeverity.Warn, LogSource,
            $"locating walk gave up after {outcome.Steps} step(s): {outcome.Kind}.");
    }

    // LocatorWalk.ChooseSplittingExit only needs to know which directions are
    // worth trying — it already drops any direction a given candidate can't
    // actually take (RoomLocator.ChooseSplittingExit's per-candidate
    // usability check) — so a live render of "here" isn't required to start
    // walking: the union of every surviving candidate's own graph exits is a
    // safe stand-in. Name is unused by ChooseSplittingExit.
    private RoomObservation SyntheticExitUnion(IReadOnlyCollection<RoomKey> candidates)
    {
        var exits = new HashSet<Direction>();
        foreach (RoomKey key in candidates)
        {
            if (_graph.GetRoom(key) is not { } room) continue;
            foreach (Direction d in room.Exits.Keys) exits.Add(d);
        }
        return new RoomObservation(string.Empty, exits);
    }

    // Same shape EngineRecoveryGate wires its own FootprintMatcher with —
    // duplicated rather than shared because both are private per-instance
    // closures over each type's own RoomGraphManager reference.
    private HopOutcome ProbeHop(RoomKey from, Direction dir)
    {
        Room? source = _graph.GetRoom(from);
        if (source is null) return HopOutcome.NoExit();
        if (!source.Exits.TryGetValue(dir, out RoomExit exit)) return HopOutcome.NoExit();
        if (exit.Hint == RoomExitHint.Trap) return HopOutcome.TrappedExit();
        return HopOutcome.Reached(exit.Target);
    }

    private bool KeyMatchesObservation(RoomKey key, RoomObservation obs)
    {
        Room? r = _graph.GetRoom(key);
        if (r is null) return false;
        if (!string.Equals(r.Name, obs.Name, StringComparison.OrdinalIgnoreCase)) return false;
        uint observedMask = 0;
        foreach (Direction d in obs.Exits) observedMask |= 1u << (int)d;
        return (observedMask & r.ExitMask) == observedMask;
    }
}
