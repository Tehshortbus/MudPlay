using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

public sealed class LoopBuilderSessionTests : IDisposable
{
    private readonly string _setName = "test-set-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public void Dispose()
    {
        foreach (string set in new[] { _setName, _setName + "-other" })
        {
            try
            {
                string setFolder = AppPaths.GameDataSetDir(set);
                if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

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

    // GraphJson plus a room with no exits, which no loop can reach.
    private const string IslandGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Island",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (LoopBuilderSessionViewModel Session, LoopManager Loops) NewSession(string json = GraphJson)
    {
        // Unique per-test set name so concurrent tests don't collide
        // and Dispose can clean up. AppPaths.GameDataRoot can't be
        // sandboxed (cached at static-init).
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), json);
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        BfsMapper bfs = new(graph);
        LoopManager loops = new(bfs, graph);
        loops.LoadAll(_setName);
        return (new LoopBuilderSessionViewModel(loops, graph), loops);
    }

    [Fact]
    public void Fresh_HasNoClicks_CannotSave()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        Assert.False(s.HasClicks);
        Assert.False(s.CanSave);
    }

    [Fact]
    public void AddClick_AppendsRowAndRecomputesExpansion()
    {
        // Schema v2: every expansion closes the cycle. 1 → 3 forward
        // is N, N gap-filled; closing back is S, S = 4 steps total.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        Assert.Equal(2, s.Clicks.Count);
        Assert.Equal(4, s.ExpandedStepCount);
        Assert.True(s.CanSave);
    }

    [Fact]
    public void AddClick_AdjacentDuplicate_Kept()
    {
        // The same room twice in a row is a deliberate "stay put" step (run another
        // command in place), so both clicks are kept.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 1));
        Assert.Equal(2, s.Clicks.Count);
    }

    [Fact]
    public void Save_PersistsLoopAndClearsSession()
    {
        (LoopBuilderSessionViewModel s, LoopManager loops) = NewSession();
        s.ProposedName = "MyLoop";
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        Loop? saved = s.Save();

        Assert.NotNull(saved);
        Assert.Equal("MyLoop", saved!.Name);
        Assert.NotNull(loops.Get("MyLoop"));
        Assert.False(s.HasClicks);
    }

    [Fact]
    public void SetClickAction_AttachesCommand_CarriedIntoBuiltLoop()
    {
        // Clicking a builder row attaches a per-waypoint command while building
        // (not only via the post-save loop editor); it must ride into the built
        // loop's LoopWaypoint.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        s.SetClickAction(0, "sea", 500);
        Assert.True(s.Clicks[0].HasCommand);
        Assert.Equal("sea", s.Clicks[0].Command);
        Assert.Equal(500, s.Clicks[0].DelayMs);
        Assert.False(s.Clicks[1].HasCommand);

        Loop? loop = s.BuildTransient();
        Assert.NotNull(loop);
        Assert.Equal("sea", loop!.Waypoints[0].Command);
        Assert.Equal(500, loop.Waypoints[0].DelayMs);
        Assert.Null(loop.Waypoints[1].Command);
    }

    [Fact]
    public void SetClickAction_DoNotRest_CarriedIntoBuiltLoop()
    {
        // The "do not rest in this room" flag rides from the builder row into the
        // built loop's LoopWaypoint, independent of any command.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        s.SetClickAction(0, command: null, delayMs: 0, doNotRest: true);
        Assert.True(s.Clicks[0].DoNotRest);
        Assert.False(s.Clicks[1].DoNotRest);

        Loop? loop = s.BuildTransient();
        Assert.NotNull(loop);
        Assert.True(loop!.Waypoints[0].DoNotRest);
        Assert.False(loop.Waypoints[1].DoNotRest);
    }

    [Fact]
    public void SetClickAction_DoNotAttack_CarriedIntoBuiltLoop()
    {
        // The "do not attack in this room" flag rides from the builder row into
        // the built loop's LoopWaypoint, independent of command / do-not-rest.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        s.SetClickAction(0, command: null, delayMs: 0, doNotRest: false, doNotAttack: true);
        Assert.True(s.Clicks[0].DoNotAttack);
        Assert.False(s.Clicks[1].DoNotAttack);

        Loop? loop = s.BuildTransient();
        Assert.NotNull(loop);
        Assert.True(loop!.Waypoints[0].DoNotAttack);
        Assert.False(loop.Waypoints[1].DoNotAttack);
    }

