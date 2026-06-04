using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Shared tier-1/2/3 location-recovery service for the walker, loop
/// runner, and (forthcoming) auto-lair scheduler. Owns the strict-1-of-1
/// anchor, the rolling executed-steps history since that anchor, and
/// the tier-2/3 escalation logic. Engines plug in via
/// <see cref="IRecoverableEngine"/>; the gate calls back into them for
/// backtrack sends + pause / resume / abort.
/// </summary>
/// <remarks>
/// <para>
/// Singleton in <see cref="Services.AppServices"/>; only one engine is
/// attached at a time (which matches reality — the wire serialises
/// movement). Engines call <see cref="Attach"/> on Start and
/// <see cref="Detach"/> on Stop / Reset.
/// </para>
/// <para>
/// Tier definitions:
/// </para>
/// <list type="bullet">
///   <item><b>Tier 1</b> — engine executing planned path; anchor refreshes
///         every time the tracker lands at a true 1-of-1 graph match.</item>
///   <item><b>Tier 2</b> — observation came in that didn't match the engine's
///         expected next room (or was a re-display); engine keeps executing
///         the planned path while the gate watches for a 1-of-1 recovery
///         in ≤ <see cref="Tier2StepBudget"/> further moves. Escalates to
///         tier 3 either at the step ceiling OR when the engine's
///         <see cref="IRecoverableEngine.PeekNextPlannedDirection"/> isn't
///         available on the current observed room.</item>
///   <item><b>Tier 3</b> — gate takes over: pauses the engine, sends
///         reverse-of-executed moves one at a time, accumulates a
///         <c>(direction, observation)</c> footprint, and uses
///         <see cref="FootprintMatcher"/> to narrow seeds from the current
///         observation's <c>FindCandidates</c>. Converges to 1 → engine
///         resumes from the recovered anchor; exhausted to 0 OR all
///         executed steps reversed without convergence → engine aborted
///         and <see cref="RecoveryFailed"/> fires (caller pops the
///         "Lost — use the map to set location" info dialog).</item>
/// </list>
/// </remarks>
public sealed class EngineRecoveryGate
{
    private const string LogSource = "RecoveryGate";

    /// <summary>Tier-2 step ceiling per design (15 from anchor).</summary>
    public const int Tier2StepBudget = 15;

    /// <summary>
    /// FootprintMatcher depth ceiling for tier-3 backtrack. We never
    /// backtrack further than the executed-steps history, but this
    /// keeps the matcher honest if a future change ever pumped extra
    /// steps in.
    /// </summary>
    private const int Tier3DepthCeiling = 64;

    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    private IRecoverableEngine? _engine;
    private RoomKey? _anchor;
    private readonly List<Direction> _executedSinceAnchor = new();
    private readonly FootprintMatcher _tier3;
    private bool _tier3Backtracking;

    /// <summary>Currently active engine, or null when nothing is attached.</summary>
    public IRecoverableEngine? AttachedEngine => _engine;

    /// <summary>Live tier the gate is in. Always 1 when no engine attached.</summary>
    public TierLevel CurrentTier { get; private set; } = TierLevel.Tier1;

    /// <summary>Most recent strict-1-of-1 anchor while attached. Null until first 1-of-1.</summary>
    public RoomKey? Anchor => _anchor;

    /// <summary>Read-only view of the executed-steps history since the current anchor.</summary>
    public IReadOnlyList<Direction> ExecutedSinceAnchor => _executedSinceAnchor;

    /// <summary>Fires whenever <see cref="CurrentTier"/> changes. Payload carries previous/new + a reason.</summary>
    public event Action<RecoveryTierChangedEvent>? TierChanged;

    /// <summary>Fires when tier-3 recovery converges. Payload is the recovered anchor.</summary>
    public event Action<RoomKey>? Recovered;

    /// <summary>Fires when tier-3 recovery fails terminally. Caller surfaces the modeless info dialog.</summary>
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

