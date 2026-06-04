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
    {
        AutoLairRooms = new HashSet<RoomKey>(_services.AutoLair.Marked);
        RefreshDerivedState();
    }

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

    /// <summary>
    /// Destination armed for the Run button. Set by selecting a room from
    /// the search dropdown (or clicking one in the room context menu's
    /// "queue" command later); cleared on Run / Stop. When non-null, the
    /// top-bar destination chip shows its display name + key and the Run
    /// button is enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueuedDestinationLabel))]
    [NotifyPropertyChangedFor(nameof(HasQueuedDestination))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private RoomKey? _queuedDestination;

    public bool HasQueuedDestination => QueuedDestination is not null;

    /// <summary>Display string for the top-bar chip: <c>"Name 1/123"</c> when set.</summary>
    public string QueuedDestinationLabel
    {
        get
        {
            if (QueuedDestination is not { } k) return string.Empty;
            Room? r = Graph?.GetRoom(k);
            string name = r?.DisplayName ?? "???";
            return $"{name} {k.Map}/{k.Room}";
        }
    }

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
        // Re-layout from the selected room so the map pans to it AND
        // arm the Run button by queuing the destination — clicking Run
        // walks there, mirroring the right-click → "Walk here" path.
        Layout = _services.Bfs.BuildLayout(result.Key);
        SelectedRoomKey = result.Key;
        QueuedDestination = result.Key;
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

    /// <summary>
    /// Window listens and forwards to <c>MapControl.RecenterOnPlayer()</c>.
    /// The VM can't call the control directly so we route through this
    /// event — same pattern the right-click menu uses for other map-
    /// only operations.
    /// </summary>
    public event Action? CenterOnPlayerRequested;

    /// <summary>
    /// Right-click → "Center on Player". Re-centres the map on the live
    /// current room and clears the 10 s browse-suppression window so
    /// subsequent live moves resume auto-centring. Same as the Home key.
    /// </summary>
    [RelayCommand]
    private void CenterOnPlayer() => CenterOnPlayerRequested?.Invoke();

    /// <summary>
    /// Right-click → "Center on…". Opens the two-int (map / room) input
    /// dialog; on commit, routes through <see cref="OnFloorChangeRequested"/>
    /// so the BFS layout rebuilds from the chosen room and the map
    /// centres on it. Cancel / X dismisses without changing the view.
    /// </summary>
    [RelayCommand]
    private async Task CenterOnSpecificAsync()
    {
        ManualCenterDialogViewModel vm = new(_services.RoomGraph);
        RoomKey? result = await _services.Dialogs
            .OpenWindowAsync<ManualCenterDialogViewModel, RoomKey?>(vm);
        if (result is { } k) OnFloorChangeRequested(k);
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
        RefreshDerivedState();
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

    private void OnTrackerStateChanged(RoomTransition _)
    {
        RefreshFromTracker();
        RefreshDerivedState();
    }
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
            EngineActionKind = NavigationEngineKind.AutoLair;
        }
        else if (_services.LoopRunner.State != LoopState.Idle
            && _services.LoopRunner.CurrentLoop is { } loop)
        {
            EngineActionLabel = $"Looping: {loop.Name}";
            EngineActionKind = NavigationEngineKind.Looping;
        }
        else if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
        {
            string dest = _services.Walker.Destination is { } key
                ? (_services.RoomGraph.GetRoom(key) is { } room
                    ? $"{room.DisplayName} ({key})"
                    : key.ToString())
                : "?";
            string verb = _services.Walker.State == WalkState.Paused ? "Paused walking" : "Walking";
            EngineActionLabel = $"{verb} to {dest}";
            EngineActionKind = NavigationEngineKind.Walking;
        }
        else
        {
            EngineActionLabel = "Idle";
            EngineActionKind = NavigationEngineKind.Idle;
        }

        RefreshDerivedState();
    }

    // ----- Run / Stop + mode-button state ---------------------------

    /// <summary>
    /// Which engine is currently driving — feeds top-bar status badge,
    /// CURRENT NAV section rendering, and Run/Stop button behaviour.
    /// </summary>
    [ObservableProperty] private NavigationEngineKind _engineActionKind = NavigationEngineKind.Idle;

    /// <summary>True when any movement engine is actively driving the player.</summary>
    public bool IsAnyExecuting =>
        EngineActionKind != NavigationEngineKind.Idle;

    /// <summary>Run button enabled when idle and something is queued, OR when active (then it acts as Stop).</summary>
    public bool CanRun =>
        IsAnyExecuting
        || QueuedDestination is not null
        || (CurrentMode == NavigationMode.LoopBuild && LoopBuilder?.CanSave == true)
        || (CurrentMode == NavigationMode.AutoLair && _services.AutoLair.Marked.Count > 0);

    /// <summary>Button face: <c>"Run"</c> when idle, <c>"Stop"</c> while any engine runs.</summary>
    public string RunStopLabel => IsAnyExecuting ? "Stop" : "Run";

    /// <summary>
    /// Status text used by the top-bar status indicator. Idle →
    /// <c>"Located: (M/R) - Name"</c>; active → walking with destination
    /// formatted (M/R) - Name; looping with loop name; auto-lair with
    /// marked-lair count.
    /// </summary>
    public string TopBarStatusText
    {
        get
        {
            switch (EngineActionKind)
            {
                case NavigationEngineKind.Walking:
                {
                    string dest = _services.Walker.Destination is { } k
                        ? FormatRoomRef(k)
                        : "?";
                    int total = _services.Walker.StepCount;
                    int idx = _services.Walker.CurrentStepIndex;
                    return total > 0
                        ? $"to {dest} {idx + 1}/{total}"
                        : $"to {dest}";
                }
                case NavigationEngineKind.Looping:
                {
                    string name = _services.LoopRunner.CurrentLoop?.Name ?? "?";
                    return $"{name}";
                }
                case NavigationEngineKind.AutoLair:
                {
                    int n = _services.AutoLair.Marked.Count;
                    return $"cycling {n} marked lair{(n == 1 ? "" : "s")}";
                }
                default:
                {
                    Room? here = _services.RoomTracker.State.CurrentRoom;
                    return here is null ? "Located: —" : $"Located: {FormatRoomRef(here.Key)}";
                }
            }
        }
    }

    /// <summary>
    /// Canonical room reference format used across the Navigation
    /// surfaces — <c>"(map/room) - Name"</c>. Falls back to "(M/R) - ???"
    /// when the graph doesn't know the room (typical of unimported
    /// MDB sets or null-name ganghouse rooms).
    /// </summary>
    private string FormatRoomRef(RoomKey key)
    {
        string name = Graph?.GetRoom(key)?.DisplayName ?? "???";
        return $"({key.Map}/{key.Room}) - {name}";
    }

    /// <summary>Short tag the badge displays: WALKING / LOOPING / AUTO-LAIR / LOCATED.</summary>
    public string TopBarStatusBadge => EngineActionKind switch
    {
        NavigationEngineKind.Walking  => "WALKING",
        NavigationEngineKind.Looping  => "LOOPING",
        NavigationEngineKind.AutoLair => "AUTO-LAIR",
        _                             => "LOCATED",
    };

    public string TopBarStatusBadgeBrush => EngineActionKind switch
    {
        NavigationEngineKind.Walking  => "AccentGreenBrush",
        NavigationEngineKind.Looping  => "AccentCyanBrush",
        NavigationEngineKind.AutoLair => "AccentAmberBrush",
        _                             => "AccentGreenBrush",
    };

    /// <summary>Loop-mode button face: idle → "Loop mode"; mode-on → "Building"; running → "Stop".</summary>
    public string LoopModeButtonLabel => EngineActionKind == NavigationEngineKind.Looping
        ? "Stop"
        : (CurrentMode == NavigationMode.LoopBuild ? "Building" : "Loop mode");

    public bool LoopModeButtonIsStop => EngineActionKind == NavigationEngineKind.Looping;

    /// <summary>Lair-mode button face: idle → "Lair mode"; mode-on → "Marking"; running → "Stop".</summary>
    public string LairModeButtonLabel => EngineActionKind == NavigationEngineKind.AutoLair
        ? "Stop"
        : (CurrentMode == NavigationMode.AutoLair ? "Marking" : "Lair mode");

    public bool LairModeButtonIsStop => EngineActionKind == NavigationEngineKind.AutoLair;

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(IsAnyExecuting));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunStopLabel));
        OnPropertyChanged(nameof(TopBarStatusText));
        OnPropertyChanged(nameof(TopBarStatusBadge));
        OnPropertyChanged(nameof(TopBarStatusBadgeBrush));
        OnPropertyChanged(nameof(LoopModeButtonLabel));
        OnPropertyChanged(nameof(LoopModeButtonIsStop));
        OnPropertyChanged(nameof(LairModeButtonLabel));
        OnPropertyChanged(nameof(LairModeButtonIsStop));
        OnPropertyChanged(nameof(CurrentNavHeader));
        OnPropertyChanged(nameof(CurrentNavProgress));
        OnPropertyChanged(nameof(CurrentNavHasProgress));
        RebuildCurrentNavRows();
        OnPropertyChanged(nameof(CurrentNavSelectedRow));
    }

    /// <summary>
    /// Unified Run / Stop button. Behaviour by state:
    /// <list type="bullet">
    /// <item>Active (any engine) → stops it.</item>
    /// <item>Loop builder open with savable session → save + run.</item>
    /// <item>Auto-Lair mode with marked rooms → start the scheduler.</item>
    /// <item>Otherwise, walk to the queued destination.</item>
    /// </list>
    /// </summary>
    [RelayCommand]
    private void RunStop()
    {
        // Stop path first — if anything is moving, the button stops it.
        if (_services.AutoLair.IsActive)
        {
            _services.AutoLair.Stop();
            return;
        }
        if (_services.LoopRunner.State != LoopState.Idle)
        {
            _services.LoopRunner.Stop();
            return;
        }
        if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
        {
            _services.Walker.Stop("user stop from Navigation");
            return;
        }

        // Start path — pick the queued action by current mode.
        if (CurrentMode == NavigationMode.LoopBuild
            && LoopBuilder?.CanSave == true)
        {
            LoopBuilder.Save();
            if (_services.Loops.Loops.LastOrDefault() is { } saved
                && _services.LoopRunner.Start(saved))
                _services.Loops.NoteRun(saved.Name);
            return;
        }
        if (CurrentMode == NavigationMode.AutoLair
            && _services.AutoLair.Marked.Count > 0)
        {
            _services.AutoLair.Start();
            return;
        }
        if (QueuedDestination is { } dest)
        {
            _services.Walker.WalkTo(dest);
            QueuedDestination = null;
        }
    }

    // ----- CURRENT NAV row list -------------------------------------

    /// <summary>Rows shown under CURRENT NAV — steps when walking/looping, marked lairs when auto-lairing.</summary>
    public ObservableCollection<CurrentNavRowViewModel> CurrentNavRows { get; } = new();

    /// <summary>
    /// Row the CURRENT NAV ListBox should keep in view — the active
    /// step while walking, the next-ready lair while auto-lairing. The
    /// window code-behind subscribes to property-change and calls
    /// <c>ListBox.ScrollIntoView</c> so a long path scrolls along with
    /// progress instead of forcing the entire rail to grow.
    /// </summary>
    public CurrentNavRowViewModel? CurrentNavSelectedRow
    {
        get
        {
            foreach (CurrentNavRowViewModel r in CurrentNavRows)
                if (r.IsCurrent || r.IsReady) return r;
            return CurrentNavRows.Count > 0 ? CurrentNavRows[0] : null;
        }
    }

    /// <summary>Header sentence under the section title: <c>"3 of 6 steps to (M/R) - Name"</c> / <c>"Cycling marked lairs"</c>.</summary>
    public string CurrentNavHeader => EngineActionKind switch
    {
        NavigationEngineKind.Walking =>
            _services.Walker.Destination is { } k
                ? $"{_services.Walker.CurrentStepIndex + 1} of {_services.Walker.StepCount} steps to {FormatRoomRef(k)}"
                : $"{_services.Walker.CurrentStepIndex + 1} of {_services.Walker.StepCount} steps",
        NavigationEngineKind.Looping  => "Cycling loop steps",
        NavigationEngineKind.AutoLair => "Cycling marked lairs",
        _ => "No active navigation. Start a Loop, Walk, or Lair cycle from the toolbar.",
    };

    /// <summary>Progress as a 0..1 fraction for the small inline bar; null when no progress meter applies (e.g. Auto-Lair).</summary>
    public double? CurrentNavProgress
    {
        get
        {
            if (EngineActionKind != NavigationEngineKind.Walking) return null;
            int total = _services.Walker.StepCount;
            if (total <= 0) return null;
            return Math.Clamp((double)_services.Walker.CurrentStepIndex / total, 0, 1);
        }
    }

    public bool CurrentNavHasProgress => CurrentNavProgress is not null;

    private void RebuildCurrentNavRows()
    {
        CurrentNavRows.Clear();
        switch (EngineActionKind)
        {
            case NavigationEngineKind.Walking:
            {
                int idx = _services.Walker.CurrentStepIndex;
                IReadOnlyList<WalkStep> steps = _services.Walker.Steps;
                for (int i = 0; i < steps.Count; i++)
                {
                    CurrentNavRowStatus status = i < idx
                        ? CurrentNavRowStatus.Completed
                        : (i == idx ? CurrentNavRowStatus.Current : CurrentNavRowStatus.Upcoming);
                    CurrentNavRows.Add(new CurrentNavRowViewModel(
                        index: i + 1, label: steps[i].Display, status: status));
                }
                break;
            }
            case NavigationEngineKind.AutoLair:
            {
                int i = 1;
                foreach (RoomKey key in _services.AutoLair.Marked)
                {
                    // Status data isn't tracked yet — show "ready" for
                    // all marked lairs until LairTimerStore lands.
                    CurrentNavRows.Add(new CurrentNavRowViewModel(
                        index: i++, label: FormatRoomRef(key),
                        status: CurrentNavRowStatus.Ready,
                        subLabel: "ready",
                        removeKey: key));
                }
                break;
            }
            // Looping: TODO when LoopRunner exposes step index / total
            // in a per-step shape; show the loop's room list as
            // upcoming for now.
        }
    }

    [RelayCommand]
    private void UnmarkAutoLairRoom(RoomKey? key)
    {
        if (key is { } k) _services.AutoLair.Toggle(k);
    }
}

/// <summary>Which engine is currently moving the player — gates Run/Stop, status badge, CURRENT NAV rendering.</summary>
public enum NavigationEngineKind
{
    Idle     = 0,
    Walking  = 1,
    Looping  = 2,
    AutoLair = 3,
}

/// <summary>One of the four explicit modes the Navigation window can be in.</summary>
public enum NavigationMode
{
    Idle = 0,
    LoopBuild = 1,
    AutoLair = 2,
}
