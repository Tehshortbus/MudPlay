using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// View-model for the Phase 7 <c>NavigationWindow</c>. PR 7.10 ships
/// the shell — status strip + mode bar + placeholder layout; the
/// per-section view-models for map / room tree / favourites / loop
/// builder land in PRs 7.11–7.17 and plug into this shell as
/// child VMs.
/// </summary>
public sealed partial class NavigationViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;

    public NavigationViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _services.RoomTracker.StateChanged += OnTrackerStateChanged;
        _services.Walker.Event += OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged += OnPauseChanged;
        _services.RoomGraph.GraphReloaded += OnGraphReloaded;
        _services.Loops.LoopsChanged += OnLoopsChanged;
        _services.LoopRunner.Event += OnLoopRunnerEvent;
        _services.Movement.AvoidedChanged += OnAvoidedChanged;
        OnAvoidedChanged();
        _services.AutoLair.MarkedChanged += OnAutoLairMarkedChanged;
        _services.AutoLair.ActiveChanged += OnAutoLairActiveChanged;
        _services.RoomBlacklist.Changed   += OnBlacklistChanged;
        OnAutoLairMarkedChanged();
        IsAutoLairing = _services.AutoLair.IsActive;
        Graph = _services.RoomGraph;
        _services.Macros.Macros.CollectionChanged += OnMacrosCollectionChanged;
        RefreshFromTracker();
        RefreshFromWalker();
        RefreshLayout();
        RefreshLoops();
        RefreshCrawlerChords();
    }

    public void Dispose()
    {
        _services.RoomTracker.StateChanged -= OnTrackerStateChanged;
        _services.Walker.Event -= OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged -= OnPauseChanged;
        _services.RoomGraph.GraphReloaded -= OnGraphReloaded;
        _services.Loops.LoopsChanged -= OnLoopsChanged;
        _services.LoopRunner.Event -= OnLoopRunnerEvent;
        _services.Movement.AvoidedChanged -= OnAvoidedChanged;
        _services.AutoLair.MarkedChanged -= OnAutoLairMarkedChanged;
        _services.AutoLair.ActiveChanged -= OnAutoLairActiveChanged;
        _services.RoomBlacklist.Changed   -= OnBlacklistChanged;
        _services.Macros.Macros.CollectionChanged -= OnMacrosCollectionChanged;
    }

    private void OnMacrosCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshCrawlerChords();

    private void RefreshCrawlerChords()
    {
        // Find first enabled macro whose Command sends a bare "u" or
        // "d" (ignoring whitespace + case). If none, fall back to the
        // historic PageUp / PageDown defaults so the crawler always
        // has SOMETHING bound for floor stepping.
        UpStepChord   = FindChordForDirectionCommand("u") ?? new(Avalonia.Input.Key.PageUp);
        DownStepChord = FindChordForDirectionCommand("d") ?? new(Avalonia.Input.Key.PageDown);
    }

    private FujinTerm.Models.Profile.KeyChord? FindChordForDirectionCommand(string direction)
    {
        foreach (FujinTerm.Models.GameData.Macro m in _services.Macros.Macros)
        {
            if (!m.Enabled) continue;
            string cmd = m.Command?.Trim() ?? string.Empty;
            if (!string.Equals(cmd, direction, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Enum.TryParse<Avalonia.Input.Key>(m.Key, ignoreCase: true, out Avalonia.Input.Key avk)) continue;
            return new FujinTerm.Models.Profile.KeyChord(avk, m.Ctrl, m.Shift, m.Alt);
        }
        return null;
    }

    private void OnAutoLairMarkedChanged()
        => AutoLairRooms = new HashSet<RoomKey>(_services.AutoLair.Marked);

    private void OnAutoLairActiveChanged(bool active)
    {
        IsAutoLairing = active;
        RefreshEngineActionLabel();
    }

    [RelayCommand]
    private void ToggleAutoLair()
    {
        if (_services.AutoLair.IsActive) _services.AutoLair.Stop();
        else _services.AutoLair.Start();
    }

    [RelayCommand]
    private void ToggleContextRoomAutoLair()
    {
        if (ContextRoomKey is { } k) _services.AutoLair.Toggle(k);
    }

    /// <summary>
    /// Right-click → "Add this room to Blacklist". Captures the
    /// selected room's <see cref="Room.DisplayName"/> from the
    /// active set's Rooms.json (NOT the player's current-room name)
    /// so the Modify-Blacklist dialog later shows a human label.
    /// Immediate persist + map redraw (the store fires Changed
    /// which invalidates the BFS layout cache).
    /// </summary>
    [RelayCommand]
    private void AddContextRoomToBlacklist()
    {
        if (ContextRoomKey is not { } k) return;
        string name = _services.RoomGraph.GetRoom(k)?.DisplayName ?? "???";
        _services.RoomBlacklist.Add(k, name);
    }

    private void OnAvoidedChanged()
    {
        AvoidedRooms = new HashSet<RoomKey>(_services.Movement.Avoided);
    }

    private void OnLoopRunnerEvent(LoopEvent _)
    {
        OnPropertyChanged(nameof(IsLoopRunning));
        RefreshLoopOverlays();
        RefreshEngineActionLabel();
    }

    private void RefreshLoopOverlays()
    {
        Game.Map.LoopRunner runner = _services.LoopRunner;
        if (runner.CurrentLoop is not { } loop
            || _services.RoomTracker.State.CurrentRoom is not { } current)
        {
            LoopPath = null;
            LoopSequenceNumbers = null;
            return;
        }

        // Resolve the loop's MoveLoopSteps into a room-key sequence.
        IReadOnlyList<RoomKey> keys = runner.ResolveLoopRoomKeys(current.Key);
        LoopPath = keys.Count >= 2 ? keys : null;

        // Sequence numbers: 1..N at each successive room. Duplicate
        // keys (loops with revisits) keep the LAST sighting's number
        // — matches MudProxy's convention.
        var seq = new Dictionary<RoomKey, int>();
        for (int i = 0; i < keys.Count; i++) seq[keys[i]] = i + 1;
        LoopSequenceNumbers = seq;
    }

    // ----- Status strip ---------------------------------------------

    [ObservableProperty] private string _statusLabel = "Unknown";
    [ObservableProperty] private string _statusBadgeBrush = "#888";
    [ObservableProperty] private string _currentRoomLabel = "—";
    [ObservableProperty] private bool _isPaused;

    // ----- Highlight chips + legend (PR 7.17) -----------------------

    [ObservableProperty] private bool _highlightLairs = true;
    [ObservableProperty] private bool _highlightShops = true;
    [ObservableProperty] private bool _highlightSpells = true;
    [ObservableProperty] private bool _legendVisible;

    [RelayCommand] private void ToggleLairs()  => HighlightLairs  = !HighlightLairs;
    [RelayCommand] private void ToggleShops()  => HighlightShops  = !HighlightShops;
    [RelayCommand] private void ToggleSpells() => HighlightSpells = !HighlightSpells;
    [RelayCommand] private void ToggleLegend() => LegendVisible   = !LegendVisible;

    // ----- Map binding ----------------------------------------------

    [ObservableProperty] private RoomLayout? _layout;
    [ObservableProperty] private RoomKey? _currentRoomKey;
    [ObservableProperty] private RoomKey? _destinationRoomKey;

    /// <summary>
    /// Top-of-strip action label that replaces the redundant
    /// status-badge + current-room label (current room lives in the
    /// main UI's bottom status bar as the source-of-truth). Reads:
    /// <c>"Idle"</c> when no engine is moving; <c>"Walking to {dest}"</c>
    /// while the walker is active; <c>"Looping: {name}"</c> while
    /// the loop runner is active; <c>"Auto-Lair"</c> while the
    /// scheduler is driving.
    /// </summary>
    [ObservableProperty] private string _engineActionLabel = "Idle";
    [ObservableProperty] private RoomGraphManager? _graph;
    [ObservableProperty] private IReadOnlyList<RoomKey>? _walkPath;
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopPath;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _avoidedRooms;
    [ObservableProperty] private IReadOnlyDictionary<RoomKey, int>? _loopSequenceNumbers;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _autoLairRooms;
    [ObservableProperty] private bool _isAutoLairing;
    [ObservableProperty] private RoomKey? _selectedRoomKey;

    // Map crawler floor-step chords — kept in sync with the user's
    // u / d movement macros (Settings → Macros). When the user has
    // no macro for either direction we fall back to the MapControl's
    // default chord (PageUp / PageDown).
    [ObservableProperty] private FujinTerm.Models.Profile.KeyChord _upStepChord
        = new(Avalonia.Input.Key.PageUp);
    [ObservableProperty] private FujinTerm.Models.Profile.KeyChord _downStepChord
        = new(Avalonia.Input.Key.PageDown);

    // ----- Search ---------------------------------------------------

    [ObservableProperty] private string _searchQuery = string.Empty;

    /// <summary>Top 50 matches by name (case-insensitive substring), sorted by step distance then name.</summary>
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    partial void OnSearchQueryChanged(string value) => RebuildSearchResults(value);

    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        if (Graph is null) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        string needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 2) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        // FindByName is exact-match; we want substring. Scan the live
        // graph instead. (The graph is one realm — typically <2000
        // rooms — so the per-keystroke scan is cheap.)
        RoomKey? sourceKey = CurrentRoomKey;
        List<RoomSearchResult> matches = new();
        foreach (Room room in EnumerateAllRooms(Graph))
        {
            // BBS-tier blacklist excludes rooms from search exactly as
            // it does from the map render — keeping search consistent
            // with what's visible is the whole point of the feature.
            if (_services.RoomBlacklist.IsBlacklisted(room.Key)) continue;

            // Match against Name (raw, may be empty) AND DisplayName so
            // typing "???" surfaces unnamed rooms the player wants to
            // visit and fix; named rooms still match their text.
            if (room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
             || room.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                int? steps = sourceKey is { } src
                    ? _services.Bfs.DistanceBetween(src, room.Key, _services.Movement)
                    : null;
                matches.Add(new RoomSearchResult(room.Key, room.DisplayName, steps));
                if (matches.Count >= 200) break;     // cap before sort
            }
        }
        foreach (RoomSearchResult m in matches
                     .OrderBy(m => m.StepsFromCurrent ?? int.MaxValue)
                     .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(50))
        {
            SearchResults.Add(m);
        }
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private static IEnumerable<Room> EnumerateAllRooms(RoomGraphManager graph) => graph.Rooms;

    [RelayCommand]
    private void SelectSearchResult(RoomSearchResult? result)
    {
        if (result is null) return;
        // Re-layout from the selected room so the map pans to it.
        Layout = _services.Bfs.BuildLayout(result.Key);
        SearchQuery = string.Empty;
    }

    // ----- Loops list (PR 7.13) -------------------------------------

    /// <summary>Loops in the active BBS, ordered by LoopManager (alphabetical).</summary>
    public ObservableCollection<LoopRowViewModel> Loops { get; } = new();

    public bool HasLoops => Loops.Count > 0;

    private void OnLoopsChanged() => RefreshLoops();

    private void RefreshLoops()
    {
        Loops.Clear();
        foreach (Loop loop in _services.Loops.Loops)
            Loops.Add(new LoopRowViewModel(loop));
        OnPropertyChanged(nameof(HasLoops));
    }

    [RelayCommand]
    private void RunLoop(LoopRowViewModel? row)
    {
        if (row is null) return;
        if (_services.LoopRunner.Start(row.Source))
            _services.Loops.NoteRun(row.Source.Name);
    }

    [RelayCommand]
    private void StopLoop() => _services.LoopRunner.Stop();

    public bool IsLoopRunning => _services.LoopRunner.State != LoopState.Idle;

    // ----- Room context menu (PR 7.14) -------------------------------

    /// <summary>Room currently surfaced in the context menu (set by the map's right-click handler).</summary>
    [ObservableProperty] private RoomKey? _contextRoomKey;

    /// <summary>Name of the context room — empty when none is selected.</summary>
    public string ContextRoomName =>
        ContextRoomKey is { } k && Graph?.GetRoom(k) is { } r ? r.Name : "(unknown)";

    partial void OnContextRoomKeyChanged(RoomKey? value)
    {
        OnPropertyChanged(nameof(ContextRoomName));
        OnPropertyChanged(nameof(ContextIsAvoided));
        OnPropertyChanged(nameof(ContextIsStash));
    }

    public bool ContextIsAvoided => ContextRoomKey is { } k && _services.Movement.IsAvoided(k);
    public bool ContextIsStash   => ContextRoomKey is { } k && _services.Movement.IsStash(k);

    [RelayCommand]
    private void WalkToContextRoom()
    {
        if (ContextRoomKey is { } k) _services.Walker.WalkTo(k);
    }

    [RelayCommand]
    private void SetContextRoomLocated()
    {
        if (ContextRoomKey is { } k) _services.RoomTracker.SetLocated(k);
    }

    [RelayCommand]
    private void ToggleContextRoomAvoided()
    {
        if (ContextRoomKey is not { } k) return;
        if (_services.Movement.IsAvoided(k)) _services.Movement.UnmarkAvoided(k);
        else _services.Movement.MarkAvoided(k);
        OnPropertyChanged(nameof(ContextIsAvoided));
    }

    [RelayCommand]
    private void ToggleContextRoomStash()
    {
        if (ContextRoomKey is not { } k) return;
        if (_services.Movement.IsStash(k)) _services.Movement.UnmarkStash(k);
        else _services.Movement.MarkStash(k);
        OnPropertyChanged(nameof(ContextIsStash));
    }

    // ----- Mode bar -------------------------------------------------
    //
    // PR 7.10 ships the visual toggles; the click handlers route
    // through commands that PR 7.15 / 7.18 will hook up to the
    // LoopBuilderSessionViewModel and AutoLairOverlayViewModel.
    [ObservableProperty] private NavigationMode _currentMode = NavigationMode.Idle;

    public bool IsLoopMode => CurrentMode == NavigationMode.LoopBuild;
    public bool IsLairMode => CurrentMode == NavigationMode.AutoLair;

    partial void OnCurrentModeChanged(NavigationMode value)
    {
        OnPropertyChanged(nameof(IsLoopMode));
        OnPropertyChanged(nameof(IsLairMode));
    }

    /// <summary>Active loop-builder session when <see cref="CurrentMode"/> == LoopBuild; null otherwise.</summary>
    public LoopBuilderSessionViewModel? LoopBuilder { get; private set; }

    public bool IsLoopBuilding => LoopBuilder is not null;

    [RelayCommand]
    private void ToggleLoopMode()
    {
        if (CurrentMode == NavigationMode.LoopBuild)
        {
            LoopBuilder = null;
            CurrentMode = NavigationMode.Idle;
        }
        else
        {
            LoopBuilder = new LoopBuilderSessionViewModel(
                _services.Loops, _services.RoomGraph, _services.Movement);
            CurrentMode = NavigationMode.LoopBuild;
        }
        OnPropertyChanged(nameof(LoopBuilder));
        OnPropertyChanged(nameof(IsLoopBuilding));
    }

    /// <summary>Called by the window when the map is left-clicked. Forwards to the loop builder when active.</summary>
    public void OnRoomLeftClicked(RoomKey key)
    {
        LoopBuilder?.AddClick(key);
    }

    /// <summary>
    /// Called by the window when the map crawler hits an up/down
    /// exit. Rebuilds the layout from the new room so the user can
    /// continue crawling on the new floor.
    /// </summary>
    public void OnFloorChangeRequested(RoomKey newOrigin)
    {
        if (_services.RoomGraph.GetRoom(newOrigin) is null) return;
        // Set the selection BEFORE swapping the Layout. MapControl's
        // LayoutProperty.Changed handler centres on
        // `SelectedRoomKey ?? CurrentRoomKey`; if we updated the layout
        // first the handler would see the old floor's selection (not in
        // the new layout) and the centre call would no-op. Selection-
        // change has no auto-centre on its own, so the order matters.
        SelectedRoomKey = newOrigin;
        Layout = _services.Bfs.BuildLayout(newOrigin);
    }

    [RelayCommand]
    private void SaveLoopBuilder()
    {
        LoopBuilder?.Save();
        // Stay in loop mode so the user can immediately start another loop.
    }

    [RelayCommand]
    private void DiscardLoopBuilder()
    {
        LoopBuilder?.Clear();
    }

    [RelayCommand]
    private void ToggleLairMode()
        => CurrentMode = CurrentMode == NavigationMode.AutoLair
            ? NavigationMode.Idle : NavigationMode.AutoLair;

    // ----- Walker controls (mirrored on the top-right Run/Stop) -----

    [ObservableProperty] private bool _isWalking;

    [RelayCommand]
    private void StopWalk() => _services.Walker.Stop("user stop from Navigation");

    [RelayCommand]
    private void PauseOrResume()
    {
        if (_services.MovementCoordinator.IsPaused)
            _services.Walker.Resume();
        else
            _services.Walker.Pause();
    }

    // ----- handlers --------------------------------------------------

    private void OnTrackerStateChanged(RoomTransition _) => RefreshFromTracker();
    private void OnWalkerEvent(WalkEvent _) => RefreshFromWalker();
    private void OnPauseChanged(bool paused) => IsPaused = paused;

    private void RefreshFromTracker()
    {
        RoomState state = _services.RoomTracker.State;
        // Suspect is internal-only — UI stays green to avoid churn on
        // transient observation glitches; replay-recovery or a fresh
        // Confirmed obs resolves it without user intervention. Only
        // Lost surfaces as an alarm.
        (StatusLabel, StatusBadgeBrush) = state.Confidence switch
        {
            RoomConfidence.Confirmed => ("Located", "#3DDC97"),
            RoomConfidence.Suspect   => ("Located", "#3DDC97"),
            RoomConfidence.Pending   => ("Pending", "#F8B500"),
            RoomConfidence.Lost      => ("Lost",    "#F25C54"),
            _                        => ("Unknown", "#888"),
        };
        CurrentRoomLabel = state.CurrentRoom is { } room
            ? $"{room.DisplayName}  ·  {room.Key}"
            : "—";

        // Re-centre the layout on the new room when we land on one
        // outside the cached layout (typical after a reconnect or a
        // big walk). The map control re-fits visually via its own
        // FitToCurrent helper when the binding changes.
        CurrentRoomKey = state.CurrentRoom?.Key;
        if (state.CurrentRoom is { } here)
        {
            // Standard rebuild when the new room isn't in the cached
            // layout. ALSO rebuild when the layout's origin is a
            // blacklisted room and the new room isn't — the player
            // just exited a blacklisted-origin layout, so the next
            // build should re-root on the (non-blacklisted) new
            // current room and the previously-shown blacklisted
            // origin becomes a hidden target like any other.
            bool exitedBlacklistedOrigin =
                Layout is not null
                && !here.Key.Equals(Layout.Origin)
                && _services.RoomBlacklist.IsBlacklisted(Layout.Origin)
                && !_services.RoomBlacklist.IsBlacklisted(here.Key);
            if (Layout is null
                || !Layout.Positions.ContainsKey(here.Key)
                || exitedBlacklistedOrigin)
            {
                Layout = _services.Bfs.BuildLayout(here.Key);
            }
        }
    }

    private void OnGraphReloaded() => RefreshLayout();

    /// <summary>
    /// Blacklist Changed → rebuild the cached layout (BFS already
    /// flushed its cache via AppServices wiring) and the room
    /// search results in case a search is currently typed.
    /// </summary>
    private void OnBlacklistChanged()
    {
        RefreshLayout();
        RebuildSearchResults(SearchQuery);
    }

    private void RefreshLayout()
    {
        Graph = _services.RoomGraph;

        // Origin priority:
        //   1. Tracker's current room (live in-game).
        //   2. Profile.LastKnownRoom (where the player was at end of
        //      the last session). Lets the map open already centred
        //      on the player without waiting for a live locate.
        //   3. First room in the active graph (typically Map 1 /
        //      Room 1) — first-launch / fresh-profile fallback.
        RoomKey? key = _services.RoomTracker.State.CurrentRoom?.Key;
        if (key is null && _services.Profile.Current?.LastKnownRoom is { } last
            && _services.RoomGraph.GetRoom(new RoomKey(last.Map, last.Room)) is not null)
        {
            key = new RoomKey(last.Map, last.Room);
        }
        if (key is null && _services.RoomGraph.RoomCount > 0)
        {
            foreach (Room first in _services.RoomGraph.Rooms)
            {
                key = first.Key;
                break;
            }
        }
        Layout = key is { } k ? _services.Bfs.BuildLayout(k) : null;
    }

    private void RefreshFromWalker()
    {
        IsWalking = _services.Walker.State == WalkState.Walking
                 || _services.Walker.State == WalkState.Paused;

        WalkPath = IsWalking
            ? _services.Walker.RemainingRoomKeys
            : null;
        DestinationRoomKey = IsWalking ? _services.Walker.Destination : null;
        RefreshEngineActionLabel();
    }

    private void RefreshEngineActionLabel()
    {
        // Priority: Auto-Lair (drives walker/loop internally) →
        // Looping (named) → Walking (with destination room) → Idle.
        if (_services.AutoLair.IsActive)
        {
            EngineActionLabel = "Auto-Lair";
            return;
        }
        if (_services.LoopRunner.State != LoopState.Idle
            && _services.LoopRunner.CurrentLoop is { } loop)
        {
            EngineActionLabel = $"Looping: {loop.Name}";
            return;
        }
        if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
        {
            string dest = _services.Walker.Destination is { } key
                ? (_services.RoomGraph.GetRoom(key) is { } room
                    ? $"{room.DisplayName} ({key})"
                    : key.ToString())
                : "?";
            string verb = _services.Walker.State == WalkState.Paused ? "Paused walking" : "Walking";
            EngineActionLabel = $"{verb} to {dest}";
            return;
        }
        EngineActionLabel = "Idle";
    }
}

/// <summary>One of the four explicit modes the Navigation window can be in.</summary>
public enum NavigationMode
{
    Idle = 0,
    LoopBuild = 1,
    AutoLair = 2,
}
