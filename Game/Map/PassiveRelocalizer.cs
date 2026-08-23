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
//     zero bytes sent either way. Before confirming, the converged room is
//     still checked against the render that triggered the transition (see
//     OnRoomObserved's cache) — a topology-only projection that disagrees
//     with what's actually on screen refuses rather than asserts.
//   Stage 2 — if replay alone leaves more than one candidate standing, drive
//     a LocatorWalk to walk it out live. Gated behind AllowWalking (Settings
//     -> Other; on by default, since the whole point of this driver is that
//     doing nothing was the reported bug) and, on every single send, behind
//     MovementCoordinator.IsPaused — every other autonomous engine in this
//     codebase (AutoWalkManager, LoopRunner) gates its own sends the same
//     way, and this driver has no upstream engine already enforcing it:
//     marching a mortally wounded, held, confused, or combat-engaged
//     character (or a party follower out of the leader's drag) is a failure
//     mode this driver must never cause.
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

    // Stage 1's own verification cache — the most recent NON-PEEK parsed
    // render, plus whether it's still fresh for the transition about to
    // consume it. RoomDisplayParser fires RoomParsed (reaching OnRoomObserved)
    // BEFORE RoomTracker.NoteRoomObserved runs, and StateChanged fires from
    // inside that dispatch — so when a render's own processing raises a
    // Suspect/Lost transition, this cache genuinely holds the render that
    // triggered it. But not every Suspect/Lost transition is render-driven —
    // RoomTracker.NoteDirectionFailed's EnterSuspect, off a "no exit" refusal
    // reply, carries no render at all — and KeyMatchesObservation is a
    // name+subset-exits match, not an identity check, in a world with
    // genuinely duplicate-signature rooms, so a stale cached render could
    // coincidentally match the wrong candidate. The freshness flag is set
    // true only on a genuine (non-peek) cache write and consumed — read once,
    // then cleared — by the very next StateChanged dispatch, so a later,
    // unrelated transition (no new render in between) always sees it as
    // stale. Net guarantee, Stage 1 only: this can make Stage 1 wrongly
    // REFUSE to locate (the safe direction — staying lost only idles), but it
    // will never let Stage 1 CONFIRM against a render that is stale, peeked,
    // or unrelated to the transition being processed. Stage 2 (OnRoomObserved's
    // walk-pumping tail below) is a DIFFERENT hazard with its own guard — a
    // peek arriving mid-walk abandons the walk outright rather than reading
    // this cache at all; see the comment there.
    private RoomObservation? _lastLiveRender;
    private bool _lastLiveRenderIsFresh;

    // Stage 2 (walking) only runs when true. Read live from Settings -> Other
    // (OtherSettings.WalkToLocateWhenLost) by AppServices.ApplyOtherFromActiveProfile;
    // defaults true here too so a not-yet-loaded profile still gets the fix.
    // Stage 1 (pure replay) always runs regardless — it sends nothing, so
    // there's nothing to opt into.
    public bool AllowWalking { get; set; } = true;

    // Step budget for a Stage-2 walk before it reports Ambiguous rather than
    // keep moving the character. Mirrors RoomLocator.DefaultBudget; pushed
    // live from OtherSettings.LocateWalkStepBudget the same way as AllowWalking.
    public int StepBudget { get; set; } = RoomLocator.DefaultBudget;

    // Last outcome a Stage-2 walk reported, and the working-set size behind
    // it — a bug report's only window into whether a locating walk actually
    // ran and what it found, since neither is otherwise observable state.
    public LocateOutcome? LastOutcome { get; private set; }
    public bool IsWalkActive => _walk is { IsActive: true };
    public int CandidateCount => _matcher.Candidates.Count;

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
    // EngineRecoveryGate.OnRoomObserved rides.
    public void OnRoomObserved(RoomObservation obs)
    {
        // Never cache a peek's own preview as verification ground truth —
        // IsPeekSuppressed() is non-consuming (RoomTracker's own contract),
        // so it's safe to check on every call.
        if (!_tracker.IsPeekSuppressed())
        {
            _lastLiveRender = obs;
            _lastLiveRenderIsFresh = true;
        }

        if (_walk is not { IsActive: true } walk) return;

        // An engine can attach between this walk's sends (Attach itself never
        // touches us — only OnTrackerStateChanged's own entry guard checks
        // AttachedEngine, and that only runs at walk START). Re-check here,
        // before folding this landing in: feeding it to the walk would both
        // send a second, uncoordinated move AND corrupt the shared matcher
        // with a landing that may belong to the OTHER engine's own step.
        if (_gate.AttachedEngine is not null)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                "an engine attached mid-walk — abandoning the locating walk rather than fighting it.");
            _walk = null;
            return;
        }

        // A peek here is a different hazard from the Stage-1 cache above: this
        // is a PUMP, not a seed or a verification. Silently dropping the render
        // (the seed's fix) would leave the walk waiting forever if this peek
        // actually WAS the genuine landing arriving in an armed-but-unresolved
        // peek window (the move-then-look race) — OnLanding would never fire
        // again and IsActive would stay true with nothing left to pump it.
        // Feeding it through unfiltered (no guard at all) risks the opposite:
        // FootprintMatcher.Step narrows on a room the player was never in, or
        // consumes the landing slot so the REAL landing moments later is
        // dropped with _active already false. Abandoning the walk is the only
        // option that can't hang, can't assert a false position, and can't
        // misattribute a landing — same shape as the attached-engine case
        // above. The user can retry; a later transition re-enters Stage 1.
        if (_tracker.IsPeekSuppressed())
        {
            _log?.Log(LogSeverity.Info, LogSource,
                "a peek arrived while the locating walk was active — abandoning it rather than risk a false landing.");
            _walk = null;
            return;
        }

        LocateOutcome? outcome = walk.OnLanding(obs);
        if (outcome is { } result) HandleOutcome(result);
    }

    public void Dispose() => _tracker.StateChanged -= OnTrackerStateChanged;

    private void OnTrackerStateChanged(RoomTransition t)
    {
        // Consumed unconditionally, for EVERY transition, before any of the
        // guards below — a render belongs to at most the one StateChanged
        // dispatch its own NoteRoomObserved call raises (Confirmed included;
        // most renders never touch Suspect/Lost at all). If it also precedes
        // a LATER, unrelated transition — NoteDirectionFailed's EnterSuspect
        // carries no render of its own — that later one must see it as
        // stale, not fresh, so it's read once here regardless of what the
        // rest of this method does with it.
        bool renderIsFreshForThisTransition = _lastLiveRenderIsFresh;
        _lastLiveRenderIsFresh = false;

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

        IReadOnlyList<RoomKey> seeded = _locator.Seed(obs);
        _log?.Log(LogSeverity.Info, LogSource,
            $"went {t.NewConfidence} with no engine attached — seeded {seeded.Count} candidate(s) from '{obs.Name}'.");
        _matcher.Reset(seeded);
        ReplayRecentSteps();

        if (_matcher.IsConverged)
        {
            RoomKey found = _matcher.Candidates.Single();

            // StepBlind is pure topology — it never checked the projected
            // endpoint against anything actually on screen. Require that now,
            // against the render that triggered this very transition: a wrong
            // Confirmed can drive real movement from a false position, and
            // clears RecentSteps in the process, destroying the evidence that
            // produced the mistake. Staying lost only idles.
            if (!renderIsFreshForThisTransition)
            {
                _log?.Log(LogSeverity.Info, LogSource,
                    $"footstep replay converged on {found} but no render accompanied this " +
                    "transition (a direction-failed reply, most likely) — refusing to locate; staying lost.");
                return;
            }

            if (_lastLiveRender is not { } live || !KeyMatchesObservation(found, live))
            {
                _log?.Log(LogSeverity.Info, LogSource,
                    $"footstep replay converged on {found} but the room on screen is " +
                    (_lastLiveRender is { } l ? $"'{l.Name}'" : "unknown") +
                    " — refusing to locate; staying lost.");
                return;
            }

            _log?.Log(LogSeverity.Info, LogSource,
                $"footstep replay converged -> {found}, confirmed by the room on screen, zero bytes sent.");
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

        _log?.Log(LogSeverity.Info, LogSource,
            $"beginning a locating walk over {candidates.Count} candidate(s), budget={StepBudget}.");
        RoomObservation syntheticHere = SyntheticExitUnion(candidates);
        _walk = new LocatorWalk(_locator, _matcher, Send, StepBudget);
        LocateOutcome? outcome = _walk.BeginFrom(syntheticHere, candidates);
        if (outcome is { } result) HandleOutcome(result);
    }

    // The one send choke-point for Stage 2. Checked here, not at BeginWalk,
    // so there's a single authority to reason about: any gate can assert
    // mid-walk (combat engaging, a knockdown, the party role flipping, ...),
    // and this driver has no attached engine upstream already enforcing
    // IsPaused for it the way AutoWalkManager's own SendNextStep does.
    // Abandoning rather than skipping the send matters because LocatorWalk is
    // a pump: a silently skipped send leaves IsActive true with nothing left
    // to advance it, stalling the walk forever instead of surfacing as an
    // outcome.
    private void Send(Direction direction)
    {
        if (_coordinator.IsPaused)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"movement gate(s) asserted ({string.Join(", ", _coordinator.AssertedGates)}) — abandoning the locating walk rather than move through it.");
            _walk = null;
            return;
        }
        _wireSender?.Invoke(AutoWalkManager.EncodeMove(direction));
    }

    private void HandleOutcome(LocateOutcome outcome)
    {
        _walk = null;
        LastOutcome = outcome;
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
