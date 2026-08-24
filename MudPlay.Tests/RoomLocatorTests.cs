using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Seeding and splitting-exit choice for the history-free localizer.
/// </summary>
public sealed class RoomLocatorTests : IDisposable
{
    private readonly string _root;

    public RoomLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-locator-tests-" + Path.GetRandomFileName());
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

    private RoomLocator BuildLocator()
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomLocator(graph);
    }

    // Fixture graph, map 1 throughout:
    //   1/1  "Narrow Road" — the only room with exits N,S under that name.
    //   1/2  "Shut Gate" — graph carries N,S,E; a closed door hides E from
    //        the display, so the exact bucket for {N,S} is empty and only
    //        the superset reading finds it.
    //   1/10, 1/11 "Twin Hall" (N,E) — north leads to two "Hall" rooms with
    //        identical exits (1 shape); east leads to "Larder"/"Cellar"
    //        (2 shapes), so east should be preferred.
    //   1/12 "Twin Hall" (N only) — paired with 1/10, 1/11 against {N,E}:
    //        east must be marked unusable (1/12 lacks it), not silently
    //        scored off the other two, which would wrongly outrank north.
    //   1/20, 1/21 "Long Corridor" (N) — both north neighbours are the same
    //        shape, but north is still the only usable, hence chosen, exit.
    //   1/30, 1/31 "Crossing" (N,E) — north and east both split the pair
    //        into 2 distinct shapes; north wins the tie by compass order.
    //   1/40, 1/41 "Dead End" — no exits at all; nothing usable to move on.
    //   1/50, 1/51 "Cache" (N) — 1/50's N target (1/950) is absent from the
    //        graph, so GetRoom resolves it to null; north must come back
    //        unusable, not merely low-scoring.
    //   1/60 "Twin Gate" (N,S) and 1/61 "Twin Gate" (N,S,E) — same name,
    //        1/61's mask is a strict superset of 1/60's. Observing {N,S}
    //        must resolve to 1/60 alone via the exact bucket, never widen
    //        to include 1/61.
    //   1/70, 1/71 "Hidden Alley" — 1/70 has a plain N exit only; 1/71 has
    //        N plus a hidden E, so its displayed mask ({N}) is a strict
    //        subset of its own graph mask ({N,E}) and happens to collide
    //        with 1/70's. Observing {N} at 1/71 must still return 1/71 —
    //        an index keyed on the full graph mask would bucket 1/71 under
    //        {N,E} instead, so the exact lookup for {N} would hit only
    //        1/70 and silently exclude the room actually being observed.
    private const string FixtureGraph = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Narrow Road",
            "N": "1/900", "S": "1/901", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Shut Gate",
            "N": "1/902", "S": "1/903", "E": "1/904 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 10, "Name": "Twin Hall",
            "N": "1/100", "S": "0", "E": "1/102", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Twin Hall",
            "N": "1/101", "S": "0", "E": "1/103", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Twin Hall",
            "N": "1/100", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 100, "Name": "Hall",
            "N": "0", "S": "1/905", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 101, "Name": "Hall",
            "N": "0", "S": "1/906", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 102, "Name": "Larder",
            "N": "0", "S": "1/907", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 103, "Name": "Cellar",
            "N": "0", "S": "1/908", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 20, "Name": "Long Corridor",
            "N": "1/104", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Long Corridor",
            "N": "1/105", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 104, "Name": "Empty Room",
            "N": "0", "S": "1/909", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 105, "Name": "Empty Room",
            "N": "0", "S": "1/910", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 30, "Name": "Crossing",
            "N": "1/106", "S": "0", "E": "1/108", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 31, "Name": "Crossing",
            "N": "1/107", "S": "0", "E": "1/109", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 106, "Name": "West Room",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 107, "Name": "East Room",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 108, "Name": "North Field",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 109, "Name": "South Field",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 40, "Name": "Dead End",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 41, "Name": "Dead End",
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 50, "Name": "Cache",
            "N": "1/950", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 51, "Name": "Cache",
            "N": "1/100", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 60, "Name": "Twin Gate",
            "N": "1/911", "S": "1/912", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 61, "Name": "Twin Gate",
            "N": "1/913", "S": "1/914", "E": "1/915", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 1, "Room Number": 70, "Name": "Hidden Alley",
            "N": "1/916", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 71, "Name": "Hidden Alley",
            "N": "1/917", "S": "0", "E": "1/918 (Hidden)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Seed_returns_the_exact_name_and_exit_match()
    {
        RoomLocator locator = BuildLocator();

        IReadOnlyList<RoomKey> seeded = locator.Seed(Obs("Narrow Road", Direction.N, Direction.S));

        Assert.Equal(new[] { new RoomKey(1, 1) }, seeded);
    }

    [Fact]
    public void Seed_widens_to_the_superset_match_when_exact_finds_nothing()
    {
        RoomLocator locator = BuildLocator();

        IReadOnlyList<RoomKey> seeded = locator.Seed(Obs("Shut Gate", Direction.N, Direction.S));

        Assert.Equal(new[] { new RoomKey(1, 2) }, seeded);
    }

    [Fact]
    public void Seed_prefers_the_exact_bucket_over_the_superset_when_both_would_match()
    {
        // 1/61's mask is a strict superset of 1/60's under the same name, so
        // a Seed that widened unconditionally (or before trying the exact
        // bucket) would admit both. The exact bucket alone holds only 1/60.
        RoomLocator locator = BuildLocator();

        IReadOnlyList<RoomKey> seeded = locator.Seed(Obs("Twin Gate", Direction.N, Direction.S));

        Assert.Equal(new[] { new RoomKey(1, 60) }, seeded);
    }

    [Fact]
    public void Seed_includes_a_room_with_a_hidden_exit_even_when_its_full_mask_collides_with_a_twin()
    {
        // Regression: an index keyed on the graph's FULL exit mask buckets
        // 1/71 (N + hidden E) under {N,E}, so observing what the board
        // actually shows at 1/71 — {N} — would land in 1/70's bucket alone
        // and silently exclude the room being observed.
        RoomLocator locator = BuildLocator();

        IReadOnlyList<RoomKey> seeded = locator.Seed(Obs("Hidden Alley", Direction.N));

        Assert.Contains(new RoomKey(1, 71), seeded);
    }

    [Fact]
    public void ChooseSplittingExit_prefers_the_direction_with_the_most_distinct_neighbours()
    {
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 10), new RoomKey(1, 11) },
            Obs("Twin Hall", Direction.N, Direction.E));

        Assert.Equal(Direction.E, chosen);
    }

    [Fact]
    public void ChooseSplittingExit_skips_a_direction_a_candidate_lacks()
    {
        // 1/10 and 1/11 both have east leading to a differently-named room
        // (2 shapes) — enough to outrank north (1 shape) if east's missing
        // candidate (1/12) were silently dropped instead of disqualifying
        // the whole direction. Only marking east unusable returns north.
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 10), new RoomKey(1, 11), new RoomKey(1, 12) },
            Obs("Twin Hall", Direction.N, Direction.E));

        Assert.Equal(Direction.N, chosen);
    }

    [Fact]
    public void ChooseSplittingExit_still_moves_when_no_direction_splits_anything()
    {
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 20), new RoomKey(1, 21) },
            Obs("Long Corridor", Direction.N));

        Assert.Equal(Direction.N, chosen);
    }

    [Fact]
    public void ChooseSplittingExit_breaks_ties_in_compass_order()
    {
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 30), new RoomKey(1, 31) },
            Obs("Crossing", Direction.N, Direction.E));

        Assert.Equal(Direction.N, chosen);
    }

    [Fact]
    public void ChooseSplittingExit_returns_null_when_no_listed_exit_is_usable()
    {
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 40), new RoomKey(1, 41) },
            Obs("Dead End"));

        Assert.Null(chosen);
    }

    [Fact]
    public void ChooseSplittingExit_returns_null_when_a_listed_exits_target_is_unresolved()
    {
        // North is the only listed exit and both candidates have it, but
        // 1/50's N target (1/950) isn't in the graph — GetRoom(exit.Target)
        // resolves to null, so north must come back unusable rather than
        // being scored (this is the branch the empty-Exits "Dead End" case
        // above never reaches).
        RoomLocator locator = BuildLocator();

        Direction? chosen = locator.ChooseSplittingExit(
            new[] { new RoomKey(1, 50), new RoomKey(1, 51) },
            Obs("Cache", Direction.N));

        Assert.Null(chosen);
    }
}
