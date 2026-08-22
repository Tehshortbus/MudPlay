using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using Xunit.Abstractions;

namespace MudPlay.Tests;

/// <summary>
/// Re-derives the localizer's resolution curve against a real, installed
/// game-data set. For every room in the graph: seed a candidate set from
/// what that room's own display would show, then repeatedly apply
/// <see cref="RoomLocator.ChooseSplittingExit"/> to both the true room and
/// every surviving candidate, advancing each one hop and keeping only the
/// candidates whose destination still matches the true room's new display.
/// Records how many steps it took to converge on one candidate, capped at
/// <see cref="RoomLocator.DefaultBudget"/>.
/// </summary>
/// <remarks>
/// Pure graph simulation — no wire, no <c>LocatorWalk</c>, no sender. Game
/// data lives outside the repo (<c>~/.local/share/MudPlay/game data/</c>),
/// so this must pass cleanly with no assertions when none is installed —
/// it is a measurement, not a gate, and a clean checkout must stay green.
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

        RoomLocator locator = new(graph);
        int budget = RoomLocator.DefaultBudget;

        int[] convergedAtStep = new int[budget + 1];
        int neverResolved = 0;
        int namelessExcluded = 0;
        int measured = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        foreach (Room room in graph.Rooms)
        {
            // A nameless room (HasUnknownName) is an import gap, not a real
            // display — the live game always shows some title, so measuring
            // against an empty-string observation would misrepresent both
            // this room's own resolvability and any sibling bucket it would
            // otherwise pollute.
            if (room.HasUnknownName)
            {
                namelessExcluded++;
                continue;
            }

            measured++;
            int? step = MeasureConvergence(locator, graph, room, budget);
            if (step is { } s) convergedAtStep[s]++;
            else neverResolved++;
        }

        stopwatch.Stop();

        _output.WriteLine($"Set '{setName}': {graph.RoomCount} room(s) in graph, {rawRowCount} raw row(s) " +
            $"({rejectedOnImport} rejected on import — bad/duplicate keys), {namelessExcluded} excluded " +
            $"(no name), {measured} measured.");
        _output.WriteLine("Known limitation: \"Hidden/Passable\" and \"Hidden/Passage\" exits import as " +
            "Hint.None (indistinguishable from an ordinary exit — see RoomExit.ClassifyHint), so they are " +
            "still counted as visible here even though the board never lists them. The curve below is a " +
            "slight OVER-estimate for any room carrying one; this is not an exact prediction.");
        _output.WriteLine($"Wall clock: {stopwatch.Elapsed.TotalSeconds:F1}s.");

        double[] resolvedFraction = new double[budget + 1];
        int cumulative = 0;
        for (int step = 0; step <= budget; step++)
        {
            cumulative += convergedAtStep[step];
            resolvedFraction[step] = measured == 0 ? 0.0 : (double)cumulative / measured;
            _output.WriteLine($"  {step,2} step(s): {resolvedFraction[step]:P1} resolved ({cumulative}/{measured})");
        }
        double neverFraction = measured == 0 ? 0.0 : (double)neverResolved / measured;
        _output.WriteLine($"  never resolved within {budget} steps: {neverResolved} ({neverFraction:P1})");

        // The curve the design rests on. Deliberately loose — measured on
        // this set was ~18.4% / ~44.3% / ~84.0% at 0/1/12 steps, and these
        // bounds sit well under every one of those so an unrelated change to
        // graph loading or the room mix doesn't flip the suite red; only a
        // splitting rule that's actually broken (picks arbitrarily, or never
        // narrows) should trip them.
        Assert.True(resolvedFraction[0] >= 0.10, $"0 steps: {resolvedFraction[0]:P1}");
        Assert.True(resolvedFraction[1] >= 0.30, $"1 step:  {resolvedFraction[1]:P1}");
        Assert.True(resolvedFraction[12] >= 0.65, $"12 steps: {resolvedFraction[12]:P1}");
    }

    // Steps taken until exactly one candidate survives, or null when the
    // budget runs out (or nothing usable is left to move on) with more than
    // one candidate still standing. The true room's own key is provably
    // never dropped: ChooseSplittingExit only offers a direction usable by
    // EVERY candidate, and the true room is always a candidate of itself, so
    // its own hop always resolves and its own destination always matches its
    // own next display. A dangling exit off the true room therefore never
    // strands this walk — it just disqualifies that direction from being
    // chosen at all.
    private static int? MeasureConvergence(RoomLocator locator, RoomGraphManager graph, Room trueRoom, int budget)
    {
        RoomObservation seedObservation = Observe(trueRoom);
        IReadOnlyList<RoomKey> seed = locator.Seed(seedObservation);
        if (seed.Count <= 1) return seed.Count == 1 ? 0 : null;

        HashSet<RoomKey> candidates = new(seed);
        Room current = trueRoom;

        for (int step = 1; step <= budget; step++)
        {
            RoomObservation here = Observe(current);
            Direction? direction = locator.ChooseSplittingExit(candidates, here);
            if (direction is not { } dir) break;

            RoomExit trueExit = current.Exits[dir];
            Room? nextTrue = graph.GetRoom(trueExit.Target);
            if (nextTrue is null) break;
            RoomObservation nextObservation = Observe(nextTrue);

            HashSet<RoomKey> survivors = new();
            foreach (RoomKey candidate in candidates)
            {
                Room? source = graph.GetRoom(candidate);
                if (source is null || !source.Exits.TryGetValue(dir, out RoomExit exit)) continue;
                Room? destination = graph.GetRoom(exit.Target);
                if (destination is null) continue;
                if (Matches(destination, nextObservation)) survivors.Add(exit.Target);
            }

            candidates = survivors;
            current = nextTrue;
            if (candidates.Count == 1) return step;
            if (candidates.Count == 0) return null;
        }

        return null;
    }

    // What the board's "Obvious exits:" line actually prints for room, not
    // every exit the graph carries: a SearchableHidden or MultiActionHidden
    // exit doesn't appear on that line at all (see RoomExitHint), and a Text
    // exit occupies a direction slot for the graph's own bookkeeping but
    // crosses via a typed command rather than showing as a listed compass
    // direction. Dropping those means a room carrying one seeds through the
    // superset fallback in RoomLocator.Seed exactly as a live walk would —
    // the exact (Name, ExitMask) bucket misses because the observed mask is
    // now a strict subset of the room's true mask.
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

    // Door-tolerant match: name agrees and every exit the observation
    // carries is present on the room, mirroring the production
    // matchesObservation rule (see EngineRecoveryGate.KeyMatchesObservation)
    // so this simulation narrows exactly the way a live walk would.
    private static bool Matches(Room room, RoomObservation observation)
    {
        if (!string.Equals(room.Name, observation.Name, StringComparison.OrdinalIgnoreCase)) return false;
        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;
        return (observedMask & room.ExitMask) == observedMask;
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
