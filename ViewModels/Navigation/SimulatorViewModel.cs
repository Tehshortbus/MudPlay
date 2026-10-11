using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// The Simulator window: plays the live character (LoopSimulator) around a saved loop
// or the estimator's sketch, sets the loops they've played against the simulator
// (Check against my play), and ranks every reachable hunting area at a level. It
// lives with the Navigation window rather than the estimator session, since only
// the sketch route needs estimator mode. A route, simulation setting, character or
// game-data change drops a result as stale and cancels a run still in flight.
public sealed partial class SimulatorViewModel : ObservableObject
{
    private readonly RouteExpResolver _resolver;
    private readonly LoopManager _loops;
    private readonly GameDataCache _gameData;
    private readonly IRoomFilter? _filter;
    private readonly SimulationSource? _simulation;
    private readonly LogService? _log;
    private readonly Func<ExpEstimatorSessionViewModel?> _sketch;
    private readonly Func<IReadOnlyList<RoomKey>, string, string?> _showOnMap;
    // The picker's sketch entry, kept while the estimator has a route to offer.
    private readonly SimRouteOption _sketchOption = new("", null);
    // The walk pace the shown SimResult was run at — the bug report quotes this, not
    // a pace recomputed from gear that may have changed since.
    private double _simWalkUsed;
    private string _simRouteName = "";
    private CancellationTokenSource? _simCancel;
    // The live check's own token: a route change cancels a simulation but must leave
    // the check (which reads saved loops, not the chosen route) running.
    private CancellationTokenSource? _checkCancel;
    private CancellationTokenSource? _rankCts;

