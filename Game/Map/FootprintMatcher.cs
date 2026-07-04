using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Tier-2 SLAM-style localisation accumulator. Seeded with a candidate set
// from the first ambiguous observation; each subsequent (move,
// observation) pair filters the working set down by walking every
// candidate one hop forward and dropping those whose target either has no
// matching exit, is gated behind a trapped exit (we don't traverse traps),
// or doesn't match the new observation.
//
// Lifecycle: Reset on seed, Step on each (move, observation) pair, Clear
// when the host (RoomTracker) lands a fresh Confirmed transition or the
// active graph reloads.
//
// Single-threaded: every entry point is invoked from RoomTracker which
// itself is only called from the UI thread (Dispatcher-marshalled
// upstream).
//
// All trap filtering and graph traversal lives in the injected _probeHop
// delegate — the matcher itself is pure data, graph-agnostic, fully
// testable in isolation.
public sealed class FootprintMatcher
{
    private const string LogSource = "RoomTracker";

    private readonly Func<RoomKey, Direction, HopOutcome> _probeHop;
    private readonly Func<RoomKey, RoomObservation, bool> _matchesObservation;
    private readonly LogService? _log;
    private readonly int _depthCeiling;

    private readonly HashSet<RoomKey> _candidates = new();

    // Live working set. Empty when the matcher hasn't been seeded or the
    // most recent Step exhausted every candidate.
    public IReadOnlySet<RoomKey> Candidates => _candidates;

    // Number of Step calls since the last Reset.
    public int Depth { get; private set; }

    // True while the matcher is still narrowing — has > 1 candidate AND
    // hasn't hit the depth ceiling. False on seed-with-0, converged-to-1,
    // exhausted-to-0, or ceiling reached.
    public bool IsActive => _candidates.Count > 1 && Depth < _depthCeiling;

    // Exactly one candidate remains — the host should auto-land Confirmed there.
    public bool IsConverged => _candidates.Count == 1;

    // The most recent Step dropped the last candidate. The graph and the
    // live world don't agree — the host should fire its mismatch event and
    // stop narrowing.
    public bool IsExhausted { get; private set; }

    public FootprintMatcher(
        Func<RoomKey, Direction, HopOutcome> probeHop,
        Func<RoomKey, RoomObservation, bool> matchesObservation,
        LogService? log = null,
        int depthCeiling = 50)
    {
        ArgumentNullException.ThrowIfNull(probeHop);
        ArgumentNullException.ThrowIfNull(matchesObservation);
        if (depthCeiling < 1)
            throw new ArgumentOutOfRangeException(nameof(depthCeiling), "Depth ceiling must be at least 1.");
        _probeHop = probeHop;
        _matchesObservation = matchesObservation;
        _log = log;
        _depthCeiling = depthCeiling;
    }

    // Seed the working set with the host's initial candidates (typically
    // the result of a name+exit-mask graph lookup from the observation that
    // triggered tier 2). Resets Depth and IsExhausted.
    public void Reset(IEnumerable<RoomKey> initialCandidates)
    {
        ArgumentNullException.ThrowIfNull(initialCandidates);
        _candidates.Clear();
        foreach (RoomKey k in initialCandidates) _candidates.Add(k);
        Depth = 0;
        IsExhausted = false;
    }

    // Drop everything. Caller does this on Confirmed transitions (matter
    // resolved by other means) or on graph reload.
    public void Clear()
    {
        _candidates.Clear();
        Depth = 0;
        IsExhausted = false;
    }

    // Walk every current candidate one hop in move, keep only those whose
    // target matches observation. Records the step depth regardless of
    // outcome.
    public void Step(Direction move, RoomObservation observation)
    {
        int prevCount = _candidates.Count;
        if (prevCount == 0) return;

        Depth++;
        var survivors = new List<RoomKey>(prevCount);

        foreach (RoomKey candidate in _candidates)
        {
            HopOutcome hop = _probeHop(candidate, move);
            switch (hop.Kind)
            {
                case HopOutcomeKind.NoExit:
                    _log?.Log(LogSeverity.Debug, LogSource,
                        $"Tier2.drop {candidate}: no_exit (dir={move})");
                    continue;
                case HopOutcomeKind.TrappedExit:
                    _log?.Log(LogSeverity.Debug, LogSource,
                        $"Tier2.drop {candidate}: trapped_exit (dir={move})");
                    continue;
                case HopOutcomeKind.Reached:
                    if (!_matchesObservation(hop.Target, observation))
                    {
                        _log?.Log(LogSeverity.Debug, LogSource,
                            $"Tier2.drop {candidate}: observation_mismatch → {hop.Target}");
                        continue;
                    }
                    survivors.Add(hop.Target);
                    break;
            }
        }

        _candidates.Clear();
        foreach (RoomKey k in survivors) _candidates.Add(k);
        IsExhausted = _candidates.Count == 0;

        _log?.Log(LogSeverity.Debug, LogSource,
            $"Tier2.step move={move} obs='{observation.Name}' depth={Depth}: {prevCount}→{_candidates.Count}");
    }
}

// Outcome of one candidate's hop in a given direction. Distinguishes "no
// exit there" from "exit there but trap-gated" so the host can surface
// useful drop reasons in the tier-2 log.
public readonly record struct HopOutcome(HopOutcomeKind Kind, RoomKey Target)
{
    // Hop landed at target.
    public static HopOutcome Reached(RoomKey target) => new(HopOutcomeKind.Reached, target);

    // No exit in the requested direction on the source candidate.
    public static HopOutcome NoExit() => new(HopOutcomeKind.NoExit, default);

    // Exit exists but is flagged as a trap — the matcher refuses to traverse.
    public static HopOutcome TrappedExit() => new(HopOutcomeKind.TrappedExit, default);
}

// The three outcomes a candidate's hop can produce.
public enum HopOutcomeKind
{
    Reached,
    NoExit,
    TrappedExit,
}
