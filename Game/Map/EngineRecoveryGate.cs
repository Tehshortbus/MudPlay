using MudPlay.Services;

namespace MudPlay.Game.Map;

// Shared tier-1/2/3 location-recovery service for the walker, loop runner,
// and auto-lair scheduler. Owns the strict-1-of-1 anchor, the rolling
// executed-steps history since that anchor, and the tier-2/3 escalation
// logic. Engines plug in via IRecoverableEngine; the gate calls back into
// them for backtrack sends + pause / resume / abort.
//
// Singleton in AppServices; only one engine is attached at a time (which
// matches reality — the wire serialises movement). Engines call Attach on
// Start and Detach on Stop / Reset.
//
// Tier definitions:
//   Tier 1 — engine executing planned path; anchor refreshes every time
//     the tracker lands at a true 1-of-1 graph match.
//   Tier 2 — observation came in that didn't match the engine's expected
//     next room (or was a re-display); engine keeps executing the planned
//     path while the gate watches for a 1-of-1 recovery in ≤
//     Tier2StepBudget further moves. Escalates to tier 3 either at the step
//     ceiling OR when the engine's PeekNextPlannedDirection isn't available
//     on the current observed room.
//   Tier 3 — gate takes over: pauses the engine, sends reverse-of-executed
//     moves one at a time, accumulates a (direction, observation)
//     footprint, and uses FootprintMatcher to narrow seeds from the
//     current observation's FindCandidates. Converges to 1 → engine
//     resumes from the recovered anchor; exhausted to 0 OR all executed
//     steps reversed without convergence → engine aborted and
//     RecoveryFailed fires (caller pops the "Lost — use the map to set
//     location" info dialog).
public sealed class EngineRecoveryGate
{
    private const string LogSource = "RecoveryGate";

    // Tier-2 step ceiling: 15 moves from the anchor.
    public const int Tier2StepBudget = 15;

    // FootprintMatcher depth ceiling for tier-3 backtrack. We never
    // backtrack further than the executed-steps history, but this keeps the
    // matcher honest if a future change ever pumped extra steps in.
    private const int Tier3DepthCeiling = 64;

    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    private IRecoverableEngine? _engine;
    private RoomKey? _anchor;
    private readonly List<Direction> _executedSinceAnchor = new();
    private readonly FootprintMatcher _tier3;

    // Tier-3 orchestration is a per-room phase machine. Idle when not
    // recovering. AwaitingLanding: a reverse move is in flight; the next lit
    // render (via OnRoomObserved) or dark-advance (via the tracker) is its
    // landing. WaitingCombatClear: a hostile is in the recovery room — hold
    // until it clears (lit) / until the next combat tick (dark). Sweeping:
    // peeking the landing room's exits to fingerprint it.
    private enum Tier3Phase { Idle, AwaitingLanding, WaitingCombatClear, Sweeping }
    private Tier3Phase _tier3Phase = Tier3Phase.Idle;
    private bool Tier3Active => _tier3Phase != Tier3Phase.Idle;

    // Reverse direction of the most recent backtrack move — the temporal
    // constraint fed to FootprintMatcher.Step / StepBlind on its landing.
    private Direction _lastBacktrackReverse;

    // What to run once the room clears (WaitingCombatClear resume).
    private Action? _combatClearResume;

    // Move-free look-sweep for spatial fingerprinting. Created when the wire
    // sender binds (per-session, on the UI thread). Null in headless tests /
    // before connect → recovery falls back to the temporal-only footprint.
    private RecoveryLookSweep? _sweep;

    // Combat-clear gate: true while a hostile the combat engine will
    // auto-engage is in the room. Null → no combat gating (tests / no combat
    // tracker) → recovery never waits.
    private Func<bool>? _hostilesPresent;

    // Set while we've paused the engine waiting on an authoritative `rm`
    // position reply (Paradigm only). NoteAuthoritativePosition clears it on a
    // successful re-anchor; OnAuthoritativeResyncFailed clears it and falls back
    // to the heuristic backtrack. Nothing advances the tiers while it's set.
    private bool _awaitingAuthoritative;

    // One ground-truth locate per escalation. Its failure path re-enters
    // EscalateToTier3, so without this the gate would ask, fail, and ask again.
    // Cleared whenever we settle back at tier 1.
    private bool _sysopLocateTried;

    // Which give-up boundary a pending forced `rm` is guarding, so its failure
    // callback (OnAuthoritativeResyncFailed) knows where to fall through to. On
    // Paradigm every path toward the heuristic backtrack (EscalateToTier3) and
    // toward the "Lost" dialog (FailTier3) first spends a forced `rm` — the game
    // can always report our true room — and only proceeds if that `rm` also
    // fails. BeforeBacktrack failure → run the backtrack; Terminal failure →
    // declare Lost for real. None means no gate-driven resync is in flight (a
    // NoteSuspectedMismatch resync, which predates this, also falls through to
    // the backtrack). Issue: a Paradigm client went "Lost — footprint exhausted"
    // when `rm` could have relocated it (report paradigm-20260902-223159).
    private enum ResyncStage { None, BeforeBacktrack, Terminal }
    private ResyncStage _resyncStage;
    private string? _pendingLostDetail;   // the FailTier3 detail to surface if a terminal `rm` also fails

    // A forced `rm` that goes unanswered on Paradigm means one thing: a confusion
    // fumble ate the command (the only way `rm` fails to reply — confirmed
    // mechanic). The position is fine, so we re-ask until the confusion passes
    // (self-clearing) instead of dropping to a heuristic guess / "Lost". Capped
    // only as a stuck-flag backstop — real confusion never lasts anywhere near
    // this long at the resolver's ~3s reply timeout (~2 min here).
    private int _confusedResyncRetries;
    private const int MaxConfusedResyncRetries = 40;

