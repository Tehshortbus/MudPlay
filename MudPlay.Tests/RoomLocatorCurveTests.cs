using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using Xunit.Abstractions;

namespace MudPlay.Tests;

/// <summary>
/// Re-derives the localizer's resolution curve against a real, installed
/// game-data set. For every room in the graph: seed a candidate set from
/// what that room's own display would show, then drive narrowing through a
/// real <see cref="FootprintMatcher"/> — the same accumulator, wired with
/// the same two delegates, that <c>EngineRecoveryGate</c> uses in
/// production — repeatedly asking <see cref="RoomLocator.ChooseSplittingExit"/>
/// which exit to take, advancing the true room and every surviving
/// candidate through it, and letting the matcher's own step rule decide who
/// survives (which includes refusing to walk a trap, exactly like
/// production). Records how many steps it took to converge on one
/// candidate, capped at <see cref="RoomLocator.DefaultBudget"/>.
/// </summary>
/// <remarks>
/// Pure graph simulation — no wire, no <c>LocatorWalk</c>, no sender. Game
/// data lives outside the repo (<c>~/.local/share/MudPlay/game data/</c>),
/// so this must pass cleanly with no assertions when none is installed —
/// it is a measurement, not a gate, and a clean checkout must stay green.
///
/// Measures TWO seeding strategies side by side, since <see cref="RoomLocator.Seed"/>
/// itself is the thing under review: "Option 2" is the shipped
/// <see cref="RoomLocator.Seed"/> (its own displayed-mask index, falling
/// back to the graph's superset search); "Option 1" is the always-superset
/// alternative (<c>RoomGraphManager.FindByNameCoveringExits</c> alone, no
/// exact bucket at all). Both are asserted to never seed a room that
/// excludes the room actually being observed — the defect this exists to
/// catch — regardless of which one ships.
/// </remarks>
public sealed class RoomLocatorCurveTests
{
    private readonly ITestOutputHelper _output;

