using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// LoopManager CRUD + builder helpers, working against a per-test game-data set.
public sealed class LoopManagerTests : IDisposable
{
    // AppPaths caches its roots at static-init time, so each test isolates by a
    // GUID suffix on the game-data set name (loops persist under the set's Loops/
    // folder); Dispose deletes the set folder.
    private readonly string _setName;

    public LoopManagerTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-set-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- fixture ---------------------------------------------------

    // 1/1 ──N── 1/2 ──N── 1/3 (linear strip for gap-fill).
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
        // The unique per-test set name keeps concurrent tests apart and lets
        // Dispose clean up.
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
    public void LoadAll_UnknownSet_LeavesEmpty()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        Assert.Empty(m.Loops);
        Assert.Equal(_setName, m.SetName);
    }

    [Fact]
    public void LoadAll_Null_ClearsSetAndLoops()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        m.LoadAll(null);
        Assert.Null(m.SetName);
        Assert.Empty(m.Loops);
    }

    [Fact]
    public void Save_AddsLoopToCatalogue_AndPersistsToSetLoopsFolder()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);

        var loop = new Loop("Sewer farm", new[]
        {
            new RoomKey(1, 1),
            new RoomKey(1, 2),
        });
        m.Save(loop);

        Assert.Single(m.Loops);
        Assert.Equal("Sewer farm", m.Loops[0].Name);

        string expectedPath = Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "Sewer farm" + LoopManager.LoopFileSuffix);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_RoundTripsLoopsAcrossLoadAll()
    {
        // Schema v3: commands attach to waypoints, not arbitrary
        // positions in a flat step list. Round-tripping preserves the
        // per-waypoint command + delay alongside the room.
        LoopManager m1 = NewManager();
        m1.LoadAll(_setName);
        m1.Save(new Loop("Albion run", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(1, 2), "dep 100", 500),
        }));

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);

        Loop? round = m2.Get("Albion run");
        Assert.NotNull(round);
        Assert.Equal(2, round!.Waypoints.Count);
        Assert.Equal(new RoomKey(1, 1), round.Waypoints[0].Key);
        Assert.Null(round.Waypoints[0].Command);
        Assert.Equal(new RoomKey(1, 2), round.Waypoints[1].Key);
        Assert.Equal("dep 100", round.Waypoints[1].Command);
        Assert.Equal(500, round.Waypoints[1].DelayMs);
    }

    [Fact]
    public void Save_KeepsTheFilesOwnFavoriteFlag_ButNotTheCharacters()
    {
        // The favourite is the character's now (LoopFavoritesStore), so it isn't
        // written to the shared file. The flag older files carried round-trips as
        // it was, for a character that hasn't taken its copy yet.
        LoopManager m1 = NewManager();
        m1.LoadAll(_setName);
        m1.Save(new Loop("Fav loop", new[] { new RoomKey(1, 1), new RoomKey(1, 2) })
        {
            Favorite = true, SharedFavorite = true,
        });
        m1.Save(new Loop("Mine only", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }) { Favorite = true });

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Assert.True(m2.Get("Fav loop")!.SharedFavorite);
        Assert.False(m2.Get("Fav loop")!.Favorite);
        Assert.False(m2.Get("Mine only")!.SharedFavorite);
    }

    [Fact]
    public void Save_RoundTripsCombatSuppressionFlags()
    {
        // The per-waypoint DoNotAttack is the loop's and must survive a save +
        // reload, and so must the loop-wide flag the file held from when it was
        // shared. With no character loaded, that one is what the loop runs with.
        LoopManager m1 = NewManager();
        m1.LoadAll(_setName);
        m1.Save(new Loop("Suppress loop", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), doNotAttack: true),
            new LoopWaypoint(new RoomKey(1, 2)),
        })
        { SharedOnlyAttackInLairRooms = true });

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Loop? round = m2.Get("Suppress loop");
        Assert.NotNull(round);
        Assert.True(round!.SharedOnlyAttackInLairRooms);
        Assert.True(round.OnlyAttackInLairRooms);
        Assert.True(round.Waypoints[0].DoNotAttack);
        Assert.False(round.Waypoints[1].DoNotAttack);
    }

    // The two loop-wide settings a loop is saved with are the loaded character's
    // (LoopSettingsStore), so the file every character reads gets neither.
    [Fact]
    public void Save_KeepsTheCharactersLoopSettingsOutOfTheFile()
    {
        LoopManager m1 = NewManager();
        m1.LoadAll(_setName);
        m1.Save(new Loop("Mine only", new[] { new RoomKey(1, 1), new RoomKey(1, 2) })
        {
            OnlyAttackInLairRooms = true, LairEntryDebuff = LairEntryDebuffMode.BlockSpells,
        });

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Loop round = m2.Get("Mine only")!;
        Assert.False(round.SharedOnlyAttackInLairRooms);
        Assert.Equal(LairEntryDebuffMode.Off, round.SharedLairEntryDebuff);
        Assert.False(round.OnlyAttackInLairRooms);
        Assert.Equal(LairEntryDebuffMode.Off, round.LairEntryDebuff);
    }

    // The loop file keeps the way of waiting before lairs it held by the mode's
    // name, and reads a number too (what a shared loop carries).
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void Save_RoundTripsTheLairEntryDebuffMode(LairEntryDebuffMode mode)
    {
        LoopManager m1 = NewManager();
        m1.LoadAll(_setName);
        m1.Save(new Loop("Debuff loop", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }) { SharedLairEntryDebuff = mode });

        string path = Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "Debuff loop" + LoopManager.LoopFileSuffix);
        using (System.Text.Json.JsonDocument file = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(mode.ToString(), file.RootElement.GetProperty("LairEntryDebuff").GetString());

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Assert.Equal(mode, m2.Get("Debuff loop")!.SharedLairEntryDebuff);
        Assert.Equal(mode, m2.Get("Debuff loop")!.LairEntryDebuff);
    }

    [Fact]
    public void LoadAll_LairEntryDebuffModeWrittenAsANumber_LoadsAsThatMode()
    {
        string folder = AppPaths.GameDataSetLoopsFolder(_setName);
        Directory.CreateDirectory(folder);
        const string Json = """
            {
              "SchemaVersion": 3,
              "Name": "Numbered",
              "LairEntryDebuff": 3,
              "Waypoints": [ { "Room": "1/1" }, { "Room": "1/2" } ]
            }
            """;
        File.WriteAllText(Path.Combine(folder, "Numbered" + LoopManager.LoopFileSuffix), Json);

        LoopManager m = NewManager();
        m.LoadAll(_setName);

        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, m.Get("Numbered")!.LairEntryDebuff);
    }

    [Fact]
    public void LoadAll_LegacyLoopWithoutSuppressionFields_DefaultsFalse()
    {
        // A v3 file predating the do-not-attack / only-lair fields loads with
        // both false (additive bools; System.Text.Json leaves them default).
        string folder = AppPaths.GameDataSetLoopsFolder(_setName);
        Directory.CreateDirectory(folder);
        const string LegacyJson = """
            {
              "SchemaVersion": 3,
              "Name": "PreSuppress",
              "Waypoints": [
                { "Room": "1/1" },
                { "Room": "1/2" }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(folder, "PreSuppress" + LoopManager.LoopFileSuffix), LegacyJson);

        LoopManager m = NewManager();
        m.LoadAll(_setName);
        Loop? loaded = m.Get("PreSuppress");
        Assert.NotNull(loaded);
        Assert.False(loaded!.OnlyAttackInLairRooms);
        Assert.All(loaded.Waypoints, w => Assert.False(w.DoNotAttack));
    }

    [Fact]
    public void Save_NoSetActive_IsNoOp()
    {
        LoopManager m = NewManager();
        m.Save(new Loop("orphan", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Empty(m.Loops);
    }

    [Fact]
    public void Delete_RemovesFromCatalogueAndDisk()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        m.Save(new Loop("test", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        bool removed = m.Delete("test");

        Assert.True(removed);
        Assert.Empty(m.Loops);
        Assert.False(File.Exists(Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "test" + LoopManager.LoopFileSuffix)));
    }

    [Fact]
    public void LoopsChanged_FiresOnSaveDeleteAndLoadAll()
    {
        LoopManager m = NewManager();
        int fires = 0;
        m.LoopsChanged += () => fires++;

        m.LoadAll(_setName);
        Assert.Equal(1, fires);

        m.Save(new Loop("a", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(2, fires);

        m.Delete("a");
        Assert.Equal(3, fires);
    }

    [Fact]
    public void Loops_OrderedAlphabetically()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        var pair = new[] { new RoomKey(1, 1), new RoomKey(1, 2) };
        m.Save(new Loop("zeta",   pair));
        m.Save(new Loop("alpha",  pair));
        m.Save(new Loop("MIDDLE", pair));

        Assert.Equal(new[] { "alpha", "MIDDLE", "zeta" }, m.Loops.Select(l => l.Name));
    }

    [Fact]
    public void Save_LoopWithIllegalFilenameChar_StillRoundTrips()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        m.Save(new Loop("loop/with:bad*chars", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Assert.Single(m2.Loops);
        Assert.Equal("loop/with:bad*chars", m2.Loops[0].Name);
    }

    [Fact]
    public void NumberedName_IsTheFirstNumberFromTwoThatNoLoopHas_MatchedWithoutCase()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        var pair = new[] { new RoomKey(1, 1), new RoomKey(1, 2) };
        Assert.Equal("Sewers 2", m.NumberedName("Sewers"));

        m.Save(new Loop("Sewers", pair));
        m.Save(new Loop("SEWERS 2", pair));
        Assert.Equal("Sewers 3", m.NumberedName("Sewers"));

        m.Save(new Loop("Sewers 4", pair));
        Assert.Equal("Sewers 3", m.NumberedName("Sewers"));
    }

    // NumberedCopyOf is NumberedName read the other way: it knows exactly the names
    // that rule writes, and no name a user typed that only looks like one.
    [Theory]
    [InlineData("Dragons 2", 2)]
    [InlineData("dragons 17", 17)]
    [InlineData("Dragons", 0)]
    [InlineData("Dragons 1", 0)]
    [InlineData("Dragons 0", 0)]
    [InlineData("Dragons 02", 0)]
    [InlineData("Dragons 2a", 0)]
    [InlineData("Dragons2", 0)]
    [InlineData("Dragons  2", 0)]
    [InlineData("Dragons 2 ", 0)]
    [InlineData("Dragons -2", 0)]
    [InlineData("Dragons (old)", 0)]
    [InlineData("Dragons 99999999999999999999", 0)]
    [InlineData("Wyrms 2", 0)]
    public void NumberedCopyOf_IsTheNumberANumberedSaveWrote_OrNone(string candidate, int expected) =>
        Assert.Equal(expected, LoopManager.NumberedCopyOf(candidate, "Dragons"));

    [Fact]
    public void WhatNumberedNameWrites_IsReadBackAsANumberedCopy_LowestFirst()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);
        var pair = new[] { new RoomKey(1, 1), new RoomKey(1, 2) };
        m.Save(new Loop("Sewers", pair));
        for (int expected = 2; expected <= 11; expected++)
        {
            string written = m.NumberedName("sewers");
            Assert.Equal(expected, LoopManager.NumberedCopyOf(written, "Sewers"));
            m.Save(new Loop(written, pair));
        }
        m.Save(new Loop("Sewers 2a", pair));
        m.Save(new Loop("Sewers (old)", pair));

        // Number order, not name order ("sewers 10" sorts before "sewers 2").
        Assert.Equal(new[] { "Sewers" }.Concat(Enumerable.Range(2, 10).Select(n => $"sewers {n}")),
            m.WithNumberedCopies("SEWERS").Select(l => l.Name));
        Assert.Empty(m.WithNumberedCopies("Drains"));
    }

    // ----- ExpandWaypoints --------------------------------------------

    [Fact]
    public void ExpandWaypoints_DirectlyAdjacent_OneMovePerHop_AndCloses()
    {
        // 1 → 2 → 3 + closing leg 3 → 2 → 1 = 4 move steps.
        LoopManager m = NewManager();
        var waypoints = new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(1, 2)),
            new LoopWaypoint(new RoomKey(1, 3)),
        };

        (var steps, var unreach) = m.ExpandWaypoints(waypoints);

        Assert.Empty(unreach);
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.IsType<MoveLoopStep>(s));
    }

    [Fact]
    public void ExpandWaypoints_WithCommands_IntersperseCommandsAtWaypoints()
    {
        // Each waypoint's command fires BEFORE walking to the next.
        // With commands on both waypoints, the cycle reads
        // [cmd@1] N N [cmd@3] S S — 6 steps total (2 commands + 4 moves).
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandWaypoints(new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "rest", 0),
            new LoopWaypoint(new RoomKey(1, 3), "dep 100", 500),
        });

        Assert.Empty(unreach);
        Assert.Equal(6, steps.Count);
        Assert.IsType<CommandLoopStep>(steps[0]);
        Assert.Equal("rest", ((CommandLoopStep)steps[0]).Command);
        Assert.IsType<MoveLoopStep>(steps[1]);
        Assert.IsType<MoveLoopStep>(steps[2]);
        Assert.IsType<CommandLoopStep>(steps[3]);
        Assert.Equal("dep 100", ((CommandLoopStep)steps[3]).Command);
    }

    [Fact]
    public void ExpandWaypoints_ConsecutiveSameRoom_IsZeroMoves_NotUnreachable()
    {
        // The same room twice in a row is a deliberate "stay put": 0 moves for that
        // leg (NOT flagged unreachable), and BOTH waypoints' commands fire — so a
        // loop can run several commands in one room (hand in pies, then convert coin).
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandWaypoints(new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "give", 0),
            new LoopWaypoint(new RoomKey(1, 1), "convert", 0),
        });

        Assert.Empty(unreach);
        Assert.Equal(2, steps.Count);
        Assert.Equal("give",    ((CommandLoopStep)steps[0]).Command);
        Assert.Equal("convert", ((CommandLoopStep)steps[1]).Command);
    }

    [Fact]
    public void ExpandWaypoints_FewerThan2_Empty()
    {
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandWaypoints(new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
        });
        Assert.Empty(steps);
        Assert.Empty(unreach);
    }

    // ----- schema migration -----------------------------------------

    [Fact]
    public void LoadAll_V2LoopUserWaypoints_MigratedToV3Waypoints()
    {
        // v2 file: UserWaypoints + flat Steps. v3 migration pulls the
        // waypoints out of LegacyFields via JsonExtensionData, builds
        // Waypoints with null commands. Mid-leg CommandLoopSteps in
        // the flat list are dropped (logged warning) since v3 attaches
        // commands to waypoints, not arbitrary positions.
        string folder = AppPaths.GameDataSetLoopsFolder(_setName);
        Directory.CreateDirectory(folder);
        const string V2Json = """
            {
              "SchemaVersion": 2,
              "Name": "Legacy",
              "Notes": "",
              "UserWaypoints": [
                { "Map": 1, "Room": 1 },
                { "Map": 1, "Room": 3 }
              ],
              "Steps": [
                { "kind": "move", "Direction": 0 },
                { "kind": "command", "Command": "dep 100", "DelayMs": 500 },
                { "kind": "move", "Direction": 0 }
              ],
              "LastModifiedAt": "2024-01-01T00:00:00+00:00"
            }
            """;
        File.WriteAllText(Path.Combine(folder, "Legacy.json"), V2Json);

        LoopManager m = NewManager();
        m.LoadAll(_setName);

        Loop? loaded = m.Get("Legacy");
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.SchemaVersion);
        Assert.Equal(2, loaded.Waypoints.Count);
        Assert.Equal(new RoomKey(1, 1), loaded.Waypoints[0].Key);
        Assert.Equal(new RoomKey(1, 3), loaded.Waypoints[1].Key);
        // Mid-leg command from the v2 Steps list is dropped on migration.
        Assert.Null(loaded.Waypoints[0].Command);
        Assert.Null(loaded.Waypoints[1].Command);
        Assert.Null(loaded.LegacyFields);
    }

    [Fact]
    public void Save_RoundTrip_PreservesWaypointsAndNotes()
    {
        LoopManager m = NewManager();
        m.LoadAll(_setName);

        var waypoints = new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(1, 3), "rest", 0),
        };
        Loop loop = new("RoundTrip", waypoints)
        {
            Notes = "test notes",
        };
        m.Save(loop);

        // Reload from disk via a fresh manager — confirms the JSON
        // serialiser is round-tripping the v3 fields cleanly.
        LoopManager m2 = NewManager();
        m2.LoadAll(_setName);
        Loop? r = m2.Get("RoundTrip");
        Assert.NotNull(r);
        Assert.Equal(3, r!.SchemaVersion);
        Assert.Equal(2, r.Waypoints.Count);
        Assert.Equal(new RoomKey(1, 1), r.Waypoints[0].Key);
        Assert.Equal(new RoomKey(1, 3), r.Waypoints[1].Key);
        Assert.Equal("rest", r.Waypoints[1].Command);
        Assert.Equal("test notes", r.Notes);
    }

    [Fact]
    public void ExpandWaypoints_UnreachableSegment_Surfaced()
    {
        // The cycle closes: when the user picks an unreachable
        // destination, BOTH the forward leg AND the closing leg fail.
        LoopManager m = NewManager();
        (var steps, var unreach) = m.ExpandWaypoints(new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(999, 999)),
        });

        Assert.Empty(steps);
        Assert.Equal(2, unreach.Count);
        Assert.Equal(new RoomKey(1, 1),   unreach[0].From);
        Assert.Equal(new RoomKey(999, 999), unreach[0].To);
        Assert.Equal(new RoomKey(999, 999), unreach[1].From);
        Assert.Equal(new RoomKey(1, 1),   unreach[1].To);
    }
}
