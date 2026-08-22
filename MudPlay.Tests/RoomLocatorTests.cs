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
    private const string FixtureGraph = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Narrow Road",
            "N": "1/900", "S": "1/901", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Shut Gate",
            "N": "1/902", "S": "1/903", "E": "1/904 (Door)", "W": "0",
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
}
