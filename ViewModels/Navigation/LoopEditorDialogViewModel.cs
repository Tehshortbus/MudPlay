using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Modeless editor for an existing <see cref="Loop"/> — rename, edit
/// notes, reorder / remove waypoints, attach / clear per-waypoint
/// commands + delays. Adding a new waypoint requires picking a room
/// on the map; that's the builder's job, not the editor's.
/// </summary>
/// <remarks>
/// Save mutates the loop in place + persists via
/// <see cref="LoopManager.Save"/>, which fires LoopsChanged so the
/// Navigation pane refreshes. Cancel / X discards every edit; the
/// dialog works on its own row view-models until Save runs.
/// </remarks>
public sealed partial class LoopEditorDialogViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    public event Action<Loop?>? CloseRequested;

    private readonly Loop _original;
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly LoopRunner? _runner;
    private readonly ConfirmService? _confirm;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<LoopWaypointRowViewModel> Waypoints { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LoopWaypointRowViewModel? _selectedRow;

    [ObservableProperty] private string _selectedCommand = string.Empty;
    [ObservableProperty] private int _selectedDelayMs = DefaultCommandDelayMs;

    /// <summary>
    /// Text input for the "add waypoint" row. Accepts the same input
    /// dialects as the Navigation window's room search box —
    /// coordinate (<c>"1/297"</c>, <c>"1,297"</c>, bare <c>"297"</c>
    /// across all maps) or substring against room names. Monster
    /// matches are deliberately omitted; this row's only job is to
    /// pick a waypoint room.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _newWaypointQuery = string.Empty;

    /// <summary>
    /// Inline validation message for the add-waypoint row. Empty when
    /// the input is valid OR not yet evaluated; set after a failed
    /// Add attempt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _addWaypointError = string.Empty;

    public bool HasAddWaypointError => !string.IsNullOrEmpty(AddWaypointError);

    /// <summary>
    /// Live-search dropdown rows. Mirrors the Navigation search box
    /// behaviour minus the monster category — rooms only, since the
    /// editor only ever needs to pick a waypoint room.
    /// </summary>
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    /// <summary>
    /// User-highlighted row in the dropdown ListBox. Enter on the
    /// TextBox commits this row when set; falls back to the top
    /// result otherwise, then to the literal query (key parse / single
    /// name match) so an experienced user can type "1/297&lt;Enter&gt;"
    /// without ever moving focus to the dropdown.
    /// </summary>
    [ObservableProperty] private RoomSearchResult? _selectedSearchResult;

    // Debounce keystrokes — same 120 ms window the Navigation search
    // box uses so the editor feels identical to the user.
    private DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    private const int DefaultCommandDelayMs = 1200;

    /// <summary>True when a waypoint row is selected for command edit.</summary>
    public bool HasSelection => SelectedRow is not null;

    public LoopEditorDialogViewModel(
        Loop loop,
        LoopManager loops,
        RoomGraphManager graph,
        LoopRunner? runner = null,
        ConfirmService? confirm = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _original = loop;
        _loops = loops;
        _graph = graph;
        _runner = runner;
        _confirm = confirm;
        _name = loop.Name;
        _notes = loop.Notes ?? string.Empty;
        foreach (LoopWaypoint w in loop.Waypoints)
            Waypoints.Add(new LoopWaypointRowViewModel(w, graph));
        RenumberRows();
    }

    // ----- row commands ----------------------------------------------

    [RelayCommand]
    private void MoveUp(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        int idx = Waypoints.IndexOf(row);
        if (idx <= 0) return;
        Waypoints.Move(idx, idx - 1);
        RenumberRows();
    }

    [RelayCommand]
    private void MoveDown(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        int idx = Waypoints.IndexOf(row);
        if (idx < 0 || idx >= Waypoints.Count - 1) return;
        Waypoints.Move(idx, idx + 1);
        RenumberRows();
    }

    [RelayCommand]
    private void Remove(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        Waypoints.Remove(row);
        if (SelectedRow == row) SelectedRow = null;
        RenumberRows();
    }

    [RelayCommand]
    private void ClearCommand()
    {
        if (SelectedRow is null) return;
        SelectedRow.Command = null;
        SelectedRow.DelayMs = 0;
        SelectedRow.RefreshDisplay();
        SelectedCommand = string.Empty;
        SelectedDelayMs = DefaultCommandDelayMs;
    }

    /// <summary>
    /// Apply the inline command/delay edits to the selected waypoint
    /// row so the list reflects them. Save consolidates the rows into
    /// the persisted loop.
    /// </summary>
    [RelayCommand]
    private void ApplyCommandEdit()
    {
        if (SelectedRow is null) return;
        int delay = Math.Max(0, SelectedDelayMs);
        string? cmd = string.IsNullOrWhiteSpace(SelectedCommand) ? null : SelectedCommand;
        SelectedRow.Command = cmd;
        SelectedRow.DelayMs = cmd is null ? 0 : delay;
        SelectedRow.RefreshDisplay();
    }

    partial void OnNewWaypointQueryChanged(string value)
    {
        // Debounce the rebuild — matches the Navigation search box's
        // 120 ms window so a 200-room substring scan doesn't run per
        // keystroke. Clear errors immediately so the user isn't
        // staring at a stale "no match" line while they type.
        AddWaypointError = string.Empty;
        _searchDebounce ??= new DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RebuildSearchResults(NewWaypointQuery);
    }

    /// <summary>
    /// Rebuild <see cref="SearchResults"/> from <paramref name="query"/>.
    /// Two input dialects, both mirroring the Navigation search box:
    /// coordinate (<c>"1/297"</c>, <c>"1,297"</c>, <c>"1 297"</c>, or
    /// bare <c>"297"</c> across all maps) and room-name substring.
    /// Monster matches are intentionally omitted.
    /// </summary>
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

        // Coordinate input — same parse rules as NavigationViewModel's
        // search box.
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

        // Name substring — gated by needle length to keep
        // single-character queries from flooding the dropdown.
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

    /// <summary>
    /// Parse a coordinate token. Returns <c>(map, room)</c> when both
    /// numbers were supplied (separator: <c>/</c>, <c>,</c>, or
    /// whitespace), <c>(null, room)</c> for a bare single number, or
    /// <c>(null, null)</c> for non-numeric input.
    /// </summary>
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

    /// <summary>
    /// Add a waypoint at the end of the list. Priority: the dropdown's
    /// highlighted row → the top dropdown row → the literal query
    /// parsed as a key or matched as a unique name. Empty input is a
    /// no-op (the dropdown is empty + the textbox has nothing to add).
    /// </summary>
    [RelayCommand]
    private void AddWaypoint()
    {
        RoomKey? resolved =
              SelectedSearchResult?.Key
           ?? (SearchResults.Count > 0 ? SearchResults[0].Key : (RoomKey?)null)
           ?? ResolveLiteralQuery(NewWaypointQuery);

        if (resolved is null)
        {
            string query = (NewWaypointQuery ?? string.Empty).Trim();
            AddWaypointError = query.Length == 0
                ? "Enter a room key (1/297) or room name."
                : $"No graph match for '{query}'.";
            return;
        }

        // Duplicate-of-prior guard — same protection the builder
        // applies; clicking the same room twice in a row produces
        // a zero-length leg the expander would silently drop.
        if (Waypoints.Count > 0 && Waypoints[^1].Key.Equals(resolved.Value))
        {
            AddWaypointError = "Same as the last waypoint; pick a different room.";
            return;
        }

        Waypoints.Add(new LoopWaypointRowViewModel(
            new LoopWaypoint(resolved.Value), _graph));
        RenumberRows();
        NewWaypointQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
        AddWaypointError = string.Empty;
    }

    /// <summary>
    /// Last-resort literal resolution — covers the case where the
    /// user typed something that didn't show up in
    /// <see cref="SearchResults"/> (e.g. an exact key whose debounce
    /// hadn't fired yet). Returns null when ambiguous so we don't
    /// guess.
    /// </summary>
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

    // ----- dialog commit ---------------------------------------------

    [RelayCommand]
    private async Task SaveAsync()
    {
        string newName = (Name ?? string.Empty).Trim();
        if (newName.Length == 0) return;
        if (Waypoints.Count < 2) return;        // cycles need 2+ entries

        var waypoints = new List<LoopWaypoint>(Waypoints.Count);
        foreach (LoopWaypointRowViewModel row in Waypoints)
            waypoints.Add(new LoopWaypoint(row.Key, row.Command, row.DelayMs));

        bool renamed = !string.Equals(newName, _original.Name, StringComparison.OrdinalIgnoreCase);
        string oldName = _original.Name;

        _original.Name      = newName;
        _original.Notes     = Notes ?? string.Empty;
        _original.Waypoints = waypoints;

        // Rename → delete the old file (LoopManager keys by Loop.Name
        // on disk) before saving under the new name.
        if (renamed) _loops.Delete(oldName);
        _loops.Save(_original);

        // If we just edited the live running loop, ask whether to
        // restart it with the new definition. The user might be
        // tweaking for next time and not want their current lap
        // disrupted, OR they might be fixing a bug and want it
        // applied immediately. Yes → stop+restart; No → leave the
        // runner on the in-memory pre-edit version (it'll pick up
        // the new disk version next time the user clicks Run).
        if (_runner is { } runner && _confirm is { } confirm
            && IsEditingRunningLoop(runner, oldName))
        {
            bool restart = await confirm.ConfirmAsync(
                "Apply to running loop?",
                "You edited the loop that's currently running. Restart the runner with the new version now?",
                yesLabel: "Restart now");
            if (restart)
            {
                runner.Stop("edited; restarting with new definition");
                runner.Start(_original);
            }
        }

        CloseRequested?.Invoke(_original);
    }

    /// <summary>
    /// True when <paramref name="runner"/>'s current loop is the one
    /// we just edited. Matched by the pre-rename name so a rename
    /// during the edit still detects the running-loop case.
    /// </summary>
    private static bool IsEditingRunningLoop(LoopRunner runner, string oldName)
        => runner.CurrentLoop is { } cur
        && string.Equals(cur.Name, oldName, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // ----- internals -------------------------------------------------

    partial void OnSelectedRowChanged(LoopWaypointRowViewModel? value)
    {
        SelectedCommand = value?.Command ?? string.Empty;
        SelectedDelayMs = value?.DelayMs > 0 ? value.DelayMs : DefaultCommandDelayMs;
    }

    private void RenumberRows()
    {
        for (int i = 0; i < Waypoints.Count; i++)
            Waypoints[i].Index = i + 1;
    }
}

/// <summary>
/// Per-row VM for the editor's Waypoints ListBox. Carries the
/// resolved room name (read-only) + the editable command + delay.
/// </summary>
public sealed partial class LoopWaypointRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _displayLabel = string.Empty;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private int _delayMs;

    /// <summary>True when this waypoint has a command attached — drives the row's command badge in the AXAML.</summary>
    public bool HasCommand => !string.IsNullOrEmpty(Command);

    public LoopWaypointRowViewModel(LoopWaypoint source, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(graph);
        Key = source.Key;
        _command = source.Command;
        _delayMs = source.DelayMs;
        RefreshDisplayFor(graph);
    }

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(HasCommand));
    }

    private void RefreshDisplayFor(RoomGraphManager graph)
    {
        Room? room = graph.GetRoom(Key);
        DisplayLabel = room is null
            ? Key.ToString()
            : $"{room.DisplayName}  ·  {Key}";
    }
}