    // Optional Paradigm re-sync hook. When set, NoteSuspectedMismatch asks it to
    // fire `rm` before climbing the heuristic ladder; a true return means a
    // request is in flight and we should pause + wait. Null (or a false return)
    // keeps the pure heuristic path — the only behaviour on stock realms.
    public Func<string, bool>? TryResync { get; set; }

    // Optional Paradigm forced re-sync hook — same as TryResync but skips the
    // resolver's anti-storm throttle (it still coalesces on an in-flight `rm`).
    // Used at the two give-up boundaries (EscalateToTier3 before the heuristic
    // backtrack, FailTier3 before the "Lost" dialog): a client about to fail out
    // would rather spend one `rm` than drop to Lost, even if a resync just fired
    // inside the throttle window. Null / false return keeps the heuristic path.
    public Func<string, bool>? TryResyncForced { get; set; }

    // Optional Confused check (Conditions.IsConfused), same source the walker /
    // loop-runner use. A forced `rm` only fails to answer when a confusion fumble
    // eats it, so a timed-out `rm` while this reports true is re-asked rather than
    // treated as a real failure. Null → no confusion awareness (a failed `rm`
    // always falls through), which is the correct stock behaviour anyway.
    public Func<bool>? IsConfused { get; set; }

    // Optional Paradigm one-shot re-fix hook (ParadigmPositionResolver.RequestResyncOnce).
    // Unlike TryResync, this bypasses the gate's own tier/awaiting bookkeeping
    // entirely — for a caller (LoopRunner's bounded "blocked at source" local
    // recovery, AutoWalkManager's replan-on-desync) that already owns its own
    // pause/retry loop and just wants a direct "ask rm, get the authoritative
    // room (or a failure signal)" answer before trusting a tracker belief that
    // LOOKS Confirmed but came from a name-ambiguous zone's fragile name+exit
    // match. True means a request was sent (or is already in flight) and
    // exactly one of the two callbacks will fire; false (stock realm, no wire,
    // throttled) means the caller falls back to its existing heuristic path.
    public Func<string, Action<RoomKey>, Action, bool>? TryResyncOnce { get; set; }

    // Optional ground-truth locator, consulted at the moment the gate would
    // otherwise start reversing moves. Where the character has sysop powers on
    // the BBS, `sys st` prints the room's true map/room number, and that
    // strictly beats reverse-walking a footprint until one candidate survives.
    // A true return means a request is in flight and the gate pauses for it
    // exactly as it does for a Paradigm `rm`; null (or a false return) leaves
    // the heuristic ladder untouched.
    public Func<string, bool>? TrySysopLocate { get; set; }

    // Currently active engine, or null when nothing is attached.
    public IRecoverableEngine? AttachedEngine => _engine;

    // Live tier the gate is in. Always 1 when no engine attached.
    public TierLevel CurrentTier { get; private set; } = TierLevel.Tier1;

    // Most recent strict-1-of-1 anchor while attached. Null until first 1-of-1.
    public RoomKey? Anchor => _anchor;

    // Read-only view of the executed-steps history since the current anchor.
    public IReadOnlyList<Direction> ExecutedSinceAnchor => _executedSinceAnchor;

    // True while the engine is paused waiting on a Paradigm `rm` reply.
    public bool AwaitingAuthoritativeResync => _awaitingAuthoritative;

    // Fires whenever CurrentTier changes. Payload carries previous/new + a reason.
    public event Action<RecoveryTierChangedEvent>? TierChanged;

    // Fires when tier-3 recovery converges. Payload is the recovered anchor.
    public event Action<RoomKey>? Recovered;

    // Fires when tier-3 recovery fails terminally. Caller surfaces the modeless info dialog.
    public event Action<RecoveryFailedEvent>? RecoveryFailed;

    public EngineRecoveryGate(RoomGraphManager graph, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tracker);
        _graph = graph;
        _tracker = tracker;
        _log = log;
        _tracker.StateChanged += OnTrackerStateChanged;

