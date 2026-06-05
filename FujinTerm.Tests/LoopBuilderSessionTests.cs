using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LoopBuilderSessionTests : IDisposable
{
    private readonly string _bbs;
    private readonly string _setName;

    public LoopBuilderSessionTests()
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

    private (LoopBuilderSessionViewModel Session, LoopManager Loops) NewSession()
    {
        // Unique per-test set name so concurrent tests don't collide
        // and Dispose can clean up. AppPaths.GameDataRoot can't be
        // sandboxed (cached at static-init).
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), GraphJson);
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        BfsMapper bfs = new(graph);
        LoopManager loops = new(bfs, graph);
        loops.LoadAll(_bbs);
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
    public void AddClick_AdjacentDuplicate_Dropped()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 1));
        Assert.Single(s.Clicks);
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
    public void RemoveLastClick_PopsAndRecomputes()
    {
        // Schema v2: every expansion closes the loop. Two clicks
        // 1→2 expand to N (1→2) + S (2→1, closing) = 2 steps.
        // After RemoveLastClick we drop back to 1 click which can't
        // form a cycle, so ExpandedStepCount returns 0.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        s.AddClick(new RoomKey(1, 3));
        s.RemoveLastClick();
        Assert.Equal(2, s.Clicks.Count);
        Assert.Equal(2, s.ExpandedStepCount);    // N to 2, S closing back to 1
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
    public void AllExpansionsClose_SinceLoopsAreCircular()
    {
        // No CloseLoop toggle anymore — every saved loop closes by
        // definition (schema v2). 1 → 3 click sequence produces
        // N + N (1→2→3) plus closing S + S (3→2→1) = 4 steps.
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        Assert.Equal(4, s.ExpandedStepCount);
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
        cache.Reload();
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        BfsMapper bfs = new(graph);
        LoopManager loops = new(bfs, graph);
        loops.LoadAll(_bbs);
        LoopBuilderSessionViewModel s = new(loops, graph);

        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(9, 1));   // unreachable from 1/1

        Assert.NotEmpty(s.UnreachableSummary);
        Assert.False(s.CanSave);
    }
}
