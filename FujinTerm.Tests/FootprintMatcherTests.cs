using System.Collections.Generic;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pure state-machine coverage for the tier-2 footprint accumulator.
/// Uses inline delegates instead of a live <c>RoomGraphManager</c> so
/// each test pins down a single behaviour — narrowing, trap drop, no-exit
/// drop, depth ceiling, mismatch-to-zero.
/// </summary>
public sealed class FootprintMatcherTests
{
    // ----- helpers ---------------------------------------------------

    private static RoomKey K(int map, int room) => new(map, room);

    /// <summary>
    /// Builds a step-probe delegate from a flat dictionary mapping
    /// (from, dir) → <see cref="HopOutcome"/>. Anything not in the map
    /// is treated as <see cref="HopOutcome.NoExit"/>.
    /// </summary>
    private static Func<RoomKey, Direction, HopOutcome> ProbeFrom(
        Dictionary<(RoomKey, Direction), HopOutcome> table)
        => (from, dir) => table.TryGetValue((from, dir), out HopOutcome o) ? o : HopOutcome.NoExit();

    /// <summary>
    /// Observation predicate that matches when the target's key is one
    /// of <paramref name="acceptedKeys"/>. Models the "this candidate's
    /// target room name+exits look like the observation" branch.
    /// </summary>
    private static Func<RoomKey, RoomObservation, bool> AcceptOnly(params RoomKey[] acceptedKeys)
        => (target, _) => Array.IndexOf(acceptedKeys, target) >= 0;

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // ----- tests -----------------------------------------------------

    [Fact]
    public void Reset_seeds_candidates_and_clears_depth()
    {
        FootprintMatcher m = new(
            probeHop: (_, _) => HopOutcome.NoExit(),
            matchesObservation: (_, _) => true);

        m.Reset(new[] { K(1, 10), K(2, 20), K(3, 30) });

        Assert.Equal(3, m.Candidates.Count);
        Assert.Equal(0, m.Depth);
        Assert.False(m.IsExhausted);
        Assert.True(m.IsActive);
    }

    [Fact]
    public void Single_step_narrows_set_to_unique_candidate()
    {
        RoomKey a = K(1, 1), b = K(1, 2), c = K(1, 3);
        RoomKey aN = K(1, 11), bN = K(1, 12), cN = K(1, 13);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.Reached(bN),
            [(c, Direction.N)] = HopOutcome.Reached(cN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(bN));
        m.Reset(new[] { a, b, c });

        m.Step(Direction.N, Obs("Tunnel"));

        Assert.True(m.IsConverged);
        Assert.Single(m.Candidates, bN);
        Assert.Equal(1, m.Depth);
    }

    [Fact]
    public void Two_steps_narrow_from_four_to_one()
    {
        RoomKey a = K(1, 1), b = K(1, 2), c = K(1, 3), d = K(1, 4);
        RoomKey aN = K(2, 1), bN = K(2, 2), cN = K(2, 3), dN = K(2, 4);
        RoomKey bE = K(3, 2), dE = K(3, 4);     // only bN/dN survive step 1; only bN→bE survives step 2

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            // Step 1: N from each
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.Reached(bN),
            [(c, Direction.N)] = HopOutcome.Reached(cN),
            [(d, Direction.N)] = HopOutcome.Reached(dN),
            // Step 2: E from the survivors
            [(bN, Direction.E)] = HopOutcome.Reached(bE),
            [(dN, Direction.E)] = HopOutcome.Reached(dE),
            // aN/cN have no east exit → dropped on step 1's observation match below
        };

        // First observation accepts only bN + dN
        var matches1 = new HashSet<RoomKey> { bN, dN };
        // Second observation accepts only bE
        var matches2 = new HashSet<RoomKey> { bE };
        int callCount = 0;
        Func<RoomKey, RoomObservation, bool> matcher = (target, _) =>
            (++callCount > 4 ? matches2 : matches1).Contains(target);
        // (calls 1-4 are step1's iter over a/b/c/d; calls 5+ are step2's iter)

        FootprintMatcher m = new(probeHop: ProbeFrom(table), matchesObservation: matcher);
        m.Reset(new[] { a, b, c, d });

        m.Step(Direction.N, Obs("Plain"));
        Assert.Equal(2, m.Candidates.Count);
        Assert.Contains(bN, m.Candidates);
        Assert.Contains(dN, m.Candidates);