    public RoomLocatorCurveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Curve_clears_the_design_bounds_on_installed_game_data()
    {
        GameDataCache cache = new();
        IReadOnlyList<string> sets = cache.AvailableSets;
        if (sets.Count == 0)
        {
            _output.WriteLine($"No game-data set installed under '{cache.GameDataRoot}' — skipping.");
            return;
        }

        string setName = sets[0];
        cache.SwitchSet(setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(setName);

        if (graph.RoomCount == 0)
        {
            _output.WriteLine($"Set '{setName}' loaded but carries no rooms — skipping.");
            return;
        }

        int rawRowCount = CountRawRows(cache.GameDataRoot, setName);
        int rejectedOnImport = Math.Max(0, rawRowCount - graph.RoomCount);
        int namelessExcluded = graph.Rooms.Count(r => r.HasUnknownName);

        RoomLocator locator = new(graph);
        int budget = RoomLocator.DefaultBudget;

        // Same two delegates EngineRecoveryGate wires into FootprintMatcher
        // for tier-2 narrowing — reusing the production narrowing rule
        // instead of a hand-rolled lookalike, trap-drop included.
        FootprintMatcher matcher = new(
            probeHop: (from, dir) => ProbeHop(graph, from, dir),
            matchesObservation: (key, obs) => KeyMatchesObservation(graph, key, obs));

        _output.WriteLine($"Set '{setName}': {graph.RoomCount} room(s) in graph, {rawRowCount} raw row(s) " +
            $"({rejectedOnImport} rejected on import — bad/duplicate keys), {namelessExcluded} excluded " +
            "(no name).");
        _output.WriteLine("Known limitation: \"Hidden/Passable\" and \"Hidden/Passage\" exits import as " +
            "Hint.None (indistinguishable from an ordinary exit — see RoomExit.ClassifyHint), so they are " +
            "still counted as visible here even though the board never lists them. Both curves below are a " +
            "slight OVER-estimate for any room carrying one; this is not an exact prediction.");

        Stopwatch stopwatch = Stopwatch.StartNew();

        SweepResult shipped = RunSweep(_output, "Option 2 (shipped — RoomLocator's own displayed-mask index)",
            graph, locator, matcher, locator.Seed, budget);
        SweepResult alwaysSuperset = RunSweep(_output, "Option 1 (always superset — FindByNameCoveringExits)",
            graph, locator, matcher, obs => graph.FindByNameCoveringExits(obs.Name, obs.Exits), budget);

        stopwatch.Stop();
        _output.WriteLine($"Wall clock (both sweeps): {stopwatch.Elapsed.TotalSeconds:F1}s.");

        // The regression this whole round exists to pin down: neither
        // seeding strategy may ever exclude the room the character is
        // actually standing in from its own candidate set. That is not
        // "imprecise" — it is confidently wrong, worse than failing to
        // localize at all, and it must never come back silently.
        Assert.True(shipped.SelfExcluded == 0,
            $"Option 2 (shipped) self-excluded {shipped.SelfExcluded} room(s) from their own seed.");
        Assert.True(alwaysSuperset.SelfExcluded == 0,
            $"Option 1 (always superset) self-excluded {alwaysSuperset.SelfExcluded} room(s) from their own seed.");

        // The curve the design rests on, measured against the shipped Seed.
        // Deliberately loose — measured on this set was ~18.5% / ~44.5% /
        // ~84.0% at 0/1/12 steps (see the printed numbers above for the
        // exact run) — so an unrelated change to graph loading or the room
        // mix doesn't flip the suite red; only a splitting rule that's
        // actually broken (picks arbitrarily, or never narrows) should
        // trip these.
        Assert.True(shipped.ResolvedFraction[0] >= 0.10, $"0 steps: {shipped.ResolvedFraction[0]:P1}");
        Assert.True(shipped.ResolvedFraction[1] >= 0.30, $"1 step:  {shipped.ResolvedFraction[1]:P1}");
        Assert.True(shipped.ResolvedFraction[budget] >= 0.60, $"{budget} steps: {shipped.ResolvedFraction[budget]:P1}");
    }

    // Aggregate outcome of sweeping every named room in the graph under one
    // seeding strategy: how many were measured at all (excluding the ones
    // the strategy self-excludes), the cumulative resolved fraction at each
    // step 0..budget, and how many walks lost track of the true room to a
    // trap mid-walk (a real outcome, not a bug in this harness — see
    // FootprintMatcher.Step's "we don't traverse traps" rule).
    private readonly record struct SweepResult(
        int Measured,
        int SelfExcluded,
        int LostToTrap,
        double[] ResolvedFraction,
        int NeverResolved);

    private static SweepResult RunSweep(
        ITestOutputHelper output,
        string label,
        RoomGraphManager graph,
        RoomLocator locator,
        FootprintMatcher matcher,
        Func<RoomObservation, IReadOnlyList<RoomKey>> seedFn,
        int budget)
    {
        int[] convergedAtStep = new int[budget + 1];
        int neverResolved = 0;
        int selfExcluded = 0;
        int lostToTrap = 0;
        int measured = 0;

        foreach (Room room in graph.Rooms)
        {
            if (room.HasUnknownName) continue;

            RoomObservation seedObservation = Observe(room);
            IReadOnlyList<RoomKey> seed = seedFn(seedObservation);
            if (!seed.Contains(room.Key))
            {
                selfExcluded++;
                continue;
            }

            measured++;
            (int? step, bool lostTrack) = WalkOne(locator, graph, matcher, room, seed, budget);
            if (lostTrack) lostToTrap++;
            if (step is { } s) convergedAtStep[s]++;
            else neverResolved++;
        }

        double[] resolvedFraction = new double[budget + 1];
        int cumulative = 0;
        output.WriteLine($"[{label}]");
        output.WriteLine($"  measured {measured}, self-excluded {selfExcluded}, " +
            $"lost the true room to a trap mid-walk {lostToTrap}.");
        for (int step = 0; step <= budget; step++)
        {
            cumulative += convergedAtStep[step];
            resolvedFraction[step] = measured == 0 ? 0.0 : (double)cumulative / measured;
            output.WriteLine($"    {step,2} step(s): {resolvedFraction[step]:P1} resolved ({cumulative}/{measured})");
        }
        double neverFraction = measured == 0 ? 0.0 : (double)neverResolved / measured;
        output.WriteLine($"    never resolved within {budget} steps: {neverResolved} ({neverFraction:P1})");

        return new SweepResult(measured, selfExcluded, lostToTrap, resolvedFraction, neverResolved);
    }

    // Steps taken until the matcher settles on exactly one candidate AND
    // that candidate is the room actually being walked, or null when the
    // budget runs out (or nothing usable is left to move on) first.
    // LostTrack is true when the true room's own candidate entry gets
    // dropped by a Step — under FootprintMatcher's real rule that can only
    // happen because the direction taken is a trapped exit on the true
    // room itself (a plain mismatch is structurally impossible here: the
    // next observation is built directly from the true room's own next
    // display, so it always matches the true room's own next candidate).
    // Once that happens the walk stops rather than continue steering by a
    // candidate set that no longer contains the truth.
    private static (int? Step, bool LostTrack) WalkOne(
        RoomLocator locator, RoomGraphManager graph, FootprintMatcher matcher,
        Room trueRoom, IReadOnlyList<RoomKey> seed, int budget)
    {
        matcher.Reset(seed);
        if (matcher.Candidates.Count == 1) return (0, false);

        Room current = trueRoom;
        for (int step = 1; step <= budget; step++)
        {
            RoomObservation here = Observe(current);
            Direction? direction = locator.ChooseSplittingExit(matcher.Candidates, here);
            if (direction is not { } dir) break;

            RoomExit trueExit = current.Exits[dir];
            Room? nextTrue = graph.GetRoom(trueExit.Target);
            if (nextTrue is null) break;
            RoomObservation nextObservation = Observe(nextTrue);

            matcher.Step(dir, nextObservation);
            current = nextTrue;

            if (!matcher.Candidates.Contains(current.Key)) return (null, true);
            if (matcher.Candidates.Count == 1) return (step, false);
        }

        return (null, false);
    }

    // Mirrors EngineRecoveryGate.ProbeHop exactly: a trapped exit is
    // reported as TrappedExit, which FootprintMatcher.Step drops
    // unconditionally — "we don't traverse traps" — rather than walked
    // like a plain exit.
    private static HopOutcome ProbeHop(RoomGraphManager graph, RoomKey from, Direction dir)
    {
        Room? source = graph.GetRoom(from);
        if (source is null) return HopOutcome.NoExit();
        if (!source.Exits.TryGetValue(dir, out RoomExit exit)) return HopOutcome.NoExit();
        if (exit.Hint == RoomExitHint.Trap) return HopOutcome.TrappedExit();
        return HopOutcome.Reached(exit.Target);
    }

    // Mirrors EngineRecoveryGate.KeyMatchesObservation exactly: name agrees
    // and every exit the observation carries is present on the room — the
    // room's own mask may still carry extra bits the observation can't see
    // (a closed door, an unsearched hidden exit).
    private static bool KeyMatchesObservation(RoomGraphManager graph, RoomKey key, RoomObservation observation)
    {
        Room? room = graph.GetRoom(key);
        if (room is null) return false;
        if (!string.Equals(room.Name, observation.Name, StringComparison.OrdinalIgnoreCase)) return false;
        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;
        return (observedMask & room.ExitMask) == observedMask;
    }

    // What the board's "Obvious exits:" line actually prints for room, not
    // every exit the graph carries: a SearchableHidden or MultiActionHidden
    // exit doesn't appear on that line at all (see RoomExitHint), and a Text
    // exit occupies a direction slot for the graph's own bookkeeping but
    // crosses via a typed command rather than showing as a listed compass
    // direction.
    //
    // Known gap, deliberately not chased here: RoomExit.TryParseWire folds
    // "Hidden/Passable" and "Hidden/Passage" into Hint.None, indistinguishable
    // from an ordinary exit (see RoomExit.cs ClassifyHint) — those still
    // leak through as "visible" below, so the measured curve is a slight
    // over-estimate for any room carrying one. Not detectable without
    // changing RoomExit's own classification, which this task doesn't touch.
    private static RoomObservation Observe(Room room)
    {
        HashSet<Direction> visible = new();
        foreach ((Direction dir, RoomExit exit) in room.Exits)
        {
            if (exit.Hint is RoomExitHint.SearchableHidden or RoomExitHint.MultiActionHidden or RoomExitHint.Text)
                continue;
            visible.Add(dir);
        }
        return new RoomObservation(room.Name, visible);
    }

    // Row count straight off Rooms.json, ahead of RoomGraphManager's import
    // filter — the gap against graph.RoomCount is rows TryReadRoom rejected
    // (missing/invalid Map or Room Number) plus any duplicate (Map, Room)
    // keys a later row overwrote.
    private static int CountRawRows(string gameDataRoot, string setName)
    {
        string path = Path.Combine(gameDataRoot, setName, "Rooms.json");
        if (!File.Exists(path)) return 0;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetArrayLength();
    }
}
