using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        RefreshFavorites();
        RefreshCrawlerChords();
        RefreshTeleportRooms();
    }

    public void Dispose()
    {
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
        // Distances are computed against the avoid filter — flush so
        // the next keystroke recomputes against the new avoided set.
        InvalidateDistanceCache();
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
    [ObservableProperty] private IReadOnlyDictionary<RoomKey, int>? _loopSequenceNumbers;
    [ObservableProperty] private IReadOnlySet<RoomKey>? _autoLairRooms;
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
    /// Substring search expands across three input dialects:
    /// <list type="number">
    /// <item>Coordinate input — <c>"1/123"</c>, <c>"1,123"</c>, or
    /// <c>"1 123"</c> resolves to a specific (map, room); a bare
    /// number lists every room with that room number across all maps.</item>
    /// <item>Room name — substring against <see cref="Room.Name"/> /
    /// <see cref="Room.DisplayName"/>.</item>
    /// <item>Monster name — substring against any monster in
    /// <c>Monsters.json</c> whose <c>RegenTime</c> > 0. For each match,
    /// emit one row per lair-room that hosts the monster, with the
    /// monster name (+ regen window) as the row's primary line.</item>
    /// </list>
    /// </summary>
    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        if (Graph is null) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        string needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 1) { OnPropertyChanged(nameof(HasSearchResults)); return; }

        RoomKey? sourceKey = CurrentRoomKey;
        List<RoomSearchResult> matches = new();

        // ----- Coordinate input -----
        // "1/123" / "1,123" / "1 123" → exact (map, room) lookup.
        // Bare "123" → every room with Room == 123 across all maps.
        (int? mapPart, int? roomPart) = TryParseCoordinate(needle);
        if (mapPart is int m && roomPart is int r)
        {
            if (Graph.GetRoom(new RoomKey(m, r)) is { } exact
                && !_services.RoomBlacklist.IsBlacklisted(exact.Key))
            {
                matches.Add(BuildRoomMatch(exact, sourceKey));
            }
        }
        else if (mapPart is null && roomPart is int onlyRoom)
        {
            foreach (Room room in Graph.Rooms)
            {
                if (room.Key.Room != onlyRoom) continue;
                if (_services.RoomBlacklist.IsBlacklisted(room.Key)) continue;
                matches.Add(BuildRoomMatch(room, sourceKey));
                if (matches.Count >= 200) break;
            }
        }

        // ----- Room-name substring (skip if needle < 2 chars to avoid
        //       O(rooms) noise on a single character). -----
        if (needle.Length >= 2)
        {
            foreach (Room room in Graph.Rooms)
            {
                if (_services.RoomBlacklist.IsBlacklisted(room.Key)) continue;
                if (!room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                 && !room.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Don't double-list if the coordinate branch already
                // surfaced this exact room.
                if (matches.Any(x => x.MonsterTag is null && x.Key.Equals(room.Key))) continue;
                matches.Add(BuildRoomMatch(room, sourceKey));
                if (matches.Count >= 200) break;
            }
        }

        // ----- Monster-name substring (regen > 0 only). -----
        if (needle.Length >= 2)
        {
            foreach ((int monsterId, string name, int regenHours)
                     in EnumerateRegenMonsters())
            {
                if (matches.Count >= 200) break;
                if (!name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                string monsterTag = $"{name} · regen {regenHours}h";

                // Unique bosses (GameLimit=1, etc.) often carry a
                // regen timer without any lair-tag reference — they
                // spawn via game-side script not captured in our
                // data. Still surface them so the user sees that the
                // monster exists; the row is informational (no key,
                // click is a no-op). When lair rooms ARE known, emit
                // one walkable row per (monster, lair) pair.
                if (!RoomsByMonsterId().TryGetValue(monsterId, out List<RoomKey>? lairs)
                    || lairs.Count == 0)
                {
                    matches.Add(new RoomSearchResult(
                        Key:               new RoomKey(0, 0),
                        Name:              string.Empty,
                        StepsFromCurrent:  null,
                        MonsterTag:        monsterTag));
                    continue;
                }

                foreach (RoomKey lk in lairs)
                {
                    if (_services.RoomBlacklist.IsBlacklisted(lk)) continue;
                    if (Graph.GetRoom(lk) is not { } lroom) continue;
                    int? steps = sourceKey is { } src ? DistanceFromCached(src, lroom.Key) : null;
                    matches.Add(new RoomSearchResult(lroom.Key, lroom.DisplayName, steps, monsterTag));
                    if (matches.Count >= 200) break;
                }
            }
        }

        foreach (RoomSearchResult mm in matches
                     // Monster matches sit alongside room matches; sort
                     // both by step distance (closer-first) then by the
                     // primary line for a stable read.
                     .OrderBy(mm => mm.StepsFromCurrent ?? int.MaxValue)
                     .ThenBy(mm => mm.PrimaryLine, StringComparer.OrdinalIgnoreCase)
                     .Take(50))
        {
            SearchResults.Add(mm);
        }
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private RoomSearchResult BuildRoomMatch(Room room, RoomKey? sourceKey)
    {
        int? steps = sourceKey is { } src ? DistanceFromCached(src, room.Key) : null;
        return new RoomSearchResult(room.Key, room.DisplayName, steps);
    }

    // Single-source distances from the current room, cached. Replaces
    // the per-match DistanceBetween calls (each = one full BFS) with
    // one BFS per current-room change + O(1) lookups per match. Big
    // win when the search box returns 50+ matches per keystroke.
    private RoomKey? _distanceCacheSource;
    private IReadOnlyDictionary<RoomKey, int>? _distanceCache;

    private int? DistanceFromCached(RoomKey source, RoomKey destination)
    {
        if (_distanceCache is null || _distanceCacheSource is not { } cs || !cs.Equals(source))
        {
            _distanceCache = _services.Bfs.ComputeDistancesFrom(source, _services.Movement);
            _distanceCacheSource = source;
        }
        return _distanceCache.TryGetValue(destination, out int d) ? d : (int?)null;
    }

    private void InvalidateDistanceCache()
    {
        _distanceCache = null;
        _distanceCacheSource = null;
    }

    /// <summary>
    /// Parse a coordinate token. Returns <c>(map, room)</c> when both
    /// numbers were supplied, <c>(null, room)</c> for a bare single
    /// number, or <c>(null, null)</c> for non-numeric input.
    /// </summary>
    private static (int? Map, int? Room) TryParseCoordinate(string needle)
    {
        // Strip surrounding parens / dash so "(1/123) - X" or "1/123" both
        // parse cleanly when the user types the canonical room format.
        string s = needle.Trim().TrimStart('(').TrimEnd(')').Trim();
        int dashIdx = s.IndexOf('-');
        if (dashIdx > 0) s = s[..dashIdx].Trim();

        string[] parts = s.Split(new[] { ' ', ',', '/' },
            2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 2
            && int.TryParse(parts[0], out int m) && m > 0
            && int.TryParse(parts[1], out int r) && r > 0)
            return (m, r);

        if (parts.Length == 1
            && int.TryParse(parts[0], out int n) && n > 0)
            return (null, n);

        return (null, null);
    }

    // ----- Monster-regen index --------------------------------------

    private List<(int Id, string Name, int RegenHours)>? _regenMonsterCache;
    private Dictionary<int, List<RoomKey>>? _roomsByMonsterIdCache;

    private IEnumerable<(int Id, string Name, int RegenHours)> EnumerateRegenMonsters()
    {
        if (_regenMonsterCache is not null) return _regenMonsterCache;

        List<(int, string, int)> list = new();
        System.Text.Json.JsonDocument? doc = _services.GameData.GetRawTable("Monsters");
        if (doc is null) { _regenMonsterCache = list; return list; }

        foreach (System.Text.Json.JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("RegenTime", out System.Text.Json.JsonElement regenEl)) continue;
            if (regenEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (!regenEl.TryGetInt32(out int regen) || regen <= 0) continue;
            if (!row.TryGetProperty("Number", out System.Text.Json.JsonElement numEl)
                || numEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || !numEl.TryGetInt32(out int id)) continue;
            if (!row.TryGetProperty("Name", out System.Text.Json.JsonElement nameEl)
                || nameEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            list.Add((id, name, regen));
        }
        _regenMonsterCache = list;
        return list;
    }

    private Dictionary<int, List<RoomKey>> RoomsByMonsterId()
    {
        if (_roomsByMonsterIdCache is not null) return _roomsByMonsterIdCache;
        Dictionary<int, List<RoomKey>> map = new();
        if (Graph is null) { _roomsByMonsterIdCache = map; return map; }

        // Source 1: room.RawLairTag — monster ids listed in each
        // room's lair string.
        foreach (Room room in Graph.Rooms)
        {
            if (string.IsNullOrEmpty(room.RawLairTag)) continue;
            RoomTooltipBuilder.ParseLairTag(room.RawLairTag, out _, out IReadOnlyList<int> ids);
            foreach (int id in ids) AddMonsterRoom(map, id, room.Key);
        }

        // Source 2: Monsters.json "Summoned By" — fixed boss / script
        // spawns whose room placement lives on the monster record
        // rather than the room's lair tag. Field examples:
        //   "Room 10/271, Group: 10/271"
        //   "Room 1/101, Room 1/224, Room 1/297"
        //   "[16-8-8][5]Group(lair): 10/159"
        //   "Group: 2/2551,Group: 2/2564,Group: 2/2569"
        // Bracketed Lairs.json GroupIndex tokens use '-' between
        // numbers; only the room references use '/'. Matching every
        // "<digits>/<digits>" sweeps in every spawn site regardless
        // of the surrounding label.
        System.Text.Json.JsonDocument? doc = _services.GameData.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (System.Text.Json.JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out System.Text.Json.JsonElement numEl)
                    || numEl.ValueKind != System.Text.Json.JsonValueKind.Number
                    || !numEl.TryGetInt32(out int id)) continue;
                if (!row.TryGetProperty("Summoned By", out System.Text.Json.JsonElement summonEl)
                    || summonEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                string? text = summonEl.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                foreach (System.Text.RegularExpressions.Match m
                         in s_summonedRoomRegex.Matches(text))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int mn) || mn <= 0) continue;
                    if (!int.TryParse(m.Groups[2].Value, out int rn) || rn <= 0) continue;
                    AddMonsterRoom(map, id, new RoomKey(mn, rn));
                }
            }
        }

        _roomsByMonsterIdCache = map;
        return map;
    }

    private static void AddMonsterRoom(Dictionary<int, List<RoomKey>> map, int monsterId, RoomKey key)
    {
        if (!map.TryGetValue(monsterId, out List<RoomKey>? rooms))
            map[monsterId] = rooms = new List<RoomKey>();
        if (!rooms.Contains(key)) rooms.Add(key);
    }

    private static readonly System.Text.RegularExpressions.Regex s_summonedRoomRegex
        = new(@"(\d+)/(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void InvalidateMonsterSearchCaches()
    {
        _regenMonsterCache = null;
        _roomsByMonsterIdCache = null;
    }

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
            _services.AutoLair,
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
        InvalidateMonsterSearchCaches();
        InvalidateDistanceCache();
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
                    int n = _services.AutoLair.Marked.Count;
                    return $"cycling {n} marked lair{(n == 1 ? "" : "s")}";
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
        OnPropertyChanged(nameof(EngineActionIsIdle));
        OnPropertyChanged(nameof(EngineActionIsWalking));
        OnPropertyChanged(nameof(EngineActionIsLooping));
        OnPropertyChanged(nameof(EngineActionIsLair));
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
        if (_services.AutoLair.IsActive) { _services.AutoLair.Stop(); return; }
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
