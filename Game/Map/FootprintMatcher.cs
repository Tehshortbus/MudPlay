using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Tier-2 SLAM-style localisation accumulator. Seeded with a candidate
/// set from the first ambiguous observation; each subsequent
/// (move, observation) pair filters the working set down by walking
/// every candidate one hop forward and dropping those whose target
/// either has no matching exit, is gated behind a trapped exit (we
/// don't traverse traps), or doesn't match the new observation.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="Reset"/> on seed, <see cref="Step"/> on each
/// (move, observation) pair, <see cref="Clear"/> when the host
/// (<see cref="RoomTracker"/>) lands a fresh <see cref="RoomConfidence.Confirmed"/>
/// transition or the active graph reloads.
/// </para>
/// <para>
/// Single-threaded: every entry point is invoked from
/// <see cref="RoomTracker"/> which itself is only called from the UI
/// thread (Dispatcher-marshalled upstream).
/// </para>
/// <para>
/// All trap filtering and graph traversal lives in the injected
/// <see cref="_probeHop"/> delegate — the matcher itself is pure data,
/// graph-agnostic, fully testable in isolation.
/// </para>
/// </remarks>
public sealed class FootprintMatcher
{
    private const string LogSource = "RoomTracker";

    private readonly Func<RoomKey, Direction, HopOutcome> _probeHop;
    private readonly Func<RoomKey, RoomObservation, bool> _matchesObservation;
    private readonly LogService? _log;
    private readonly int _depthCeiling;

    private readonly HashSet<RoomKey> _candidates = new();

    /// <summary>
    /// Live working set. Empty when the matcher hasn't been seeded or
    /// the most recent <see cref="Step"/> exhausted every candidate.
    /// </summary>
    public IReadOnlySet<RoomKey> Candidates => _candidates;

    /// <summary>Number of <see cref="Step"/> calls since the last <see cref="Reset"/>.</summary>
    public int Depth { get; private set; }

    /// <summary>
    /// True while the matcher is still narrowing — has &gt; 1 candidate
    /// AND hasn't hit the depth ceiling. False on seed-with-0,
    /// converged-to-1, exhausted-to-0, or ceiling reached.
    /// </summary>
    public bool IsActive => _candidates.Count > 1 && Depth < _depthCeiling;

    /// <summary>Exactly one candidate remains — the host should auto-land Confirmed there.</summary>
    public bool IsConverged => _candidates.Count == 1;

    /// <summary>
    /// The most recent <see cref="Step"/> dropped the last candidate.
    /// The graph and the live world don't agree — the host should fire
    /// its mismatch event and stop narrowing.
    /// </summary>
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

    /// <summary>
    /// Seed the working set with the host's initial candidates (typically
    /// the result of a name+exit-mask graph lookup from the observation
    /// that triggered tier 2). Resets <see cref="Depth"/> and
    /// <see cref="IsExhausted"/>.
    /// </summary>
    public void Reset(IEnumerable<RoomKey> initialCandidates)
    {
        ArgumentNullException.ThrowIfNull(initialCandidates);
        _candidates.Clear();
        foreach (RoomKey k in initialCandidates) _candidates.Add(k);
        Depth = 0;
        IsExhausted = false;
    }

    /// <summary>
    /// Drop everything. Caller does this on Confirmed transitions
    /// (matter resolved by other means) or on graph reload.
    /// </summary>
    public void Clear()
    {
        _candidates.Clear();
        Depth = 0;
        IsExhausted = false;
    }

    /// <summary>
    /// Walk every current candidate one hop in <paramref name="move"/>,
    /// keep only those whose target matches <paramref name="observation"/>.
    /// Records the step depth regardless of outcome.
    /// </summary>
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

/// <summary>
/// Outcome of one candidate's hop in a given direction. Used by
/// <see cref="FootprintMatcher"/> to distinguish "no exit there" from
/// "exit there but trap-gated" so the host can surface useful drop
/// reasons in the tier-2 log.
/// </summary>
public readonly record struct HopOutcome(HopOutcomeKind Kind, RoomKey Target)
{
    /// <summary>Hop landed at <paramref name="target"/>.</summary>
    public static HopOutcome Reached(RoomKey target) => new(HopOutcomeKind.Reached, target);

    /// <summary>No exit in the requested direction on the source candidate.</summary>
    public static HopOutcome NoExit() => new(HopOutcomeKind.NoExit, default);

    /// <summary>Exit exists but is flagged as a trap — the matcher refuses to traverse.</summary>
    public static HopOutcome TrappedExit() => new(HopOutcomeKind.TrappedExit, default);
}

/// <summary>The three outcomes a candidate's hop can produce.</summary>
public enum HopOutcomeKind
{
    Reached,
    NoExit,
    TrappedExit,
}