        _tier3 = new FootprintMatcher(
            probeHop: ProbeHop,
            matchesObservation: KeyMatchesObservation,
            log: log,
            depthCeiling: Tier3DepthCeiling);
    }

    // ----- external feeds (bound per-session by the main-window VM) ---

    // Bind the wire sender used by the tier-3 look-sweep. Creates the sweep on
    // first bind (per-session, on the UI thread where its timer is valid).
    // Mirrors every other engine's SetWireSender.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sweep ??= new RecoveryLookSweep(_log);
        _sweep.SetWireSender(sender);
    }

    // Fed every parsed room display (RoomDisplayParser.RoomParsed, which fires
    // BEFORE the tracker's peek-suppression). Routed by tier-3 phase: a landing
    // render advances the footprint; a render during a sweep is a peeked
    // neighbour handed to the sweep.
    public void OnRoomObserved(RoomObservation obs)
    {
        switch (_tier3Phase)
        {
            case Tier3Phase.AwaitingLanding:
                HandleLitLanding(obs);
                break;
            case Tier3Phase.Sweeping:
                _sweep?.OnRoomObserved(obs);
                break;
        }
    }

    // Supply the combat-clear predicate: true while a hostile the combat engine
    // will auto-engage is present. Recovery holds its look-sweep / next reverse
    // move until it clears. Left unset leaves recovery ungated (tests).
    public void SetCombatGate(Func<bool> hostilesPresent)
    {
        ArgumentNullException.ThrowIfNull(hostilesPresent);
        _hostilesPresent = hostilesPresent;
    }

    // Wired to TickEngine.CombatTickElapsed. Drives the combat-clear wait: once
    // the recovery room is clear of hostiles, resume the held sweep / step.
    public void OnCombatTick()
    {
        if (_tier3Phase != Tier3Phase.WaitingCombatClear) return;
        if (_hostilesPresent?.Invoke() == true) return;   // still fighting — keep holding
        ResumeAfterCombatClear();
    }

    // Test seam: inject a timer-free look-sweep so the sweep path runs headless.
    internal void SetLookSweepForTests(RecoveryLookSweep sweep) => _sweep = sweep;

    // ----- attach / detach -------------------------------------------

    // Bind an engine to the gate. Seeds Anchor from the tracker's current
    // room if it's a true 1-of-1 match, else from the persisted
    // CharacterProfile.LastKnownRoom if hydrated. Replaces any
    // currently-attached engine.
    public void Attach(IRecoverableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_engine is not null)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"Attach: replacing engine={_engine.Name} with {engine.Name}");
            Detach();
        }

        _engine = engine;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        ResetTier3Orchestration();
        _awaitingAuthoritative = false;
        _sysopLocateTried = false;
        ResetResyncStage();
        CurrentTier = TierLevel.Tier1;

        Room? here = _tracker.State.CurrentRoom;
        _anchor = here?.Key;

        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier1.attach engine={engine.Name} anchor={(_anchor?.ToString() ?? "(none)")} confidence={_tracker.State.Confidence}");
    }

    // Detach the current engine; clears all gate state.
    public void Detach()
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Info, LogSource, $"detach engine={_engine.Name}");
        _engine = null;
        _anchor = null;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        ResetTier3Orchestration();
        _awaitingAuthoritative = false;
        ResetResyncStage();
        SetTier(TierLevel.Tier1, "detach");
    }

    // The engine just sent a planned move. The gate appends it to the
    // executed-steps history (used as the reverse-walk source if tier 3 is
    // later triggered).
    //
    // A teleport hop is a hard position break: you can't reverse-walk back
    // through a `go hole` / cast-teleport, and the cardinal history taken
    // before it now starts from an unrelated room, so reversing it would send
    // the character somewhere bogus (and Direction.Teleport isn't even
    // encodable as a move). Drop the whole pre-teleport history instead of
    // recording the hop — the gate then rebuilds a fresh reverse-walk source
    // from steps taken after the teleport, and refreshes its anchor on the
    // next 1-of-1 landing.
    public void NoteEngineStepSent(Direction direction)
    {
        if (_engine is null) return;
        if (direction == Direction.Teleport)
        {
            _executedSinceAnchor.Clear();
            return;
        }
        _executedSinceAnchor.Add(direction);
    }

    // ----- tracker subscription --------------------------------------

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (_engine is null) return;

        // Dark backtrack landing: a dark room never renders, so OnRoomObserved
        // never fires — the tracker's dark-advance is our only "the move
        // executed" signal. Dead-reckon the footprint by the reverse move
        // alone. Non-circular: we use the FACT the move landed, not the
        // tracker's predicted identity (which came from the same graph).
        if (_tier3Phase == Tier3Phase.AwaitingLanding
            && _tracker.IsInDarkRoom
            && t.NewRoom is not null)
        {
            HandleDarkLanding();
            return;
        }

        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is not { } room) return;

        // True 1-of-1 anchor refresh — independent of tier.
        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(room.Name, ExitMaskToSet(room.ExitMask));
        bool isStrict = candidates.Count == 1;
        if (isStrict)
        {
            RoomKey? prevAnchor = _anchor;
            _anchor = room.Key;
            _executedSinceAnchor.Clear();
            _log?.Log(LogSeverity.Info, LogSource,
                $"Tier1.anchor-refresh → {room.Key} ({room.Name})"
                + (prevAnchor is { } pa && !pa.Equals(room.Key) ? $" (was {pa})" : string.Empty));

            if (Tier3Active)
            {
                // Tier 3 backtrack just hit a clean 1-of-1 — authoritative
                // recovery, independent of the footprint. Cancel any in-flight
                // sweep and finish here.
                FinishTier3Success(room.Key);
                return;
            }

            if (CurrentTier != TierLevel.Tier1) SetTier(TierLevel.Tier1, "1-of-1 anchor recovered");
            return;
        }

        // Non-strict observation — multiple graph rooms match by
        // name+exits. Useful for diagnosing recovery-tier escalations
        // ("why didn't this refresh the anchor?"). Debug level so
        // session logs aren't drowned during normal walks through
        // ambiguous areas.
        _log?.Log(LogSeverity.Debug, LogSource,
            $"non-strict observation '{room.Name}' → {candidates.Count} graph candidate(s); anchor unchanged ({(_anchor?.ToString() ?? "(none)")})");

        // A tier-3 lit landing is fingerprinted through OnRoomObserved (the raw
        // pre-suppression render), not here — the tracker's matched graph room
        // hides live-hidden exits behind a subset match. This non-strict branch
        // stays a pure diagnostic during recovery.
    }

    private static IReadOnlySet<Direction> ExitMaskToSet(uint mask)
    {
        var set = new HashSet<Direction>();
        for (int i = 0; i < 10; i++) if (((mask >> i) & 1u) != 0) set.Add((Direction)i);
        return set;
    }

    // ----- tier-2 + tier-3 triggers ----------------------------------

    // The engine's per-step reconcile logic noticed the observation didn't
    // match its expected next room (OR looked like a re-display). Caller is
    // responsible for deciding the SHAPE of the mismatch — the gate just
    // escalates to tier 2 and starts watching for either recovery or
    // further escalation.
    public void NoteSuspectedMismatch(string reason)
    {
        if (_engine is null) return;
        // Always log the mismatch signal so a "why did the gate fire?"
        // question is answerable from the log alone — SetTier only logs
        // when the tier actually changes, so without this line a
        // mismatch arriving while already in Tier2 would be invisible.
        _log?.Log(LogSeverity.Info, LogSource,
            $"NoteSuspectedMismatch (tier={CurrentTier}): {reason}");

        if (TryParadigmResync(reason)) return;

        if (CurrentTier == TierLevel.Tier1)
            SetTier(TierLevel.Tier2, $"mismatch: {reason}");

        // Tier-2 budget check immediately — if the engine is already
        // past 15 steps with no anchor refresh, we're effectively in
        // tier-3 territory.
        if (_executedSinceAnchor.Count >= Tier2StepBudget)
        {
            EscalateToTier3($"tier-2 budget exceeded ({_executedSinceAnchor.Count} steps without 1-of-1)");
        }
    }

    // The engine can't take another step: a move went out, never confirmed, and
    // its stall watchdog gave up waiting. This is NOT a suspected mismatch.
    // Tier 2's contract is "the engine keeps executing the planned path while we
    // watch for a 1-of-1 in the next few moves" — a wedged engine executes
    // nothing, so tier 2 can never resolve, never reaches its step budget, and
    // the caller's watchdog has already stopped itself. Landing here in tier 2
    // is a permanent hang (a stock-realm loop stuck Pending in a same-named
    // room), so a stall goes straight to the ladder that can act on a stationary
    // engine: the Paradigm `rm` if there is one, otherwise tier 3 — ground truth
    // if available, then the reverse-walk, then a clean Lost dialog. Any of
    // those ends the wait; tier 2 doesn't.
    public void NoteEngineStalled(string reason)
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Warn, LogSource,
            $"NoteEngineStalled (tier={CurrentTier}): {reason}");

        if (TryParadigmResync(reason)) return;
        EscalateToTier3($"engine stalled: {reason}");
    }

    // Paradigm fast-path: ask the game for our authoritative position via `rm`
    // before climbing the heuristic ladder. Pauses the engine while the reply
    // round-trips so it reports a stationary room; NoteAuthoritativePosition
    // resumes us from that anchor, and OnAuthoritativeResyncFailed falls back on
    // a miss. Returns false on stock realms / when throttled / when already
    // awaiting, leaving the caller's heuristic escalation untouched.
    private bool TryParadigmResync(string reason)
    {
        if (_engine is null) return false;
        if (_awaitingAuthoritative) return false;
        if (TryResync?.Invoke(reason) != true) return false;

        _awaitingAuthoritative = true;
        _engine.PauseForRecovery($"awaiting rm resync: {reason}");
        _log?.Log(LogSeverity.Info, LogSource,
            $"resync-wait: paused {_engine.Name}, awaiting authoritative rm position (tier={CurrentTier})");
        return true;
    }

    // An authoritative locator — the Paradigm resolver's `rm` Location: reply,
    // or a sysop `sys st` room dump — hard-located the tracker to `key`.
    // Re-anchor the gate there by id (bypassing the 1-of-1 name-uniqueness
    // wrinkle that a normal observation needs), drop to Tier1, and resume the
    // engine from that anchor. If the key isn't in the active graph we can't
    // anchor — fall back to the heuristic.
    public void NoteAuthoritativePosition(RoomKey key)
    {
        if (_engine is null) return;
        if (!_awaitingAuthoritative)
        {
            _log?.Log(LogSeverity.Debug, LogSource,
                $"NoteAuthoritativePosition {key} ignored — not awaiting a resync");
            return;
        }
        if (_graph.GetRoom(key) is null)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"authoritative {key} not in active graph; falling back to heuristic backtrack");
            // `rm` answered — this isn't a confusion fumble, so don't re-ask (the
            // reply would just name the same absent room); fall straight through.
            HandleResyncFailure(retryable: false);
            return;
        }

        _awaitingAuthoritative = false;
        ResetResyncStage();
        _anchor = key;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        ResetTier3Orchestration();
        SetTier(TierLevel.Tier1, "authoritative position resync");
        _log?.Log(LogSeverity.Info, LogSource, $"authoritative resync → anchored {key}");
        Recovered?.Invoke(key);
        _engine.ResumeAfterRecovery(key);
    }

    // An authoritative locator we were waiting on didn't land — a Paradigm `rm`
    // that timed out, or a sysop `sys st` that came back empty, or either naming
    // a room the active graph lacks. Drop the wait flag and route by which give-up
    // boundary the resync was guarding. The engine is already paused from the
    // resync-wait; a Tier2 non-paused watch can't be re-entered from here, so
    // the fall-through is always the Tier3 footprint backtrack (or, for a
    // terminal resync, the actual "Lost" abort).
    public void OnAuthoritativeResyncFailed() => HandleResyncFailure(retryable: true);

    // retryable is true for a `rm` that went UNANSWERED (the resolver's timeout) —
    // on Paradigm that's a confusion fumble eating the command, so if we're
    // confused we re-ask instead of giving up. It's false when `rm` DID answer but
    // named a room the active map set lacks (NoteAuthoritativePosition's
    // out-of-graph path): re-asking would just replay the same unusable room, so
    // fall straight through to the heuristic / Lost routing.
    private void HandleResyncFailure(bool retryable)
    {
        if (_engine is null) return;
        if (!_awaitingAuthoritative) return;

        if (retryable
            && IsConfused?.Invoke() == true
            && _confusedResyncRetries < MaxConfusedResyncRetries
            && TryResyncForced?.Invoke("`rm` eaten by a confusion fumble; re-asking") == true)
        {
            _confusedResyncRetries++;
            _log?.Log(LogSeverity.Info, LogSource,
                $"resync retry {_confusedResyncRetries}/{MaxConfusedResyncRetries}: `rm` unanswered while confused — "
                + $"re-asking, holding {_engine.Name} (confusion is self-clearing)");
            return;   // stay awaiting, same stage — the re-fired `rm` drives the next callback
        }

        _awaitingAuthoritative = false;
        ResyncStage stage = _resyncStage;
        _resyncStage = ResyncStage.None;

        if (stage == ResyncStage.Terminal)
        {
            // The last-resort `rm` before the "Lost" dialog also failed (no reply,
            // or a room the active graph doesn't contain) — now it's genuinely
            // Lost. Fall through to the real abort with the reason we deferred.
            _log?.Log(LogSeverity.Warn, LogSource,
                "terminal rm resync failed; declaring Lost");
            DeclareLost(_pendingLostDetail ?? "rm resync failed");
            return;
        }

        // BeforeBacktrack (or a NoteSuspectedMismatch resync, stage None): the
        // pre-backtrack `rm` didn't land. Fall back exactly as the original
        // post-resync path did — trust a still-Confirmed tracker if there is one,
        // else run the heuristic backtrack — but do NOT re-enter EscalateToTier3,
        // which would fire yet another `rm` and loop. The terminal FailTier3 gets
        // the final `rm` shot instead.
        _log?.Log(LogSeverity.Warn, LogSource,
            "rm resync failed; falling back to heuristic recovery");
        FallBackToHeuristicAfterResync("rm resync failed");
    }

    // Post-resync fallback: a Confirmed tracker at a graph-known room still wins
    // (the stock-realm analogue), otherwise the heuristic backtrack runs. Split
    // out so OnAuthoritativeResyncFailed reuses EscalateToTier3's non-`rm` steps
    // without recursively re-attempting the `rm` that just failed.
    private void FallBackToHeuristicAfterResync(string reason)
    {
        if (TryTrustConfirmedTracker(reason)) return;
        // `rm` didn't land, but a sysop `sys st` is a different mechanism and may
        // still answer — and it beats a backtrack that can't converge among
        // identically-named rooms. Guarded one-per-escalation, so a sysop failure
        // routes back here and falls through rather than looping.
        if (TrySysopGroundTruth(reason)) return;
        BeginHeuristicTier3(reason);
    }

    // Check before sending the engine's next planned step. Returns true
    // when the gate is OK with the send. Returns false when the gate has
    // escalated to tier 3 — engine must stop and surrender control until
    // Recovered fires (or RecoveryFailed).
    public bool MayProceedWithPlannedStep()
    {
        if (_engine is null) return true;
        if (_awaitingAuthoritative)
        {
            // Paused mid-flight on an authoritative locator — hold every planned
            // step until NoteAuthoritativePosition resumes us or the fallback fires.
            _log?.Log(LogSeverity.Info, LogSource,
                $"MayProceed=false: awaiting authoritative position resync; engine={_engine.Name} must wait");
            return false;
        }
        if (CurrentTier == TierLevel.Tier3)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"MayProceed=false: tier3 active; engine={_engine.Name} must wait for Recovered/RecoveryFailed");
            return false;
        }

        if (CurrentTier == TierLevel.Tier2
            && _engine.PeekNextPlannedDirection() is { } nextDir
            && _tracker.State.CurrentRoom is { } here
            && !here.Exits.ContainsKey(nextDir))
        {
            EscalateToTier3($"planned direction {nextDir} not available from {here.Key}");
            return false;
        }

        return true;
    }

    private void EscalateToTier3(string reason)
    {
        if (CurrentTier == TierLevel.Tier3) return;
        if (_engine is null) return;

        // Before committing to the heuristic reverse-walk (which ends in the
        // "Lost" dialog when it can't converge), defer to the RoomTracker if
        // it's still Confirmed at a graph-known room. Confirmed means the
        // current room is trusted as a source — that's the contract of
        // RoomConfidence.Confirmed — so the tracker already knows where we
        // are; the backtrack exists to recover from Suspect / Lost / ambiguity,
        // not to second-guess a room the tracker is sure of. In a
        // name-ambiguous area (e.g. Darkwood Forest, 66 same-named rooms) the
        // name+exits matcher can even converge to the WRONG room and reroute
        // from there, which is exactly the failure this guards against. Treat
        // the confirmed key as authoritative and let the engine reroute from
        // it — the stock-realm analogue of the Paradigm `rm` resync.
        if (TryTrustConfirmedTracker(reason)) return;

        // Paradigm: before committing to the heuristic backtrack at all, ask the
        // game where we are. `rm` is authoritative and cheap, and the backtrack
        // is exactly the thing that ends in the "Lost" dialog when it can't
        // converge — so on a realm that can just answer the question, don't guess.
        // Deferred: pause and wait for the reply; a success re-anchors us (never
        // entering Tier3), a failure falls through to BeginHeuristicTier3.
        if (TryResyncBeforeBacktrack(reason)) return;

        // Same argument one realm over: where the character has sysop powers,
        // `sys st` answers the question outright, and the backtrack is the thing
        // that ends in the "Lost" dialog when it can't converge. After the `rm`
        // attempt, because on Paradigm that one is cheaper and already tried.
        if (TrySysopGroundTruth(reason)) return;

        BeginHeuristicTier3(reason);
    }

    // Fire a forced `rm` and hold the engine for the reply, guarding the entry to
    // the heuristic backtrack. Returns true when a request is in flight (caller
    // must stop); false on stock realms / no wire / maze-owned `rm`, so the caller
    // proceeds straight to the backtrack.
    private bool TryResyncBeforeBacktrack(string reason)
    {
        if (_engine is null) return false;
        if (_awaitingAuthoritative) return true;   // one already in flight — hold
        if (TryResyncForced?.Invoke(reason) != true) return false;

        _awaitingAuthoritative = true;
        _resyncStage = ResyncStage.BeforeBacktrack;
        _engine.PauseForRecovery($"rm resync before backtrack: {reason}");
        _log?.Log(LogSeverity.Info, LogSource,
            $"resync-before-backtrack: paused {_engine.Name}, asking `rm` before the heuristic backtrack — {reason}");
        return true;
    }

    // The actual heuristic reverse-walk recovery: seed the footprint from the
    // current observation and start backtracking. Reached when there's no
    // authoritative `rm` to lean on (stock realm), or after a pre-backtrack `rm`
    // failed to land. This is the body EscalateToTier3 used to inline.
    private void BeginHeuristicTier3(string reason)
    {
        if (_engine is null) return;
        if (CurrentTier == TierLevel.Tier3 && Tier3Active) return;   // already backtracking

        _log?.Log(LogSeverity.Warn, LogSource,
            $"Tier3.start: {reason} (engine={_engine.Name} anchor={(_anchor?.ToString() ?? "(none)")} executed={_executedSinceAnchor.Count} steps)");
        SetTier(TierLevel.Tier3, reason);
        _engine.PauseForRecovery(reason);

        if (_anchor is null)
        {
            // No anchor — can't backtrack. Terminal failure.
            FailTier3("no anchor available; backtrack impossible");
            return;
        }

        // Seed the matcher from the current observation's name+exits
        // candidates (the universe of "where might we be right now?").
        Room? here = _tracker.State.CurrentRoom;
        if (here is null)
        {
            FailTier3("tracker has no current room; backtrack impossible");
            return;
        }

        IReadOnlySet<Direction> obsExits = ExitMaskToSet(here.ExitMask);
        IReadOnlyList<RoomKey> seeds = _graph.FindCandidates(here.Name, obsExits);
        if (seeds.Count == 0) seeds = new[] { here.Key };   // best-effort

        _tier3.Reset(seeds);
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.seed: {seeds.Count} candidates from observation '{here.Name}'");

        // Fingerprint where we stand BEFORE reversing a single step — a
        // look-sweep in place may already break the twin. If it doesn't (or we
        // can't look, e.g. dark), fall through to the reverse-walk.
        RelocalizeInPlace(here);
    }

    // Stock-realm authoritative re-anchor: when the RoomTracker is Confirmed at
    // a room that exists in the active graph, that room is trusted (Confirmed's
    // own contract), so treat its key as authoritative instead of running the
    // tier-3 backtrack. Mirrors NoteAuthoritativePosition's re-anchor + resume,
    // minus the Paradigm rm-resync bookkeeping. The engine reroutes from the
    // confirmed key (its EnterRecovery already trusts a Confirmed tracker); a
    // reroute that stays unroutable re-enters here at most until the engine's
    // own bounded recovery-attempt cap fails it cleanly, so this can't loop.
    // Returns true when it short-circuited the escalation.
    private bool TryTrustConfirmedTracker(string reason)
    {
        if (_engine is null) return false;
        if (_tracker.State.Confidence != RoomConfidence.Confirmed) return false;
        if (_tracker.State.CurrentRoom is not { } room) return false;
        if (_graph.GetRoom(room.Key) is null) return false;

        _awaitingAuthoritative = false;
        ResetResyncStage();
        _anchor = room.Key;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        ResetTier3Orchestration();
        if (CurrentTier != TierLevel.Tier1) SetTier(TierLevel.Tier1, "confirmed-tracker re-anchor");
        _log?.Log(LogSeverity.Info, LogSource,
            $"trust-confirmed: re-anchored to {room.Key} ({room.Name}) instead of tier-3 backtrack — {reason}");
        Recovered?.Invoke(room.Key);
        _engine.ResumeAfterRecovery(room.Key);
        return true;
    }

    // Ask the ground-truth locator (a sysop `sys st`) for our real room instead
    // of reversing moves to deduce it. Reuses the authoritative-resync wait the
    // Paradigm `rm` path already owns: the engine pauses, planned steps are
    // held, and exactly one of NoteAuthoritativePosition / OnAuthoritativeResyncFailed
    // follows. Returns true when it took over the escalation.
    private bool TrySysopGroundTruth(string reason)
    {
        if (_engine is null) return false;
        if (_awaitingAuthoritative) return false;
        if (_sysopLocateTried) return false;
        if (TrySysopLocate?.Invoke(reason) != true) return false;

        _sysopLocateTried = true;
        _awaitingAuthoritative = true;
        _engine.PauseForRecovery($"awaiting sysop status: {reason}");
        _log?.Log(LogSeverity.Info, LogSource,
            $"sysop-locate: paused {_engine.Name}, awaiting `sys st` ground truth instead of backtracking (tier={CurrentTier})");
        return true;
    }

    // ----- tier-3 footprint orchestration ----------------------------

    // Fingerprint the room we escalated from, before any reverse move. Lit: a
    // look-sweep here (combat-gated) may already break the twin. Dark: nothing
    // to look at, so go straight to the reverse-walk. The initial room carries
    // no move, so no temporal step — only the spatial sweep narrows it.
    private void RelocalizeInPlace(Room here)
    {
        if (_tracker.IsInDarkRoom)
        {
            AdvanceReverseWalk();
            return;
        }
        BeginSweepOrAdvance(new HashSet<Direction>(here.Exits.Keys));
    }

    // Lit backtrack landing: temporal-narrow by the reverse move we just made,
    // then (combat-gated) spatially-narrow with a look-sweep of this room.
    private void HandleLitLanding(RoomObservation obs)
    {
        if (_engine is null) return;

        _tier3.Step(_lastBacktrackReverse, obs);
        if (_tier3.IsConverged) { FinishTier3Success(_tier3.Candidates.Single()); return; }
        if (_tier3.IsExhausted) { FailTier3("footprint exhausted after reverse step"); return; }

        BeginSweepOrAdvance(new HashSet<Direction>(obs.Exits));
    }

    // Dark backtrack landing: the room never rendered, so the only constraint
    // is that the move executed. Per the dark protocol, wait for the next
    // combat tick (clearing any ambush that swings) before dead-reckoning.
    private void HandleDarkLanding()
    {
        if (_engine is null) return;

        // No combat gating wired (headless) → step immediately; otherwise hold
        // until a combat tick with the room clear, then dead-reckon.
        if (_hostilesPresent is null) { DoDarkStep(); return; }

        _tier3Phase = Tier3Phase.WaitingCombatClear;
        _combatClearResume = DoDarkStep;
        _log?.Log(LogSeverity.Info, LogSource,
            "Tier3.dark: holding for combat tick before dead-reckoning the reverse step");
    }

    private void DoDarkStep()
    {
        _tier3.StepBlind(_lastBacktrackReverse);
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.dark-step reverse={_lastBacktrackReverse} → {_tier3.Candidates.Count} candidate(s)");
        EvaluateFootprint();
    }

    // Combat-gate a look-sweep (lit): clear the room before peeking. Skips
    // straight to the sweep when nothing hostile is present / no gate is wired.
    private void BeginSweepOrAdvance(IReadOnlySet<Direction> ownExits)
    {
        if (_hostilesPresent?.Invoke() == true)
        {
            _tier3Phase = Tier3Phase.WaitingCombatClear;
            _combatClearResume = () => StartSweep(ownExits);
            _log?.Log(LogSeverity.Info, LogSource,
                "Tier3: holding look-sweep until the room is clear of hostiles");
            return;
        }
        StartSweep(ownExits);
    }

    private void ResumeAfterCombatClear()
    {
        Action? resume = _combatClearResume;
        _combatClearResume = null;
        _log?.Log(LogSeverity.Info, LogSource, "Tier3: room clear — resuming recovery");
        resume?.Invoke();
    }

    // Peek every exit of the landing room and spatially narrow the footprint by
    // what the neighbours reveal. No sweep available (headless / no wire / no
    // exits) → decide on the temporal footprint alone.
    private void StartSweep(IReadOnlySet<Direction> ownExits)
    {
        if (_sweep is null || !_sweep.CanSweep)
        {
            EvaluateFootprint();
            return;
        }

        _tier3Phase = Tier3Phase.Sweeping;
        if (!_sweep.Begin(ownExits, OnSweepComplete))
            EvaluateFootprint();
    }

    private void OnSweepComplete(IReadOnlyDictionary<Direction, RoomObservation> neighbours)
    {
        if (!Tier3Active) return;
        _tier3.FilterByNeighbours(neighbours);
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.look-sweep: {neighbours.Count} neighbour(s) → {_tier3.Candidates.Count} candidate(s)");
        EvaluateFootprint();
    }

    // Converged → re-confirm; exhausted → fail; otherwise take one more reverse
    // step and keep growing the footprint.
    private void EvaluateFootprint()
    {
        if (_tier3.IsConverged) { FinishTier3Success(_tier3.Candidates.Single()); return; }
        if (_tier3.IsExhausted) { FailTier3("footprint exhausted — live world disagrees with the graph"); return; }
        AdvanceReverseWalk();
    }

    // Pop the most-recent executed step, reverse it, and send it. The landing
    // (lit via OnRoomObserved, dark via the tracker) continues the footprint.
    private void AdvanceReverseWalk()
    {
        if (_engine is null) return;
        if (_executedSinceAnchor.Count == 0)
        {
            // Fully reversed the executed history without converging. If the
            // anchor itself had reconciled we'd have exited already, so this is
            // a terminal failure.
            FailTier3("backtrack exhausted to anchor without convergence");
            return;
        }

        Direction lastSent = _executedSinceAnchor[^1];
        _executedSinceAnchor.RemoveAt(_executedSinceAnchor.Count - 1);
        _lastBacktrackReverse = Reverse(lastSent);
        _tier3Phase = Tier3Phase.AwaitingLanding;

        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.backtrack: reverse({lastSent})={_lastBacktrackReverse} (history remaining={_executedSinceAnchor.Count})");
        _engine.SendBacktrackMove(_lastBacktrackReverse);
    }

    private void ResetTier3Orchestration()
    {
        _tier3Phase = Tier3Phase.Idle;
        _combatClearResume = null;
        _sweep?.Cancel();
    }

    private void FinishTier3Success(RoomKey recovered)
    {
        if (_engine is null) return;
        if (!Tier3Active) return;   // already finished (re-entrant tracker 1-of-1)
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.recovered → {recovered}");
        _anchor = recovered;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        ResetTier3Orchestration();
        ResetResyncStage();
        SetTier(TierLevel.Tier1, "tier-3 recovered");

        // Re-confirm the tracker at the room the footprint resolved — unless it
        // already agrees (the tracker-1-of-1 fast path lands here with the
        // tracker Confirmed at `recovered`, and re-locating would needlessly
        // re-enter StateChanged). SetLocated promotes to Confirmed so the next
        // observation reconciles against the right room.
        if (_tracker.State.Confidence != RoomConfidence.Confirmed
            || _tracker.State.CurrentRoom?.Key != recovered)
        {
            _tracker.SetLocated(recovered);
        }

        Recovered?.Invoke(recovered);
        _engine.ResumeAfterRecovery(recovered);
    }

    // The heuristic recovery ran out of road. On Paradigm, spend one last forced
    // `rm` before the "Lost" dialog — the game can report our true room even when
    // the footprint backtrack couldn't converge, so a client is only ever
    // genuinely Lost when `rm` itself doesn't answer (or names a room the loaded
    // map set doesn't contain). Only when there's no `rm` to lean on does this
    // declare Lost outright (report paradigm-20260902-223159).
    private void FailTier3(string detail)
    {
        if (_engine is null) return;
        if (TryTerminalResync(detail)) return;
        DeclareLost(detail);
    }

    // Fire the last-resort forced `rm` before Lost. Returns true when the request
    // is in flight (caller must stop and wait) — a success re-anchors us and
    // resumes; a failure routes back through OnAuthoritativeResyncFailed, which
    // declares Lost for real with the deferred detail. Returns false when no `rm`
    // is available (stock realm / no wire / maze-owned), so the caller declares
    // Lost immediately. Guarded on the stage so a failed terminal `rm` can't
    // re-arm another one.
    private bool TryTerminalResync(string detail)
    {
        if (_engine is null) return false;
        if (_resyncStage == ResyncStage.Terminal) return false;   // already spent it this episode
        if (_awaitingAuthoritative) return true;                   // one in flight — hold
        if (TryResyncForced?.Invoke($"last resort before Lost: {detail}") != true) return false;

        _resyncStage = ResyncStage.Terminal;
        _pendingLostDetail = detail;
        _awaitingAuthoritative = true;
        _engine.PauseForRecovery($"last-resort rm before Lost: {detail}");
        _log?.Log(LogSeverity.Warn, LogSource,
            $"terminal resync: asking `rm` before declaring Lost — {detail}");
        return true;
    }

    private void DeclareLost(string detail)
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Warn, LogSource, $"Tier3.failed: {detail}");
        _tier3.Clear();
        ResetTier3Orchestration();
        ResetResyncStage();

        // Capture the engine name BEFORE aborting. AbortFromRecoveryFailure
        // re-enters the gate synchronously — both engines reset themselves,
        // and Reset() calls back into Detach() which nulls _engine — so
        // reading _engine.Name after the abort would NullReference. This is
        // deterministic re-entrancy, not a thread race.
        string engineName = _engine.Name;
        RoomKey? lastGood = _anchor;
        // Stay in tier 3 visually; engine aborts; UI pops the dialog.
        _engine.AbortFromRecoveryFailure(detail);
        RecoveryFailed?.Invoke(new RecoveryFailedEvent(engineName, detail, lastGood));
    }

    private void ResetResyncStage()
    {
        _resyncStage = ResyncStage.None;
        _pendingLostDetail = null;
        _confusedResyncRetries = 0;
    }

    // ----- matcher delegates -----------------------------------------

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

    // ----- helpers ---------------------------------------------------

    private void SetTier(TierLevel target, string reason)
    {
        // Settling at tier 1 ends the escalation, so the next one gets its own
        // ground-truth locate. Cleared before the no-change early-return: an
        // escalation that started FROM tier 1 and resolved there never changes
        // tier, and would otherwise leave the flag stuck on.
        if (target == TierLevel.Tier1) _sysopLocateTried = false;
        if (target == CurrentTier) return;
        TierLevel prev = CurrentTier;
        CurrentTier = target;
        // Tier transitions are first-class diagnostic signal — log
        // every flip so a session-log replay shows the recovery
        // ladder climbing / descending. Tier3 is Warn (we're in
        // recovery mode); everything else is Info.
        LogSeverity severity = target == TierLevel.Tier3 ? LogSeverity.Warn : LogSeverity.Info;
        _log?.Log(severity, LogSource,
            $"tier {prev} → {target} ({reason}); engine={_engine?.Name ?? "?"} anchor={(_anchor?.ToString() ?? "(none)")} executed={_executedSinceAnchor.Count}");
        TierChanged?.Invoke(new RecoveryTierChangedEvent(prev, target, reason));
    }

    private static Direction Reverse(Direction d) => d switch
    {
        Direction.N  => Direction.S,
        Direction.S  => Direction.N,
        Direction.E  => Direction.W,
        Direction.W  => Direction.E,
        Direction.NE => Direction.SW,
        Direction.SW => Direction.NE,
        Direction.NW => Direction.SE,
        Direction.SE => Direction.NW,
        Direction.U  => Direction.D,
        Direction.D  => Direction.U,
        _ => d,
    };
}

// Three tiers the gate cycles through.
public enum TierLevel
{
    Tier1 = 1,
    Tier2 = 2,
    Tier3 = 3,
}

// Payload of EngineRecoveryGate.TierChanged.
public readonly record struct RecoveryTierChangedEvent(TierLevel Previous, TierLevel Current, string Reason);

// Payload of EngineRecoveryGate.RecoveryFailed. LastGoodRoom is the anchor the
// backtrack started from — the last room the tracker was confident about — so the
// Lost dialog can point the user at a concrete "you were here" spot instead of a
// bare "couldn't recover".
public readonly record struct RecoveryFailedEvent(string EngineName, string Detail, RoomKey? LastGoodRoom);
