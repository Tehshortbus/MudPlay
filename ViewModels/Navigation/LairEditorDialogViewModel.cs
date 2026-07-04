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

// Modeless editor for a saved LairSetup — rename, edit notes, remove
// markers, adjust per-marker respawn overrides. Adding a marker requires
// picking a room on the map; that's the rail's job (right-click → "Add to
// Auto-Lair"), not the editor's.
//
// Save persists via LairManager.Save, which fires LairManager.SetupsChanged
// so the rail refreshes. Cancel / X discards every edit; the dialog works
// on its own row view-models until Save runs.
public sealed partial class LairEditorDialogViewModel : ObservableObject, IDialogViewModel<LairSetup?>
{
    public event Action<LairSetup?>? CloseRequested;

    private readonly LairSetup _original;
    private readonly LairManager _setups;
    private readonly RoomGraphManager _graph;
    private readonly LairTimerStore _timers;
    private readonly ConfirmService? _confirm;
    private readonly bool _isNew;

    // Window title — "Create Setup" when new, "Edit Setup" otherwise.
    public string DialogTitle => _isNew ? "Create Auto-Lair Setup" : "Edit Auto-Lair Setup";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    private string _name = string.Empty;

    [ObservableProperty] private string _notes = string.Empty;

    // Per-marker row, ordered by Map / Room for a stable display.
    public ObservableCollection<LairMarkerRowViewModel> Markers { get; } = new();

    // ----- Add-marker search box (mirrors LoopEditor's Add row) -----

    // Text input for the add-marker row. Accepts the same dialects as the
    // Loop editor's add-room box: coordinate (1/297, 1,297, bare 297) or
    // substring against room names.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddMarkerError))]
    private string _newMarkerQuery = string.Empty;

    // Inline validation message for the add-marker row. Empty when the
    // input is valid OR not yet evaluated; set after a failed Add attempt.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddMarkerError))]
    private string _addMarkerError = string.Empty;

    public bool HasAddMarkerError => !string.IsNullOrEmpty(AddMarkerError);

    // Live-search dropdown rows mirroring the Loop editor's.
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    [ObservableProperty] private RoomSearchResult? _selectedSearchResult;

    // Debounce keystrokes — same 120 ms window as the Loop editor +
    // Navigation search box so the surfaces feel identical.
    private DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    // True when the name field is non-empty after trim.
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    // Inline-validation flag for the name TextBox.
    public bool HasNameError => !HasName;

    // Enabled-state for the Save button — name set + at least one marker.
    public bool CanSave => HasName && Markers.Count > 0;

    public LairEditorDialogViewModel(
        LairSetup setup,
        LairManager setups,
        RoomGraphManager graph,
        LairTimerStore timers,
        ConfirmService? confirm = null,
        bool isNew = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(setups);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(timers);
        _original = setup;
        _setups = setups;
        _graph = graph;
        _timers = timers;
        _confirm = confirm;
        _isNew = isNew;

        Name  = setup.Name ?? string.Empty;
        Notes = setup.Notes ?? string.Empty;
        foreach (LairMarker m in setup.Markers) AddMarkerRow(m);
        Markers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
    }

    private void AddMarkerRow(LairMarker marker)
    {
        RoomKey key = new(marker.Map, marker.Room);
        string roomName = _graph.GetRoom(key)?.DisplayName ?? key.ToString();
        int? defaultRespawn = _timers.DefaultRespawnSeconds(key);
        Markers.Add(new LairMarkerRowViewModel(
            key, roomName, defaultRespawn, marker.OverrideRespawnSeconds));
    }

    [RelayCommand]
    private void RemoveMarker(LairMarkerRowViewModel? row)
    {
        if (row is null) return;
        Markers.Remove(row);
    }

    // Add a marker resolved from the search box. Priority: highlighted
    // dropdown row → top dropdown row → literal query parsed as a key or
    // matched as a unique room name. Mirrors LoopEditor.AddWaypoint.
    [RelayCommand]
    private void AddMarker()
    {
        RoomKey? resolved =
              SelectedSearchResult?.Key
           ?? (SearchResults.Count > 0 ? SearchResults[0].Key : (RoomKey?)null)
           ?? ResolveLiteralQuery(NewMarkerQuery);

        if (resolved is null)
        {
            string query = (NewMarkerQuery ?? string.Empty).Trim();
            AddMarkerError = query.Length == 0
                ? "Enter a room key (1/297) or room name."
                : $"No graph match for '{query}'.";
            return;
        }

        RoomKey key = resolved.Value;
        if (Markers.Any(m => m.Key.Equals(key)))
        {
            AddMarkerError = "That room is already marked.";
            return;
        }

        AddMarkerRow(new LairMarker(key.Map, key.Room));
        NewMarkerQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
        AddMarkerError = string.Empty;
    }

    partial void OnNewMarkerQueryChanged(string value)
    {
        AddMarkerError = string.Empty;
        _searchDebounce ??= new DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RebuildSearchResults(NewMarkerQuery);
    }

    // Rebuild SearchResults from the supplied query. Same two-dialect
    // parser as the Loop editor: coordinate (1/297, 1,297, 1 297, or bare
    // 297 across all maps) and room-name substring.
    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        string needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        List<RoomSearchResult> matches = new();

