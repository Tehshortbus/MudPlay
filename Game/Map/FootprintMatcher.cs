using MudPlay.Services;

namespace MudPlay.Game.Map;

// SLAM-style localisation accumulator shared by three localisers: the recovery
// gate's tier-2 forward localiser and tier-3 backtrack, and RoomTracker's passive
// re-localiser. Seeded with a candidate set from an ambiguous observation; each
// later (move, observation) pair walks every candidate one hop and keeps those
// whose target matches the new observation. A candidate drops when its move has
// no exit, is trap-gated (we don't traverse traps), or lands somewhere the
// observation doesn't fit.
//
// The set never grows: every operation maps a candidate to at most one survivor,
// so a step costs no more than the seed did. Nothing here bounds how many steps a
// host takes; each host owns that (the gate caps its tier-3 walk-back, and a
// passive episode ends on convergence, exhaustion or the tracker settling).
//
// Single-threaded: every entry point is invoked from RoomTracker or the gate,
// which only run on the UI thread (Dispatcher-marshalled upstream).
//
// All trap filtering and graph traversal lives in the injected hop delegates; the
// matcher itself is pure data and graph-agnostic.
public sealed class FootprintMatcher
{
    private const string LogSource = "RoomTracker";

    private readonly Func<RoomKey, Direction, HopOutcome> _probeHop;
    private readonly Func<RoomKey, RoomObservation, bool> _matchesObservation;
    private readonly LogService? _log;

    private readonly HashSet<RoomKey> _candidates = new();

    // Live working set. Empty when the matcher hasn't been seeded or the
    // most recent Step exhausted every candidate.
    public IReadOnlySet<RoomKey> Candidates => _candidates;

    // Number of Step calls since the last Reset.
    public int Depth { get; private set; }

    // Exactly one candidate remains — the host should auto-land Confirmed there.
    public bool IsConverged => _candidates.Count == 1;

    // The most recent Step dropped the last candidate. The graph and the
    // live world don't agree — the host should fire its mismatch event and
    // stop narrowing.
    public bool IsExhausted { get; private set; }

    public FootprintMatcher(
        Func<RoomKey, Direction, HopOutcome> probeHop,
        Func<RoomKey, RoomObservation, bool> matchesObservation,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(probeHop);
        ArgumentNullException.ThrowIfNull(matchesObservation);
        _probeHop = probeHop;
        _matchesObservation = matchesObservation;
        _log = log;
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
        => Step(move.ToString(), candidate => _probeHop(candidate, move), observation);

    // The same step for a move each candidate resolves on its own: a text exit
    // (`go path`) sits in whichever exit slot each room keeps it in, so there is
    // no one direction to hop every candidate through. moveLabel is for the log.
    public void Step(string moveLabel, Func<RoomKey, HopOutcome> hopFrom, RoomObservation observation)
    {
        ArgumentNullException.ThrowIfNull(hopFrom);
        int prevCount = _candidates.Count;
        if (prevCount == 0) return;

        Depth++;
        var survivors = new List<RoomKey>(prevCount);

        foreach (RoomKey candidate in _candidates)
        {
            HopOutcome hop = hopFrom(candidate);
            switch (hop.Kind)
            {
                case HopOutcomeKind.NoExit:
                    _log?.Log(LogSeverity.Debug, LogSource,
                        $"Tier2.drop {candidate}: no_exit (dir={moveLabel})");
                    continue;
                case HopOutcomeKind.TrappedExit:
                    _log?.Log(LogSeverity.Debug, LogSource,
                        $"Tier2.drop {candidate}: trapped_exit (dir={moveLabel})");
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
            $"Tier2.step move={moveLabel} obs='{observation.Name}' depth={Depth}: {prevCount}→{_candidates.Count}");
    }

    // Spatial narrowing from a live look-sweep, WITHOUT moving the player.
    // peeked maps each direction we looked (`look <dir>`) to the neighbour
    // room's rendered observation. A candidate survives only if, for every
    // peeked direction, its graph-neighbour in that direction exists and
    // matches what the look revealed (name + exit-subset via
    // _matchesObservation). This is what breaks name-ambiguous twins that
    // Step alone can't: two identical "Darkwood Forest" rooms differ in the
    // exits of the rooms AROUND them, and the sweep reads exactly that.
    //
    // Because the player never moved, Depth is untouched (this isn't a hop).
    // A trapped neighbour is skipped, not used as a filter — we peeked
    // through a `look`, which doesn't spring the trap, but we also refuse to
    // assert the candidate is wrong on an arm we can't verify by walking, so
    // a trap simply contributes no constraint.
    public void FilterByNeighbours(IReadOnlyDictionary<Direction, RoomObservation> peeked)
    {
        ArgumentNullException.ThrowIfNull(peeked);
        int prevCount = _candidates.Count;
        if (prevCount == 0 || peeked.Count == 0) return;

        var survivors = new List<RoomKey>(prevCount);
        foreach (RoomKey candidate in _candidates)
        {
            bool consistent = true;
            foreach ((Direction dir, RoomObservation obs) in peeked)
            {
                HopOutcome hop = _probeHop(candidate, dir);
                if (hop.Kind == HopOutcomeKind.TrappedExit) continue;   // unverifiable arm — no constraint
                if (hop.Kind == HopOutcomeKind.NoExit
                    || !_matchesObservation(hop.Target, obs))
                {
                    consistent = false;
                    break;
                }
            }
            if (consistent) survivors.Add(candidate);
        }

        _candidates.Clear();
        foreach (RoomKey k in survivors) _candidates.Add(k);
        IsExhausted = _candidates.Count == 0;

        _log?.Log(LogSeverity.Debug, LogSource,
            $"Tier2.look-filter dirs={peeked.Count} depth={Depth}: {prevCount}→{_candidates.Count}");
    }

    // Blind temporal step for a dark / unseeable room: the only signal is
    // that a move command succeeded (no bonk), so hop every candidate one
    // hop in move and keep its target unconditionally — there's no room
    // display to match against. A candidate is dropped only when the move is
    // structurally impossible from it (no exit, or trap-gated). Advances
    // Depth like Step, since the player did move.
    public void StepBlind(Direction move)
        => StepBlind(move.ToString(), candidate => _probeHop(candidate, move));

    // StepBlind for a move each candidate resolves on its own (a text exit).
    public void StepBlind(string moveLabel, Func<RoomKey, HopOutcome> hopFrom)
    {
        ArgumentNullException.ThrowIfNull(hopFrom);
        int prevCount = _candidates.Count;
        if (prevCount == 0) return;

        Depth++;
        var survivors = new List<RoomKey>(prevCount);
        foreach (RoomKey candidate in _candidates)
        {
            HopOutcome hop = hopFrom(candidate);
            if (hop.Kind == HopOutcomeKind.Reached) survivors.Add(hop.Target);
        }

        _candidates.Clear();
        foreach (RoomKey k in survivors) _candidates.Add(k);
        IsExhausted = _candidates.Count == 0;

        _log?.Log(LogSeverity.Debug, LogSource,
            $"Tier2.blind-step move={moveLabel} depth={Depth}: {prevCount}→{_candidates.Count}");
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