    [Fact]
    public void OnlyAttackInLairRooms_CarriedIntoBuiltLoop()
    {
        // The build-time "Entire Loop Settings" loop-wide toggle rides into the
        // built loop.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        s.OnlyAttackInLairRooms = true;

        Loop? loop = s.BuildTransient();
        Assert.NotNull(loop);
        Assert.True(loop!.OnlyAttackInLairRooms);
    }

    [Fact]
    public void WaitForPriorityBuffs_CarriedIntoBuiltLoop_FromTheTickBoxAndThirdChoice()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        s.WaitToDebuffBeforeLairs = true;
        s.LairDebuffWaitChoice = LairEntryDebuffModes.WaitForPriorityBuffsChoice;

        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, s.BuildTransient()!.LairEntryDebuff);

        s.WaitToDebuffBeforeLairs = false;      // unticked, the choice is kept but the loop gets Off
        Assert.Equal(LairEntryDebuffMode.Off, s.BuildTransient()!.LairEntryDebuff);
    }

    [Fact]
    public void DoNotRest_JsonRoundTrips_AndDefaultsFalseForOlderFiles()
    {
        // New loops persist the flag; older .loop files lacking it load as false —
        // so no file migration is needed.
        var loop = new Loop("t", new List<LoopWaypoint>
        {
            new(new RoomKey(1, 1), doNotRest: true),
            new(new RoomKey(1, 3)),
        });
        string json = System.Text.Json.JsonSerializer.Serialize(loop);
        Loop? round = System.Text.Json.JsonSerializer.Deserialize<Loop>(json);
        Assert.NotNull(round);
        Assert.True(round!.Waypoints[0].DoNotRest);
        Assert.False(round.Waypoints[1].DoNotRest);

        const string legacy = """{"SchemaVersion":3,"Name":"old","Waypoints":[{"Room":"1/1"}]}""";
        Loop? old = System.Text.Json.JsonSerializer.Deserialize<Loop>(legacy);
        Assert.NotNull(old);
        Assert.False(old!.Waypoints[0].DoNotRest);
    }

    [Fact]
    public void SetClickAction_BlankCommand_ClearsActionAndZeroesDelay()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        s.SetClickAction(0, "sea", 500);

        s.SetClickAction(0, "   ", 500);   // blank command clears the action
        Assert.False(s.Clicks[0].HasCommand);
        Assert.Null(s.Clicks[0].Command);
        Assert.Equal(0, s.Clicks[0].DelayMs);
    }

    [Fact]
    public void RemoveClickAt_TheLast_PopsAndRecomputes()
    {
        // Schema v2: every expansion closes the loop. Two clicks
        // 1→2 expand to N (1→2) + S (2→1, closing) = 2 steps.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        s.AddClick(new RoomKey(1, 3));
        s.RemoveClickAt(2);
        Assert.Equal(2, s.Clicks.Count);
        Assert.Equal(2, s.ExpandedStepCount);    // N to 2, S closing back to 1
    }

    // Alt+click on the map takes a room back out: the most recent click on it,
    // since a loop can pass through a room more than once.
    [Fact]
    public void RemoveLastClickOf_RemovesTheLatestClickOnThatRoom()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        s.AddClick(new RoomKey(1, 3));
        s.AddClick(new RoomKey(1, 2));

        Assert.True(s.RemoveLastClickOf(new RoomKey(1, 2)));
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) },
            s.Clicks.Select(c => c.Key));
        Assert.Equal(new[] { 1, 2, 3 }, s.Clicks.Select(c => c.Index));   // renumbered

        Assert.False(s.RemoveLastClickOf(new RoomKey(9, 9)));             // not in the loop
        Assert.Equal(3, s.Clicks.Count);
    }

    // Map drag: the waypoint moves to the drop room and keeps its number and command.
    [Fact]
    public void MoveLastClickOf_MovesTheWaypoint_KeepingItsCommand()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        s.SetClickAction(1, "search", 500);

        Assert.True(s.MoveLastClickOf(new RoomKey(1, 2), new RoomKey(1, 3)));
        LoopBuilderRow moved = s.Clicks[1];
        Assert.Equal((2, new RoomKey(1, 3), "C", "search"), (moved.Index, moved.Key, moved.Name, moved.Command));
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 3) }, s.WaypointKeys);
        Assert.Equal(4, s.ExpandedStepCount);   // N, N out and S, S back
    }

    // A drop on a room the loop can't reach puts the chip back.
    [Fact]
    public void MoveLastClickOf_OntoAnUnreachableRoom_IsUndone()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession(IslandGraphJson);
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));

        Assert.False(s.MoveLastClickOf(new RoomKey(1, 2), new RoomKey(1, 9)));
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, s.WaypointKeys);
        Assert.Equal(string.Empty, s.UnreachableSummary);
    }

    [Fact]
    public void PreviewedRoomKeys_PopulatesAfterTwoClicks()
    {
        // After two clicks the cycle is well-defined: clicks[0] →
        // (gap-fill) → clicks[1] → (closing gap-fill) → clicks[0].
        // PreviewedRoomKeys is the flattened RoomKey sequence the
        // map's LoopBuilderPath polyline binds to.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));

        // Only one click — no cycle yet, no preview.
        Assert.Null(s.PreviewedRoomKeys);

        s.AddClick(new RoomKey(1, 3));

        Assert.NotNull(s.PreviewedRoomKeys);
        // 1 (start) + 2 (forward N, N) + 2 (closing S, S) = 5 entries
        // including the closing return to the start room.
        Assert.Equal(5, s.PreviewedRoomKeys!.Count);
        Assert.Equal(new RoomKey(1, 1), s.PreviewedRoomKeys[0]);
        Assert.Equal(new RoomKey(1, 3), s.PreviewedRoomKeys[2]);
        Assert.Equal(new RoomKey(1, 1), s.PreviewedRoomKeys[^1]);
    }

    [Fact]
    public void Clear_ResetsPreview()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        Assert.NotNull(s.PreviewedRoomKeys);

        s.Clear();
        Assert.Null(s.PreviewedRoomKeys);
    }

    [Fact]
    public void UnreachableSegment_SurfacedAndBlocksSave()
    {
        // Two disconnected components: 1/1 ↔ 1/2 and 9/1 (isolated).
        const string DisconnectedGraph = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1, "Name": "Island",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), DisconnectedGraph);
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        BfsMapper bfs = new(graph);
        LoopManager loops = new(bfs, graph);
        loops.LoadAll(_setName);
        LoopBuilderSessionViewModel s = new(loops, graph);

        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(9, 1));   // unreachable from 1/1

        Assert.NotEmpty(s.UnreachableSummary);
        Assert.False(s.CanSave);
    }

    // ----- A build belongs to the game data its rooms were clicked on ----------

    [Fact]
    public void ASwitchToOtherGameData_DropsTheLoopBeingBuilt_SoItsRoomsAreNotSavedThere()
    {
        (LoopBuilderSessionViewModel s, LoopManager loops) = NewSession();
        s.ProposedName = "Built on the first";
        s.Notes = "the first set's";
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        Assert.True(s.CanSave);
        Assert.False(s.DropIfBuiltOnOtherGameData(out _));

        string other = _setName + "-other";
        loops.LoadAll(other);

        Assert.True(s.DropIfBuiltOnOtherGameData(out string? builtOn));
        Assert.Equal(_setName, builtOn);
        Assert.False(s.HasClicks);
        Assert.False(s.CanSave);
        Assert.Null(s.PreviewedRoomKeys);
        Assert.Null(s.Save());
        Assert.Empty(loops.Loops);
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(other)));
        Assert.Equal(string.Empty, s.Notes);
        Assert.NotEqual("Built on the first", s.ProposedName);

        // The session carries on, on the set now active: its rooms are that set's.
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        Assert.False(s.DropIfBuiltOnOtherGameData(out _));
        Assert.True(s.CanSave);
    }

    [Fact]
    public void TheSameGameDataReadInAgain_KeepsTheLoopBeingBuilt()
    {
        (LoopBuilderSessionViewModel s, LoopManager loops) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        loops.LoadAll(_setName);

        Assert.False(s.DropIfBuiltOnOtherGameData(out _));
        Assert.True(s.HasClicks);
        Assert.True(s.CanSave);
    }
}
