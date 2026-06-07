using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
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

        // 1 s tick — keeps the CURRENT NAV lair countdowns + the
        // BUILDING-LAIR strip in sync as time passes. Only runs while
        // the user cares (build mode OR active run); idle Navigation
        // pays nothing. Constructed BEFORE the AutoLair event hookups
        // below so the initial OnAutoLairMarkedChanged call below
        // (which routes through EnsureLairTickRunning) doesn't deref a
        // null timer.
        _lairTick = new DispatcherTimer(TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal, (_, _) => OnLairTick());
        _lairTick.Stop();

        _services.RoomTracker.StateChanged += OnTrackerStateChanged;
        _services.Recovery.TierChanged    += OnRecoveryTierChanged;
        _services.Walker.Event += OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged += OnPauseChanged;
        _services.RoomGraph.GraphReloaded += OnGraphReloaded;
        _services.TBInfo.StoreReloaded    += RefreshTeleportRooms;
        _services.Loops.LoopsChanged += OnLoopsChanged;
        _services.Favorites.Changed += OnFavoritesChanged;
        _services.LoopRunner.Event += OnLoopRunnerEvent;
        _services.Movement.AvoidedChanged += OnAvoidedChanged;
        OnAvoidedChanged();
        _services.Movement.StashChanged   += OnStashChanged;
        OnStashChanged();
        _services.AutoLair.MarkedChanged += OnAutoLairMarkedChanged;
        _services.AutoLair.ActiveChanged += OnAutoLairActiveChanged;
        _services.AutoLair.PhaseChanged  += OnAutoLairPhaseChanged;
        _services.AutoLair.PausedChanged += OnAutoLairPausedChanged;
        _services.RoomBlacklist.Changed   += OnBlacklistChanged;
        _services.Lairs.SetupsChanged    += OnSetupsChanged;
        OnAutoLairMarkedChanged();
        IsAutoLairing = _services.AutoLair.IsActive;
        RefreshSetups();
        Graph = _services.RoomGraph;
        EnsureLairTickRunning();
        _services.Macros.Macros.CollectionChanged += OnMacrosCollectionChanged;
        RefreshFromTracker();
        RefreshFromWalker();
        RefreshLayout();
        RefreshLoops();
        RefreshFavorites();
        RefreshCrawlerChords();
        RefreshTeleportRooms();
    }

    /// <summary>
    /// Per-second pump for CURRENT NAV lair countdowns. Cheap to leave
    /// running, but explicitly gated so an idle Navigation window does
    /// no work. See <see cref="EnsureLairTickRunning"/>.
    /// </summary>
    private readonly DispatcherTimer _lairTick;

    public void Dispose()
    {
        _lairTick.Stop();
        _services.RoomTracker.StateChanged -= OnTrackerStateChanged;
        _services.Recovery.TierChanged    -= OnRecoveryTierChanged;
        _services.Walker.Event -= OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged -= OnPauseChanged;
        _services.RoomGraph.GraphReloaded -= OnGraphReloaded;
        _services.TBInfo.StoreReloaded    -= RefreshTeleportRooms;
        _services.Loops.LoopsChanged -= OnLoopsChanged;
        _services.Favorites.Changed -= OnFavoritesChanged;
        _services.LoopRunner.Event -= OnLoopRunnerEvent;
        _services.Movement.AvoidedChanged -= OnAvoidedChanged;
        _services.Movement.StashChanged   -= OnStashChanged;
        _services.AutoLair.MarkedChanged -= OnAutoLairMarkedChanged;
        _services.AutoLair.ActiveChanged -= OnAutoLairActiveChanged;
        _services.AutoLair.PhaseChanged  -= OnAutoLairPhaseChanged;
        _services.AutoLair.PausedChanged -= OnAutoLairPausedChanged;
        _services.RoomBlacklist.Changed   -= OnBlacklistChanged;
        _services.Lairs.SetupsChanged    -= OnSetupsChanged;
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
        RefreshAutoLairMarkedKeys();
        OnPropertyChanged(nameof(HasLairMarkers));
        OnPropertyChanged(nameof(LairBuildStatusText));
        EnsureLairTickRunning();
        RefreshDerivedState();
    }

    /// <summary>
    /// Rebuild <see cref="AutoLairMarkedKeys"/> using the same
    /// ordering as <see cref="PopulateLairRows"/> — active target
    /// first when there is one, then sorted by Map / Room. Null when
    /// no markers OR the user isn't in any Lair-related context (so
    /// the map doesn't draw stale overlays).
    /// </summary>
    private void RefreshAutoLairMarkedKeys()
    {
        Game.Map.AutoLairManager mgr = _services.AutoLair;
        bool relevant = CurrentMode == NavigationMode.AutoLair || mgr.IsActive;
        if (!relevant || mgr.Marked.Count == 0)
        {
            AutoLairMarkedKeys = null;
            return;
        }

        RoomKey? target = mgr.CurrentTarget;
        List<RoomKey> ordered = new(mgr.Marked.Count);
        if (target is { } t && mgr.Marked.Contains(t)) ordered.Add(t);
        foreach (RoomKey key in mgr.Marked
            .Where(k => target is not { } tt || !tt.Equals(k))
            .OrderBy(k => k.Map).ThenBy(k => k.Room))
            ordered.Add(key);

        AutoLairMarkedKeys = ordered;
    }

    /// <summary>
    /// Flip the per-second pump (<see cref="_lairTick"/>) on / off
    /// based on whether the user is currently looking at a CURRENT
    /// NAV that has lair countdowns. Active = build mode with at
    /// least one marker OR scheduler running. Anything else means
    /// nothing on screen ticks once a second, so leave the timer off.
    /// </summary>
    private void EnsureLairTickRunning()
    {
        bool shouldRun =
            (CurrentMode == NavigationMode.AutoLair && _services.AutoLair.Marked.Count > 0)
            || _services.AutoLair.IsActive;
        if (shouldRun && !_lairTick.IsEnabled) _lairTick.Start();
        else if (!shouldRun && _lairTick.IsEnabled) _lairTick.Stop();
    }

    /// <summary>
    /// One-second tick — re-render the lair rows so the countdown
    /// sub-labels stay current. Rebuilding the whole list isn't free,
    /// but the list is short (typically &lt; 10 rows) and a once-a-
    /// second refresh keeps the binding logic simple. If profile + UI
    /// scale demand a finer touch later, move the sub-label out to a
    /// per-row observable property.
    /// </summary>
    private void OnLairTick()
    {
        RebuildCurrentNavRows();
        OnPropertyChanged(nameof(AutoLairStatusText));
    }

    /// <summary>
    /// Bottom-strip status for Auto-Lair build mode — counterpart of
    /// the loop builder's room/step count line. Reads the live marker
    /// count and explains how to commit (Run) vs discard (toggle Lair).
    /// </summary>
    public string LairBuildStatusText
    {
        get
        {
            int n = _services.AutoLair.Marked.Count;
            string lairWord = n == 1 ? "lair" : "lairs";
            return n switch
            {
                0 => "click rooms on the map to mark them as lairs",
                1 => "1 lair marked — add at least one more, or click Save lairs to keep this one for later",
                _ => $"{n} {lairWord} marked · Run to start cycling, Save lairs to persist, toggle Lair mode to discard",
            };
        }
    }

    private void OnAutoLairPhaseChanged(AutoLairPhase _)
    {
        OnPropertyChanged(nameof(AutoLairPhaseLabel));
        OnPropertyChanged(nameof(AutoLairStatusText));
        OnPropertyChanged(nameof(TopBarStatusText));
        // Target switch reorders the map overlay (active target = #1).
        RefreshAutoLairMarkedKeys();
        RefreshAutoLairApproachPath();
    }

    /// <summary>
    /// Rebuild <see cref="AutoLairApproachPath"/> from the current
    /// tracker position + scheduler target. Only populated during the
    /// active-leg phases (Approaching, Waiting, Entering); Engaging
    /// and Idle clear it so the line disappears when the walker
    /// reaches the lair.
    /// </summary>
    private void RefreshAutoLairApproachPath()
    {
        Game.Map.AutoLairManager mgr = _services.AutoLair;
        if (mgr.Phase is not (Game.Map.AutoLairPhase.Approaching
                              or Game.Map.AutoLairPhase.Waiting
                              or Game.Map.AutoLairPhase.Entering)
            || mgr.CurrentTarget is not { } target
            || mgr.CurrentWaitRoom is not { } waitRoom
            || _services.RoomTracker.State.CurrentRoom is not { } current)
        {
            AutoLairApproachPath = null;
            return;
        }

        // BFS the current→wait-room leg, then append the lair entry
        // hop. Pass returnEmptyWhenAtDestination so an arrived-at-the-
        // wait-room state still yields a renderable path (just the
        // wait-room + the lair entry hop). Without it the line
        // vanished the moment the walker reached the wait-room and
        // didn't come back until phase advanced to Engaging — the
        // user-visible "appearing and disappearing" flicker.
        IReadOnlyList<Game.Map.Direction>? dirs = _services.Bfs.FindPath(
            current.Key, waitRoom, _services.Movement,
            returnEmptyWhenAtDestination: true);
        if (dirs is null)
        {
            AutoLairApproachPath = null;
            return;
        }

        List<RoomKey> rooms = new(dirs.Count + 2) { current.Key };
        RoomKey cursor = current.Key;
        foreach (Game.Map.Direction d in dirs)
        {
            if (_services.RoomGraph.GetRoom(cursor) is not { } room
                || !room.Exits.TryGetValue(d, out Game.Map.RoomExit exit))
            {
                AutoLairApproachPath = null;
                return;
            }
            cursor = exit.Target;
            rooms.Add(cursor);
        }
        // Append the entry hop into the lair so the rendered line
        // shows the FULL journey, not just the approach.
        if (!cursor.Equals(target)) rooms.Add(target);

        AutoLairApproachPath = rooms;
    }

    private void OnAutoLairPausedChanged(bool _)
    {
        OnPropertyChanged(nameof(RunStopLabel));
        OnPropertyChanged(nameof(AutoLairPhaseLabel));
        OnPropertyChanged(nameof(AutoLairStatusText));
        OnPropertyChanged(nameof(TopBarStatusText));
    }

    /// <summary>
    /// One-word label for the bottom-strip badge —
    /// <c>"Approaching"</c> / <c>"Waiting"</c> / <c>"Entering"</c> /
    /// <c>"Engaging"</c> / <c>"Idle"</c>. Surfaced as a separate
    /// property so the badge can colour-code without recomputing the
    /// full status line.
    /// </summary>
    public string AutoLairPhaseLabel => _services.AutoLair.Phase switch
    {
        AutoLairPhase.Approaching => "Approaching",
        AutoLairPhase.Waiting     => "Waiting",
        AutoLairPhase.Entering    => "Entering",
        AutoLairPhase.Engaging    => "Engaging",
        _                         => "Idle",
    };

    /// <summary>
    /// Bottom-strip status line for a running Auto-Lair session — e.g.
    /// <c>"Sewer Lair via 5/99 — 0:42 to entry"</c>. Empty when the
    /// scheduler isn't actively driving the walker (Idle / Engaging
    /// without a target).
    /// </summary>
    public string AutoLairStatusText
    {
        get
        {
            if (_services.AutoLair.LastDecision is not { } pick) return string.Empty;
            string lairLabel = FormatRoomRef(pick.Lair);
            string waitLabel = FormatRoomRef(pick.WaitRoom);
            return _services.AutoLair.Phase switch
            {
                AutoLairPhase.Approaching => $"{lairLabel} via wait-room {waitLabel}",
                AutoLairPhase.Waiting     => $"Waiting at {waitLabel} → {lairLabel}",
                AutoLairPhase.Entering    => $"Entering {lairLabel}",
                AutoLairPhase.Engaging    => $"Engaging at {lairLabel}",
                _                         => string.Empty,
            };
        }
    }

    private void OnAutoLairActiveChanged(bool active)
    {
        IsAutoLairing = active;
        EnsureLairTickRunning();
        RefreshEngineActionLabel();
        RefreshAutoLairApproachPath();
    }

    [RelayCommand]
    private void ToggleAutoLair()
    {
        if (_services.AutoLair.IsActive) _services.AutoLair.Stop();
        else _services.AutoLair.Start();
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
        // RoomSearchService subscribes to MovementFilter.AvoidedChanged
        // directly and flushes its own distance cache.
    }

    private void OnStashChanged()
    {
        HashSet<RoomKey> next = new(_services.Movement.Stash);
        _services.Log?.Debug("Navigation",
            $"stash set changed — count={next.Count}");
        StashRooms = next;
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
        LoopSequenceNumbers = null;     // per UX rule: no number overlay during execution

        if (runner.CurrentLoop is not { } loop)
        {
            LoopPath = null;
            LoopApproachPreviewPath = null;
            return;
        }

        // Pause-with-builder-open: the user opened the build session
        // by pausing a running loop (OpenBuilderForRunningLoop), so the
        // edit-style red polyline + numbered waypoint markers (driven
        // by LoopBuilderPath / LoopBuilderWaypoints) are the right
        // overlay. Suppress the runner's blue cycle so it doesn't sit
        // on top of the red preview — once the user resumes, the
        // runner re-fires LoopRunner events and this branch falls
        // through to the running-cycle path below.
        if (CurrentMode == NavigationMode.LoopBuild
            && LoopBuilder is { HasClicks: true })
        {
            LoopPath = null;
            LoopApproachPreviewPath = null;
            return;
        }

        // Both phases anchor the rendered cycle to runner.CircleStartRoom
        // (the rotation entry). Walking the cycle from a fixed anchor
        // means the polyline stays still as the player steps through
        // each leg — the user sees the complete loop the whole time
        // instead of "what's left from here" shifting with every step.
        // Legacy v1 loops (no rotation anchor) fall back to the live
        // current room so they still render something.
        RoomKey? source = runner.CircleStartRoom
                           ?? _services.RoomTracker.State.CurrentRoom?.Key;

        if (runner.State == Game.Map.LoopState.Approaching)
        {
            // Approach phase — show the red loop preview alongside the
            // blue walker overlay. The walker owns WalkPath; we own
            // the preview ring drawn under it.
            if (source is { } entry)
            {
                IReadOnlyList<RoomKey> previewKeys = runner.ResolveLoopRoomKeys(entry);
                LoopApproachPreviewPath = previewKeys.Count >= 2 ? previewKeys : null;
            }
            else
            {
                LoopApproachPreviewPath = null;
            }
            LoopPath = null;
            return;
        }

        // Running / paused circle — blue cycle line, full ring,
        // anchored at CircleStartRoom so it stays static across step
        // advances.
        LoopApproachPreviewPath = null;
        if (source is { } start)
        {
            IReadOnlyList<RoomKey> keys = runner.ResolveLoopRoomKeys(start);
            LoopPath = keys.Count >= 2 ? keys : null;
        }
        else
        {
            LoopPath = null;
        }
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
    partial void OnCurrentRoomKeyChanged(RoomKey? value) => RefreshPreviewPath();
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

    /// <summary>
    /// Dashed cyan preview polyline drawn under the active loop / walk
    /// while the user is in the LoopBuilder strip. Pulled from
    /// <see cref="LoopBuilderSessionViewModel.PreviewedRoomKeys"/>
    /// whenever the builder changes.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopBuilderPath;

    /// <summary>
    /// Ordered RoomKey list for the map's numbered builder-waypoint
    /// markers. Mirrors <see cref="LoopBuilderSessionViewModel.WaypointKeys"/>.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopBuilderWaypoints;

    /// <summary>
    /// Red preview polyline drawn during the walker-approach phase of
    /// a loop run. Lets the user see the upcoming cycle alongside the
    /// blue walk-to line that's actively driving them to the start
    /// waypoint.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopApproachPreviewPath;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _avoidedRooms;

    /// <summary>Rooms the user has flagged as stash drops. Bound to
    /// the MapControl's StashRooms property — each room renders with
    /// a gold outline. Refreshed on
    /// <see cref="OnStashChanged"/>.</summary>
    [ObservableProperty] private IReadOnlySet<RoomKey>? _stashRooms;

    [ObservableProperty] private IReadOnlyDictionary<RoomKey, int>? _loopSequenceNumbers;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _autoLairRooms;

    /// <summary>
    /// Ordered marker list driving the map's numbered amber overlay.
    /// Same ordering rule as <see cref="PopulateLairRows"/>: active
    /// target first (when the scheduler is running), then the rest
    /// sorted by Map / Room. Visible whenever the user is in
    /// AutoLair mode OR the scheduler is running; null when no
    /// markers are placed.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _autoLairMarkedKeys;

    /// <summary>
    /// Full projected route the walker will follow during the current
    /// Auto-Lair leg: <c>current → wait-room → lair</c>. Held stable
    /// across the Approaching → Waiting → Entering transitions so the
    /// map line doesn't flicker every time the walker briefly goes
    /// Idle between sub-legs. Null when no leg is active (Idle /
    /// Engaging) or when the BFS can't resolve the route.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _autoLairApproachPath;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _teleportRooms;
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

    partial void OnQueuedDestinationChanged(RoomKey? value) => RefreshPreviewPath();

    /// <summary>
    /// Red preview line drawn on the map while a destination is queued
    /// but not yet running. Bound to <c>MapControl.PreviewPath</c>.
    /// Cleared when no destination is queued OR no path exists.
    /// Recomputed on <see cref="QueuedDestination"/> change and on
    /// <see cref="CurrentRoomKey"/> change (so the preview tracks the
    /// player if they move while a target is armed).
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _previewPath;

    private void RefreshPreviewPath()
    {
        if (Graph is null
            || CurrentRoomKey is not { } src
            || QueuedDestination is not { } dest
            || src.Equals(dest))
        {
            PreviewPath = null;
            return;
        }
        IReadOnlyList<Direction>? path = _services.Bfs.FindPath(src, dest, _services.Movement);
        if (path is null || path.Count == 0) { PreviewPath = null; return; }

        var keys = new List<RoomKey>(path.Count + 1) { src };
        RoomKey cur = src;
        foreach (Direction d in path)
        {
            if (Graph.GetRoom(cur) is not { } room) break;
            if (!room.Exits.TryGetValue(d, out RoomExit exit)) break;
            cur = exit.Target;
            keys.Add(cur);
        }
        PreviewPath = keys.Count >= 2 ? keys : null;
    }

    /// <summary>Click handler for the queued-destination chip — discards the queued target + clears the preview line.</summary>
    [RelayCommand]
    private void ClearQueuedDestination() => QueuedDestination = null;

    // ----- Section expand/collapse state (right rail) ---------------
    //
    // Persisted only for this window session — open/closed state isn't
    // worth a profile field. Defaults: everything expanded.
    [ObservableProperty] private bool _isCurrentNavExpanded = true;
    [ObservableProperty] private bool _isGotoExpanded       = true;
    [ObservableProperty] private bool _isLoopsExpanded      = true;

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

    // Debounce keystrokes: rebuild runs after the user pauses for the
    // configured delay rather than on every character. Without this a
    // long graph + monster scan piles up on the UI thread per keystroke
    // and the dropdown feels sticky. Restarting the timer on every
    // change collapses bursts into a single rebuild.
    private Avalonia.Threading.DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounce ??= new Avalonia.Threading.DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RebuildSearchResults(SearchQuery);
    }

    /// <summary>
    /// Repopulate <see cref="SearchResults"/> from <paramref name="query"/>.
    /// Resolution + monster lookup + step distance come from the shared
    /// <see cref="RoomSearchService"/>; this method's only job is to
    /// hand the cap-50 ordered list into the observable collection the
    /// dropdown binds to.
    /// </summary>
    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        if (Graph is null) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        string needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 1) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        // Cap 200 internally; we only display 50 to keep the dropdown
        // scannable. Larger cap gives the sort more candidates to pull
        // the best 50 closest-first from.
        IReadOnlyList<RoomSearchResult> matches =
            _services.RoomSearch.Search(needle, CurrentRoomKey, cap: 200);

        foreach (RoomSearchResult mm in matches.Take(50))
            SearchResults.Add(mm);
        OnPropertyChanged(nameof(HasSearchResults));
    }

    // Coordinate parsing, monster index, regen-monster cache, and
    // per-source distance cache all live in the shared
    // RoomSearchService now (Services.RoomSearchService). Cache
    // invalidation on graph/game-data swap is handled there too.

    [RelayCommand]
    private void SelectSearchResult(RoomSearchResult? result)
    {
        if (result is null) return;
        // Informational rows (monster with no recorded lair) carry no
        // walkable target — click is a no-op so the dropdown row
        // behaves as a label.
        if (result.IsInformational) return;
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

    // ----- Auto-Lair Setups list (PR 7.20) --------------------------

    /// <summary>
    /// Saved <see cref="Models.Profile.LairSetup"/>s for the active
    /// BBS — rendered alongside <see cref="Loops"/> in the rail's
    /// "LOOPS + AUTO-LAIRS" section. Ordered alphabetically by
    /// <see cref="LairManager.Setups"/>.
    /// </summary>
    public ObservableCollection<LairSetupRowViewModel> Setups { get; } = new();

    public bool HasSetups => Setups.Count > 0;

    private void OnSetupsChanged() => RefreshSetups();

    private void RefreshSetups()
    {
        Setups.Clear();
        foreach (Models.Profile.LairSetup s in _services.Lairs.Setups)
            Setups.Add(new LairSetupRowViewModel(s));
        OnPropertyChanged(nameof(HasSetups));
    }

    /// <summary>
    /// Run a saved setup — wipes <see cref="AutoLairManager"/>'s current
    /// markers, loads the setup's markers (with their per-marker
    /// override timers + Skip flags), then calls <c>Start</c>. Stops
    /// any in-flight loop / walk first so the scheduler has clean
    /// ground.
    /// </summary>
    [RelayCommand]
    private void RunSetup(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        LoadSetupInternal(row.Source);
        _services.AutoLair.Start();
    }

    /// <summary>
    /// Right-click → Load on a Setups row. Wipes current markers and
    /// loads the setup's markers without starting the scheduler — lets
    /// the user inspect / tweak before hitting Run.
    /// </summary>
    [RelayCommand]
    private void LoadSetup(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        LoadSetupInternal(row.Source);
    }

    private void LoadSetupInternal(Models.Profile.LairSetup setup)
    {
        if (_services.LoopRunner.State != Game.Map.LoopState.Idle)
            _services.LoopRunner.Stop("auto-lair setup loaded");
        if (_services.AutoLair.IsActive)
            _services.AutoLair.Stop("auto-lair setup loaded");

        _services.AutoLair.Clear();
        foreach (Models.Profile.LairMarker m in setup.Markers)
        {
            RoomKey key = new(m.Map, m.Room);
            _services.AutoLair.Mark(key, m.OverrideRespawnSeconds);
            // Skip flag — currently informational only at the marker
            // level; the scheduler treats every marker as active.
            // Phase 7 PR 7.24 wires Skip through the candidate filter.
        }

        // Transition the Navigation window into Lair build mode so the
        // user sees the just-loaded markers on the map (numbered amber
        // overlay) and the BUILDING LAIR bottom strip. Tear down any
        // existing LoopBuild session first so the two build modes
        // don't overlap. If we're called from Run (scheduler is about
        // to Start), the subsequent ActiveChanged event flips the
        // engine UI over; until then build mode is the right surface.
        if (CurrentMode == NavigationMode.LoopBuild)
            ToggleLoopMode();
        if (CurrentMode != NavigationMode.AutoLair)
            CurrentMode = NavigationMode.AutoLair;
    }

    /// <summary>
    /// Right-click → Edit on a Setups row → opens
    /// <see cref="LairEditorDialog"/>. Save persists via
    /// <see cref="LairManager.Save"/> which fires SetupsChanged so the
    /// rail refreshes.
    /// </summary>
    [RelayCommand]
    private async Task EditSetupAsync(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        LairEditorDialogViewModel vm = new(
            row.Source, _services.Lairs, _services.RoomGraph,
            _services.LairTimers, _services.Confirm);
        await _services.Dialogs
            .OpenWindowAsync<LairEditorDialogViewModel, Models.Profile.LairSetup?>(vm);
    }

    /// <summary>
    /// CURRENT NAV ✎ button on a marked-lair row → single-marker timer
    /// override editor. Dialog mutates <see cref="AutoLairManager"/>
    /// directly via SetOverride; the scheduler picks up the change on
    /// its next tick.
    /// </summary>
    [RelayCommand]
    private async Task EditLairTimerAsync(CurrentNavRowViewModel? row)
    {
        if (row is null || row.EditKey is not { } key) return;
        LairTimerEditDialogViewModel vm = new(
            _services.AutoLair,
            _services.LairTimers,
            key,
            row.Label);
        await _services.Dialogs
            .OpenWindowAsync<LairTimerEditDialogViewModel, LairTimerEditDialogResult?>(vm);
        // Override changes fire AutoLair.MarkedChanged which rebuilds
        // the CURRENT NAV rows; no manual refresh needed.
    }

    /// <summary>
    /// Right-click → Delete on a Setups row. Confirms via the shared
    /// ConfirmService (which honours the user's "skip delete confirms"
    /// setting), then removes the setup from disk + refreshes the rail.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSetupAsync(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync($"auto-lair setup \"{row.Source.Name}\"");
        if (!ok) return;
        _services.Lairs.Delete(row.Source.Name);
    }

    /// <summary>
    /// True when the top-bar Save chip should be active — covers the
    /// four situations the user might want to persist what they've
    /// built or are running: Loop build mode with savable clicks,
    /// Loop running, Auto-Lair build mode with markers, Auto-Lair
    /// running with markers. Drives the chip's visibility AND its
    /// enabled state (we show the chip in all four situations and
    /// disable it only when nothing is savable yet).
    /// </summary>
    public bool CanSaveCurrent
    {
        get
        {
            // Loop build: at least 2 reachable clicks committed.
            if (CurrentMode == NavigationMode.LoopBuild
                && LoopBuilder is { CanSave: true })
                return true;
            // Loop running: the runner has a loop in flight.
            if (_services.LoopRunner.State is Game.Map.LoopState.Running
                                          or Game.Map.LoopState.Approaching
                                          or Game.Map.LoopState.Paused
                && _services.LoopRunner.CurrentLoop is not null)
                return true;
            // Auto-Lair build / running with at least one marker.
            if ((CurrentMode == NavigationMode.AutoLair || _services.AutoLair.IsActive)
                && _services.AutoLair.Marked.Count > 0)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Dispatcher for the top-bar Save chip. Opens the right editor
    /// dialog (Loop or Lair) pre-seeded with the current state — the
    /// user reviews / renames / commits there. Mirrors the dispatch
    /// in <see cref="RunStop"/> so the chip's behaviour stays
    /// predictable regardless of which build / running combination
    /// the user is in.
    /// </summary>
    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        // Lair takes priority over Loop when both could apply — the
        // chip is only relevant for ONE of them at a time, but if a
        // weird state has both build modes briefly active (e.g.
        // transition jitter), Lair is the more recent surface.
        if ((CurrentMode == NavigationMode.AutoLair || _services.AutoLair.IsActive)
            && _services.AutoLair.Marked.Count > 0)
        {
            await SaveCurrentMarkersAsSetupAsync();
            return;
        }

        // Loop build: open the editor pre-seeded with the click list.
        if (CurrentMode == NavigationMode.LoopBuild
            && LoopBuilder is { CanSave: true } b
            && b.BuildTransient() is { } transient)
        {
            LoopEditorDialogViewModel vm = new(
                transient, _services.Loops, _services.RoomGraph,
                _services.LoopRunner, _services.Confirm, isNew: true);
            await _services.Dialogs
                .OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
            return;
        }

        // Loop running: snapshot from the runner.
        if (_services.LoopRunner.CurrentLoop is { } running)
        {
            Loop draft = new(running.Name, running.Waypoints)
            {
                Notes = running.Notes ?? string.Empty,
            };
            LoopEditorDialogViewModel vm = new(
                draft, _services.Loops, _services.RoomGraph,
                _services.LoopRunner, _services.Confirm, isNew: true);
            await _services.Dialogs
                .OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
        }
    }

    /// <summary>
    /// Save the live <see cref="AutoLairManager.Marked"/> set (plus the
    /// per-marker overrides the user has set) as a new named setup.
    /// Opens <see cref="LairEditorDialog"/> on a draft so the user can
    /// pick a name + adjust overrides before committing. No-op when
    /// no markers are placed.
    /// </summary>
    [RelayCommand]
    private async Task SaveCurrentMarkersAsSetupAsync()
    {
        if (_services.AutoLair.Marked.Count == 0) return;

        List<Models.Profile.LairMarker> markers = new();
        foreach (RoomKey key in _services.AutoLair.Marked)
        {
            int? overrideSec = _services.AutoLair.GetOverride(key);
            markers.Add(new Models.Profile.LairMarker(
                map: key.Map, room: key.Room,
                overrideRespawnSeconds: overrideSec));
        }
        // Default name uses HH-mm-ss so the editor's commit can leave
        // the auto-generated value if the user just wants a quick save.
        Models.Profile.LairSetup draft = new(
            name: $"Lairs {DateTime.Now:HH-mm-ss}",
            markers: markers);

        LairEditorDialogViewModel vm = new(
            draft, _services.Lairs, _services.RoomGraph,
            _services.LairTimers, _services.Confirm, isNew: true);
        await _services.Dialogs
            .OpenWindowAsync<LairEditorDialogViewModel, Models.Profile.LairSetup?>(vm);
    }

    /// <summary>True when the user has at least one marker placed — gates the Save-as button.</summary>
    public bool HasLairMarkers => _services.AutoLair.Marked.Count > 0;

    // ----- GOTO / Favorites pane ------------------------------------

    /// <summary>Per-character favourite-room bookmarks. Click to walk.</summary>
    public ObservableCollection<FavoriteRowViewModel> Favorites { get; } = new();

    public bool HasFavorites => Favorites.Count > 0;

    private void OnFavoritesChanged() => RefreshFavorites();

    private void RefreshFavorites()
    {
        Favorites.Clear();
        // Sort by display label so the user sees a stable alphabetical
        // ordering — the store's underlying dictionary doesn't preserve
        // insertion order anyway.
        var entries = new List<FavoriteRowViewModel>();
        foreach (FavoriteRoom f in _services.Favorites.All)
        {
            RoomKey key = new(f.Map, f.Room);
            string label = !string.IsNullOrWhiteSpace(f.Label)
                ? f.Label!
                : _services.RoomGraph.GetRoom(key) is { } r
                    ? r.Name
                    : key.ToString();
            entries.Add(new FavoriteRowViewModel(key, label));
        }
        entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        foreach (FavoriteRowViewModel e in entries) Favorites.Add(e);
        OnPropertyChanged(nameof(HasFavorites));
    }

    /// <summary>Click a favourite → walk there (stops loop/lair first).</summary>
    [RelayCommand]
    private void GoToFavorite(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        if (_services.LoopRunner.State is Game.Map.LoopState.Running
                                       or Game.Map.LoopState.Paused
                                       or Game.Map.LoopState.Approaching)
            _services.LoopRunner.Stop("user walk-to from Favorites");
        if (_services.AutoLair.IsActive) _services.AutoLair.Stop();
        _services.Walker.WalkTo(row.Key);
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        _services.Favorites.Remove(row.Key);
    }

    /// <summary>
    /// Open a small modeless rename dialog for the favourite. The
    /// dialog returns the new label string on Save or null on Cancel;
    /// non-null results route through
    /// <see cref="FavoritesStore.Rename"/> which fires
    /// <c>Changed</c> and refreshes the rail.
    /// </summary>
    [RelayCommand]
    private async Task RenameFavoriteAsync(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        FavoriteRenameDialogViewModel vm = new(
            currentLabel: row.Label,
            coordTag: $"{row.Key.Map}/{row.Key.Room}");
        string? newLabel = await _services.Dialogs
            .OpenWindowAsync<FavoriteRenameDialogViewModel, string?>(vm);
        if (newLabel is null) return;  // cancelled
        _services.Favorites.Rename(row.Key, string.IsNullOrWhiteSpace(newLabel) ? null : newLabel);
    }

    [RelayCommand]
    private void RunLoop(LoopRowViewModel? row)
    {
        if (row is null) return;
        _services.LoopRunner.Start(row.Source);
    }

    /// <summary>
    /// Load a saved loop's waypoints into LoopBuild mode so the user
    /// can preview it on the map (red polyline + numbered markers)
    /// and optionally edit before hitting Run. Distinct from
    /// <see cref="RunLoop"/> (which starts the runner immediately)
    /// and from <see cref="PreviewLoop"/> (which just paints an
    /// overlay without entering build mode).
    /// </summary>
    [RelayCommand]
    private void LoadLoop(LoopRowViewModel? row)
    {
        if (row is null) return;

        // If a loop is currently running / approaching / paused, stop
        // it first — loading is a "start from scratch in builder"
        // intent and shouldn't leave a stale engine in the
        // background.
        if (_services.LoopRunner.State != Game.Map.LoopState.Idle)
            _services.LoopRunner.Stop("loop loaded into builder");

        // Tear down any prior build session, then seed a fresh one
        // from the saved loop's waypoints. ProposedName + Notes
        // carry over so Save in the Manage dialog round-trips back
        // to the same file (the LoopManager keys by name on disk).
        if (LoopBuilder is not null)
            LoopBuilder.PropertyChanged -= OnLoopBuilderPropertyChanged;

        var builder = new LoopBuilderSessionViewModel(
            _services.Loops, _services.RoomGraph, _services.Movement);
        builder.PropertyChanged += OnLoopBuilderPropertyChanged;
        builder.ProposedName = row.Source.Name;
        builder.Notes        = row.Source.Notes;
        foreach (LoopWaypoint w in row.Source.Waypoints)
            builder.AddClick(w.Key);

        LoopBuilder = builder;
        CurrentMode = NavigationMode.LoopBuild;
        // Distinct from the pause-opens-builder path — Load is
        // user-initiated, not a side effect of Pause, so the
        // resume-restart logic in RunStop shouldn't see this as
        // "paused with edits".
        _loopBuilderOpenedByPause = false;
        OnPropertyChanged(nameof(LoopBuilder));
        OnPropertyChanged(nameof(IsLoopBuilding));

        // Paint the red preview + numbered markers immediately,
        // same belt-and-braces as OpenBuilderForRunningLoop —
        // PropertyChanged during the AddClick loop fired before the
        // field assignment.
        LoopBuilderPath      = builder.PreviewedRoomKeys;
        LoopBuilderWaypoints = builder.WaypointKeys;
        RefreshLoopOverlays();
    }

    [RelayCommand]
    private void StopLoop() => _services.LoopRunner.Stop();

    /// <summary>
    /// Right-click → Edit… on a Loops-pane row. Opens the modeless
    /// loop editor dialog; the editor mutates the loop in place +
    /// persists via LoopManager.Save which fires LoopsChanged so the
    /// pane refreshes.
    /// </summary>
    [RelayCommand]
    private async Task EditLoopAsync(LoopRowViewModel? row)
    {
        if (row is null) return;
        // Pass the runner + confirm service so the editor can offer
        // "apply changes to running loop now?" when the user edits
        // the loop that's currently in flight.
        var vm = new LoopEditorDialogViewModel(
            row.Source,
            _services.Loops,
            _services.RoomGraph,
            _services.LoopRunner,
            _services.Confirm);
        await _services.Dialogs
            .OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
    }

    /// <summary>
    /// Right-click → Delete on a Loops-pane row. Confirms via the
    /// shared ConfirmService (which honours the user's "skip
    /// delete confirms" setting), then removes the loop from disk
    /// and refreshes the pane.
    /// </summary>
    [RelayCommand]
    private async Task DeleteLoopAsync(LoopRowViewModel? row)
    {
        if (row is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync($"loop \"{row.Source.Name}\"");
        if (!ok) return;
        _services.Loops.Delete(row.Source.Name);
    }

    /// <summary>
    /// Right-click → Preview on a Loops-pane row. Lays the loop's
    /// expanded room sequence onto the map's LoopPath polyline
    /// without starting it. Clicking the same row again clears the
    /// preview. While a loop is actually running the live LoopPath
    /// wins (PR-7.16 RefreshLoopOverlays); previewing an idle loop
    /// is the only path this overlay surfaces.
    /// </summary>
    [RelayCommand]
    private void PreviewLoop(LoopRowViewModel? row)
    {
        if (row is null) { LoopPath = null; return; }
        if (row.Source.Waypoints.Count < 2)
        {
            LoopPath = null;
            return;
        }
        // Render the full cycle anchored at waypoint 0, sourced via
        // LoopExpander so the polyline matches what the runner would
        // actually drive at start time.
        IReadOnlyList<RoomKey> keys = LoopExpander.ResolveCycleRoomKeys(
            row.Source.Waypoints, _services.Bfs, _services.RoomGraph, _services.Movement);
        LoopPath = keys.Count >= 2 ? keys : null;
    }

    private IReadOnlyList<RoomKey> WalkLoopSteps(RoomKey from, IReadOnlyList<LoopStep> steps)
    {
        var seq = new List<RoomKey>(steps.Count + 1) { from };
        RoomKey cursor = from;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (_services.RoomGraph.GetRoom(cursor) is not { } room) break;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            cursor = exit.Target;
            seq.Add(cursor);
        }
        return seq;
    }

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
        OnPropertyChanged(nameof(ContextIsFavorite));
    }

    public bool ContextIsAvoided => ContextRoomKey is { } k && _services.Movement.IsAvoided(k);
    public bool ContextIsStash   => ContextRoomKey is { } k && _services.Movement.IsStash(k);
    public bool ContextIsFavorite => ContextRoomKey is { } k && _services.Favorites.IsFavorite(k);

    [RelayCommand]
    private void WalkToContextRoom()
    {
        if (ContextRoomKey is not { } k) return;
        // If a loop or Auto-Lair is currently driving movement, stop
        // it before handing control to the walker — the user's explicit
        // walk-to takes precedence over the automation in the
        // background. Auto-Lair owns the walker for its routing, so
        // stopping it cleanly releases the walker for our WalkTo call.
        if (_services.LoopRunner.State is Game.Map.LoopState.Running
                                       or Game.Map.LoopState.Paused
                                       or Game.Map.LoopState.Approaching)
            _services.LoopRunner.Stop("user walk-to from Navigation");
        if (_services.AutoLair.IsActive)
            _services.AutoLair.Stop();
        // Exit LoopBuild mode too — same UX contract as the loop /
        // lair stops above. The user picked a fresh walk-to, so any
        // in-progress build session should drop out of the way (the
        // CURRENT NAV pane swaps to the walker's step list, the
        // bottom builder strip collapses).
        if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
        _loopBuilderOpenedByPause = false;
        _services.Walker.WalkTo(k);
    }

    [RelayCommand]
    private void SetContextRoomLocated()
    {
        if (ContextRoomKey is { } k) _services.RoomTracker.SetLocated(k);
    }

    /// <summary>
    /// Right-click → "Add to favorites" (or "Remove from favorites"
    /// when already bookmarked). Persists via
    /// <see cref="FavoritesStore"/>; the GOTO pane refreshes from
    /// the store's Changed event.
    /// </summary>
    [RelayCommand]
    private void ToggleContextRoomFavorite()
    {
        if (ContextRoomKey is not { } k) return;
        if (_services.Favorites.IsFavorite(k))
            _services.Favorites.Remove(k);
        else
            _services.Favorites.Add(k);
        OnPropertyChanged(nameof(ContextIsFavorite));
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
        if (ContextRoomKey is not { } k)
        {
            _services.Log?.Debug("Navigation", "stash toggle aborted — no ContextRoomKey");
            return;
        }
        bool wasStash = _services.Movement.IsStash(k);
        if (wasStash) _services.Movement.UnmarkStash(k);
        else _services.Movement.MarkStash(k);
        _services.Log?.Info("Navigation",
            $"stash toggled key={k} {(wasStash ? "unmarked" : "marked")} profile-loaded={_services.Profile.Current is not null}");
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

    /// <summary>
    /// True while the user is in <see cref="NavigationMode.AutoLair"/>
    /// AND the Auto-Lair scheduler isn't actively running. Drives the
    /// bottom-strip "Building lair setup: N lair(s) marked" surface —
    /// counterpart of <see cref="IsLoopBuilding"/> for loops. While
    /// the scheduler is running, the engine phase strip
    /// (<see cref="IsAutoLairing"/>) takes precedence.
    /// </summary>
    public bool IsLairBuilding =>
        CurrentMode == NavigationMode.AutoLair && !IsAutoLairing;

    partial void OnCurrentModeChanged(NavigationMode value)
    {
        OnPropertyChanged(nameof(IsLoopMode));
        OnPropertyChanged(nameof(IsLairMode));
        OnPropertyChanged(nameof(IsLairBuilding));
        OnPropertyChanged(nameof(LairBuildStatusText));
        EnsureLairTickRunning();
        // Map overlay is gated on AutoLair mode OR scheduler-active —
        // toggle mode flip needs to refresh either way.
        RefreshAutoLairMarkedKeys();
        // Entering / leaving LoopBuild flips the overlay-suppression
        // branch in RefreshLoopOverlays — re-render so the blue cycle
        // (running) or red preview (build) switches over without
        // waiting for the next LoopEvent or tracker tick.
        RefreshLoopOverlays();
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
            if (LoopBuilder is not null)
                LoopBuilder.PropertyChanged -= OnLoopBuilderPropertyChanged;
            LoopBuilder = null;
            LoopBuilderPath = null;
            LoopBuilderWaypoints = null;
            CurrentMode = NavigationMode.Idle;
        }
        else
        {
            // Entering Loop build from anywhere else — tear down any
            // engine that's currently driving (AutoLair scheduler OR
            // an in-flight walk) AND any opposing build mode so the
            // user lands in a clean Loop build session.
            if (_services.AutoLair.IsActive)
                _services.AutoLair.Stop("loop mode requested");
            if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
                _services.Walker.Stop("loop mode requested");
            _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);
            if (CurrentMode == NavigationMode.AutoLair)
            {
                if (!_services.AutoLair.IsActive) _services.AutoLair.Clear();
                CurrentMode = NavigationMode.Idle;
            }

            LoopBuilder = new LoopBuilderSessionViewModel(
                _services.Loops, _services.RoomGraph, _services.Movement);
            // Mirror the builder's PreviewedRoomKeys onto our own
            // observable so the map's LoopBuilderPath binding picks
            // up every click without a Navigation-VM-side timer.
            LoopBuilder.PropertyChanged += OnLoopBuilderPropertyChanged;
            CurrentMode = NavigationMode.LoopBuild;
        }
        OnPropertyChanged(nameof(LoopBuilder));
        OnPropertyChanged(nameof(IsLoopBuilding));
    }

    private void OnLoopBuilderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Read off the sender, not the LoopBuilder field — during
        // OpenBuilderForRunningLoop the seeding AddClick() calls fire
        // before the field is assigned (we attach the handler before
        // looping over the waypoints so we don't miss the click-list
        // change notifications). Falling back to `LoopBuilder` here
        // produced null reads → the map's red preview never rendered
        // on Pause.
        LoopBuilderSessionViewModel? b = sender as LoopBuilderSessionViewModel ?? LoopBuilder;
        switch (e.PropertyName)
        {
            case nameof(LoopBuilderSessionViewModel.PreviewedRoomKeys):
                LoopBuilderPath = b?.PreviewedRoomKeys;
                break;
            case nameof(LoopBuilderSessionViewModel.WaypointKeys):
                LoopBuilderWaypoints = b?.WaypointKeys;
                break;
            case nameof(LoopBuilderSessionViewModel.CanSave):
                // CanRun + RunStopLabel depend on CanSave while in
                // LoopBuild mode — re-notify so the Run button enables
                // the moment the user has two reachable clicks.
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(RunStopLabel));
                RebuildCurrentNavRows();
                OnPropertyChanged(nameof(CurrentNavHeader));
                break;
            case nameof(LoopBuilderSessionViewModel.Clicks):
            case nameof(LoopBuilderSessionViewModel.HasClicks):
            case nameof(LoopBuilderSessionViewModel.ProposedName):
                RebuildCurrentNavRows();
                OnPropertyChanged(nameof(CurrentNavHeader));
                break;
        }
    }

    /// <summary>
    /// Called by the window when the map is left-clicked. Dispatched by
    /// the active <see cref="CurrentMode"/>:
    /// <list type="bullet">
    ///   <item><see cref="NavigationMode.LoopBuild"/> — forward the
    ///   click to the loop builder so the room joins the click
    ///   sequence.</item>
    ///   <item><see cref="NavigationMode.AutoLair"/> — toggle the room
    ///   as a marker on <see cref="AutoLairManager"/>. Mirrors how
    ///   loop-build mode accumulates waypoints; the user enters
    ///   Lair mode from the top-right action chip, clicks rooms to
    ///   add / remove, then commits via the rail's "Save lairs"
    ///   button.</item>
    ///   <item>Idle — no-op (the click already moved
    ///   <see cref="SelectedRoomKey"/> upstream).</item>
    /// </list>
    /// </summary>
    public void OnRoomLeftClicked(RoomKey key)
    {
        switch (CurrentMode)
        {
            case NavigationMode.LoopBuild:
                LoopBuilder?.AddClick(key);
                break;
            case NavigationMode.AutoLair:
                _services.AutoLair.Toggle(key);
                break;
        }
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

    /// <summary>
    /// Open the Manage dialog — modeless surface for renaming /
    /// deleting saved loops and unmarking Auto-Lair rooms. Per UX
    /// direction this is where naming + lifecycle CRUD live; the
    /// bottom build strip is a pure status display.
    /// </summary>
    [RelayCommand]
    private async Task OpenManagerAsync()
    {
        // Pass the live LoopBuilder (when in build mode) so the
        // dialog's Draft section is the user's authoritative Save
        // surface. The consumed callback exits LoopBuild mode here
        // after Save / Discard so the bottom builder strip collapses.
        // Runner reference lets the dialog show "Save running loop"
        // + drive the editor's apply-to-running-loop prompt.
        // New is now an away-from-the-map editor flow (opens the
        // LoopEditor dialog) so no map-side hand-off callback is
        // needed any more.
        NavigationManagerDialogViewModel vm = new(
            _services.Loops,
            _services.Lairs,
            _services.LairTimers,
            _services.RoomGraph,
            _services.Confirm,
            _services.Dialogs,
            draft: LoopBuilder,
            onDraftConsumed: () =>
            {
                if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
            },
            runner: _services.LoopRunner,
            mpImporter: _services.MpImporter,
            log: _services.Log);
        await _services.Dialogs
            .OpenWindowAsync<NavigationManagerDialogViewModel, bool>(vm);
    }

    /// <summary>
    /// Toggle the Lair "build" mode (mirrors <see cref="ToggleLoopMode"/>).
    /// Exiting build mode DISCARDS the in-progress marker set — matches
    /// LoopBuilder's "exit discards clicks" semantics so the user has a
    /// clean idle state. Markers loaded from a saved setup are equally
    /// transient; persist them via the rail's "Save lairs" button before
    /// toggling out.
    /// </summary>
    /// <remarks>
    /// Exception: when the scheduler is actively running we keep the
    /// markers in place — clearing them would yank the rug out from
    /// under the live engine. The Lair-mode chip then shows "Stop"
    /// instead of "Building" and routes through the
    /// <c>LoopModeButtonCommand</c> dispatcher (per
    /// <see cref="LairModeButtonIsStop"/>).
    /// </remarks>
    [RelayCommand]
    private void ToggleLairMode()
    {
        if (CurrentMode == NavigationMode.AutoLair)
        {
            // Drop transient build-state on exit, unless the scheduler
            // is using it. The Save button persisted what the user
            // wanted; everything else is scratchpad.
            if (!_services.AutoLair.IsActive)
                _services.AutoLair.Clear();
            CurrentMode = NavigationMode.Idle;
        }
        else
        {
            // Entering Lair mode from anywhere else — tear down any
            // active loop run / walker / loop-build session so the
            // user lands in a clean Lair build mode regardless of
            // what was happening before.
            if (_services.LoopRunner.State != Game.Map.LoopState.Idle)
                _services.LoopRunner.Stop("lair mode requested");
            if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
                _services.Walker.Stop("lair mode requested");
            _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);
            if (CurrentMode == NavigationMode.LoopBuild)
                ToggleLoopMode();
            CurrentMode = NavigationMode.AutoLair;
        }
    }

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
        RefreshAutoLairApproachPath();
        RefreshDerivedState();
    }

    private void OnRecoveryTierChanged(RecoveryTierChangedEvent _) => RefreshRecoveryTierBools();

    /// <summary>True when the engine-recovery gate is in tier 2 — engine chip border goes yellow.</summary>
    public bool IsTier2 => _isTier2;
    /// <summary>True when the engine-recovery gate is in tier 3 — engine chip border goes red.</summary>
    public bool IsTier3 => _isTier3;
    private bool _isTier2;
    private bool _isTier3;

    private void RefreshRecoveryTierBools()
    {
        TierLevel tier = _services.Recovery.CurrentTier;
        bool tier2 = tier == TierLevel.Tier2;
        bool tier3 = tier == TierLevel.Tier3;
        if (tier2 != _isTier2)
        {
            _isTier2 = tier2;
            OnPropertyChanged(nameof(IsTier2));
        }
        if (tier3 != _isTier3)
        {
            _isTier3 = tier3;
            OnPropertyChanged(nameof(IsTier3));
        }
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

    private void OnGraphReloaded()
    {
        // RoomSearchService listens to GraphReloaded itself and flushes
        // its monster + distance caches.
        RefreshLayout();
        RefreshTeleportRooms();
    }

    /// <summary>
    /// Walk every room with a non-zero Cmd and ask TBInfo whether the
    /// CMD's Action chain contains a teleport directive. The resulting
    /// set drives the map's diagonal hash-line overlay so the user
    /// can spot non-exit movement spots at a glance.
    /// </summary>
    private void RefreshTeleportRooms()
    {
        if (Graph is null) { TeleportRooms = null; return; }
        HashSet<RoomKey> set = new();
        foreach (Room room in Graph.Rooms)
        {
            if (room.Cmd <= 0) continue;
            using IEnumerator<(string, RoomKey)> e =
                TBInfoTeleportResolver.EnumerateTeleports(_services.TBInfo, room.Cmd).GetEnumerator();
            if (e.MoveNext()) set.Add(room.Key);
        }
        TeleportRooms = set;
    }

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

    /// <summary>
    /// Primary action-chip face. Loops transform into Pause / Run for
    /// pause-resume cycling (the user can edit the loop while paused);
    /// walker + auto-lair stay as Run / Stop (one-shot engines).
    /// </summary>
    public string RunStopLabel
    {
        get
        {
            Game.Map.LoopRunner runner = _services.LoopRunner;
            if (runner.State is Game.Map.LoopState.Running
                              or Game.Map.LoopState.Approaching) return "Pause";
            if (runner.State == Game.Map.LoopState.Paused) return "Run";
            // Auto-Lair gets Pause / Run too — the chip stays distinct
            // from the Lair-mode "Stop" so the user has both Pause (this
            // chip) and Stop (the mode chip) without duplication.
            if (_services.AutoLair.IsActive)
                return _services.AutoLair.IsPaused ? "Run" : "Pause";
            return IsAnyExecuting ? "Stop" : "Run";
        }
    }


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
                    Game.Map.LoopRunner lr = _services.LoopRunner;
                    string name = lr.CurrentLoop?.Name ?? "?";
                    int total = lr.StepCount;
                    if (total <= 0) return name;
                    // CurrentIndex is the next step to send, clamped to
                    // [0, total). Display as 1-based so the user reads
                    // it the same way the LoopRunner logs its steps
                    // ("step 14: move S").
                    int human = Math.Min(total, lr.CurrentIndex + 1);
                    return $"{name} on step {human} of {total}";
                }
                case NavigationEngineKind.AutoLair:
                {
                    // Surface the same phase + target detail that used
                    // to live in the bottom strip — the strip's gone
                    // during active runs, so all the running-state info
                    // now rides next to the top-left AUTO-LAIR badge.
                    int n = _services.AutoLair.Marked.Count;
                    string countLabel = $"cycling {n} marked lair{(n == 1 ? "" : "s")}";
                    if (_services.AutoLair.IsPaused) return $"{countLabel} · paused";
                    if (AutoLairStatusText is { Length: > 0 } status)
                        return $"{AutoLairPhaseLabel} · {status}";
                    return countLabel;
                }
                default:
                {
                    Room? here = _services.RoomTracker.State.CurrentRoom;
                    return here is null ? "—" : FormatRoomRef(here.Key);
                }
            }
        }
    }

    /// <summary>
    /// Shared row population for the AutoLair branch of
    /// <see cref="RebuildCurrentNavRows"/> — runs both in Build mode
    /// (pre-scheduler) and during an active run. The only behavioural
    /// difference is the per-row status / sub-label, both keyed off
    /// whether the scheduler has a <see cref="Game.Map.AutoLairManager.CurrentTarget"/>.
    /// Order: target first, then the rest sorted by Map / Room.
    /// </summary>
    private void PopulateLairRows()
    {
        Game.Map.AutoLairManager mgr = _services.AutoLair;
        RoomKey? target = mgr.CurrentTarget;

        List<RoomKey> ordered = new(mgr.Marked.Count);
        if (target is { } t && mgr.Marked.Contains(t)) ordered.Add(t);
        foreach (RoomKey key in mgr.Marked
            .Where(k => target is not { } tt || !tt.Equals(k))
            .OrderBy(k => k.Map).ThenBy(k => k.Room))
            ordered.Add(key);

        int i = 1;
        foreach (RoomKey key in ordered)
        {
            bool isTarget = target is { } t2 && t2.Equals(key);
            CurrentNavRows.Add(new CurrentNavRowViewModel(
                index: i++,
                label: FormatRoomRef(key),
                status: isTarget ? CurrentNavRowStatus.Current : CurrentNavRowStatus.Ready,
                subLabel: BuildLairSubLabel(key, isTarget),
                removeKey: key,
                editKey: key));
        }
    }

    /// <summary>
    /// Compose the CURRENT NAV sub-label for a marked lair row.
    /// Behaviour by phase:
    /// <list type="bullet">
    ///   <item><b>Active target</b> — show the scheduler phase + the
    ///   countdown to entry ("Waiting · 0:42 to entry").</item>
    ///   <item><b>Visited this session</b> — show the per-room
    ///   respawn countdown ("respawns in 12:34") or "ready" once
    ///   <see cref="LairTimerStore.NextReadyAt"/> falls past now.</item>
    ///   <item><b>Never visited</b> — show the game-data default
    ///   respawn ("game default 30:00") so the user can see whether
    ///   the room they marked actually carries a lair tag; rooms
    ///   without a tag surface "no game-data timer" instead.</item>
    /// </list>
    /// </summary>
    private string BuildLairSubLabel(RoomKey key, bool isTarget)
    {
        Game.Map.AutoLairManager mgr = _services.AutoLair;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (isTarget)
        {
            string phase = mgr.Phase.ToString();
            if (mgr.Phase == Game.Map.AutoLairPhase.Waiting
                && mgr.CurrentEntryArrivalAt is { } at)
            {
                TimeSpan remain = at - now;
                return remain > TimeSpan.Zero
                    ? $"{phase} · {FormatMmSs(remain)} to entry"
                    : $"{phase} · entering";
            }
            return phase;
        }

        // Visited this session → live countdown from LastEntered.
        int? overrideSec = mgr.GetOverride(key);
        DateTimeOffset? ready = _services.LairTimers.NextReadyAt(key, overrideSec);
        if (ready is { } readyAt)
        {
            TimeSpan delta = readyAt - now;
            return delta <= TimeSpan.Zero
                ? "ready"
                : $"respawns in {FormatMmSs(delta)}";
        }

        // Never visited → fall back to the resolved timer (override
        // first, then game-data default) so the user can confirm at
        // click-time whether the room has a usable lair timer.
        int? defaultSec = overrideSec ?? _services.LairTimers.DefaultRespawnSeconds(key);
        if (defaultSec is { } secs)
            return FormatMmSs(TimeSpan.FromSeconds(secs));

        return "no timer";
    }

    /// <summary>
    /// Format a lair-timer duration for the CURRENT NAV sub-labels.
    /// Plain total seconds (e.g. <c>"270s"</c>) — per user direction
    /// the rooms we mark in this surface only ever respawn in the
    /// 30-300 s range, so a single number stays compact and scannable
    /// without the user mentally converting <c>4:30</c> back into
    /// seconds. Negative inputs clamp to 0.
    /// </summary>
    private static string FormatMmSs(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int totalSec = (int)Math.Round(t.TotalSeconds);
        return $"{totalSec}s";
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

    /// <summary>Engine-state tag the badge displays: WALKING / LOOPING / AUTO-LAIR / IDLE.</summary>
    public string TopBarStatusBadge => EngineActionKind switch
    {
        NavigationEngineKind.Walking  => "WALKING",
        NavigationEngineKind.Looping  => "LOOPING",
        NavigationEngineKind.AutoLair => "AUTO-LAIR",
        _                             => "IDLE",
    };

    // Boolean view-shaped helpers — drive the badge background class via
    // Classes.{IdleState,WalkingState,LoopingState,LairState} so styles
    // own the per-state colours (grey / green / green / orange per user).
    public bool EngineActionIsIdle    => EngineActionKind == NavigationEngineKind.Idle;
    public bool EngineActionIsWalking => EngineActionKind == NavigationEngineKind.Walking;
    public bool EngineActionIsLooping => EngineActionKind == NavigationEngineKind.Looping;
    public bool EngineActionIsLair    => EngineActionKind == NavigationEngineKind.AutoLair;

    /// <summary>Loop-mode button face: idle → "Loop mode"; mode-on → "Building"; running → "Stop".</summary>
    public string LoopModeButtonLabel => EngineActionKind == NavigationEngineKind.Looping
        ? "Stop"
        : (CurrentMode == NavigationMode.LoopBuild ? "Building" : "Loop mode");

    public bool LoopModeButtonIsStop => EngineActionKind == NavigationEngineKind.Looping;

    /// <summary>
    /// Dispatcher for the Loop-mode button: when looping (any state)
    /// the button is a full Stop; otherwise it's the build-mode
    /// toggle. Keeping one physical button keeps the action chip row
    /// compact and matches the user's expectation that the Run chip
    /// transforms to Pause while the Loop-mode chip carries the Stop.
    /// </summary>
    [RelayCommand]
    private void LoopModeButton()
    {
        if (LoopModeButtonIsStop)
        {
            StopAll();
            return;
        }
        ToggleLoopMode();
    }

    /// <summary>Lair-mode button face: idle → "Lair mode"; mode-on → "Building"; running → "Stop".</summary>
    public string LairModeButtonLabel => EngineActionKind == NavigationEngineKind.AutoLair
        ? "Stop"
        : (CurrentMode == NavigationMode.AutoLair ? "Building" : "Lair mode");

    public bool LairModeButtonIsStop => EngineActionKind == NavigationEngineKind.AutoLair;

    /// <summary>
    /// Dispatcher for the Lair-mode chip — symmetric with
    /// <see cref="LoopModeButton"/>. When the scheduler is active
    /// the button carries Stop semantics (routes through
    /// <see cref="StopAll"/>); otherwise it's the build-mode
    /// toggle.
    /// </summary>
    [RelayCommand]
    private void LairModeButton()
    {
        if (LairModeButtonIsStop)
        {
            StopAll();
            return;
        }
        ToggleLairMode();
    }

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(IsAnyExecuting));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunStopLabel));
        OnPropertyChanged(nameof(TopBarStatusText));
        OnPropertyChanged(nameof(TopBarStatusBadge));
        OnPropertyChanged(nameof(EngineActionIsIdle));
        OnPropertyChanged(nameof(EngineActionIsWalking));
        OnPropertyChanged(nameof(EngineActionIsLooping));
        OnPropertyChanged(nameof(EngineActionIsLair));
        OnPropertyChanged(nameof(LoopModeButtonLabel));
        OnPropertyChanged(nameof(LoopModeButtonIsStop));
        OnPropertyChanged(nameof(LairModeButtonLabel));
        OnPropertyChanged(nameof(LairModeButtonIsStop));
        OnPropertyChanged(nameof(IsLairBuilding));
        OnPropertyChanged(nameof(LairBuildStatusText));
        OnPropertyChanged(nameof(CanSaveCurrent));
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
        Game.Map.LoopRunner runner = _services.LoopRunner;

        // Loop paused → resume (clear the user-pause gate). If the
        // builder was auto-opened by Pause AND the user edited the
        // click list while paused, treat Run as "stop + restart with
        // the new clicks" so the edits actually apply. Otherwise
        // just clear the gate and let the runner continue from where
        // it left off.
        if (runner.State == Game.Map.LoopState.Paused && runner.CurrentLoop is { } pausedLoop)
        {
            bool edited = _loopBuilderOpenedByPause
                       && LoopBuilder is { } b
                       && BuilderClicksDifferFrom(b, pausedLoop);
            if (edited && LoopBuilder is { CanSave: true } edBuilder)
            {
                Game.Map.Loop? rebuilt = edBuilder.BuildTransient();
                if (rebuilt is not null)
                {
                    runner.Stop("edits applied during pause; restarting");
                    _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);
                    runner.Start(rebuilt);
                    _loopBuilderOpenedByPause = false;
                    if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
                    return;
                }
            }

            _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);
            if (_loopBuilderOpenedByPause)
            {
                _loopBuilderOpenedByPause = false;
                if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
            }
            return;
        }

        // Loop running or approaching → pause (assert user gate) and
        // auto-open the builder seeded from the running loop so the
        // user can edit clicks before resuming.
        if (runner.State is Game.Map.LoopState.Running
                         or Game.Map.LoopState.Approaching)
        {
            _services.MovementCoordinator.AssertGate(Game.Map.MovementCoordinator.UserGate);
            OpenBuilderForRunningLoop();
            return;
        }

        // Walker / auto-lair stay as one-shot Stop semantics — no
        // pause-resume cycle wanted by the UX rules.
        // Auto-Lair: Run chip is now Pause / Resume. The Lair-mode
        // chip carries the actual Stop. This keeps the two chips
        // distinct rather than both reading "Stop" while the run is
        // in flight.
        if (_services.AutoLair.IsActive)
        {
            if (_services.AutoLair.IsPaused) _services.AutoLair.Resume();
            else _services.AutoLair.Pause();
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
            // Transient run: build the Loop in memory + start the
            // runner without writing to disk. Per UX direction the
            // saved-loops list is owned by explicit user action (Save
            // in the Manage dialog), never by Run. We exit LoopBuild
            // mode so the bottom builder strip collapses and the
            // running loop's CURRENT NAV pane takes over.
            Game.Map.Loop? transient = LoopBuilder.BuildTransient();
            if (transient is not null)
            {
                _services.LoopRunner.Start(transient);
                if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
            }
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

    /// <summary>
    /// Set true when <see cref="OpenBuilderForRunningLoop"/> opened
    /// build mode in response to user-pause so a subsequent Run / Stop
    /// can decide whether to close it again.
    /// </summary>
    private bool _loopBuilderOpenedByPause;

    /// <summary>
    /// True when the builder's click list no longer matches the loop
    /// it was seeded from — used by the Pause → Edit → Run flow to
    /// decide whether to resume the in-flight loop or stop and restart
    /// with the new clicks. Compares 1:1 in order; renames + waypoint
    /// reorders all count as edits.
    /// </summary>
    private static bool BuilderClicksDifferFrom(LoopBuilderSessionViewModel builder, Game.Map.Loop loop)
    {
        if (builder.Clicks.Count != loop.Waypoints.Count) return true;
        for (int i = 0; i < builder.Clicks.Count; i++)
        {
            if (!builder.Clicks[i].Key.Equals(loop.Waypoints[i].Key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pause flow: stop the runner via the user gate, then re-open the
    /// builder pre-seeded with the running loop's name + notes + click
    /// list so the user can edit before hitting Run again.
    /// </summary>
    private void OpenBuilderForRunningLoop()
    {
        Game.Map.LoopRunner runner = _services.LoopRunner;
        if (runner.CurrentLoop is not { } loop) return;

        if (LoopBuilder is not null)
            LoopBuilder.PropertyChanged -= OnLoopBuilderPropertyChanged;

        var builder = new LoopBuilderSessionViewModel(
            _services.Loops, _services.RoomGraph, _services.Movement);
        builder.PropertyChanged += OnLoopBuilderPropertyChanged;
        builder.ProposedName = loop.Name;
        builder.Notes        = loop.Notes;
        foreach (LoopWaypoint w in loop.Waypoints) builder.AddClick(w.Key);

        LoopBuilder = builder;
        CurrentMode = NavigationMode.LoopBuild;
        _loopBuilderOpenedByPause = true;
        OnPropertyChanged(nameof(LoopBuilder));
        OnPropertyChanged(nameof(IsLoopBuilding));

        // Belt-and-braces: even with the sender-fallback in
        // OnLoopBuilderPropertyChanged the path/waypoint observables
        // may already be null because PreviewedRoomKeys/WaypointKeys
        // were set during AddClick before this method's final assignment
        // could fire. Pull the current values across so the map's
        // builder overlay paints immediately.
        LoopBuilderPath       = builder.PreviewedRoomKeys;
        LoopBuilderWaypoints  = builder.WaypointKeys;
        RefreshLoopOverlays();
    }

    /// <summary>
    /// Full-stop action. Always returns the user to the idle map
    /// view — engines stopped, builder closed, user gate cleared so
    /// the next Run isn't accidentally held paused.
    /// </summary>
    [RelayCommand]
    private void StopAll()
    {
        if (_services.AutoLair.IsActive) _services.AutoLair.Stop();
        if (_services.LoopRunner.State != Game.Map.LoopState.Idle)
            _services.LoopRunner.Stop();
        if (_services.Walker.State is WalkState.Walking or WalkState.Paused)
            _services.Walker.Stop("user stop from Navigation");

        _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);

        if (CurrentMode == NavigationMode.LoopBuild)
        {
            if (LoopBuilder is not null)
                LoopBuilder.PropertyChanged -= OnLoopBuilderPropertyChanged;
            LoopBuilder = null;
            LoopBuilderPath = null;
            LoopBuilderWaypoints = null;
            CurrentMode = NavigationMode.Idle;
            OnPropertyChanged(nameof(LoopBuilder));
            OnPropertyChanged(nameof(IsLoopBuilding));
        }
        // Exit AutoLair build mode too — previously StopAll stopped
        // the scheduler but left CurrentMode == AutoLair, forcing the
        // user to click Stop a second time to actually return to
        // Idle.  Now one click does it.  Markers stay around so a
        // quick Run is still cheap; the user explicitly toggles the
        // Lair chip to discard them.
        if (CurrentMode == NavigationMode.AutoLair)
            CurrentMode = NavigationMode.Idle;
        _loopBuilderOpenedByPause = false;
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
    public string CurrentNavHeader
    {
        get
        {
            // Building mode overrides engine state — surface the in-
            // progress click list so the user sees what they're laying
            // down before they hit Run.
            if (IsLoopBuilding && LoopBuilder is { } b)
            {
                string namePart = string.IsNullOrWhiteSpace(b.ProposedName) ? "Loop" : b.ProposedName;
                int clicks = b.Clicks.Count;
                string suffix = clicks switch
                {
                    0 => "click rooms on the map to add waypoints",
                    1 => "1 room clicked — add at least one more",
                    _ => $"{clicks} rooms clicked",
                };
                return $"Building loop: {namePart} · {suffix}";
            }
            // Auto-Lair build mode mirrors the loop-builder line — show
            // the marked-rooms hint instead of the default "No Active
            // Navigation" placeholder so the CURRENT NAV section's
            // header reflects the live work-in-progress.
            if (IsLairBuilding)
            {
                int markers = _services.AutoLair.Marked.Count;
                return markers switch
                {
                    0 => "Building lair setup · click rooms to add markers",
                    1 => "Building lair setup · 1 lair marked",
                    _ => $"Building lair setup · {markers} lairs marked",
                };
            }
            return EngineActionKind switch
            {
                NavigationEngineKind.Walking =>
                    _services.Walker.Destination is { } k
                        ? $"{_services.Walker.CurrentStepIndex + 1} of {_services.Walker.StepCount} steps to {FormatRoomRef(k)}"
                        : $"{_services.Walker.CurrentStepIndex + 1} of {_services.Walker.StepCount} steps",
                NavigationEngineKind.Looping  => BuildLoopHeader(),
                NavigationEngineKind.AutoLair => "Cycling marked lairs",
                _ => "No Active Navigation",
            };
        }
    }

    private string BuildLoopHeader()
    {
        Game.Map.LoopRunner runner = _services.LoopRunner;
        if (runner.CurrentLoop is not { } loop) return "Cycling loop steps";

        // Approach phase: walker is driving toward the loop's chosen
        // entry waypoint. The walker owns the step counter during this
        // window; surface ITS progress, not the loop's.
        if (runner.State == Game.Map.LoopState.Approaching)
        {
            string target = runner.ApproachTarget is { } t
                ? FormatRoomRef(t)
                : "first waypoint";
            return $"Approaching {target} ({_services.Walker.CurrentStepIndex + 1}" +
                   $"/{_services.Walker.StepCount})";
        }

        // Circle phase: show step N/K + lap counter + average lap.
        int laps = runner.LapHistory.Count;
        int total = runner.StepCount;
        int stepNum = total == 0 ? 0 : runner.CurrentIndex + 1;
        string lapPart = laps switch
        {
            0 => "lap 1",
            1 => $"lap 2 · avg {FormatDuration(runner.AverageLapTime)}",
            _ => $"lap {laps + 1} · avg {FormatDuration(runner.AverageLapTime)}",
        };
        return $"{loop.Name} · step {stepNum}/{total} · {lapPart}";
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    /// <summary>Progress as a 0..1 fraction for the small inline bar; null when no progress meter applies (e.g. Auto-Lair).</summary>
    public double? CurrentNavProgress
    {
        get
        {
            if (EngineActionKind == NavigationEngineKind.Walking)
            {
                int total = _services.Walker.StepCount;
                if (total <= 0) return null;
                return Math.Clamp((double)_services.Walker.CurrentStepIndex / total, 0, 1);
            }
            if (EngineActionKind == NavigationEngineKind.Looping)
            {
                Game.Map.LoopRunner runner = _services.LoopRunner;
                if (runner.State == Game.Map.LoopState.Approaching)
                {
                    int wt = _services.Walker.StepCount;
                    if (wt <= 0) return null;
                    return Math.Clamp((double)_services.Walker.CurrentStepIndex / wt, 0, 1);
                }
                int total = runner.StepCount;
                if (total <= 0) return null;
                return Math.Clamp((double)runner.CurrentIndex / total, 0, 1);
            }
            return null;
        }
    }

    public bool CurrentNavHasProgress => CurrentNavProgress is not null;

    private void RebuildCurrentNavRows()
    {
        CurrentNavRows.Clear();

        // Build mode for Auto-Lair populates the rows BEFORE the
        // scheduler starts so the user sees what they've marked +
        // each room's resolved respawn timer as they click. The same
        // builder produces the running view too — the only difference
        // is whether the scheduler's CurrentTarget is set.
        if (EngineActionKind != NavigationEngineKind.AutoLair
            && CurrentMode == NavigationMode.AutoLair
            && _services.AutoLair.Marked.Count > 0)
        {
            PopulateLairRows();
            return;
        }

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
                PopulateLairRows();
                break;
            case NavigationEngineKind.Looping:
            {
                Game.Map.LoopRunner runner = _services.LoopRunner;
                if (runner.CurrentLoop is not { } loop) break;

                // Approach phase: borrow the walker's step list so the
                // user sees the actual moves driving them to the entry
                // waypoint, not the dormant loop's circle.
                if (runner.State == Game.Map.LoopState.Approaching)
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

                // Running circle: render the runner's expanded step
                // sequence (BFS-filled moves + per-waypoint commands)
                // with the same Completed/Current/Upcoming shape the
                // walker uses.
                int loopIdx = runner.CurrentIndex;
                IReadOnlyList<LoopStep> expanded = runner.ExpandedSteps;
                for (int i = 0; i < expanded.Count; i++)
                {
                    CurrentNavRowStatus status = i < loopIdx
                        ? CurrentNavRowStatus.Completed
                        : (i == loopIdx ? CurrentNavRowStatus.Current : CurrentNavRowStatus.Upcoming);
                    CurrentNavRows.Add(new CurrentNavRowViewModel(
                        index: i + 1, label: expanded[i].Display, status: status));
                }
                break;
            }
        }
    }

    [RelayCommand]
    private void UnmarkAutoLairRoom(RoomKey? key)
    {
        if (key is { } k) _services.AutoLair.Toggle(k);
    }

    /// <summary>
    /// Building Loop row click — remove the click at the given
    /// 1-based index (Clicks renderer's Index field). Called from the
    /// builder ListBox's row PointerPressed handler.
    /// </summary>
    public void RemoveBuilderClickAt(int oneBasedIndex)
    {
        if (LoopBuilder is null) return;
        LoopBuilder.RemoveClickAt(oneBasedIndex - 1);
    }

    /// <summary>
    /// Building Loop drag-reorder — move the row at
    /// <paramref name="fromOneBased"/> to <paramref name="toOneBased"/>.
    /// </summary>
    public void MoveBuilderClick(int fromOneBased, int toOneBased)
    {
        if (LoopBuilder is null) return;
        LoopBuilder.MoveClick(fromOneBased - 1, toOneBased - 1);
    }

    /// <summary>Up-arrow click on a builder row — moves it one place earlier in the click order.</summary>
    [RelayCommand]
    private void MoveBuilderClickUp(LoopBuilderRow? row)
    {
        if (row is null) return;
        MoveBuilderClick(row.Index, row.Index - 1);
    }

    /// <summary>Down-arrow click on a builder row — moves it one place later in the click order.</summary>
    [RelayCommand]
    private void MoveBuilderClickDown(LoopBuilderRow? row)
    {
        if (row is null) return;
        MoveBuilderClick(row.Index, row.Index + 1);
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
