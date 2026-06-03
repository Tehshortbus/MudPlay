using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LoopBuilderSessionTests : IDisposable
{
    private readonly string _bbs;

    public LoopBuilderSessionTests()
    {
        _bbs = "test-" + Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_bbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
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
        string setRoot = Path.Combine(AppPaths.GameDataRoot, "alpha");
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), GraphJson);
        GameDataCache cache = new();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
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
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));

        Assert.Equal(2, s.Clicks.Count);
        Assert.Equal(2, s.ExpandedStepCount);                // N, N gap-filled
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
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 2));
        s.AddClick(new RoomKey(1, 3));
        s.RemoveLastClick();
        Assert.Equal(2, s.Clicks.Count);
        Assert.Equal(1, s.ExpandedStepCount);
    }

    [Fact]
    public void CloseLoop_AppendsReturnPath_DoublesStepCount()
    {
        (LoopBuilderSessionViewModel s, _) = NewSession();
        s.AddClick(new RoomKey(1, 1));
        s.AddClick(new RoomKey(1, 3));
        Assert.Equal(2, s.ExpandedStepCount);

        s.CloseLoop = true;
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
        string setRoot = Path.Combine(AppPaths.GameDataRoot, "alpha");
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), DisconnectedGraph);
        GameDataCache cache = new();
        cache.SwitchSet("alpha");
        cache.Reload();
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
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
