using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The Simulator window's route picker and ranking pick: the estimator's sketch is
// offered only while it holds a route, editing it drops a result played on it, and
// a picked ranking goes on the map as the sketch — the picker follows it, and the
// row's own result shows beside the list.
public sealed class SimulatorViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-simulator-" + Path.GetRandomFileName());
    private readonly string _loopSet = "test-set-" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        try
        {
            string loops = Path.Combine(AppPaths.GameDataRoot, _loopSet);
            if (Directory.Exists(loops)) Directory.Delete(loops, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    //  1/1 ─N─ 1/2
    private const string Json = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "N": "1/2" },
          { "Map Number": 1, "Room Number": 2, "Name": "North", "S": "1/1" }
        ]
        """;

    private static readonly LoopSimRun Run =
        new(3600, 0, 600_000, 1, 1, 0, 0, 0, 0, 0, 0, 50, 0, new Dictionary<string, int>());

    private sealed class Harness : IDisposable
    {
        public required LairTimerStore Timers { get; init; }
        public required RouteExpResolver Resolver { get; init; }
        public required ExpEstimatorSessionViewModel Session { get; init; }
        public required SimulatorViewModel Simulator { get; init; }
        public required LoopManager Loops { get; init; }
        public void Dispose()
        {
            Resolver.Dispose();
            Timers.Dispose();
        }
    }

    private Harness Build(bool saveLoops = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        var resolver = new RouteExpResolver(graph, new BfsMapper(graph), timers, cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        if (saveLoops) loops.LoadAll(_loopSet);
        var simulation = new SimulationSource(
            _ => null, () => null, () => 30, _ => 1.0, () => new RoomKey(1, 1), _root);
        var session = new ExpEstimatorSessionViewModel(resolver, loops, graph, cache);
        SimulatorViewModel? sim = null;
        sim = new SimulatorViewModel(resolver, loops, cache, null, simulation, null, () => session,
            (rooms, name) => { session.LoadRoute(rooms, name); return null; });
        session.RouteChanged += sim.SketchChanged;
        return new Harness { Timers = timers, Resolver = resolver, Session = session, Simulator = sim, Loops = loops };
    }

    [Fact]
    public void SketchIsOfferedOnceItHoldsARouteAndEditingItDropsTheResult()
    {
        using Harness h = Build();
        Assert.DoesNotContain(h.Simulator.Routes, r => r.IsSketch);

        h.Session.AddClick(new RoomKey(1, 1));
        h.Session.AddClick(new RoomKey(1, 2));
        Assert.Contains(h.Simulator.Routes, r => r.IsSketch);
        h.Simulator.SelectedRoute = h.Simulator.Routes.First(r => r.IsSketch);
        Assert.True(h.Simulator.SimulateCommand.CanExecute(null));

        h.Simulator.SimResult = new LoopSimSummary(new[] { Run });
        h.Session.AddClick(new RoomKey(1, 1));
        Assert.Null(h.Simulator.SimResult);
    }

    [Fact]
    public void PickingARankingPutsItOnTheMapAndShowsItsResult()
    {
        using Harness h = Build();
        h.Simulator.SimResult = new LoopSimSummary(new[] { Run });

        h.Simulator.SelectedRanking = new AreaRank("Area", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { Run }));

        Assert.Equal(2, h.Session.Clicks.Count);
        Assert.Equal("Area", h.Session.ProposedName);
        Assert.True(h.Simulator.SelectedRoute?.IsSketch);
        Assert.Null(h.Simulator.SimResult);
        Assert.True(h.Simulator.HasRankDetail);
        Assert.Contains("L40", h.Simulator.RankHeadline);
        Assert.True(h.Simulator.SimulateCommand.CanExecute(null));
    }

    [Fact]
    public void SavingARankedAreaMakesARunnableLoopOfItsTourWithoutOverwritingOne()
    {
        using Harness h = Build(saveLoops: true);
        RoomKey[] tour = { new(1, 2), new(1, 1) };
        h.Loops.Save(new Loop("Hills / Caves (L40 sim)".Replace('/', '-'), tour));
        h.Simulator.SelectedRanking = new AreaRank("Hills / Caves", 40, tour, 2, new LoopSimSummary(new[] { Run }));
        Assert.True(h.Simulator.RankIsArea);

        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Loop saved = Assert.IsType<Loop>(h.Loops.Get("Hills - Caves (L40 sim) 2"));
        Assert.Equal(tour, saved.Waypoints.Select(w => w.Key));
        Assert.Contains("L40", saved.Notes);
        Assert.Contains("1/2 → 1/1", saved.Notes);
        Assert.Equal("Hills - Caves (L40 sim) 2", h.Simulator.SelectedRoute?.Loop?.Name);
    }

    [Fact]
    public void ASavedLoopRowOffersNoSave()
    {
        using Harness h = Build();
        h.Simulator.SelectedRanking = new AreaRank("Mine", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { Run }), IsLoop: true);

        Assert.False(h.Simulator.RankIsArea);
        Assert.False(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));
    }
}
