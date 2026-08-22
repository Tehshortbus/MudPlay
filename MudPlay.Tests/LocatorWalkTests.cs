using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The send/land pump: it must send nothing when the first display already
/// settles the question, converge on a unique candidate, and report an
/// honest count when the rooms are genuinely indistinguishable.
/// </summary>
public sealed class LocatorWalkTests : IDisposable
{
    private readonly string _root;

    public LocatorWalkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-locatorwalk-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), FixtureGraph);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // Fixture graph, map 1 throughout:
    //   1/1  "Narrow Road" (N,S) — unique by (name, exit-set); settles on
    //        the first display with no move needed. N/S deliberately point
    //        at real graph rooms (not dangling) so a mutant that checks
    //        for a splitting exit before convergence would actually find
    //        one and send it, making that mutation observable.
    //   1/20, 1/21 "Twin Hall" (N,E) — north leads to two "Hall" rooms with
    //        identical (name, exit-set) (1 shape); east leads to
    //        "Larder"/"Cellar" (2 shapes), so east is the splitting exit.
    //   1/10 "Larder" (W) — 1/20's east target; the only one of the pair
    //        whose name matches the landing display, so it's what the walk
    //        converges on.
    //   1/11 "Cellar" — 1/21's east target; dropped on the landing
    //        observation mismatch.
    //   1/30, 1/31 "Hall" — north targets of 1/20 and 1/21; same (name,
    //        exit-set), so north carries no discriminating information.
    //   1/40, 1/41 "Long Corridor" (N) — each is a self-loop on N,  so the
    //        pair never narrows and never converges; a budget of 2 must
    //        exhaust with both still standing.
    private const string FixtureGraph = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Narrow Road",
            "N": "1/30", "S": "1/31", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 20, "Name": "Twin Hall",
            "N": "1/30", "S": "0", "E": "1/10", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Twin Hall",
            "N": "1/31", "S": "0", "E": "1/11", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 30, "Name": "Hall",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 31, "Name": "Hall",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 10, "Name": "Larder",
            "N": "0", "S": "0", "E": "0", "W": "1/905",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Cellar",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 40, "Name": "Long Corridor",
            "N": "1/40", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 41, "Name": "Long Corridor",
            "N": "1/41", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Builds a walk over the fixture graph, wiring FootprintMatcher's probes
    // straight off RoomGraphManager the same way EngineRecoveryGate does
    // (ProbeHop / KeyMatchesObservation) — the only shape production code
    // for this wiring takes.
    private LocatorWalk BuildWalk(List<Direction> sent, int budget = RoomLocator.DefaultBudget)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        var locator = new RoomLocator(graph);
        var matcher = new FootprintMatcher(
            probeHop: (from, dir) =>
            {
                Room? source = graph.GetRoom(from);
                if (source is null || !source.Exits.TryGetValue(dir, out RoomExit exit))
                    return HopOutcome.NoExit();
                return exit.Hint == RoomExitHint.Trap ? HopOutcome.TrappedExit() : HopOutcome.Reached(exit.Target);
            },
            matchesObservation: (key, obs) =>
            {
                Room? r = graph.GetRoom(key);
                if (r is null) return false;
                if (!string.Equals(r.Name, obs.Name, StringComparison.OrdinalIgnoreCase)) return false;
                uint observedMask = 0;
                foreach (Direction d in obs.Exits) observedMask |= 1u << (int)d;
                return (observedMask & r.ExitMask) == observedMask;
            });

        return new LocatorWalk(locator, matcher, sent.Add, budget);
    }

    [Fact]
    public void Begin_sends_nothing_when_one_display_already_settles_it()
    {
        var sent = new List<Direction>();
        LocatorWalk walk = BuildWalk(sent);

        LocateOutcome? outcome = walk.Begin(Obs("Narrow Road", Direction.N, Direction.S));

        Assert.Empty(sent);
        Assert.Equal(LocateOutcomeKind.Converged, outcome!.Value.Kind);
        Assert.Equal(new RoomKey(1, 1), outcome.Value.Room);
        Assert.Equal(0, outcome.Value.Steps);
    }

    [Fact]
    public void Begin_reports_unknown_when_the_graph_has_no_such_room()
    {
        var sent = new List<Direction>();
        LocatorWalk walk = BuildWalk(sent);

        LocateOutcome? outcome = walk.Begin(Obs("Nowhere At All", Direction.N));

        Assert.Empty(sent);
        Assert.Equal(LocateOutcomeKind.Unknown, outcome!.Value.Kind);
    }

    [Fact]
    public void A_landing_that_narrows_to_one_converges()
    {
        var sent = new List<Direction>();
        LocatorWalk walk = BuildWalk(sent);

        LocateOutcome? first = walk.Begin(Obs("Twin Hall", Direction.N, Direction.E));
        Assert.Null(first);                       // ambiguous — a move went out
        Assert.Equal(new[] { Direction.E }, sent);

        LocateOutcome? done = walk.OnLanding(Obs("Larder", Direction.W));

        Assert.Equal(LocateOutcomeKind.Converged, done!.Value.Kind);
        Assert.Equal(new RoomKey(1, 10), done.Value.Room);
        Assert.Equal(1, done.Value.Steps);
    }

    [Fact]
    public void An_exhausted_budget_reports_how_many_rooms_remain()
    {
        var sent = new List<Direction>();
        LocatorWalk walk = BuildWalk(sent, budget: 2);

        walk.Begin(Obs("Long Corridor", Direction.N));
        walk.OnLanding(Obs("Long Corridor", Direction.N));
        LocateOutcome? outcome = walk.OnLanding(Obs("Long Corridor", Direction.N));

        Assert.Equal(LocateOutcomeKind.Ambiguous, outcome!.Value.Kind);
        Assert.True(outcome.Value.CandidateCount > 1);
        Assert.Equal(2, outcome.Value.Steps);
    }
}