        (int? mapPart, int? roomPart) = TryParseCoordinate(needle);
        if (mapPart is int m && roomPart is int r
            && _graph.GetRoom(new RoomKey(m, r)) is { } exactRoom)
        {
            matches.Add(new RoomSearchResult(exactRoom.Key, exactRoom.DisplayName, null));
        }
        else if (mapPart is null && roomPart is int onlyRoom)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (room.Key.Room != onlyRoom) continue;
                matches.Add(new RoomSearchResult(room.Key, room.DisplayName, null));
                if (matches.Count >= 200) break;
            }
        }

        if (needle.Length >= 2)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (!room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                 && !room.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (matches.Any(x => x.Key.Equals(room.Key))) continue;
                matches.Add(new RoomSearchResult(room.Key, room.DisplayName, null));
                if (matches.Count >= 200) break;
            }
        }

        foreach (RoomSearchResult rr in matches
                     .OrderBy(rr => rr.PrimaryLine, StringComparer.OrdinalIgnoreCase)
                     .Take(50))
            SearchResults.Add(rr);
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private static (int? Map, int? Room) TryParseCoordinate(string text)
    {
        string[] parts = text.Split(new[] { '/', ',', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out int onlyRoom))
            return (null, onlyRoom);
        if (parts.Length == 2
            && int.TryParse(parts[0], out int map)
            && int.TryParse(parts[1], out int room))
            return (map, room);
        return (null, null);
    }

    // Last-resort literal resolution for an exact key or unique name the
    // user typed before the debounce fired.
    private RoomKey? ResolveLiteralQuery(string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return null;

        if (RoomKey.TryParseWire(q, out RoomKey key)
            && _graph.GetRoom(key) is not null)
            return key;

        RoomKey? exact = null;
        List<RoomKey> substrings = new();
        foreach (Room r in _graph.Rooms)
        {
            if (string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase))
            {
                if (exact is not null) return null;
                exact = r.Key;
            }
            else if (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                substrings.Add(r.Key);
            }
        }
        if (exact is { } e) return e;
        if (substrings.Count == 1) return substrings[0];
        return null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        // Renaming an existing setup to a name that's already taken
        // would silently clobber. Mirror LoopEditorDialog's behaviour:
        // confirm the overwrite explicitly.
        string trimmed = Name.Trim();
        bool nameChanged = !string.Equals(trimmed, _original.Name, StringComparison.OrdinalIgnoreCase);
        if (nameChanged && _setups.Get(trimmed) is not null && _confirm is not null)
        {
            bool ok = await _confirm.ConfirmAsync(
                title: "Overwrite existing setup?",
                body: $"A setup named \"{trimmed}\" already exists on this BBS. Overwrite it?");
            if (!ok) return;
        }

        // Persist a fresh LairSetup. We don't mutate _original — the
        // rail rows hold references to it via LairSetupRowViewModel,
        // and the LairManager keys on Name for the on-disk file.
        // Skip is no longer surfaced in the editor (the field stays on
        // LairMarker for forward-compat with the scheduler's eventual
        // pause-this-lair affordance); always emit false here.
        List<LairMarker> markers = new(Markers.Count);
        foreach (LairMarkerRowViewModel r in Markers)
        {
            markers.Add(new LairMarker(
                map: r.Key.Map,
                room: r.Key.Room,
                overrideRespawnSeconds: r.OverrideRespawnSeconds,
                skip: false));
        }

        LairSetup saved = new(trimmed, markers) { Notes = Notes ?? string.Empty };

        // Renaming an existing setup needs the old file deleted —
        // LairManager keys files by name so a rename leaves the old
        // file orphaned otherwise.
        if (!_isNew && nameChanged && !string.IsNullOrWhiteSpace(_original.Name))
            _setups.Delete(_original.Name);

        _setups.Save(saved);
        CloseRequested?.Invoke(saved);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// One row in the editor's marker grid: the room key + display name +
// game-data default respawn (read-only context) + user override (editable).
// The default hint stays visible even when an override is set so the user
// can always see what they're replacing.
public sealed partial class LairMarkerRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    public string RoomName { get; }

    // "5/100 — Sewer Lair" header for the row.
    public string DisplayHeader => $"{Key.Map}/{Key.Room} — {RoomName}";

    // Game-data default respawn in seconds, surfaced read-only so the user
    // can see what the override is replacing. null when no default could be
    // resolved (lookup missed or the room isn't tagged as a lair in the
    // active set).
    public int? DefaultRespawnSeconds { get; }

    // Sub-text shown below the room header — always visible, even when the
    // user has set an override. Format: "game default 270s" / "no game-data
    // timer".
    public string DefaultHint =>
        DefaultRespawnSeconds is int s
            ? $"game default {s}s"
            : "no game-data timer";

    [ObservableProperty] private int? _overrideRespawnSeconds;

    public LairMarkerRowViewModel(
        RoomKey key,
        string roomName,
        int? defaultRespawnSeconds,
        int? overrideRespawnSeconds)
    {
        Key = key;
        RoomName = roomName;
        DefaultRespawnSeconds = defaultRespawnSeconds;
        _overrideRespawnSeconds = overrideRespawnSeconds;
    }
}