    // showOnMap loads a ranked tour into the estimator's sketch and returns null, or
    // returns why it couldn't.
    public SimulatorViewModel(
        RouteExpResolver resolver, LoopManager loops, GameDataCache gameData, IRoomFilter? filter,
        SimulationSource? simulation, LogService? log,
        Func<ExpEstimatorSessionViewModel?> sketch, Func<IReadOnlyList<RoomKey>, string, string?> showOnMap)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(showOnMap);
        _resolver = resolver;
        _loops = loops;
        _gameData = gameData;
        _filter = filter;
        _simulation = simulation;
        _log = log;
        _sketch = sketch;
        _showOnMap = showOnMap;
        RefreshRoutes();
    }

    public string RealmLabel => _gameData.ActiveRealm == RealmType.ParaMud ? "Paradigm" : "Stock";

    // ----- Route ---------

    // Saved loops by name, with the estimator's sketch first while it has a route.
    public ObservableCollection<SimRouteOption> Routes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    private SimRouteOption? _selectedRoute;

    // Set while the picker is re-listed, so re-selecting the same route keeps its result.
    private bool _relisting;

    partial void OnSelectedRouteChanged(SimRouteOption? value)
    {
        if (!_relisting) ClearSimulation();
    }

    // Rebuild the picker: the loops were saved / deleted / renamed, or the estimator
    // opened or closed. The selection survives when its route is still offered.
    public void RefreshRoutes()
    {
        SimRouteOption? keep = SelectedRoute;
        var wanted = new List<SimRouteOption>();
        if (SketchOffered()) wanted.Add(_sketchOption);
        foreach (Loop loop in _loops.Loops.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            if (loop.Waypoints.Count >= 2) wanted.Add(new SimRouteOption(loop.Name, loop));
        SimRouteOption? again = keep is null ? null
            : keep.IsSketch ? (wanted.Contains(_sketchOption) ? _sketchOption : null)
            : wanted.FirstOrDefault(o => o.Loop is { } l && string.Equals(l.Name, keep.Loop!.Name, StringComparison.OrdinalIgnoreCase));
        Relist(() =>
        {
            Routes.Clear();
            foreach (SimRouteOption o in wanted) Routes.Add(o);
            SelectedRoute = again ?? wanted.FirstOrDefault();
        });
        if (again is null) ClearSimulation();
    }

    // The estimator's route was edited: a result played on the sketch is stale, and
    // the sketch comes into (or drops out of) the picker as it gains or loses a route.
    public void SketchChanged()
    {
        if (SelectedRoute?.IsSketch == true) ClearSimulation();
        bool offered = SketchOffered(), listed = Routes.Contains(_sketchOption);
        if (offered && !listed)
        {
            // With nothing picked, the new sketch is the route; any result left is stale.
            bool pick = SelectedRoute is null;
            Relist(() =>
            {
                Routes.Insert(0, _sketchOption);
                if (pick) SelectedRoute = _sketchOption;
            });
            if (pick) ClearSimulation();
        }
        else if (!offered && listed)
        {
            bool wasSelected = SelectedRoute?.IsSketch == true;
            Relist(() =>
            {
                Routes.Remove(_sketchOption);
                if (wasSelected) SelectedRoute = Routes.FirstOrDefault();
            });
        }
    }

    // The sketch was renamed: its picker entry follows.
    public void SketchRenamed() => SketchOffered();

    // The sketch is offered while the estimator holds a route, under its name.
    private bool SketchOffered()
    {
        if (_sketch() is not { CanSave: true } sketch) return false;
        _sketchOption.Label = $"Estimator sketch — {sketch.ProposedName}";
        return true;
    }

    private void Relist(Action change)
    {
        _relisting = true;
        try { change(); }
        finally { _relisting = false; }
        SimulateCommand.NotifyCanExecuteChanged();
    }

    private (IReadOnlyList<LoopWaypoint> Waypoints, string Name)? ChosenRoute()
    {
        if (SelectedRoute is not { } route) return null;
        if (route.Loop is { } loop) return (loop.Waypoints, loop.Name);
        return _sketch() is { CanSave: true } s ? (s.SimWaypoints(), s.ProposedName) : null;
    }

    // ----- Settings ---------

    // 0 = the character's own pace (WalkSeconds).
    [ObservableProperty] private double _simSecondsPerStep;
    [ObservableProperty] private int _simLagMs = 100;
    [ObservableProperty] private double _simHours = 1.0;
    [ObservableProperty] private int _simRuns = 3;

    // A simulation setting also clears the live check, which ran under it; a route
    // change doesn't — the check reads saved loops, not the chosen route.
    partial void OnSimSecondsPerStepChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimLagMsChanged(int value) => ClearSimulation(alsoCheck: true);
    partial void OnSimHoursChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimRunsChanged(int value) => ClearSimulation(alsoCheck: true);

    // The walk between rooms the simulation uses: the user's figure when set, else
    // the character's own pace — on Paradigm the server's move timer from gear
    // quickness and encumbrance plus SimLagMs of lag (GAME_MECHANICS "Per-hop
    // movement speed"); on Stock Auto-Lair's wall-clock pace by encumbrance, lag
    // already in it. Combat is simulated separately, so this is the bare walk,
    // unlike the estimate's Seconds per room.
    public double SimWalkSeconds => SimSecondsPerStep > 0
        ? SimSecondsPerStep
        : _simulation?.WalkSeconds(Math.Max(0, SimLagMs) / 1000.0)
          ?? (_gameData.ActiveRealm == RealmType.ParaMud ? 1.1 : 0.7);

    // ----- Simulation ---------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAgainstPlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RankAreasCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSimulationCommand))]
    private bool _isSimulating;
    [ObservableProperty] private string _simStatus = "";
    [ObservableProperty] private LoopSimSummary? _simResult;
    [ObservableProperty] private string _simHeadline = "";
    public ObservableCollection<SimStat> SimStats { get; } = new();
    public bool HasSimResult => SimResult is not null;

    partial void OnSimResultChanged(LoopSimSummary? value) => OnPropertyChanged(nameof(HasSimResult));

    // Play the live character around the chosen route for SimRuns seeded runs of
    // SimHours each, off the UI thread (a run is a few hundred milliseconds of CPU per
    // hour). A run cancelled by a route / setting change or by closing the Navigation
    // window is dropped, so a late result never lands on a route it wasn't played on.
    [RelayCommand(CanExecute = nameof(CanRunSimulation))]
    private async Task SimulateAsync()
    {
        if (ChosenRoute() is not { } route) return;
        using var cancel = new CancellationTokenSource();
        _simCancel = cancel;
        try
        {
            if (_simulation?.Build(null) is not { } setup)
            {
                SimStatus = "No character yet — log in and type stat so the client knows your level and pools.";
                return;
            }
            IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(route.Waypoints, _filter);
            if (lap.Count == 0)
            {
                SimStatus = "The route has no walkable lap — fix the loop first.";
                return;
            }

            (SimCharacter character, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            IsSimulating = true;
            SimStatus = $"Simulating {route.Name}, {runs} × {hours:0.#} h…";
            CancellationToken token = cancel.Token;
            LoopSimSummary result = await Task.Run(() =>
                LoopSimulator.RunMany(character, lap, world, step, hours, runs, token), token);
            if (token.IsCancellationRequested) return;
            _simWalkUsed = step;
            _simRouteName = route.Name;
            SimResult = result;
            SimHeadline = Headline(result);
            Fill(SimStats, result, step);
            SimStatus = $"{route.Name} · {lap.Count} rooms · L{character.Level}" +
                        (character.Backstab is not null ? " · opens with a sneak backstab" : "");
            _log?.Info("Simulator",
                $"simulated '{route.Name}' ({lap.Count} rooms, {runs}×{hours:0.#}h, {step:0.##}s/step, L{character.Level}" +
                $"{(character.Backstab is not null ? ", backstab opener" : "")}): " +
                $"{result.ExpPerHour:N0} exp/hr ({result.MinExpPerHour:N0}–{result.MaxExpPerHour:N0}, " +
                $"bosses +{result.BossExpPerHour:N0}), {result.KillsPerHour:0} kills/hr, " +
                $"{result.DamageTakenPerHour:N0} dmg taken/hr, {result.FleesPerHour:0.#} flees/hr, " +
                $"{result.Deaths} death(s), {result.HangUps} hang-up(s), low HP {result.LowestHpPercent}%");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose (route or setting changed, window closed) — not an error.
        }
        catch (Exception ex)
        {
            // A simulation fault must never take the client down with it.
            _log?.Warn("Simulator", $"simulation of '{route.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            SimStatus = "Simulation failed — see the program log.";
        }
        finally
        {
            if (ReferenceEquals(_simCancel, cancel)) _simCancel = null;
            IsSimulating = false;
        }
    }

    private bool CanRunSimulation() => _simulation is not null && !IsSimulating && SelectedRoute is not null;

    // Stop a simulation or live check still running; its result is dropped. The
    // Cancel button, and the Navigation window when it closes.
    [RelayCommand(CanExecute = nameof(IsSimulating))]
    public void CancelSimulation()
    {
        _simCancel?.Cancel();
        _checkCancel?.Cancel();
    }

    // Drop the result (and stop a run still going) — the route, a simulation
    // setting, the character or the game-data set changed under it. alsoCheck also
    // drops the live check, which reads saved loops rather than the chosen route.
    public void ClearSimulation(bool alsoCheck = false)
    {
        _simCancel?.Cancel();
        SimResult = null;
        SimHeadline = "";
        SimStats.Clear();
        SimStatus = "";
        if (alsoCheck)
        {
            _checkCancel?.Cancel();
            CheckRows.Clear();
            CheckStatus = "";
            OnPropertyChanged(nameof(HasCheckRows));
        }
        SimulateCommand.NotifyCanExecuteChanged();
    }

    // ----- Check against my play ---------

    // Loops need this many live hours at one level before they're checked — a
    // shorter sample swings too far on luck to judge the simulator by.
    private const double CheckMinHours = 1.0;

    [ObservableProperty] private string _checkStatus = "";
    [ObservableProperty] private string _checkNote = "";
    public ObservableCollection<SimLiveCheckRow> CheckRows { get; } = new();
    public bool HasCheckRows => CheckRows.Count > 0;

    // Set every loop this character has played (per level, from the program logs)
    // against the simulator at that level, so the user can see how far to trust it.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckAgainstPlayAsync()
    {
        if (_simulation is null) return;
        if (string.IsNullOrWhiteSpace(_simulation.Character()))
        {
            CheckStatus = "No character yet — log in and type stat first.";
            return;
        }
        string character = _simulation.Character()!;
        using var cancel = new CancellationTokenSource();
        _checkCancel = cancel;
        CancellationToken token = cancel.Token;
        IsSimulating = true;
        CheckRows.Clear();
        OnPropertyChanged(nameof(HasCheckRows));
        CheckNote = "";
        CheckStatus = "Reading your program logs…";
        try
        {
            string dir = _simulation.LogsDir;
            IReadOnlyList<LiveLoopRecord> records = await Task.Run(() => LiveLoopSessions.Pool(
                LiveLoopSessions.ReadFolder(dir, msg => _log?.Warn("Simulator", $"live check: {msg}")),
                character, CheckMinHours), token);
            if (token.IsCancellationRequested) return;
            if (records.Count == 0)
            {
                // Program logs exist only while Auto-collect logs is on (off by default)
                // and are pruned after DebugLogWriter.DefaultRetentionDays.
                CheckStatus = $"No loop played for {CheckMinHours:0} h or more at one level in your program logs " +
                              $"(the last {DebugLogWriter.DefaultRetentionDays} days). They're only written while " +
                              "Program Log (F4) → Auto-collect logs is on — turn it on and play your loops.";
                _log?.Info("Simulator", $"live check for {character}: no loop with {CheckMinHours:0} h at one level in {dir}");
                return;
            }

            // Characters and laps are built (and frozen) here on the UI thread; the
            // simulations themselves run on a worker.
            var jobs = new List<(LiveLoopRecord Live, SimCharacter? Character, SimWorld? World, IReadOnlyList<SimRoom>? Lap, string? Problem)>();
            var laps = new Dictionary<string, IReadOnlyList<SimRoom>>(StringComparer.Ordinal);
            foreach (LiveLoopRecord r in records)
            {
                if (_loops.Get(r.Loop) is not { } loop) { jobs.Add((r, null, null, null, "loop no longer saved")); continue; }
                // One loop is often played at several levels; its lap is the same for each.
                if (!laps.TryGetValue(r.Loop, out IReadOnlyList<SimRoom>? lap))
                    laps[r.Loop] = lap = _resolver.ResolveSimLap(loop.Waypoints, _filter);
                if (lap.Count == 0) { jobs.Add((r, null, null, null, "route no longer resolves")); continue; }
                if (_simulation.Build(r.Level) is not { } setup) { jobs.Add((r, null, null, null, "no character")); continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((r, ch, world, lap, null));
            }

            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            CheckStatus = $"Simulating {jobs.Count(j => j.Problem is null)} loop(s), {runs} × {hours:0.#} h each…";
            IReadOnlyList<SimLiveCheckRow> rows = await Task.Run(() => jobs
                .Select(j => j.Problem is not null
                    ? new SimLiveCheckRow(j.Live, null, j.Problem)
                    : new SimLiveCheckRow(j.Live, LoopSimulator.RunMany(j.Character!, j.Lap!, j.World!, step, hours, runs, token)))
                .ToList(), token);
            if (token.IsCancellationRequested) return;

            foreach (SimLiveCheckRow row in rows) CheckRows.Add(row);
            OnPropertyChanged(nameof(HasCheckRows));
            // Another level is simulated with today's gear, stats and spells, so a
            // session from before an upgrade reads high for reasons the simulator
            // can't see.
            int now = _simulation.Level();
            if (rows.Any(r => r.Live.Level != now))
                CheckNote = $"Other levels are simulated with today's gear, stats and spells (you're L{now} now) — older sessions read high.";
            var diffs = rows.Where(r => r.DiffPercent is not null).Select(r => r.DiffPercent!.Value).ToList();
            CheckStatus = diffs.Count == 0 ? "" :
                $"{diffs.Count(d => Math.Abs(d) <= 10)} of {diffs.Count} within 10% · average {diffs.Average():+0.0;-0.0}%";
            _log?.Info("Simulator", $"live check for {character} ({runs}×{hours:0.#}h, {step:0.##}s/step): " +
                string.Join(" | ", rows.Select(r => r.Label)));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose (setting changed, window closed) — not an error.
        }
        catch (Exception ex)
        {
            CheckRows.Clear();
            OnPropertyChanged(nameof(HasCheckRows));
            CheckStatus = $"The check failed: {ex.Message}";
            _log?.Warn("Simulator", $"live check for {character} failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_checkCancel, cancel)) _checkCancel = null;
            IsSimulating = false;
        }
    }

    private bool CanRunCheck() => _simulation is not null && !IsSimulating;

    // ----- Area rankings ---------

    // 0 = the character's current level.
    [ObservableProperty] private int _rankLevel;
    [ObservableProperty] private string _rankStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRankingAsLoopCommand))]
    private AreaRank? _selectedRanking;
    [ObservableProperty] private string _rankHeadline = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRankingCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRankingAsLoopCommand))]
    private bool _isRanking;
    public ObservableCollection<AreaRank> Rankings { get; } = new();
    public ObservableCollection<SimStat> RankStats { get; } = new();
    public bool HasRankings => Rankings.Count > 0;
    public bool HasRankDetail => RankStats.Count > 0;
    // The settings the shown ranking ran with — its detail and a loop saved from it
    // quote these, not settings the user has changed since.
    private RankRun? _rankRun;

    private sealed record RankRun(double Step, double Hours, int Runs, RoomKey From, string Realm);

    // Every hunting area's lair tour (AreaTours, grouped by the Monsters' Region /
    // Area labels) played by the character at RankLevel, safe areas first by
    // exp/hr. Tours are mapped here on the UI thread — the room graph is rebuilt on
    // it (a set switch) and game data is read there — so a yield that lets input and
    // rendering through comes before each piece of that work (the reach search, each
    // area grouping, each tour search, each lap resolve, area and saved loop alike);
    // then the areas simulate in parallel on workers. CancelRanking (the Cancel
    // button, the window's teardown, a profile swap or a room-graph reload) stops it
    // at the next yield or between simulations.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RankAreasAsync()
    {
        if (_simulation is null || _filter is null) return;
        int level = RankLevel > 0 ? RankLevel : _simulation.Level();
        if (_simulation.Build(level) is not { } setup)
        {
            RankStatus = "No character yet — log in and type `stat` first.";
            return;
        }
        if (_simulation.Room() is not { } here)
        {
            RankStatus = "MudPlay doesn't know which room you're in yet — step into a room so it can tell which areas you can reach.";
            return;
        }
        using var cts = new CancellationTokenSource();
        _rankCts = cts;
        CancellationToken cancel = cts.Token;
        IsSimulating = true;
        IsRanking = true;
        Rankings.Clear();
        OnPropertyChanged(nameof(HasRankings));
        ClearRankDetail();
        _rankRun = null;
        try
        {
            // Only what the character could reach from here at that level: level gates
            // (a (Level 50+) exit, a minimum-level sailing) judged at `level`, every
            // other gate as the live movement filter has it, boats and portals included.
            IRoomFilter gates = new LevelIgnoringFilter(_filter, level);
            RankStatus = "Finding the areas you can reach…";
            await YieldToUi(cancel);
            IReadOnlyDictionary<RoomKey, int> reach = _resolver.DistancesFrom(here, gates, viaBoats: true);
            await YieldToUi(cancel);
            var lairs = _resolver.LairRooms().ToList();
            var reachable = lairs.Where(l => reach.ContainsKey(l.Room)).ToList();
            await YieldToUi(cancel);
            IReadOnlyList<(string Area, IReadOnlyList<RoomKey> Rooms)> groups = AreaTours.Group(
                reachable, n => AreaLabel(setup.Character.Overlay(n)));
            await YieldToUi(cancel);
            int gatedAreas = AreaTours.Group(lairs, n => AreaLabel(setup.Character.Overlay(n))).Count - groups.Count;
            var jobs = new List<(AreaTour Tour, SimCharacter Character, SimWorld World, IReadOnlyList<SimRoom> Lap, bool IsLoop)>();
            int skippedAreas = 0, skippedLoops = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                RankStatus = $"Mapping area {i + 1} of {groups.Count}…";
                (string area, IReadOnlyList<RoomKey> rooms) = groups[i];
                var distances = new Dictionary<RoomKey, IReadOnlyDictionary<RoomKey, int>>();
                foreach (RoomKey from in AreaTours.SearchSources(rooms))
                {
                    await YieldToUi(cancel);
                    distances[from] = _resolver.DistancesTo(from, rooms, gates);
                }
                AreaTour tour = AreaTours.Order(area, rooms, k => distances[k]);
                if (tour.Rooms.Count < 2) { skippedAreas++; continue; }
                await YieldToUi(cancel);
                IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(tour.Rooms.Select(k => new LoopWaypoint(k)).ToList(), gates);
                if (lap.Count == 0) { skippedAreas++; continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((tour, ch, world, lap, false));
            }

            // The user's own saved loops rank beside the area tours — a loop tuned
            // inside a good area beats that area's whole tour — with what the logs
            // say the user actually made on each.
            foreach (Loop loop in _loops.Loops)
            {
                if (loop.Waypoints.Count < 2 || !reach.ContainsKey(loop.Waypoints[0].Key)) continue;
                RankStatus = $"Mapping your loop {loop.Name}…";
                await YieldToUi(cancel);
                IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(loop.Waypoints, gates);
                if (lap.Count == 0) { skippedLoops++; continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((new AreaTour(loop.Name, loop.Waypoints.Select(w => w.Key).ToList()), ch, world, lap, true));
            }
            string? character = _simulation.Character();
            string logs = _simulation.LogsDir;
            IReadOnlyList<LiveLoopRecord> live = string.IsNullOrWhiteSpace(character)
                ? Array.Empty<LiveLoopRecord>()
                : await Task.Run(() => LiveLoopSessions.Pool(LiveLoopSessions.ReadFolder(logs), character!, CheckMinHours), cancel);

            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            string simulating = $"Simulating {jobs.Count(j => !j.IsLoop)} areas and {jobs.Count(j => j.IsLoop)} of your loops " +
                $"at L{level}, {runs} × {hours:0.#} h each";
            RankStatus = simulating + "…";
            int done = 0;
            var progress = new Progress<int>(_ =>
            {
                // A report queued behind the finish or a cancel mustn't overwrite its status.
                if (!IsRanking || cancel.IsCancellationRequested) return;
                RankStatus = $"{simulating} — {++done} of {jobs.Count} done…";
            });
            // One core is left free so the client (and the game it's playing) stays responsive.
            IReadOnlyList<AreaRank> ranked = AreaRank.Rank(await Task.Run(() => jobs.AsParallel()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
                .WithCancellation(cancel)
                .Select(j =>
                {
                    var rank = new AreaRank(j.Tour.Name, level, j.Tour.Rooms, j.Lap.Count,
                        LoopSimulator.RunMany(j.Character, j.Lap, j.World, step, hours, runs, cancel),
                        j.IsLoop, j.IsLoop ? LiveAt(live, j.Tour.Name, level) : null);
                    ((IProgress<int>)progress).Report(1);
                    return rank;
                })
                .ToList(), cancel));

            _rankRun = new RankRun(step, hours, runs, here, RealmLabel);
            foreach (AreaRank r in ranked) Rankings.Add(r);
            OnPropertyChanged(nameof(HasRankings));
            int safe = ranked.Count(r => r.Safe);
            string skipped = (skippedAreas > 0 ? $"; {skippedAreas} area(s) skipped (one lair or no walkable lap)" : "")
                + (skippedLoops > 0 ? $"; {skippedLoops} of your loops skipped (no walkable lap)" : "");
            RankStatus = $"L{level} from {here.Map}/{here.Room}: {safe} safe option(s) (areas and your loops), best first; {ranked.Count - safe} where you died listed last"
                + (gatedAreas > 0 ? $"; {gatedAreas} area(s) you can't reach at L{level} left out" : "") + skipped
                + ". Pick one to see its route and details, show it on the map, and save it as a loop to run.";
            _log?.Info("Simulator", $"ranked {ranked.Count} options at L{level} ({gatedAreas} areas unreachable, " +
                $"{skippedAreas} areas and {skippedLoops} loops skipped): " +
                string.Join(" | ", ranked.Take(10).Select(r => r.Label)));
        }
        catch (Exception ex) when (cancel.IsCancellationRequested && IsCancellation(ex))
        {
            Rankings.Clear();
            OnPropertyChanged(nameof(HasRankings));
            RankStatus = "Ranking cancelled.";
            _log?.Info("Simulator", $"area ranking at L{level} cancelled");
        }
        catch (Exception ex)
        {
            // A ranking fault must never take the client down with it.
            _log?.Warn("Simulator", $"area ranking at L{level} failed: {ex.GetType().Name}: {ex.Message}");
            Rankings.Clear();
            OnPropertyChanged(nameof(HasRankings));
            RankStatus = "Ranking failed — see the program log.";
        }
        finally
        {
            _rankCts = null;
            IsRanking = false;
            IsSimulating = false;
        }
    }

    // Stops a running RankAreas: the Cancel button, the Navigation window's teardown,
    // a profile swap, or a room-graph reload.
    [RelayCommand(CanExecute = nameof(IsRanking))]
    public void CancelRanking() => _rankCts?.Cancel();

    // PLINQ can hand a worker's cancel back wrapped in an AggregateException.
    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException
        || ex is AggregateException agg && agg.Flatten().InnerExceptions.All(e => e is OperationCanceledException);

    // Let queued input and rendering run before the next search on the UI thread.
    private static async Task YieldToUi(CancellationToken cancel)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        cancel.ThrowIfCancellationRequested();
    }

    // What the user made on a loop: their record at the ranked level, and their
    // biggest sample within a few levels of it (the long runs are the trustworthy
    // ones) when that's a different level.
    private static IReadOnlyList<LiveLoopRecord> LiveAt(IReadOnlyList<LiveLoopRecord> live, string loop, int level)
    {
        var mine = live.Where(r => string.Equals(r.Loop, loop, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = new List<LiveLoopRecord>();
        if (mine.FirstOrDefault(r => r.Level == level) is { } same) shown.Add(same);
        if (mine.Where(r => Math.Abs(r.Level - level) <= 3).OrderByDescending(r => r.Hours).FirstOrDefault() is { } big
            && !shown.Contains(big))
            shown.Add(big);
        return shown;
    }

    // "Region / Area", or just the area when the two match; null when unfiled.
    private static string? AreaLabel(Models.GameData.MonsterOverlay o) =>
        string.IsNullOrWhiteSpace(o.Area) ? null
        : !string.IsNullOrWhiteSpace(o.Region) && !string.Equals(o.Region, o.Area, StringComparison.OrdinalIgnoreCase)
            ? $"{o.Region} / {o.Area}" : o.Area;

    // A picked row shows its full result here and its route on the map, where the
    // estimator's sketch takes it — and the picker follows, so Simulate replays it
    // (at the character's current level and today's gates, not the ranked ones).
    partial void OnSelectedRankingChanged(AreaRank? value)
    {
        OnPropertyChanged(nameof(RankIsArea));
        if (value is null) { ClearRankDetail(); return; }
        RankHeadline = $"{value.Area} at L{value.Level} — {Headline(value.Result)}";
        double step = _rankRun?.Step ?? SimWalkSeconds;
        Fill(RankStats, value.Result, step);
        if (_rankRun is { } run)
            RankStats.Insert(0, new SimStat("Tested", $"L{value.Level} · {run.Runs} × {run.Hours:0.#} h · " +
                                                     $"{run.Step:0.00} s a room · {run.Realm} · from {run.From.Map}/{run.From.Room}"));
        RankStats.Insert(0, new SimStat("Route", RouteLine(value)));
        OnPropertyChanged(nameof(HasRankDetail));
        if (_showOnMap(value.Tour, value.Area) is { } why)
        {
            RankStatus = why;
            return;
        }
        RefreshRoutes();
        if (Routes.Contains(_sketchOption)) SelectedRoute = _sketchOption;
    }

    // The rooms the run walked between, in order — the waypoints a saved loop gets.
    private static string RouteLine(AreaRank r) =>
        $"{(r.IsLoop ? $"{r.Tour.Count} waypoints" : $"{r.Tour.Count} lairs")}, {r.LapRooms} rooms a lap: " +
        string.Join(" → ", r.Tour.Select(k => $"{k.Map}/{k.Room}"));

    // Save the picked area's tour as a loop the user can run: the same rooms, in the
    // same order, the simulation walked between (the runner re-routes the legs with
    // the gates as they are when it starts), with how it was tested in its notes.
    [RelayCommand(CanExecute = nameof(CanSaveRankingAsLoop))]
    private void SaveRankingAsLoop()
    {
        if (SelectedRanking is not { IsLoop: false } rank) return;
        if (_loops.SetName is null)
        {
            RankStatus = "No game-data set is active, so there's nowhere to save a loop.";
            return;
        }
        string name = FreeLoopName($"{rank.Area.Replace('/', '-')} (L{rank.Level} sim)");
        var loop = new Loop(name, rank.Tour) { Notes = LoopNotes(rank, _rankRun) };
        try
        {
            _loops.Save(loop);
        }
        catch (Exception ex)
        {
            _log?.Warn("Simulator", $"saving ranked tour '{rank.Area}' as loop '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            RankStatus = $"Couldn't save the loop: {ex.Message}";
            return;
        }
        _log?.Info("Simulator", $"saved ranked tour '{rank.Area}' (L{rank.Level}, {rank.Tour.Count} waypoints, " +
                                $"{rank.LapRooms} rooms a lap, {rank.Result.ExpPerHour:N0} exp/hr) as loop '{name}'");
        RefreshRoutes();
        if (Routes.FirstOrDefault(o => o.Loop is { } l && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } saved)
            SelectedRoute = saved;
        RankStatus = $"Saved as loop \"{name}\" — it's in the Navigation window's Loops list to run, and picked as the Route above. " +
                     "Its notes say how it was tested.";
    }

    // A ★ row is already saved, so only an area row offers Save as loop.
    public bool RankIsArea => SelectedRanking is { IsLoop: false };

    private bool CanSaveRankingAsLoop() => SelectedRanking is { IsLoop: false } && !IsRanking;

    // Saving under a taken name would overwrite that loop, so a second save of the
    // same area gets a number.
    private string FreeLoopName(string baseName)
    {
        string name = baseName;
        for (int n = 2; _loops.Get(name) is not null; n++) name = $"{baseName} {n}";
        return name;
    }

    private static string LoopNotes(AreaRank r, RankRun? run)
    {
        LoopSimSummary s = r.Result;
        string deaths = s.Deaths + s.HangUps == 0 ? "no deaths"
            : $"died or hung up in {s.Deaths + s.HangUps} of {s.Runs.Count} runs";
        var lines = new List<string>
        {
            $"Saved from the Simulator's area ranking on {DateTime.Now:yyyy-MM-dd}.",
            $"Simulated at L{r.Level}{(run is null ? "" : $" on {run.Realm}")}: {s.ExpPerHour:N0} exp/hr " +
            $"({s.MinExpPerHour:N0} – {s.MaxExpPerHour:N0} over {s.Runs.Count} runs{(run is null ? "" : $" × {run.Hours:0.#} h")}), " +
            $"{s.KillsPerHour:0} kills/hr, lowest HP {s.LowestHpPercent}%, {deaths}.",
        };
        if (run is not null)
            lines.Add($"Walk pace {run.Step:0.00} s a room; ranked from {run.From.Map}/{run.From.Room}.");
        lines.Add($"Route: {RouteLine(r)}.");
        lines.Add($"The simulation judged level gates at L{r.Level}; run it there, since the loop walks the gates as they are when it starts.");
        return string.Join("\n", lines);
    }

    private void ClearRankDetail()
    {
        RankHeadline = "";
        RankStats.Clear();
        OnPropertyChanged(nameof(HasRankDetail));
    }

    // ----- Readout ---------

    private static string Headline(LoopSimSummary r) =>
        $"≈ {r.ExpPerHour:N0} exp/hr  ({r.MinExpPerHour:N0} – {r.MaxExpPerHour:N0} over {r.Runs.Count} runs)";

    private static void Fill(ObservableCollection<SimStat> stats, LoopSimSummary r, double walkSeconds)
    {
        stats.Clear();
        foreach (SimStat s in Stats(r, walkSeconds)) stats.Add(s);
    }

    private static IEnumerable<SimStat> Stats(LoopSimSummary r, double walkSeconds)
    {
        yield return new SimStat("Kills", $"{r.KillsPerHour:0} an hour");
        yield return new SimStat("Lap", r.AvgLapSeconds > 0 ? $"{r.AvgLapSeconds:0} s" : "no lap finished");
        yield return new SimStat("Walking", $"{walkSeconds:0.00} s a room");
        yield return new SimStat("Time", $"attacking {r.Share(x => x.AttackingSeconds):P0} · moving {r.Share(x => x.MovingSeconds):P0} · " +
                                         $"resting {r.Share(x => x.RestingSeconds):P0} · meditating {r.Share(x => x.MeditatingSeconds):P0} · " +
                                         $"waiting {r.Share(x => x.WaitingSeconds):P0}");
        yield return new SimStat("Lowest", $"HP {r.LowestHpPercent}% · mana {r.LowestManaPercent}%");
        yield return new SimStat("Damage taken", $"{r.DamageTakenPerHour:N0} an hour");
        yield return new SimStat("Deaths", r.Deaths == 0 ? "none" : $"died in {r.Deaths} of {r.Runs.Count} runs");
        if (r.HangUps > 0) yield return new SimStat("Hang-ups", $"{r.HangUps} of {r.Runs.Count} runs");
        if (r.FleesPerHour > 0) yield return new SimStat("Flees", $"{r.FleesPerHour:0.#} an hour");
        foreach (ExpBossStat b in r.Bosses ?? Array.Empty<ExpBossStat>())
            yield return new SimStat($"Boss {b.Name}", $"+{b.ExpPerHour:N0}/hr (once per {b.RegenHours:0.#}h, not fought in the runs)");
        var casts = r.CastsPerHour();
        if (casts.Count > 0)
            yield return new SimStat("Per hour", string.Join(", ", casts.Take(8).Select(c => $"{c.Spell} {c.PerHour:0}")));
    }

    // Frozen for a bug-report capture, pre-formatted so the report is a straight print.
    public SimulatorSnapshot ToSnapshot()
    {
        static string Line(SimStat s) => $"{s.Label}: {s.Value}";
        return new SimulatorSnapshot(
            SelectedRoute?.Label, RealmLabel,
            SimResult is null ? null : SimStats.Select(Line).Prepend(SimHeadline).Prepend(SimStatus).ToList(),
            SimResult is null ? 0 : _simWalkUsed, SimHours, SimRuns,
            CheckRows.Count == 0 ? null
                : CheckRows.Select(r => r.Label).Prepend(CheckStatus).Append(CheckNote)
                    .Where(l => !string.IsNullOrEmpty(l)).ToList(),
            // The status alone still reports a ranking that was cancelled, failed or found nothing.
            Rankings.Count == 0 && string.IsNullOrEmpty(RankStatus) ? null
                : Rankings.Take(15).Select(r => r.Label).Prepend(RankStatus)
                    .Concat(RankStats.Count == 0 ? Array.Empty<string>()
                        : RankStats.Select(s => "  " + Line(s)).Prepend($"Picked: {RankHeadline}"))
                    .Where(l => !string.IsNullOrEmpty(l)).ToList(),
            SimResult is null ? null : _simRouteName);
    }
}

// One route the Simulator window can play: a saved loop, or (Loop null) the
// estimator's sketch.
public sealed partial class SimRouteOption(string label, Loop? loop) : ObservableObject
{
    [ObservableProperty] private string _label = label;
    public Loop? Loop { get; } = loop;
    public bool IsSketch => Loop is null;
    public override string ToString() => Label;
}

// One labelled figure of a simulation result.
public sealed record SimStat(string Label, string Value);
