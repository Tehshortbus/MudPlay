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

// View-model for the NavigationWindow shell — owns the status strip + mode
// bar and hosts the per-section state for map / room tree / favourites /
// loop builder.
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
        _services.MovementCoordinator.GatesChanged += OnGatesChanged;
        _services.PlayerDeathHalt.HaltedForDeathChanged += OnGatesChanged;
        _services.DeathRecovery.PropertyChanged += OnDeathRecoveryChanged;
        _services.RoomTracker.PlayerDeathObserved += RefreshDeathRooms;
        _services.Conditions.PropertyChanged += OnConditionsChanged;
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
        _services.NavFolders.FoldersChanged += OnNavFoldersChanged;
        OnAutoLairMarkedChanged();
        IsAutoLairing = _services.AutoLair.IsActive;
        RefreshLoopsAndLairs();
        Graph = _services.RoomGraph;
        EnsureLairTickRunning();
        _services.Macros.Macros.CollectionChanged += OnMacrosCollectionChanged;
        RefreshFromTracker();
        RefreshFromWalker();
        // Seed the loop overlay from the live LoopRunner state. RefreshFromWalker
        // above already seeds the walker path + the engine-action text, but the
        // blue loop polyline (LoopPath) is otherwise only drawn from
        // OnLoopRunnerEvent — so a Navigation window reopened mid-loop would show
        // no loop line until the loop's next step fired an event. Seeding here
        // mirrors the overlay refresh the event handler runs.
        RefreshLoopOverlays();
        RefreshLayout();
        RefreshFavorites();
        RefreshCrawlerChords();
        RefreshTeleportRooms();
        RefreshDeathRooms();
    }

    // Per-second pump for CURRENT NAV lair countdowns. Cheap to leave
    // running, but explicitly gated so an idle Navigation window does no work.
    // See EnsureLairTickRunning.
    private readonly DispatcherTimer _lairTick;

    public void Dispose()
    {
        _lairTick.Stop();
        _services.RoomTracker.StateChanged -= OnTrackerStateChanged;
        _services.Recovery.TierChanged    -= OnRecoveryTierChanged;
        _services.Walker.Event -= OnWalkerEvent;
        _services.MovementCoordinator.PauseStateChanged -= OnPauseChanged;
        _services.MovementCoordinator.GatesChanged -= OnGatesChanged;
        _services.PlayerDeathHalt.HaltedForDeathChanged -= OnGatesChanged;
        _services.DeathRecovery.PropertyChanged -= OnDeathRecoveryChanged;
        _services.RoomTracker.PlayerDeathObserved -= RefreshDeathRooms;
        _services.Conditions.PropertyChanged -= OnConditionsChanged;
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
        _services.NavFolders.FoldersChanged -= OnNavFoldersChanged;
        _services.Macros.Macros.CollectionChanged -= OnMacrosCollectionChanged;
    }

    // Loops + lairs share the on-disk folder tree; a folder add / rename /
    // delete from either the rail or the Manage dialog rebuilds both trees
    // so an empty folder (or a moved-contents reparent) shows up at once.
    private void OnNavFoldersChanged() => RefreshLoopsAndLairs();

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

        // The 8 compass directions mirror the user's movement macros too:
        // a macro sending a bare "n" / "se" / etc. binds its key as the
        // crawler chord for that direction. Directions with no macro are
        // omitted and fall through to MapControl's numpad / arrow
        // defaults, so the crawler is never left unbound.
        Dictionary<Direction, FujinTerm.Models.Profile.KeyChord> compass = new();
        AddCompassChord(compass, Direction.N,  "n");
        AddCompassChord(compass, Direction.S,  "s");
        AddCompassChord(compass, Direction.E,  "e");
        AddCompassChord(compass, Direction.W,  "w");
        AddCompassChord(compass, Direction.NE, "ne");
        AddCompassChord(compass, Direction.NW, "nw");
        AddCompassChord(compass, Direction.SE, "se");
        AddCompassChord(compass, Direction.SW, "sw");
        CompassChords = compass.Count > 0 ? compass : null;
    }

    private void AddCompassChord(Dictionary<Direction, FujinTerm.Models.Profile.KeyChord> map,
        Direction dir, string command)
    {
        if (FindChordForDirectionCommand(command) is { } chord) map[dir] = chord;
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

    // Rebuild AutoLairMarkedKeys using the same ordering as PopulateLairRows
    // — active target first when there is one, then sorted by Map / Room.
    // Null when no markers OR the user isn't in any Lair-related context (so
    // the map doesn't draw stale overlays).
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

    // Flip the per-second pump (_lairTick) on / off based on whether the user
    // is currently looking at a CURRENT NAV that has lair countdowns. Active =
    // build mode with at least one marker OR scheduler running. Anything else
    // means nothing on screen ticks once a second, so leave the timer off.
    private void EnsureLairTickRunning()
    {
        bool shouldRun =
            (CurrentMode == NavigationMode.AutoLair && _services.AutoLair.Marked.Count > 0)
            || _services.AutoLair.IsActive;
        if (shouldRun && !_lairTick.IsEnabled) _lairTick.Start();
        else if (!shouldRun && _lairTick.IsEnabled) _lairTick.Stop();
    }

    // One-second tick — re-render the lair rows so the countdown sub-labels
    // stay current. Rebuilding the whole list isn't free, but the list is
    // short (typically < 10 rows) and a once-a-second refresh keeps the
    // binding logic simple. If profile + UI scale demand a finer touch later,
    // move the sub-label out to a per-row observable property.
    private void OnLairTick()
    {
        RebuildCurrentNavRows();
        OnPropertyChanged(nameof(AutoLairStatusText));
    }

    // Bottom-strip status for Auto-Lair build mode — counterpart of the loop
    // builder's room/step count line. Reads the live marker count and
    // explains how to commit (Run) vs discard (toggle Lair).
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

    // Rebuild AutoLairApproachPath from the current tracker position +
    // scheduler target. Only populated during the active-leg phases
    // (Approaching, Waiting, Entering); Engaging and Idle clear it so the
    // line disappears when the walker reaches the lair.
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

    // One-word label for the bottom-strip badge — "Approaching" / "Waiting" /
    // "Entering" / "Engaging" / "Idle". Surfaced as a separate property so the
    // badge can colour-code without recomputing the full status line.
    public string AutoLairPhaseLabel => _services.AutoLair.Phase switch
    {
        AutoLairPhase.Approaching => "Approaching",
        AutoLairPhase.Waiting     => "Waiting",
        AutoLairPhase.Entering    => "Entering",
        AutoLairPhase.Engaging    => "Engaging",
        _                         => "Idle",
    };

    // Bottom-strip status line for a running Auto-Lair session — e.g. "Sewer
    // Lair via 5/99 — 0:42 to entry". Empty when the scheduler isn't actively
    // driving the walker (Idle / Engaging without a target).
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

    // Right-click → "Add this room to Blacklist". Captures the selected
    // room's DisplayName from the active set's Rooms.json (NOT the player's
    // current-room name) so the Modify-Blacklist dialog later shows a human
    // label. Immediate persist + map redraw (the store fires Changed which
    // invalidates the BFS layout cache).
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

    // The DEATH aggregator broadcasts a bulk Records change (add / mark-recovered
    // / clear) via OnPropertyChanged(nameof(Records)); a null name is the
    // reset-everything signal. Either way, re-derive the skull set.
    private void OnDeathRecoveryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(Game.Recovery.DeathRecoveryManager.Records))
            RefreshDeathRooms();
    }

    // Rooms holding an un-recovered deathpile, projected onto the map as skull
    // markers. Refreshed on every death — RoomTracker.PlayerDeathObserved is the
    // universal signal that fires for the miracle-save phrasing too, so the skull
    // lands the instant we die — and on any DeathRecoveryManager.Records change,
    // so it clears the instant the pile flips to Recovered. Null when nothing is
    // outstanding so the map draws no marker.
    private void RefreshDeathRooms()
    {
        HashSet<RoomKey>? next = null;
        foreach (DeathRecord r in _services.DeathRecovery.Records)
        {
            if (r.Status == DeathRecoveryStatus.Recovered || r.Room is not { } room) continue;
            (next ??= new()).Add(new RoomKey(room.Map, room.Room));
        }
        DeathRooms = next;
    }

    private void OnLoopRunnerEvent(LoopEvent _)
    {
        OnPropertyChanged(nameof(IsLoopRunning));
        RefreshLoopOverlays();
        RefreshEngineActionLabel();
        // The lap counter ticks over on the RepeatStarted wrap event, which
        // isn't a tracker-state change — refresh the top-bar readout here so
        // the action line advances the moment a lap closes, not only on the
        // next room observation.
        OnPropertyChanged(nameof(TopBarStatusText));
        OnPropertyChanged(nameof(CurrentNavProgress));
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

    // ----- Highlight chips + legend ---------------------------------

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

    // Emit a system-log line each time a fresh map draw is generated,
    // recording the diagnostic "seed" — the BFS root that fully determines
    // the drawn shape (RoomLayout.LayoutRoot) — alongside the coverage
    // (room/stub counts) and any re-root note from the score-and-retry
    // pass. Lets a sparse vs. fuller draw be reported precisely without
    // occupying header chrome.
    partial void OnLayoutChanged(RoomLayout? value)
    {
        if (value is not { } l) return;
        int rooms = l.Positions.Count;
        string msg = $"map drawn — seed {l.LayoutRoot}, {rooms} room{(rooms == 1 ? "" : "s")}";
        if (l.StubCount > 0) msg += $", {l.StubCount} stub{(l.StubCount == 1 ? "" : "s")}";
        if (!l.LayoutRoot.Equals(l.Origin)) msg += $", re-rooted from {l.Origin}";
        _services.Log?.Info("Navigation", msg);
    }

    [ObservableProperty] private RoomKey? _currentRoomKey;
    partial void OnCurrentRoomKeyChanged(RoomKey? value) => RefreshPreviewPath();
    [ObservableProperty] private RoomKey? _destinationRoomKey;

    // Top-of-strip action label that replaces the redundant status-badge +
    // current-room label (current room lives in the main UI's bottom status
    // bar as the source-of-truth). Reads: "Idle" when no engine is moving;
    // "Walking to {dest}" while the walker is active; "Looping: {name}" while
    // the loop runner is active; "Auto-Lair" while the scheduler is driving.
    [ObservableProperty] private string _engineActionLabel = "Idle";
    [ObservableProperty] private RoomGraphManager? _graph;
    [ObservableProperty] private IReadOnlyList<RoomKey>? _walkPath;
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopPath;

    // Dashed cyan preview polyline drawn under the active loop / walk while
    // the user is in the LoopBuilder strip. Pulled from
    // LoopBuilderSessionViewModel.PreviewedRoomKeys whenever the builder
    // changes.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopBuilderPath;

    // Ordered RoomKey list for the map's numbered builder-waypoint markers.
    // Mirrors LoopBuilderSessionViewModel.WaypointKeys.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopBuilderWaypoints;

    // Red preview polyline drawn during the walker-approach phase of a loop
    // run. Lets the user see the upcoming cycle alongside the blue walk-to
    // line that's actively driving them to the start waypoint.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _loopApproachPreviewPath;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _avoidedRooms;

    // Rooms the user has flagged as stash drops. Bound to the MapControl's
    // StashRooms property — each room renders with a gold outline. Refreshed
    // on OnStashChanged.
    [ObservableProperty] private IReadOnlySet<RoomKey>? _stashRooms;

    [ObservableProperty] private IReadOnlyDictionary<RoomKey, int>? _loopSequenceNumbers;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _autoLairRooms;

    // Ordered marker list driving the map's numbered amber overlay. Same
    // ordering rule as PopulateLairRows: active target first (when the
    // scheduler is running), then the rest sorted by Map / Room. Visible
    // whenever the user is in AutoLair mode OR the scheduler is running; null
    // when no markers are placed.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _autoLairMarkedKeys;

    // Full projected route the walker will follow during the current
    // Auto-Lair leg: current → wait-room → lair. Held stable across the
    // Approaching → Waiting → Entering transitions so the map line doesn't
    // flicker every time the walker briefly goes Idle between sub-legs. Null
    // when no leg is active (Idle / Engaging) or when the BFS can't resolve
    // the route.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _autoLairApproachPath;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _teleportRooms;

    // Rooms with an un-recovered deathpile, bound to MapControl.DeathRooms — each
    // renders a skull. Refreshed by RefreshDeathRooms.
    [ObservableProperty] private IReadOnlySet<RoomKey>? _deathRooms;
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
    [ObservableProperty] private IReadOnlyDictionary<Direction, FujinTerm.Models.Profile.KeyChord>? _compassChords;

    // ----- Search ---------------------------------------------------

    [ObservableProperty] private string _searchQuery = string.Empty;

    // Top 50 matches by name (case-insensitive substring), sorted by step distance then name.
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    // Destination armed for the Run button. Set by selecting a room from the
    // search dropdown (or clicking one in the room context menu's "queue"
    // command later); cleared on Run / Stop. When non-null, the top-bar
    // destination chip shows its display name + key and the Run button is
    // enabled.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueuedDestinationLabel))]
    [NotifyPropertyChangedFor(nameof(HasQueuedDestination))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(RunStopLabel))]
    private RoomKey? _queuedDestination;

    partial void OnQueuedDestinationChanged(RoomKey? value) => RefreshPreviewPath();

    // Red preview line drawn on the map while a destination is queued but not
    // yet running. Bound to MapControl.PreviewPath. Cleared when no
    // destination is queued OR no path exists. Recomputed on QueuedDestination
    // change and on CurrentRoomKey change (so the preview tracks the player if
    // they move while a target is armed).
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

    // Click handler for the queued-destination chip — discards the queued target + clears the preview line.
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

    // Display string for the top-bar chip: "Name 1/123" when set.
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

    // Repopulate SearchResults from query. Resolution + monster lookup + step
    // distance come from the shared RoomSearchService; this method's only job
    // is to hand the cap-50 ordered list into the observable collection the
    // dropdown binds to.
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

    // ----- Loops + Auto-Lair setups (combined) ----------------------

    // Loops in the active BBS (flat backing list).
    public ObservableCollection<LoopRowViewModel> Loops { get; } = new();

    // Saved Models.Profile.LairSetups for the active BBS (flat backing
    // list).
    public ObservableCollection<LairSetupRowViewModel> Setups { get; } = new();

    // Folder-grouped tree mixing NavFolderNodeViewModel folders with both
    // loop (LoopRowViewModel) and Auto-Lair (LairSetupRowViewModel)
    // leaves. Loops and lairs share one on-disk Loops directory, so the
    // rail renders them together as a single combined list under one
    // header.
    public ObservableCollection<object> NavTree { get; } = new();

    // True when the combined tree has any node (leaf or empty folder).
    public bool HasNavTree => NavTree.Count > 0;

    private void OnLoopsChanged() => RefreshLoopsAndLairs();

    private void OnSetupsChanged() => RefreshLoopsAndLairs();

    private void RefreshLoopsAndLairs()
    {
        Loops.Clear();
        foreach (Loop loop in _services.Loops.Loops)
            Loops.Add(new LoopRowViewModel(loop));
        Setups.Clear();
        foreach (Models.Profile.LairSetup s in _services.Lairs.Setups)
            Setups.Add(new LairSetupRowViewModel(s));

        // Both leaf kinds feed one tree keyed off the shared
        // NavFolderManager folder set; NavTreeBuilder orders folders
        // first then leaves alphabetically by type-aware sort key.
        var rows = new List<object>(Setups.Count + Loops.Count);
        rows.AddRange(Setups);
        rows.AddRange(Loops);
        NavTreeBuilder.Sync<object>(NavTree, rows, FolderOfNavRow, _services.NavFolders.AllFolders);
        OnPropertyChanged(nameof(HasNavTree));
    }

    private static string FolderOfNavRow(object row) => row switch
    {
        LoopRowViewModel l => l.Source.Folder,
        LairSetupRowViewModel s => s.Source.Folder,
        _ => string.Empty,
    };

    // Run a saved setup — wipes AutoLairManager's current markers, loads the
    // setup's markers (with their per-marker override timers + Skip flags),
    // then calls Start. Stops any in-flight loop / walk first so the scheduler
    // has clean ground.
    [RelayCommand]
    private void RunSetup(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        LoadSetupInternal(row.Source);
        _services.AutoLair.Start();
    }

    // Right-click → Load on a Setups row. Wipes current markers and loads the
    // setup's markers without starting the scheduler — lets the user inspect
    // / tweak before hitting Run.
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
            // Skip flag — currently informational only at the marker level;
            // the scheduler treats every marker as active.
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

    // Right-click → Edit on a Setups row → opens LairEditorDialog. Save
    // persists via LairManager.Save which fires SetupsChanged so the rail
    // refreshes.
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

    // CURRENT NAV ✎ button on a marked-lair row → single-marker timer
    // override editor. Dialog mutates AutoLairManager directly via
    // SetOverride; the scheduler picks up the change on its next tick.
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

    // Right-click → Delete on a Setups row. Confirms via the shared
    // ConfirmService (which honours the user's "skip delete confirms"
    // setting), then removes the setup from disk + refreshes the rail.
    [RelayCommand]
    private async Task DeleteSetupAsync(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync($"auto-lair setup \"{row.Source.Name}\"");
        if (!ok) return;
        _services.Lairs.Delete(row.Source.Name);
    }

    // True when the top-bar Save chip should be active — covers the four
    // situations the user might want to persist what they've built or are
    // running: Loop build mode with savable clicks, Loop running, Auto-Lair
    // build mode with markers, Auto-Lair running with markers. Drives the
    // chip's visibility AND its enabled state (we show the chip in all four
    // situations and disable it only when nothing is savable yet).
    public bool CanSaveCurrent
    {
        get
        {
            // Loop build: at least 2 reachable clicks committed.
            if (CurrentMode == NavigationMode.LoopBuild
                && LoopBuilder is { CanSave: true })
                return true;
            // Loop running: the runner has a loop in flight (any non-Idle state,
            // including the transient Recovering).
            if (_services.LoopRunner.State != Game.Map.LoopState.Idle
                && _services.LoopRunner.CurrentLoop is not null)
                return true;
            // Auto-Lair build / running with at least one marker.
            if ((CurrentMode == NavigationMode.AutoLair || _services.AutoLair.IsActive)
                && _services.AutoLair.Marked.Count > 0)
                return true;
            return false;
        }
    }

    // Dispatcher for the top-bar Save chip. Opens the right editor dialog
    // (Loop or Lair) pre-seeded with the current state — the user reviews /
    // renames / commits there. Mirrors the dispatch in RunStop so the chip's
    // behaviour stays predictable regardless of which build / running
    // combination the user is in.
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

    // Save the live AutoLairManager.Marked set (plus the per-marker overrides
    // the user has set) as a new named setup. Opens LairEditorDialog on a
    // draft so the user can pick a name + adjust overrides before committing.
    // No-op when no markers are placed.
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

    // True when the user has at least one marker placed — gates the Save-as button.
    public bool HasLairMarkers => _services.AutoLair.Marked.Count > 0;

    // ----- GOTO / Favorites pane ------------------------------------

    // Per-character favourite-room bookmarks (flat backing list — source
    // the folder tree is grouped from).
    public ObservableCollection<FavoriteRowViewModel> Favorites { get; } = new();

    // Folder-grouped GOTO tree (mixed NavFolderNodeViewModel +
    // FavoriteRowViewModel), bound by the rail's TreeView.
    public ObservableCollection<object> FavoriteTree { get; } = new();

    public bool HasFavorites => Favorites.Count > 0;

    // True when the GOTO tree has any node (favourite or empty folder) —
    // drives tree-vs-placeholder visibility.
    public bool HasFavoriteTree => FavoriteTree.Count > 0;

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
            entries.Add(new FavoriteRowViewModel(key, label, _services.Favorites.FolderOf(key)));
        }
        entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        foreach (FavoriteRowViewModel e in entries) Favorites.Add(e);
        NavTreeBuilder.Sync(FavoriteTree, Favorites, r => r.Folder, _services.Favorites.AllFolders);
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasFavoriteTree));
    }

    // Click a favourite → stage it as the queued destination, mirroring a pick
    // from the search box: pan the map to it, draw the preview line, and arm Run.
    // The user hits Run to walk there or the X to cancel. Staging deliberately
    // does NOT stop a running loop/lair — that interruption belongs to Run
    // (RunStop stops the engine and walks the queued destination on commit), so
    // a mis-click no longer wipes an in-flight run.
    [RelayCommand]
    private void GoToFavorite(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        Layout = _services.Bfs.BuildLayout(row.Key);
        SelectedRoomKey = row.Key;
        QueuedDestination = row.Key;
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        _services.Favorites.Remove(row.Key);
    }

    // Open a small modeless rename dialog for the favourite. The dialog
    // returns the new label string on Save or null on Cancel; non-null
    // results route through FavoritesStore.Rename which fires Changed and
    // refreshes the rail.
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

    // Load a saved loop's waypoints into LoopBuild mode so the user can
    // preview it on the map (red polyline + numbered markers) and
    // optionally edit before hitting Run. Distinct from RunLoop (which
    // starts the runner immediately) and from PreviewLoop (which just
    // paints an overlay without entering build mode).
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

    // Right-click → Edit… on a Loops-pane row. Opens the modeless loop
    // editor dialog; the editor mutates the loop in place + persists via
    // LoopManager.Save which fires LoopsChanged so the pane refreshes.
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

    // Right-click → Delete on a Loops-pane row. Confirms via the shared
    // ConfirmService (which honours the user's "skip delete confirms"
    // setting), then removes the loop from disk and refreshes the pane.
    [RelayCommand]
    private async Task DeleteLoopAsync(LoopRowViewModel? row)
    {
        if (row is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync($"loop \"{row.Source.Name}\"");
        if (!ok) return;
        _services.Loops.Delete(row.Source.Name);
    }

    // Right-click → Preview on a Loops-pane row. Lays the loop's expanded
    // room sequence onto the map's LoopPath polyline without starting it.
    // Clicking the same row again clears the preview. While a loop is
    // actually running the live LoopPath wins; previewing an idle loop is
    // the only path this overlay surfaces.
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

    // ----- Rail folder commands --------------------------------------
    // Two folder namespaces back the rail: GOTO folders live in the
    // character profile (FavoritesStore), while Loops + Lairs share one
    // on-disk Loops directory tree (NavFolderManager). The move commands
    // are per-section; folder CRUD is per-namespace.

    // New folder for Loops/Lairs (shared on-disk tree). Nested under
    // parent when invoked on a folder node, else at the root.
    [RelayCommand]
    private async Task NewLoopFolderAsync(NavFolderNodeViewModel? parent)
    {
        string? name = await PromptFolderNameAsync(
            "New folder", "Name the new folder (use / to nest).");
        if (string.IsNullOrEmpty(name)) return;
        string full = parent is null ? name : NavFolders.Combine(parent.Path, name);
        _services.NavFolders.CreateFolder(full);
    }

    // Rename a Loops/Lairs folder (and everything beneath it).
    [RelayCommand]
    private async Task RenameLoopFolderAsync(NavFolderNodeViewModel? node)
    {
        if (node is null) return;
        string? name = await PromptFolderNameAsync(
            "Rename folder", "New name for this folder.", node.Name);
        if (string.IsNullOrEmpty(name)) return;
        string target = name.Contains(NavFolders.Separator)
            ? name
            : NavFolders.Combine(NavFolders.Parent(node.Path), name);
        _services.NavFolders.RenameFolder(node.Path, target);
    }

    // Delete a Loops/Lairs folder — its loops / lairs / sub-folders
    // re-parent one level up.
    [RelayCommand]
    private async Task DeleteLoopFolderAsync(NavFolderNodeViewModel? node)
    {
        if (node is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync(
            $"folder \"{node.Name}\" (its contents move up one level)");
        if (!ok) return;
        _services.NavFolders.DeleteFolder(node.Path, moveContentsToParent: true);
    }

    // New GOTO folder (profile-backed). Nested under parent when invoked
    // on a folder node, else at the root.
    [RelayCommand]
    private async Task NewGotoFolderAsync(NavFolderNodeViewModel? parent)
    {
        string? name = await PromptFolderNameAsync(
            "New folder", "Name the new folder (use / to nest).");
        if (string.IsNullOrEmpty(name)) return;
        string full = parent is null ? name : NavFolders.Combine(parent.Path, name);
        _services.Favorites.AddFolder(full);
    }

    // Rename a GOTO folder (and every favourite / sub-folder beneath it).
    [RelayCommand]
    private async Task RenameGotoFolderAsync(NavFolderNodeViewModel? node)
    {
        if (node is null) return;
        string? name = await PromptFolderNameAsync(
            "Rename folder", "New name for this folder.", node.Name);
        if (string.IsNullOrEmpty(name)) return;
        string target = name.Contains(NavFolders.Separator)
            ? name
            : NavFolders.Combine(NavFolders.Parent(node.Path), name);
        _services.Favorites.RenameFolder(node.Path, target);
    }

    // Delete a GOTO folder — its favourites / sub-folders re-parent one
    // level up.
    [RelayCommand]
    private async Task DeleteGotoFolderAsync(NavFolderNodeViewModel? node)
    {
        if (node is null) return;
        bool ok = await _services.Confirm.ConfirmDeleteAsync(
            $"folder \"{node.Name}\" (its contents move up one level)");
        if (!ok) return;
        _services.Favorites.RemoveFolder(node.Path, moveContentsToParent: true);
    }

    // Move a loop into folder (empty = root). Used by drag-drop +
    // context-menu move.
    public void MoveLoopToFolder(LoopRowViewModel? row, string? folder)
    {
        if (row is null) return;
        _services.Loops.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    // Move an Auto-Lair setup into folder (empty = root).
    public void MoveSetupToFolder(LairSetupRowViewModel? row, string? folder)
    {
        if (row is null) return;
        _services.Lairs.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    // Move a GOTO favourite into folder (empty = root).
    public void MoveFavoriteToFolder(FavoriteRowViewModel? row, string? folder)
    {
        if (row is null) return;
        _services.Favorites.MoveFavorite(row.Key, NavFolders.Normalize(folder));
    }

    // Context-menu "Move to folder…" for a loop — prompts for a
    // destination path.
    [RelayCommand]
    private async Task MoveLoopToFolderPromptAsync(LoopRowViewModel? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move loop", "Destination folder (blank = root).", row.Source.Folder);
        if (folder is null) return;
        MoveLoopToFolder(row, folder);
    }

    // Context-menu "Move to folder…" for an Auto-Lair setup — prompts for
    // a destination path.
    [RelayCommand]
    private async Task MoveSetupToFolderPromptAsync(LairSetupRowViewModel? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move setup", "Destination folder (blank = root).", row.Source.Folder);
        if (folder is null) return;
        MoveSetupToFolder(row, folder);
    }

    // Context-menu "Move to folder…" for a GOTO favourite — prompts for a
    // destination path.
    [RelayCommand]
    private async Task MoveFavoriteToFolderPromptAsync(FavoriteRowViewModel? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move favourite", "Destination folder (blank = root).", row.Folder);
        if (folder is null) return;
        MoveFavoriteToFolder(row, folder);
    }

    private async Task<string?> PromptFolderNameAsync(string title, string prompt, string initial = "")
    {
        NavFolderNameDialogViewModel vm = new(title, prompt, initial);
        return await _services.Dialogs.OpenWindowAsync<NavFolderNameDialogViewModel, string?>(vm);
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

    // ----- Room context menu -----------------------------------------

    // Room currently surfaced in the context menu (set by the map's
    // right-click handler).
    [ObservableProperty] private RoomKey? _contextRoomKey;

    // Name of the context room — empty when none is selected.
    public string ContextRoomName =>
        ContextRoomKey is { } k && Graph?.GetRoom(k) is { } r ? r.Name : "(unknown)";

    partial void OnContextRoomKeyChanged(RoomKey? value)
    {
        OnPropertyChanged(nameof(ContextRoomName));
        OnPropertyChanged(nameof(ContextIsAvoided));
        OnPropertyChanged(nameof(ContextIsStash));
        OnPropertyChanged(nameof(ContextIsFavorite));
        RebuildContextTeleports(value);
    }

    public bool ContextIsAvoided => ContextRoomKey is { } k && _services.Movement.IsAvoided(k);
    public bool ContextIsStash   => ContextRoomKey is { } k && _services.Movement.IsStash(k);
    public bool ContextIsFavorite => ContextRoomKey is { } k && _services.Favorites.IsFavorite(k);

    // ----- "Use Teleport" (right-click a CMD/teleport room) ----------
    // A CMD room's TBInfo Action chain teleports to one room (the common
    // case) or several distinct rooms. "Use Teleport" just shifts the map
    // view to where you'd land — the actual traversal command is
    // irrelevant here. One destination → a flat "Use Teleport" item; many
    // → one flat "Use Teleport → <room>" entry each, rendered directly in
    // the context menu via indexed slots (mirrors the File-menu
    // Recent0..4 pattern — no submenu/flyout).
    private const int MaxTeleportSlots = 5;

    private readonly List<RoomKey> _contextTeleportDests = new();
    private readonly TeleportDestinationItem?[] _teleportSlots =
        new TeleportDestinationItem?[MaxTeleportSlots];

    // True when the context room teleports to exactly one room — show the
    // flat "Use Teleport" item.
    public bool ContextTeleportSingle => _contextTeleportDests.Count == 1;

    // Indexed flat "Use Teleport → room" entries, populated only when the
    // room has multiple distinct destinations.
    public TeleportDestinationItem? Teleport0 => _teleportSlots[0];
    public TeleportDestinationItem? Teleport1 => _teleportSlots[1];
    public TeleportDestinationItem? Teleport2 => _teleportSlots[2];
    public TeleportDestinationItem? Teleport3 => _teleportSlots[3];
    public TeleportDestinationItem? Teleport4 => _teleportSlots[4];

    private void RebuildContextTeleports(RoomKey? value)
    {
        _contextTeleportDests.Clear();
        Array.Clear(_teleportSlots);
        if (value is { } k && Graph?.GetRoom(k) is { Cmd: > 0 } room)
        {
            foreach ((string _, RoomKey dest, int _) in
                     TBInfoTeleportResolver.EnumerateTeleports(_services.TBInfo, room.Cmd))
            {
                if (!_contextTeleportDests.Contains(dest)) _contextTeleportDests.Add(dest);
            }
            // Cast-delivered teleports (`cast <spell>` where the spell carries
            // a teleport ability — the chained-spell rooms) surface their
            // landing rooms too. A fixed room contributes one destination; a
            // random jump contributes every room in its range, which lands the
            // user in the multi-destination per-room menu below.
            foreach ((string _, IReadOnlyList<RoomKey> dests, bool _, int _) in
                     TBInfoCastTeleportResolver.EnumerateCastTeleports(
                         _services.TBInfo, room.Cmd, room.Key.Map, _services.SpellCatalog))
            {
                foreach (RoomKey dest in dests)
                    if (!_contextTeleportDests.Contains(dest)) _contextTeleportDests.Add(dest);
            }
            // Only the multi-destination case needs per-room entries — a
            // single destination is served by the flat UseTeleport command.
            if (_contextTeleportDests.Count > 1)
            {
                for (int i = 0; i < _contextTeleportDests.Count && i < MaxTeleportSlots; i++)
                {
                    RoomKey dest = _contextTeleportDests[i];
                    string name = Graph.GetRoom(dest)?.Name ?? "(unknown)";
                    _teleportSlots[i] = new TeleportDestinationItem(
                        $"Use Teleport → {name}  ({dest})",
                        new RelayCommand(() => OnFloorChangeRequested(dest)));
                }
            }
        }
        OnPropertyChanged(nameof(ContextTeleportSingle));
        OnPropertyChanged(nameof(Teleport0));
        OnPropertyChanged(nameof(Teleport1));
        OnPropertyChanged(nameof(Teleport2));
        OnPropertyChanged(nameof(Teleport3));
        OnPropertyChanged(nameof(Teleport4));
    }

    // Single-destination "Use Teleport": shift the map to the one room the
    // teleport leads to. Multi-destination rooms use the per-room
    // Teleport0..Teleport4 entries instead.
    [RelayCommand]
    private void UseTeleport()
    {
        if (_contextTeleportDests.Count > 0) OnFloorChangeRequested(_contextTeleportDests[0]);
    }

    [RelayCommand]
    private async Task WalkToContextRoom()
    {
        if (ContextRoomKey is not { } k) return;
        // If a loop or Auto-Lair is currently driving movement, stop
        // it before handing control to the walker — the user's explicit
        // walk-to takes precedence over the automation in the
        // background. Auto-Lair owns the walker for its routing, so
        // stopping it cleanly releases the walker for our WalkTo call.
        if (_services.LoopRunner.State != Game.Map.LoopState.Idle)
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
        // User-initiated walk: offer the free-vs-direct route choice when a
        // shorter gated shortcut exists (falls straight through to WalkTo when
        // it doesn't).
        await RouteChoicePrompt.WalkAsync(_services, k);
    }

    [RelayCommand]
    private void SetContextRoomLocated()
    {
        if (ContextRoomKey is { } k) _services.RoomTracker.SetLocated(k);
    }

    // Right-click → "Add to favorites" (or "Remove from favorites" when
    // already bookmarked). Persists via FavoritesStore; the GOTO pane
    // refreshes from the store's Changed event.
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

    // Window listens and forwards to MapControl.RecenterOnPlayer(). The VM
    // can't call the control directly so we route through this event —
    // same pattern the right-click menu uses for other map-only
    // operations.
    public event Action? CenterOnPlayerRequested;

    // Right-click → "Center on Player". Re-centres the map on the live
    // current room and clears the 10 s browse-suppression window so
    // subsequent live moves resume auto-centring. Same as the Home key.
    [RelayCommand]
    private void CenterOnPlayer() => CenterOnPlayerRequested?.Invoke();

    // Right-click → "Center on…". Opens the two-int (map / room) input
    // dialog; on commit, routes through OnFloorChangeRequested so the BFS
    // layout rebuilds from the chosen room and the map centres on it.
    // Cancel / X dismisses without changing the view.
    [RelayCommand]
    private async Task CenterOnSpecificAsync()
    {
        ManualCenterDialogViewModel vm = new(_services.RoomGraph);
        RoomKey? result = await _services.Dialogs
            .OpenWindowAsync<ManualCenterDialogViewModel, RoomKey?>(vm);
        if (result is { } k) OnFloorChangeRequested(k);
    }

    // ----- Mode bar -------------------------------------------------
    [ObservableProperty] private NavigationMode _currentMode = NavigationMode.Idle;

    public bool IsLoopMode => CurrentMode == NavigationMode.LoopBuild;
    public bool IsLairMode => CurrentMode == NavigationMode.AutoLair;

    // True while the user is in NavigationMode.AutoLair AND the Auto-Lair
    // scheduler isn't actively running. Drives the bottom-strip "Building
    // lair setup: N lair(s) marked" surface — counterpart of
    // IsLoopBuilding for loops. While the scheduler is running, the engine
    // phase strip (IsAutoLairing) takes precedence.
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

    // Active loop-builder session when CurrentMode == LoopBuild; null
    // otherwise.
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
                // CanRun + RunStopLabel + CanSaveCurrent all depend on
                // CanSave while in LoopBuild mode — re-notify so the Run
                // AND Save buttons enable the moment the user has two
                // reachable clicks.
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(RunStopLabel));
                OnPropertyChanged(nameof(CanSaveCurrent));
                RebuildCurrentNavRows();
                OnPropertyChanged(nameof(TopBarStatusText));
                break;
            case nameof(LoopBuilderSessionViewModel.Clicks):
            case nameof(LoopBuilderSessionViewModel.HasClicks):
            case nameof(LoopBuilderSessionViewModel.ProposedName):
                RebuildCurrentNavRows();
                OnPropertyChanged(nameof(TopBarStatusText));
                break;
        }
    }

    // Called by the window when the map is left-clicked. Dispatched by the
    // active CurrentMode:
    //   - LoopBuild — forward the click to the loop builder so the room
    //     joins the click sequence.
    //   - AutoLair — toggle the room as a marker on AutoLairManager.
    //     Mirrors how loop-build mode accumulates waypoints; the user
    //     enters Lair mode from the top-right action chip, clicks rooms to
    //     add / remove, then commits via the rail's "Save lairs" button.
    //   - Idle — no-op (the click already moved SelectedRoomKey upstream).
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

    // Called by the window when the map crawler hits an up/down exit.
    // Rebuilds the layout from the new room so the user can continue
    // crawling on the new floor.
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

    // Open the Manage dialog — modeless surface for renaming / deleting
    // saved loops and unmarking Auto-Lair rooms. This is where naming +
    // lifecycle CRUD live; the bottom build strip is a pure status
    // display.
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
            folders: _services.NavFolders,
            draft: LoopBuilder,
            onDraftConsumed: () =>
            {
                if (CurrentMode == NavigationMode.LoopBuild) ToggleLoopMode();
            },
            runner: _services.LoopRunner,
            mpImporter: _services.MpImporter,
            log: _services.Log,
            search: _services.RoomSearch,
            walker: _services.Walker,
            movement: _services.MovementControl);
        await _services.Dialogs
            .OpenWindowAsync<NavigationManagerDialogViewModel, bool>(vm);
    }

    // Toggle the Lair "build" mode (mirrors ToggleLoopMode). Exiting build
    // mode DISCARDS the in-progress marker set — matches LoopBuilder's
    // "exit discards clicks" semantics so the user has a clean idle state.
    // Markers loaded from a saved setup are equally transient; persist
    // them via the rail's "Save lairs" button before toggling out.
    //
    // Exception: when the scheduler is actively running we keep the
    // markers in place — clearing them would yank the rug out from under
    // the live engine. The Lair-mode chip then shows "Stop" instead of
    // "Building" and routes through the LoopModeButtonCommand dispatcher
    // (per LairModeButtonIsStop).
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

    // Backs the walk-to Pause/Resume chip (col 4, replacing the disabled Save
    // button during a walk-to). Routes through the shared controller so it keys
    // off the user-override tier, NOT the coalesced pause state — a mid-walk
    // fight (an engine wait) must not flip this into "Resume".
    [RelayCommand]
    private void PauseOrResume() => _services.MovementControl.TogglePause();

    // Walk-to's Pause/Resume face. "Resume" once the user has manually paused
    // the walk; "Pause" while it's running (including through engine waits, so
    // the user can stack a manual pause on a fight/rest). Engine waits never
    // change this label — only the user's own pause does.
    public bool IsWalkUserPaused => _services.MovementControl.IsUserPaused;
    public string WalkPauseLabel => IsWalkUserPaused ? "Resume" : "Pause";

    // ----- handlers --------------------------------------------------

    private void OnTrackerStateChanged(RoomTransition _)
    {
        RefreshFromTracker();
        RefreshAutoLairApproachPath();
        // Re-trim the walk-to overlay to the room we just confirmed. The
        // walker itself only refires on its own step events, which are
        // suppressed while it's paused (combat, resting) — so without this
        // the drawn route kept showing the leg already walked until the walk
        // resumed. RemainingRoomKeys reads the tracker's current room, so it
        // trims correctly even while the walker is gated.
        RefreshFromWalker();
        RefreshDerivedState();
    }

    private void OnRecoveryTierChanged(RecoveryTierChangedEvent _) => RefreshRecoveryTierBools();

    // True when the engine-recovery gate is in tier 2 — engine chip border
    // goes yellow.
    public bool IsTier2 => _isTier2;
    // True when the engine-recovery gate is in tier 3 — engine chip border
    // goes red.
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

    private void OnWalkerEvent(WalkEvent e)
    {
        // A failed walk leaves the engine Idle; stash the reason so the
        // top-bar status + CURRENT NAV header can explain why we didn't
        // move (e.g. "all routes blocked by a level or toll requirement").
        // Any forward progress or a fresh start clears it.
        switch (e.Kind)
        {
            case WalkEventKind.Failed:
                EngineError = e.Detail;
                break;
            case WalkEventKind.Started:
            case WalkEventKind.Resumed:
            case WalkEventKind.StepCompleted:
            case WalkEventKind.Finished:
            case WalkEventKind.Stopped:
                EngineError = null;
                break;
        }
        RefreshFromWalker();
    }
    private void OnPauseChanged(bool paused) => IsPaused = paused;

    // Every gate assert/clear may change the live "why are we paused" label,
    // even when the overall paused state doesn't flip (Combat → Resting keeps
    // IsPaused true). RefreshActivityStatus is a no-op when nothing actually
    // changed, so refiring on each transition is cheap.
    private void OnGatesChanged()
    {
        RefreshActivityStatus();
        // The walk-to Pause/Resume chip reads the user-override tier, which only
        // moves on a gate transition — refresh its face here so it flips the
        // instant the user pauses/resumes (RefreshDerivedState covers the engine
        // start/stop path).
        OnPropertyChanged(nameof(IsWalkUserPaused));
        OnPropertyChanged(nameof(WalkPauseLabel));
    }

    // Our own "held" ailment stops movement server-side without asserting any
    // client gate, so the chip watches the condition flags directly to surface
    // it. Only ActiveFlags is relevant here — ignore other property changes.
    private void OnConditionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Game.Conditions.ConditionTracker.ActiveFlags))
            RefreshActivityStatus();
    }

    // ----- Top-bar activity status (moving / fighting / waiting-why) --

    // Human-readable "what is the movement engine doing right now" for the
    // top-bar chip: Moving, Fighting, Paused (user), or Waiting with the
    // reason. The reason comes straight from the MovementCoordinator gate that
    // is holding the engine — the client never has to guess why a loop stalled.
    private enum NavActivityKind { None, Moving, Fighting, Waiting, Paused }

    private string _activityStatus = string.Empty;
    private NavActivityKind _activityKind = NavActivityKind.None;

    // Chip text ("Fighting", "Waiting — resting (low HP)", …). Empty when idle.
    public string ActivityStatus => _activityStatus;
    // Chip only shows while an engine is executing.
    public bool HasActivityStatus => _activityKind != NavActivityKind.None;
    // Colour-class selectors for the chip (green / red / amber / muted).
    public bool ActivityIsMoving   => _activityKind == NavActivityKind.Moving;
    public bool ActivityIsFighting => _activityKind == NavActivityKind.Fighting;
    public bool ActivityIsWaiting  => _activityKind == NavActivityKind.Waiting;
    public bool ActivityIsPaused   => _activityKind == NavActivityKind.Paused;

    private void RefreshActivityStatus()
    {
        (string text, NavActivityKind kind) = ComputeActivity();
        if (text == _activityStatus && kind == _activityKind) return;
        _activityStatus = text;
        _activityKind   = kind;
        OnPropertyChanged(nameof(ActivityStatus));
        OnPropertyChanged(nameof(HasActivityStatus));
        OnPropertyChanged(nameof(ActivityIsMoving));
        OnPropertyChanged(nameof(ActivityIsFighting));
        OnPropertyChanged(nameof(ActivityIsWaiting));
        OnPropertyChanged(nameof(ActivityIsPaused));
    }

    // Map the live engine + gate state to the chip. Idle → hidden. Running with
    // nothing gating → Moving. Otherwise the highest-priority reason wins: an
    // explicit user pause is the headline (only the user can clear it), then
    // combat (Fighting), then our own "held" ailment (blocks movement
    // server-side even with no client gate asserted), then the recovery / party
    // holds. Gate constants are matched by value so a future gate never silently
    // maps to "Moving" — an unrecognized one still surfaces as "Waiting — <gate>".
    private (string Text, NavActivityKind Kind) ComputeActivity()
    {
        if (!IsAnyExecuting) return (string.Empty, NavActivityKind.None);

        Game.Map.MovementCoordinator mc = _services.MovementCoordinator;
        IReadOnlyCollection<string> gates = mc.AssertedGates;

        // User pause and combat outrank everything, including a held ailment:
        // an explicit pause is the user's own doing, and mid-fight "Fighting" is
        // the more useful readout than "Held". A death-induced halt rides the
        // same UserGate but flavours the chip so the user knows why we stopped.
        if (gates.Contains(Game.Map.MovementCoordinator.UserGate))
            return _services.PlayerDeathHalt.HaltedForDeath
                ? ("Paused — recovering", NavActivityKind.Paused)
                : ("Paused", NavActivityKind.Paused);
        if (gates.Contains(Game.Map.MovementCoordinator.CombatGate))
            return ("Fighting", NavActivityKind.Fighting);
        // Engine-owned hold right after a walk left a room with an engaged
        // hostile — auto-clears the instant the room settles, so this rarely
        // lingers, but name it so it never falls through to the raw-gate label.
        if (gates.Contains(Game.Map.MovementCoordinator.AbandonedCombatGate))
            return ("Waiting — leaving a fight", NavActivityKind.Waiting);

        // Our own held/entangled state stops movement at the server; no gate is
        // asserted for it, so a stuck loop would otherwise read "Moving".
        if (_services.Conditions.IsMovementPrevented)
            return ("Waiting — held", NavActivityKind.Waiting);

        if (!mc.IsPaused) return ("Moving", NavActivityKind.Moving);

        if (gates.Contains(Game.Map.MovementCoordinator.HealthRecoveryGate))
            return ("Waiting — resting (low HP)", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.ManaRecoveryGate))
            return ("Waiting — meditating (low mana)", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.PartyWaitGate))
            return ("Waiting — party asked to wait", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.PartyVitalsGate))
            return ("Waiting — party member hurt", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.PartyInviteGate))
            return ("Waiting — for invitee to join", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.FollowerGate))
            return ("Waiting — following leader", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.CorpseRecoveryGate))
            return ("Waiting — recovering corpse", NavActivityKind.Waiting);
        if (gates.Contains(Game.Map.MovementCoordinator.AcquisitionGate))
            return ("Waiting — looting", NavActivityKind.Waiting);

        string first = gates.FirstOrDefault() ?? "?";
        return ($"Waiting — {first}", NavActivityKind.Waiting);
    }

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
        RoomKey? previousRoom = CurrentRoomKey;
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

            // Don't yank the layout back to the player mid-browse. When the
            // player's PREVIOUS room wasn't on the displayed layout, the user
            // has floor-crawled / searched off to look at a different part of
            // the map, so a movement step must leave that view alone — the
            // reported bug was the map re-rooting on the player every step
            // while browsing elsewhere. Only re-root when the layout was
            // actually following the player (previous room on it, or no fix
            // yet) and this step walked off it — the usual stairs / reconnect
            // / big-walk case. "Center on Player" (Home / context menu) is the
            // way back; it rebuilds around the live room itself.
            bool browsingOffPlayer =
                Layout is not null
                && previousRoom is { } prev
                && !Layout.Positions.ContainsKey(prev);

            if (Layout is null
                || exitedBlacklistedOrigin
                || (!browsingOffPlayer && !Layout.Positions.ContainsKey(here.Key)))
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

    // Walk every room with a non-zero Cmd and ask TBInfo whether the CMD's
    // Action chain contains a teleport directive. Both literal
    // (teleport <room> <map>) and cast-delivered (cast <spell> where the
    // spell carries a teleport ability) directives qualify — a random
    // cast-teleport drops the walker into the same room-uncertainty state
    // a literal one does, so it earns the same glyph. The resulting set
    // drives the map's diagonal hash-line overlay so the user can spot
    // non-exit movement spots at a glance.
    private void RefreshTeleportRooms()
    {
        if (Graph is null) { TeleportRooms = null; return; }
        HashSet<RoomKey> set = new();
        foreach (Room room in Graph.Rooms)
        {
            if (room.Cmd <= 0) continue;
            using IEnumerator<(string, RoomKey, int)> literal =
                TBInfoTeleportResolver.EnumerateTeleports(_services.TBInfo, room.Cmd).GetEnumerator();
            if (literal.MoveNext()) { set.Add(room.Key); continue; }

            using IEnumerator<(string, IReadOnlyList<RoomKey>, bool, int)> cast =
                TBInfoCastTeleportResolver.EnumerateCastTeleports(
                    _services.TBInfo, room.Cmd, room.Key.Map, _services.SpellCatalog).GetEnumerator();
            if (cast.MoveNext()) set.Add(room.Key);
        }
        TeleportRooms = set;
    }

    // Blacklist Changed → rebuild the cached layout (BFS already flushed
    // its cache via AppServices wiring) and the room search results in
    // case a search is currently typed.
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
        //
        // The live current room (1) stays the origin even when blacklisted
        // — BfsMapper exempts the origin so the "you are here" marker has
        // an anchor, and the movement path re-roots off it once the player
        // leaves. The fallback anchors (2, 3) are NOT the player's live
        // position, so they must not anchor — and thereby exempt — a
        // blacklisted room: blacklisting the parked / last-known room
        // should hide it at once, not keep it visible until a live move.
        RoomKey? key = _services.RoomTracker.State.CurrentRoom?.Key;
        if (key is null && _services.Profile.Current?.LastKnownRoom is { } last
            && _services.RoomGraph.GetRoom(new RoomKey(last.Map, last.Room)) is not null)
        {
            key = _services.Bfs.NearestVisibleRoom(new RoomKey(last.Map, last.Room));
        }
        if (key is null && _services.RoomGraph.RoomCount > 0)
        {
            foreach (Room first in _services.RoomGraph.Rooms)
            {
                if (_services.RoomBlacklist.IsBlacklisted(first.Key)) continue;
                key = first.Key;
                break;
            }
            // Degenerate (every room blacklisted): fall back to the first so
            // the map still renders something via the origin exemption.
            if (key is null)
                foreach (Room first in _services.RoomGraph.Rooms) { key = first.Key; break; }
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

    // Which engine is currently driving — feeds top-bar status badge,
    // CURRENT NAV section rendering, and Run/Stop button behaviour.
    [ObservableProperty] private NavigationEngineKind _engineActionKind = NavigationEngineKind.Idle;

    // Reason the last navigation attempt failed, surfaced in the top-bar
    // status text + CURRENT NAV header while the engine is Idle. Null when
    // there's nothing to report. Set by OnWalkerEvent on a Failed event;
    // cleared on any Started / progress / Stopped event.
    [ObservableProperty] private string? _engineError;

    // True when any movement engine is actively driving the player.
    public bool IsAnyExecuting =>
        EngineActionKind != NavigationEngineKind.Idle;

    // Run button enabled when idle and something is queued, OR when active
    // (then it acts as Stop).
    public bool CanRun =>
        IsAnyExecuting
        || QueuedDestination is not null
        || (CurrentMode == NavigationMode.LoopBuild && LoopBuilder?.CanSave == true)
        || (CurrentMode == NavigationMode.AutoLair && _services.AutoLair.Marked.Count > 0);

    // Primary action-chip face. Loops transform into Pause / Run for
    // pause-resume cycling (the user can edit the loop while paused);
    // walker + auto-lair stay as Run / Stop (one-shot engines).
    public string RunStopLabel
    {
        get
        {
            // A queued destination overrides the engine faces: clicking Run walks
            // there (see RunStop). Present a stable "Run" even while a loop or
            // lair cycles its state machine, so the chip stops flickering
            // Pause/Run under the armed destination.
            if (QueuedDestination is not null) return "Run";

            Game.Map.LoopRunner runner = _services.LoopRunner;
            if (runner.State is Game.Map.LoopState.Running
                              or Game.Map.LoopState.Approaching
                              or Game.Map.LoopState.Recovering) return "Pause";
            if (runner.State == Game.Map.LoopState.Paused) return "Run";
            // Auto-Lair gets Pause / Run too — the chip stays distinct
            // from the Lair-mode "Stop" so the user has both Pause (this
            // chip) and Stop (the mode chip) without duplication.
            if (_services.AutoLair.IsActive)
                return _services.AutoLair.IsPaused ? "Run" : "Pause";
            return IsAnyExecuting ? "Stop" : "Run";
        }
    }


    // The top-bar action line next to the "Navigation" title — a plain-English
    // description of what the engine is doing right now. Building modes surface
    // the in-progress click/marker hint; a walk-to reads "Walking to (M/R) -
    // Name"; a loop that's still approaching its entry reads "Walking to (M/R) -
    // Name then looping <loop>"; a running loop reads "Looping <loop> - step X of
    // Y on lap Z"; auto-lair keeps its phase readout; Idle falls back to the
    // located room.
    public string TopBarStatusText
    {
        get
        {
            // Building modes override engine state — the user is laying down
            // waypoints/markers before Run, so echo that progress here (the
            // CURRENT NAV pane no longer carries a description line).
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
            switch (EngineActionKind)
            {
                case NavigationEngineKind.Walking:
                {
                    Game.Map.AutoWalkManager w = _services.Walker;
                    string dest = w.Destination is { } k
                        ? FormatRoomRef(k)
                        : "?";
                    // Spell out progress like the loop readout does: step is the
                    // next-to-send index 1-based, clamped to the path length;
                    // remaining counts that step and everything past it.
                    int total = w.StepCount;
                    if (total <= 0) return $"Walking to {dest}";
                    int step = Math.Min(total, w.CurrentStepIndex + 1);
                    int remaining = Math.Max(0, total - w.CurrentStepIndex);
                    return $"Walking to {dest} on step {step} of {total}, remaining {remaining}";
                }
                case NavigationEngineKind.Looping:
                {
                    Game.Map.LoopRunner lr = _services.LoopRunner;
                    string name = lr.CurrentLoop?.Name ?? "?";
                    // Still walking to the loop's entry — spell out the whole
                    // intent ("walk here, then loop that") so the user knows the
                    // loop hasn't begun cycling yet.
                    if (lr.State == Game.Map.LoopState.Approaching)
                    {
                        string target = lr.ApproachTarget is { } t
                            ? FormatRoomRef(t)
                            : "first waypoint";
                        return $"Walking to {target} then looping {name}";
                    }
                    // Running circle — spell out where in the cycle we are. Step
                    // is CurrentIndex (next-to-send) as 1-based, clamped to the
                    // step count; lap is completed-laps + 1 (the lap in flight).
                    int total = lr.StepCount;
                    if (total <= 0) return $"Looping {name}";
                    int step = Math.Min(total, lr.CurrentIndex + 1);
                    return $"Looping {name} - step {step} of {total} on lap {lr.CompletedLaps + 1}";
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
                    if (EngineError is { Length: > 0 } err) return $"⚠ {err}";
                    Room? here = _services.RoomTracker.State.CurrentRoom;
                    return here is null ? "—" : FormatRoomRef(here.Key);
                }
            }
        }
    }

    // Shared row population for the AutoLair branch of
    // RebuildCurrentNavRows — runs both in Build mode (pre-scheduler) and
    // during an active run. The only behavioural difference is the per-row
    // status / sub-label, both keyed off whether the scheduler has a
    // Game.Map.AutoLairManager.CurrentTarget. Order: target first, then
    // the rest sorted by Map / Room.
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

    // Compose the CURRENT NAV sub-label for a marked lair row. Behaviour
    // by phase:
    //   - Active target — show the scheduler phase + the countdown to
    //     entry ("Waiting · 0:42 to entry").
    //   - Visited this session — show the per-room respawn countdown
    //     ("respawns in 12:34") or "ready" once LairTimerStore.NextReadyAt
    //     falls past now.
    //   - Never visited — show the game-data default respawn ("game
    //     default 30:00") so the user can see whether the room they marked
    //     actually carries a lair tag; rooms without a tag surface "no
    //     game-data timer" instead.
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

    // Format a lair-timer duration for the CURRENT NAV sub-labels. Plain
    // total seconds (e.g. "270s") — the rooms we mark in this surface only
    // ever respawn in the 30-300 s range, so a single number stays compact
    // and scannable without the user mentally converting 4:30 back into
    // seconds. Negative inputs clamp to 0.
    private static string FormatMmSs(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int totalSec = (int)Math.Round(t.TotalSeconds);
        return $"{totalSec}s";
    }

    // Canonical room reference format used across the Navigation surfaces
    // — "(map/room) - Name". Falls back to "(M/R) - ???" when the graph
    // doesn't know the room (typical of unimported MDB sets or null-name
    // ganghouse rooms).
    private string FormatRoomRef(RoomKey key)
    {
        string name = Graph?.GetRoom(key)?.DisplayName ?? "???";
        return $"({key.Map}/{key.Room}) - {name}";
    }

    // Engine-state tag the badge displays: WALKING / LOOPING / AUTO-LAIR /
    // IDLE.
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

    // Loop-mode button face: idle → "Loop mode"; mode-on → "Building";
    // running → "Stop".
    public string LoopModeButtonLabel => EngineActionKind == NavigationEngineKind.Looping
        ? "Stop"
        : (CurrentMode == NavigationMode.LoopBuild ? "Building" : "Loop mode");

    public bool LoopModeButtonIsStop => EngineActionKind == NavigationEngineKind.Looping;

    // Dispatcher for the Loop-mode button: when looping (any state) the
    // button is a full Stop; otherwise it's the build-mode toggle. Keeping
    // one physical button keeps the action chip row compact and matches
    // the user's expectation that the Run chip transforms to Pause while
    // the Loop-mode chip carries the Stop.
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

    // Lair-mode button face: idle → "Lair mode"; mode-on → "Building";
    // running → "Stop".
    public string LairModeButtonLabel => EngineActionKind == NavigationEngineKind.AutoLair
        ? "Stop"
        : (CurrentMode == NavigationMode.AutoLair ? "Building" : "Lair mode");

    public bool LairModeButtonIsStop => EngineActionKind == NavigationEngineKind.AutoLair;

    // Dispatcher for the Lair-mode chip — symmetric with LoopModeButton.
    // When the scheduler is active the button carries Stop semantics
    // (routes through StopAll); otherwise it's the build-mode toggle.
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
        RefreshActivityStatus();
        OnPropertyChanged(nameof(EngineActionIsIdle));
        OnPropertyChanged(nameof(EngineActionIsWalking));
        OnPropertyChanged(nameof(IsWalkUserPaused));
        OnPropertyChanged(nameof(WalkPauseLabel));
        OnPropertyChanged(nameof(EngineActionIsLooping));
        OnPropertyChanged(nameof(EngineActionIsLair));
        OnPropertyChanged(nameof(LoopModeButtonLabel));
        OnPropertyChanged(nameof(LoopModeButtonIsStop));
        OnPropertyChanged(nameof(LairModeButtonLabel));
        OnPropertyChanged(nameof(LairModeButtonIsStop));
        OnPropertyChanged(nameof(IsLairBuilding));
        OnPropertyChanged(nameof(LairBuildStatusText));
        OnPropertyChanged(nameof(CanSaveCurrent));
        OnPropertyChanged(nameof(CurrentNavProgress));
        OnPropertyChanged(nameof(CurrentNavHasProgress));
        RebuildCurrentNavRows();
        OnPropertyChanged(nameof(CurrentNavSelectedRow));
    }

    // Unified Run / Stop button. Behaviour by state:
    //   - Active (any engine) → stops it.
    //   - Loop builder open with savable session → save + run.
    //   - Auto-Lair mode with marked rooms → start the scheduler.
    //   - Otherwise, walk to the queued destination.
    [RelayCommand]
    private async Task RunStop()
    {
        Game.Map.LoopRunner runner = _services.LoopRunner;

        // A queued destination is an explicit "go here now" and outranks the
        // loop/lair pause-resume cycle: stop whatever engine is running and walk
        // there. SelectSearchResult's arm-comment promises "clicking Run walks
        // there", which the running-engine branches below would otherwise
        // preempt — so the check has to come first. Mirrors GoToFavorite, plus a
        // ClearGate so a lingering user-pause (loop paused when the destination
        // was queued) doesn't block the walker from sending.
        if (QueuedDestination is { } queued)
        {
            if (runner.State != Game.Map.LoopState.Idle)
                runner.Stop("user walk-to queued destination");
            if (_services.AutoLair.IsActive) _services.AutoLair.Stop();
            _services.MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.UserGate);
            // Committing the staged destination consumes it — clear before the
            // (possibly awaited) route picker so Run disarms immediately and a
            // second press can't open a second picker. The search box, the
            // favourites list, and the map right-click all funnel through the
            // same shared engine here; only how the walk is confirmed differs.
            QueuedDestination = null;
            await RouteChoicePrompt.WalkAsync(_services, queued);
            return;
        }

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

        // Loop running / approaching / recovering → pause (assert user gate) and
        // auto-open the builder seeded from the running loop so the
        // user can edit clicks before resuming.
        if (runner.State is Game.Map.LoopState.Running
                         or Game.Map.LoopState.Approaching
                         or Game.Map.LoopState.Recovering)
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
    }

    // Set true when OpenBuilderForRunningLoop opened build mode in
    // response to user-pause so a subsequent Run / Stop can decide whether
    // to close it again.
    private bool _loopBuilderOpenedByPause;

    // True when the builder's click list no longer matches the loop it was
    // seeded from — used by the Pause → Edit → Run flow to decide whether
    // to resume the in-flight loop or stop and restart with the new
    // clicks. Compares 1:1 in order; renames + waypoint reorders all count
    // as edits.
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

    // Pause flow: stop the runner via the user gate, then re-open the
    // builder pre-seeded with the running loop's name + notes + click list
    // so the user can edit before hitting Run again.
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

    // Full-stop action. Always returns the user to the idle map view —
    // engines stopped, builder closed, user gate cleared so the next Run
    // isn't accidentally held paused.
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
        // Exit AutoLair mode and wipe its markers. Stop is the "clear the board"
        // action, so leaving lair marks on the map — over a freshly drawn loop,
        // even — reads as a bug. The Lair chip's own toggle already clears
        // build-mode marks (ToggleLairMode); the master Stop must match it so the
        // reported "hitting stop didn't wipe the markers" path lands the same way.
        // Clear is a no-op when nothing's marked. Setting CurrentMode → Idle also
        // makes one Stop click return from build mode instead of two.
        _services.AutoLair.Clear();
        if (CurrentMode == NavigationMode.AutoLair)
            CurrentMode = NavigationMode.Idle;
        _loopBuilderOpenedByPause = false;
    }

    // ----- CURRENT NAV row list -------------------------------------

    // Rows shown under CURRENT NAV — steps when walking/looping, marked
    // lairs when auto-lairing.
    public ObservableCollection<CurrentNavRowViewModel> CurrentNavRows { get; } = new();

    // Row the CURRENT NAV ListBox should keep in view — the active step
    // while walking, the next-ready lair while auto-lairing. The window
    // code-behind subscribes to property-change and calls
    // ListBox.ScrollIntoView so a long path scrolls along with progress
    // instead of forcing the entire rail to grow.
    public CurrentNavRowViewModel? CurrentNavSelectedRow
    {
        get
        {
            foreach (CurrentNavRowViewModel r in CurrentNavRows)
                if (r.IsCurrent || r.IsReady) return r;
            return CurrentNavRows.Count > 0 ? CurrentNavRows[0] : null;
        }
    }

    // Progress as a 0..1 fraction for the small inline bar; null when no
    // progress meter applies (e.g. Auto-Lair).
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

                // Approach phase: show the walker's approach steps FIRST,
                // then the loop's own circle steps appended below (all
                // Upcoming — the loop hasn't begun). The runner expands its
                // circle up front, so ExpandedSteps is already the rotated
                // cycle we'll run on arrival. Numbering continues across both
                // so the user reads one itinerary: walk to the entry, then
                // loop. Once the approach finishes the view drops to the
                // loop-only branch below.
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
                    IReadOnlyList<LoopStep> circle = runner.ExpandedSteps;
                    for (int i = 0; i < circle.Count; i++)
                    {
                        CurrentNavRows.Add(new CurrentNavRowViewModel(
                            index: steps.Count + i + 1,
                            label: circle[i].Display,
                            status: CurrentNavRowStatus.Upcoming));
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

    // Building Loop row click — remove the click at the given 1-based
    // index (Clicks renderer's Index field). Called from the builder
    // ListBox's row PointerPressed handler.
    public void RemoveBuilderClickAt(int oneBasedIndex)
    {
        if (LoopBuilder is null) return;
        LoopBuilder.RemoveClickAt(oneBasedIndex - 1);
    }

    // Building Loop drag-reorder — move the row at fromOneBased to
    // toOneBased.
    public void MoveBuilderClick(int fromOneBased, int toOneBased)
    {
        if (LoopBuilder is null) return;
        LoopBuilder.MoveClick(fromOneBased - 1, toOneBased - 1);
    }

    // Up-arrow click on a builder row — moves it one place earlier in the
    // click order.
    [RelayCommand]
    private void MoveBuilderClickUp(LoopBuilderRow? row)
    {
        if (row is null) return;
        MoveBuilderClick(row.Index, row.Index - 1);
    }

    // Down-arrow click on a builder row — moves it one place later in the
    // click order.
    [RelayCommand]
    private void MoveBuilderClickDown(LoopBuilderRow? row)
    {
        if (row is null) return;
        MoveBuilderClick(row.Index, row.Index + 1);
    }
}

// Which engine is currently moving the player — gates Run/Stop, status
// badge, CURRENT NAV rendering.
public enum NavigationEngineKind
{
    Idle     = 0,
    Walking  = 1,
    Looping  = 2,
    AutoLair = 3,
}

// One of the four explicit modes the Navigation window can be in.
public enum NavigationMode
{
    Idle = 0,
    LoopBuild = 1,
    AutoLair = 2,
}
