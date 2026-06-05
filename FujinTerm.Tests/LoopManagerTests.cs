using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.8 — LoopManager CRUD + builder helpers, working against an
/// isolated XDG_DATA_HOME so the suite doesn't touch user data.
/// </summary>
public sealed class LoopManagerTests : IDisposable
{
    // AppPaths caches its roots at static-init time, so XDG_DATA_HOME
    // can't be swapped between tests. We isolate via per-test GUID
    // suffixes on BOTH the BBS name AND the game-data set name; the
    // test cleans them up on Dispose so nothing leaks into the user's
    // real Data/ tree.
    private readonly string _bbs;
    private readonly string _setName;

    public LoopManagerTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _bbs = "test-" + suffix;
        _setName = "test-set-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_bbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best-effort */ }
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- fixture ---------------------------------------------------

    // 1/1 ──N── 1/2 ──N── 1/3 (linear strip for gap-fill).
    // Active set lives in the same isolated XDG root so AppPaths
    // resolves Data/game data/ + Data/BBS/ under it.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private LoopManager NewManager()
    {
        // GameDataCache uses AppPaths.GameDataRoot under the XDG root —
        // we can't sandbox it because AppPaths caches the root at
        // static-init. Use the unique per-test set name so concurrent
        // tests don't collide and Dispose can clean up cleanly.
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), GraphJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        BfsMapper bfs = new(graph);
        return new LoopManager(bfs, graph);
    }

    // ----- LoadAll lifecycle ----------------------------------------

    [Fact]
    public void LoadAll_UnknownBbs_LeavesEmpty()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        Assert.Empty(m.Loops);
        Assert.Equal(_bbs, m.BbsName);
    }

    [Fact]
    public void LoadAll_Null_ClearsBbsAndLoops()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        m.LoadAll(null);
        Assert.Null(m.BbsName);
        Assert.Empty(m.Loops);
    }

    [Fact]
    public void Save_AddsLoopToCatalogue_AndPersistsToBbsLoopsFolder()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);

        var loop = new Loop("Sewer farm", new LoopStep[]
        {
            new MoveLoopStep(Direction.N),
            new MoveLoopStep(Direction.N),
        });
        m.Save(loop);

        Assert.Single(m.Loops);
        Assert.Equal("Sewer farm", m.Loops[0].Name);

        string expectedPath = Path.Combine(
            AppPaths.BbsLoopsFolder(_bbs), "Sewer farm.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_RoundTripsLoopsAcrossLoadAll()
    {
        LoopManager m1 = NewManager();
        m1.LoadAll(_bbs);
        m1.Save(new Loop("Albion run", new LoopStep[]
        {
            new MoveLoopStep(Direction.N),
            new CommandLoopStep("dep 100", 500),
            new MoveLoopStep(Direction.N),
        }));

        LoopManager m2 = NewManager();
        m2.LoadAll(_bbs);

        Loop? round = m2.Get("Albion run");
        Assert.NotNull(round);
        Assert.Equal(3, round!.Steps.Count);
        Assert.IsType<MoveLoopStep>(round.Steps[0]);
        Assert.IsType<CommandLoopStep>(round.Steps[1]);
        Assert.Equal("dep 100", ((CommandLoopStep)round.Steps[1]).Command);
        Assert.Equal(500, ((CommandLoopStep)round.Steps[1]).DelayMs);
    }

    [Fact]
    public void Save_NoBbsBound_IsNoOp()
    {
        LoopManager m = NewManager();
        m.Save(new Loop("orphan", new LoopStep[] { new MoveLoopStep(Direction.N) }));
        Assert.Empty(m.Loops);
    }

    [Fact]
    public void Delete_RemovesFromCatalogueAndDisk()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        m.Save(new Loop("test", new LoopStep[] { new MoveLoopStep(Direction.N) }));

        bool removed = m.Delete("test");

        Assert.True(removed);
        Assert.Empty(m.Loops);
        Assert.False(File.Exists(Path.Combine(AppPaths.BbsLoopsFolder(_bbs), "test.json")));
    }

    [Fact]
    public void NoteRun_StampsLastRunAt()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        m.Save(new Loop("test", new LoopStep[] { new MoveLoopStep(Direction.N) }));

        Assert.Null(m.Get("test")!.LastRunAt);
        m.NoteRun("test");
        Assert.NotNull(m.Get("test")!.LastRunAt);
    }

    [Fact]
    public void LoopsChanged_FiresOnSaveDeleteAndLoadAll()
    {
        LoopManager m = NewManager();
        int fires = 0;
        m.LoopsChanged += () => fires++;

        m.LoadAll(_bbs);
        Assert.Equal(1, fires);

        m.Save(new Loop("a", new LoopStep[] { new MoveLoopStep(Direction.N) }));
        Assert.Equal(2, fires);

        m.Delete("a");
        Assert.Equal(3, fires);
    }

    [Fact]
    public void Loops_OrderedAlphabetically()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        m.Save(new Loop("zeta", new LoopStep[] { new MoveLoopStep(Direction.N) }));
        m.Save(new Loop("alpha", new LoopStep[] { new MoveLoopStep(Direction.N) }));
        m.Save(new Loop("MIDDLE", new LoopStep[] { new MoveLoopStep(Direction.N) }));

        Assert.Equal(new[] { "alpha", "MIDDLE", "zeta" }, m.Loops.Select(l => l.Name));
    }

    [Fact]
    public void Save_LoopWithIllegalFilenameChar_StillRoundTrips()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);
        m.Save(new Loop("loop/with:bad*chars", new LoopStep[] { new MoveLoopStep(Direction.N) }));

        LoopManager m2 = NewManager();
        m2.LoadAll(_bbs);
        Assert.Single(m2.Loops);
        Assert.Equal("loop/with:bad*chars", m2.Loops[0].Name);
    }

    // ----- ExpandClickedRooms ---------------------------------------

    [Fact]
    public void ExpandClickedRooms_DirectlyAdjacent_OneMovePerClick_AndCloses()
    {
        // Schema v2: all expansions close the cycle. 1→2→3 produces
        // N + N forward (2 steps) + S + S closing back to 1 (2 steps).
        LoopManager m = NewManager();

        (var steps, var unreach) = m.ExpandClickedRooms(new[]
        {
            new RoomKey(1, 1),
            new RoomKey(1, 2),
            new RoomKey(1, 3),
        });

        Assert.Empty(unreach);
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.IsType<MoveLoopStep>(s));
    }

    [Fact]
    public void ExpandClickedRooms_GapFillsViaBfs_AndCloses()
    {
        LoopManager m = NewManager();

        // Skip the middle room — gap-fill should insert the two N
        // steps, then close the cycle with two S steps back to start.
        // Schema v2: all loops close by definition.
        (var steps, var unreach) = m.ExpandClickedRooms(new[]
        {
            new RoomKey(1, 1),
            new RoomKey(1, 3),
        });

        Assert.Empty(unreach);
        Assert.Equal(4, steps.Count);
        Assert.Equal(Direction.N, ((MoveLoopStep)steps[0]).Direction);
        Assert.Equal(Direction.N, ((MoveLoopStep)steps[1]).Direction);
        Assert.Equal(Direction.S, ((MoveLoopStep)steps[2]).Direction);
        Assert.Equal(Direction.S, ((MoveLoopStep)steps[3]).Direction);
    }

    [Fact]
    public void ExpandClickedRooms_FewerThan2Clicks_Empty()
    {
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandClickedRooms(new[] { new RoomKey(1, 1) });
        Assert.Empty(steps);
        Assert.Empty(unreach);
    }

    // ----- schema migration -----------------------------------------

    [Fact]
    public void LoadAll_V1LoopWithoutWaypointsOrNotes_UpgradesInMemory()
    {
        // Hand-write a v1 loop file (no SchemaVersion field, no
        // UserWaypoints, no Notes, has the old IsCircular field). On
        // load, LoopManager upgrades it in memory to v2 defaults:
        // UserWaypoints empty, Notes empty, SchemaVersion = 2.
        // IsCircular is silently ignored (loops are always circular now).
        string folder = AppPaths.BbsLoopsFolder(_bbs);
        Directory.CreateDirectory(folder);
        const string V1Json = """
            {
              "$type": "v1",
              "Name": "Legacy",
              "IsCircular": false,
              "Steps": [
                { "kind": "move", "Direction": 0 }
              ],
              "LastModifiedAt": "2024-01-01T00:00:00+00:00"
            }
            """;
        File.WriteAllText(Path.Combine(folder, "Legacy.json"), V1Json);

        LoopManager m = NewManager();
        m.LoadAll(_bbs);

        Loop? loaded = m.Get("Legacy");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.SchemaVersion);
        Assert.NotNull(loaded.UserWaypoints);
        Assert.Empty(loaded.UserWaypoints);
        Assert.NotNull(loaded.Notes);
        Assert.Equal(string.Empty, loaded.Notes);
        Assert.Single(loaded.Steps);
    }

    [Fact]
    public void Save_RoundTrip_PreservesUserWaypointsAndNotes()
    {
        LoopManager m = NewManager();
        m.LoadAll(_bbs);

        var keys = new[] { new RoomKey(1, 1), new RoomKey(1, 3) };
        (var steps, _) = m.ExpandClickedRooms(keys);

        Loop loop = new("RoundTrip", steps)
        {
            UserWaypoints = new List<RoomKey>(keys),
            Notes = "test notes",
        };
        m.Save(loop);

        // Reload from disk via a fresh manager — confirms the JSON
        // serialiser is round-tripping the v2 fields cleanly.
        LoopManager m2 = NewManager();
        m2.LoadAll(_bbs);
        Loop? r = m2.Get("RoundTrip");
        Assert.NotNull(r);
        Assert.Equal(2, r!.SchemaVersion);
        Assert.Equal(2, r.UserWaypoints.Count);
        Assert.Equal(new RoomKey(1, 1), r.UserWaypoints[0]);
        Assert.Equal(new RoomKey(1, 3), r.UserWaypoints[1]);
        Assert.Equal("test notes", r.Notes);
    }

    [Fact]
    public void ExpandClickedRooms_UnreachableSegment_Surfaced()
    {
        // Schema v2 closes the cycle — when the user clicks an
        // unreachable destination, BOTH the forward leg AND the
        // closing leg fail, so two unreachable segments are surfaced
        // (the user sees one logical "can't path here" but the
        // record-keeping is per-leg).
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandClickedRooms(new[]
        {
            new RoomKey(1, 1),
            new RoomKey(999, 999),
        });

        Assert.Empty(steps);
        Assert.Equal(2, unreach.Count);
        Assert.Equal(new RoomKey(1, 1),   unreach[0].From);
        Assert.Equal(new RoomKey(999, 999), unreach[0].To);
        Assert.Equal(new RoomKey(999, 999), unreach[1].From);
        Assert.Equal(new RoomKey(1, 1),   unreach[1].To);
    }
}