        m.Step(Direction.E, Obs("Bridge"));
        Assert.True(m.IsConverged);
        Assert.Single(m.Candidates, bE);
        Assert.Equal(2, m.Depth);
    }

    [Fact]
    public void Step_drops_candidates_whose_exit_in_direction_is_trapped()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey aN = K(2, 1);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.TrappedExit(),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(aN));
        m.Reset(new[] { a, b });

        m.Step(Direction.N, Obs("Cave"));

        Assert.Single(m.Candidates, aN);
        Assert.True(m.IsConverged);
    }

    [Fact]
    public void Step_drops_candidates_with_no_exit_in_direction()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey aN = K(2, 1);

        // b has no N exit at all — not in the table
        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(aN));
        m.Reset(new[] { a, b });

        m.Step(Direction.N, Obs("Cave"));

        Assert.Single(m.Candidates, aN);
    }

    [Fact]
    public void Step_to_zero_marks_exhausted_and_clears_active()
    {
        RoomKey a = K(1, 1);
        RoomKey aN = K(2, 1);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: (_, _) => false);                   // nothing matches
        m.Reset(new[] { a });

        m.Step(Direction.N, Obs("Mismatched"));

        Assert.Empty(m.Candidates);
        Assert.True(m.IsExhausted);
        Assert.False(m.IsActive);
        Assert.False(m.IsConverged);
    }

    [Fact]
    public void Depth_ceiling_stops_marking_active_even_with_many_candidates()
    {
        FootprintMatcher m = new(
            probeHop: (from, dir) => HopOutcome.Reached(from),       // self-loop keeps candidate alive
            matchesObservation: (_, _) => true,                      // and observation always matches
            depthCeiling: 3);
        m.Reset(new[] { K(1, 1), K(1, 2), K(1, 3) });

        for (int i = 0; i < 3; i++) m.Step(Direction.N, Obs("Same"));

        Assert.Equal(3, m.Candidates.Count);
        Assert.Equal(3, m.Depth);
        Assert.False(m.IsActive);                                    // ceiling reached → not active
    }

    [Fact]
    public void Clear_drops_everything()
    {
        FootprintMatcher m = new(
            probeHop: (_, _) => HopOutcome.NoExit(),
            matchesObservation: (_, _) => true);
        m.Reset(new[] { K(1, 1), K(1, 2) });
        m.Step(Direction.N, Obs("X"));

        m.Clear();

        Assert.Empty(m.Candidates);
        Assert.Equal(0, m.Depth);
        Assert.False(m.IsExhausted);
        Assert.False(m.IsActive);
    }

    [Fact]
    public void Empty_seed_makes_matcher_inactive_immediately()
    {
        FootprintMatcher m = new(
            probeHop: (_, _) => HopOutcome.NoExit(),
            matchesObservation: (_, _) => true);
        m.Reset(Array.Empty<RoomKey>());

        Assert.False(m.IsActive);
        Assert.False(m.IsConverged);
        Assert.False(m.IsExhausted);
    }

    // ----- FilterByNeighbours (look-sweep spatial narrowing) ----------

    /// <summary>
    /// Two identical-name twins that Step alone can't separate ARE separated
    /// by a look-sweep: the peeked neighbour in a direction matches only one
    /// twin's graph-neighbour, dropping the other.
    /// </summary>
    [Fact]
    public void FilterByNeighbours_breaks_twins_by_peeked_neighbour()
    {
        RoomKey a = K(1, 1), b = K(1, 2);        // twins: same name+exits
        RoomKey aN = K(1, 11), bN = K(1, 12);    // but different neighbours to the north

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.Reached(bN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(aN));  // the look north looks like aN only
        m.Reset(new[] { a, b });

        m.FilterByNeighbours(new Dictionary<Direction, RoomObservation>
        {
            [Direction.N] = Obs("Clearing"),
        });

        Assert.True(m.IsConverged);
        Assert.Single(m.Candidates, a);
        Assert.Equal(0, m.Depth);                 // a look is not a move
    }

    /// <summary>
    /// A candidate that has no exit in a direction we successfully peeked is
    /// inconsistent (the real room does have that exit) and is dropped.
    /// </summary>
    [Fact]
    public void FilterByNeighbours_drops_candidate_missing_the_peeked_exit()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey aN = K(1, 11);

        // b has no N exit at all (absent from the table → NoExit).
        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(aN));
        m.Reset(new[] { a, b });

        m.FilterByNeighbours(new Dictionary<Direction, RoomObservation>
        {
            [Direction.N] = Obs("Clearing"),
        });

        Assert.Single(m.Candidates, a);
    }

    /// <summary>
    /// A trapped arm is unverifiable, so it contributes no constraint — a
    /// candidate is NOT dropped merely because the peeked direction is a trap
    /// on it, even when its target wouldn't match.
    /// </summary>
    [Fact]
    public void FilterByNeighbours_trapped_arm_is_no_constraint()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey aN = K(1, 11);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.TrappedExit(),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: AcceptOnly(aN));   // would reject b's arm if it were verifiable
        m.Reset(new[] { a, b });

        m.FilterByNeighbours(new Dictionary<Direction, RoomObservation>
        {
            [Direction.N] = Obs("Clearing"),
        });

        Assert.Equal(2, m.Candidates.Count);       // b kept — its only peeked arm was a trap
        Assert.Contains(a, m.Candidates);
        Assert.Contains(b, m.Candidates);
    }

    /// <summary>
    /// An empty peek map (nothing resolved — e.g. every look timed out) is a
    /// no-op: the candidate set is untouched.
    /// </summary>
    [Fact]
    public void FilterByNeighbours_empty_peek_is_noop()
    {
        FootprintMatcher m = new(
            probeHop: (_, _) => HopOutcome.NoExit(),
            matchesObservation: (_, _) => false);
        m.Reset(new[] { K(1, 1), K(1, 2) });

        m.FilterByNeighbours(new Dictionary<Direction, RoomObservation>());

        Assert.Equal(2, m.Candidates.Count);
    }

    // ----- StepBlind (dark dead-reckon) -------------------------------

    /// <summary>
    /// In the dark there's no room to match, so StepBlind hops every
    /// candidate and keeps its target unconditionally — even when the
    /// observation predicate would reject all of them.
    /// </summary>
    [Fact]
    public void StepBlind_hops_without_observation_filter()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey aN = K(2, 1), bN = K(2, 2);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(aN),
            [(b, Direction.N)] = HopOutcome.Reached(bN),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: (_, _) => false);  // would kill everything under Step
        m.Reset(new[] { a, b });

        m.StepBlind(Direction.N);

        Assert.Equal(2, m.Candidates.Count);
        Assert.Contains(aN, m.Candidates);
        Assert.Contains(bN, m.Candidates);
        Assert.Equal(1, m.Depth);                  // a blind move still advances depth
        Assert.False(m.IsExhausted);
    }

    /// <summary>
    /// StepBlind still drops a candidate the move is structurally impossible
    /// from (no exit / trap-gated).
    /// </summary>
    [Fact]
    public void StepBlind_drops_no_exit_and_trapped()
    {
        RoomKey a = K(1, 1), b = K(1, 2);

        // a has no N exit (absent → NoExit); b's N is trapped.
        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(b, Direction.N)] = HopOutcome.TrappedExit(),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: (_, _) => true);
        m.Reset(new[] { a, b });

        m.StepBlind(Direction.N);

        Assert.Empty(m.Candidates);
        Assert.True(m.IsExhausted);
    }

    /// <summary>
    /// Two candidates that dead-reckon to the same room collapse to one —
    /// blind movement alone can converge when paths merge.
    /// </summary>
    [Fact]
    public void StepBlind_converges_when_paths_merge()
    {
        RoomKey a = K(1, 1), b = K(1, 2);
        RoomKey shared = K(2, 9);

        var table = new Dictionary<(RoomKey, Direction), HopOutcome>
        {
            [(a, Direction.N)] = HopOutcome.Reached(shared),
            [(b, Direction.N)] = HopOutcome.Reached(shared),
        };

        FootprintMatcher m = new(
            probeHop: ProbeFrom(table),
            matchesObservation: (_, _) => true);
        m.Reset(new[] { a, b });

        m.StepBlind(Direction.N);

        Assert.True(m.IsConverged);
        Assert.Single(m.Candidates, shared);
    }
}