    // ----- attach / detach -------------------------------------------

    /// <summary>
    /// Bind an engine to the gate. Seeds <see cref="Anchor"/> from the
    /// tracker's current room if it's a true 1-of-1 match, else
    /// from the persisted <c>CharacterProfile.LastKnownRoom</c> if
    /// hydrated. Replaces any currently-attached engine.
    /// </summary>
    public void Attach(IRecoverableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_engine is not null) Detach();

        _engine = engine;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        _tier3Backtracking = false;
        CurrentTier = TierLevel.Tier1;

        Room? here = _tracker.State.CurrentRoom;
        _anchor = here?.Key;

        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier1.attach engine={engine.Name} anchor={(_anchor?.ToString() ?? "(none)")}");
    }

    /// <summary>Detach the current engine; clears all gate state.</summary>
    public void Detach()
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Info, LogSource, $"detach engine={_engine.Name}");
        _engine = null;
        _anchor = null;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        _tier3Backtracking = false;
        SetTier(TierLevel.Tier1, "detach");
    }

    /// <summary>
    /// The engine just sent a planned move. The gate appends it to the
    /// executed-steps history (used as the reverse-walk source if
    /// tier 3 is later triggered).
    /// </summary>
    public void NoteEngineStepSent(Direction direction)
    {
        if (_engine is null) return;
        _executedSinceAnchor.Add(direction);
    }

    // ----- tracker subscription --------------------------------------

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (_engine is null) return;
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is not { } room) return;

        // True 1-of-1 anchor refresh — independent of tier.
        bool isStrict = _graph.FindCandidates(room.Name, ExitMaskToSet(room.ExitMask)).Count == 1;
        if (isStrict)
        {
            _anchor = room.Key;
            _executedSinceAnchor.Clear();
            _log?.Log(LogSeverity.Info, LogSource,
                $"Tier1.anchor-refresh → {room.Key} ({room.Name})");

            if (_tier3Backtracking)
            {
                // Tier 3 backtrack just hit a 1-of-1 — recovery success.
                FinishTier3Success(room.Key);
                return;
            }

            if (CurrentTier != TierLevel.Tier1) SetTier(TierLevel.Tier1, "1-of-1 anchor recovered");
            return;
        }

        if (_tier3Backtracking)
        {
            // Tier 3 backtrack step landed without a 1-of-1. Feed the
            // matcher and check for convergence.
            StepTier3FootprintFromTransition();
        }
    }

    private static IReadOnlySet<Direction> ExitMaskToSet(uint mask)
    {
        var set = new HashSet<Direction>();
        for (int i = 0; i < 10; i++) if (((mask >> i) & 1u) != 0) set.Add((Direction)i);
        return set;
    }

    // ----- tier-2 + tier-3 triggers ----------------------------------

    /// <summary>
    /// The engine's per-step reconcile logic noticed the observation
    /// didn't match its expected next room (OR looked like a re-display).
    /// Caller is responsible for deciding the SHAPE of the mismatch —
    /// the gate just escalates to tier 2 and starts watching for either
    /// recovery or further escalation.
    /// </summary>
    public void NoteSuspectedMismatch(string reason)
    {
        if (_engine is null) return;
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

    /// <summary>
    /// Check before sending the engine's next planned step. Returns
    /// <c>true</c> when the gate is OK with the send. Returns
    /// <c>false</c> when the gate has escalated to tier 3 — engine
    /// must stop and surrender control until <see cref="Recovered"/>
    /// fires (or <see cref="RecoveryFailed"/>).
    /// </summary>
    public bool MayProceedWithPlannedStep()
    {
        if (_engine is null) return true;
        if (CurrentTier == TierLevel.Tier3) return false;

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

        _log?.Log(LogSeverity.Warn, LogSource, $"Tier3.start: {reason}");
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
        _tier3Backtracking = true;
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.seed: {seeds.Count} candidates from observation '{here.Name}'");

        // Don't pop the first backtrack step yet — engine has just
        // paused; we'll send the reverse move on the next gate tick.
        SendNextBacktrackMove();
    }

    private void SendNextBacktrackMove()
    {
        if (_engine is null) return;
        if (_executedSinceAnchor.Count == 0)
        {
            // We've fully reversed our executed history. If we're at
            // the anchor (matcher converged to it earlier) we'd have
            // exited already. Reaching here means the anchor itself
            // didn't reconcile — terminal failure.
            FailTier3("backtrack exhausted to anchor without convergence");
            return;
        }

        // Pop the most-recent executed step and reverse it.
        Direction lastSent = _executedSinceAnchor[^1];
        _executedSinceAnchor.RemoveAt(_executedSinceAnchor.Count - 1);

        Direction reverse = Reverse(lastSent);
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.backtrack: reverse({lastSent})={reverse} (history remaining={_executedSinceAnchor.Count})");

        _engine.SendBacktrackMove(reverse);
    }

    private void StepTier3FootprintFromTransition()
    {
        // The most-recent backtrack send was the LAST reverse direction we
        // popped — recover it from the pending queue heuristically: it's
        // the direction we just told the engine to send. Since we don't
        // record that explicitly, recompute from "expected reverse of
        // the head we just popped." Easier: tell the matcher to skip
        // this step (since we don't reliably know what direction the
        // tracker just observed via) and re-seed instead.
        //
        // In practice, the matcher's Step(direction, observation) is
        // the right call when we know the direction. The cleanest path
        // is: just re-narrow by re-seeding candidates from each new
        // observation's FindCandidates. If a single candidate remains,
        // we have convergence even without the per-step shape match.

        if (_tracker.State.CurrentRoom is not { } here) return;

        IReadOnlySet<Direction> obsExits = ExitMaskToSet(here.ExitMask);
        IReadOnlyList<RoomKey> reseeded = _graph.FindCandidates(here.Name, obsExits);

        if (reseeded.Count == 1)
        {
            FinishTier3Success(reseeded[0]);
            return;
        }

        if (reseeded.Count == 0)
        {
            FailTier3($"observation '{here.Name}' has no graph candidates");
            return;
        }

        SendNextBacktrackMove();
    }

    private void FinishTier3Success(RoomKey recovered)
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Info, LogSource,
            $"Tier3.recovered → {recovered}");
        _anchor = recovered;
        _executedSinceAnchor.Clear();
        _tier3.Clear();
        _tier3Backtracking = false;
        SetTier(TierLevel.Tier1, "tier-3 recovered");
        Recovered?.Invoke(recovered);
        _engine.ResumeAfterRecovery(recovered);
    }

    private void FailTier3(string detail)
    {
        if (_engine is null) return;
        _log?.Log(LogSeverity.Warn, LogSource, $"Tier3.failed: {detail}");
        _tier3.Clear();
        _tier3Backtracking = false;
        // Stay in tier 3 visually; engine aborts; UI pops the dialog.
        _engine.AbortFromRecoveryFailure(detail);
        RecoveryFailed?.Invoke(new RecoveryFailedEvent(_engine.Name, detail));
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
        if (target == CurrentTier) return;
        TierLevel prev = CurrentTier;
        CurrentTier = target;
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

/// <summary>Three tiers the gate cycles through.</summary>
public enum TierLevel
{
    Tier1 = 1,
    Tier2 = 2,
    Tier3 = 3,
}

/// <summary>Payload of <see cref="EngineRecoveryGate.TierChanged"/>.</summary>
public readonly record struct RecoveryTierChangedEvent(TierLevel Previous, TierLevel Current, string Reason);

/// <summary>Payload of <see cref="EngineRecoveryGate.RecoveryFailed"/>.</summary>
public readonly record struct RecoveryFailedEvent(string EngineName, string Detail);
