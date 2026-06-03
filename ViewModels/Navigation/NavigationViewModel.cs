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
        Graph = _services.RoomGraph;
        RefreshFromTracker();
        RefreshFromWalker();
        RefreshLayout();
    }

    public void Dispose()
    {
        _services.RoomTracker.StateChanged -= OnTrackerStateChanged;
        _services.Walker.Event -= OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged -= OnPauseChanged;
        _services.RoomGraph.GraphReloaded -= OnGraphReloaded;
    }

    // ----- Status strip ---------------------------------------------

    [ObservableProperty] private string _statusLabel = "Unknown";
    [ObservableProperty] private string _statusBadgeBrush = "#888";
    [ObservableProperty] private string _currentRoomLabel = "—";
    [ObservableProperty] private bool _isPaused;

    // ----- Map binding ----------------------------------------------

    [ObservableProperty] private RoomLayout? _layout;
    [ObservableProperty] private RoomKey? _currentRoomKey;
    [ObservableProperty] private RoomGraphManager? _graph;

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
            if (room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                int? steps = sourceKey is { } src
                    ? _services.Bfs.DistanceBetween(src, room.Key, _services.Movement)
                    : null;
                matches.Add(new RoomSearchResult(room.Key, room.Name, steps));
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

    [RelayCommand]
    private void ToggleLoopMode()
        => CurrentMode = CurrentMode == NavigationMode.LoopBuild
            ? NavigationMode.Idle : NavigationMode.LoopBuild;

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
        (StatusLabel, StatusBadgeBrush) = state.Confidence switch
        {
            RoomConfidence.Located     => ("Located",     "#3DDC97"),
            RoomConfidence.Pending     => ("Pending",     "#F8B500"),
            RoomConfidence.Reconciling => ("Reconciling", "#F25C54"),
            RoomConfidence.Lost        => ("Lost",        "#F25C54"),
            _                          => ("Unknown",     "#888"),
        };
        CurrentRoomLabel = state.CurrentRoom is { } room
            ? $"{room.Name}  ·  {room.Key}"
            : "—";

        // Re-centre the layout on the new room when we land on one
        // outside the cached layout (typical after a reconnect or a
        // big walk). The map control re-fits visually via its own
        // FitToCurrent helper when the binding changes.
        CurrentRoomKey = state.CurrentRoom?.Key;
        if (state.CurrentRoom is { } here && (Layout is null
            || !Layout.Positions.ContainsKey(here.Key)))
        {
            Layout = _services.Bfs.BuildLayout(here.Key);
        }
    }

    private void OnGraphReloaded() => RefreshLayout();

    private void RefreshLayout()
    {
        Graph = _services.RoomGraph;
        RoomKey? key = _services.RoomTracker.State.CurrentRoom?.Key;
        Layout = key is { } k ? _services.Bfs.BuildLayout(k) : null;
    }

    private void RefreshFromWalker()
    {
        IsWalking = _services.Walker.State == WalkState.Walking
                 || _services.Walker.State == WalkState.Paused;
    }
}

/// <summary>One of the four explicit modes the Navigation window can be in.</summary>
public enum NavigationMode
{
    Idle = 0,
    LoopBuild = 1,
    AutoLair = 2,
}
