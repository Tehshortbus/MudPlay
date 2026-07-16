using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Modeless editor for an existing Loop — rename, edit notes, reorder /
// remove waypoints, attach / clear per-waypoint commands + delays. Adding a
// new waypoint requires picking a room on the map; that's the builder's job,
// not the editor's.
//
// Save mutates the loop in place + persists via LoopManager.Save, which
// fires LoopsChanged so the Navigation pane refreshes. Cancel / X discards
// every edit; the dialog works on its own row view-models until Save runs.
public sealed partial class LoopEditorDialogViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    public event Action<Loop?>? CloseRequested;

    private readonly Loop _original;
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly LoopRunner? _runner;
    private readonly ConfirmService? _confirm;
    private readonly bool _isNew;

    // The loop's runnable steps as opened, so Save can tell whether the user
    // actually edited the cycle. Gates the "apply to the running loop?" prompt
    // — a Save with no step change must not nag. Compared against the dialog's
    // own current rows (not the runner's live copy): the runner rotates its
    // waypoint list to the current room at Start, so a positional diff against
    // it would false-positive; a restart re-rotates anyway, making rotation
    // irrelevant to whether the run would differ.
    private readonly List<(string Room, string? Command, int DelayMs)> _openedSteps;

    // Window title — flips between "Create Loop" (when the dialog was opened
    // on a fresh empty loop) and "Edit Loop" (mutating an existing saved
    // loop). Bound from the AXAML Window.Title.
    public string DialogTitle => _isNew ? "Create Loop" : "Edit Loop";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<LoopWaypointRowViewModel> Waypoints { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LoopWaypointRowViewModel? _selectedRow;

    [ObservableProperty] private string _selectedCommand = string.Empty;
    [ObservableProperty] private int _selectedDelayMs = DefaultCommandDelayMs;

    // Text input for the "add waypoint" row. Accepts the same input dialects
    // as the Navigation window's room search box — coordinate ("1/297",
    // "1,297", bare "297" across all maps) or substring against room names.
    // Monster matches are deliberately omitted; this row's only job is to
    // pick a waypoint room.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _newWaypointQuery = string.Empty;

    // Inline validation message for the add-waypoint row. Empty when the
    // input is valid OR not yet evaluated; set after a failed Add attempt.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _addWaypointError = string.Empty;

    public bool HasAddWaypointError => !string.IsNullOrEmpty(AddWaypointError);

    // Live-search dropdown rows. Mirrors the Navigation search box behaviour
    // minus the monster category — rooms only, since the editor only ever
    // needs to pick a waypoint room.
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    // User-highlighted row in the dropdown ListBox. Enter on the TextBox
    // commits this row when set; falls back to the top result otherwise, then
    // to the literal query (key parse / single name match) so an experienced
    // user can type "1/297<Enter>" without ever moving focus to the dropdown.
    [ObservableProperty] private RoomSearchResult? _selectedSearchResult;

    // Debounce keystrokes — same 120 ms window the Navigation search
    // box uses so the editor feels identical to the user.
    private DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    private const int DefaultCommandDelayMs = 1200;

    // True when a waypoint row is selected for command edit.
    public bool HasSelection => SelectedRow is not null;

    public LoopEditorDialogViewModel(
        Loop loop,
        LoopManager loops,
        RoomGraphManager graph,
        LoopRunner? runner = null,
        ConfirmService? confirm = null,
        bool isNew = false)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _original = loop;
        _loops = loops;
        _graph = graph;
        _runner = runner;
        _confirm = confirm;
        _isNew = isNew;
        _name = loop.Name;
        _notes = loop.Notes ?? string.Empty;
        foreach (LoopWaypoint w in loop.Waypoints)
            Waypoints.Add(new LoopWaypointRowViewModel(w, graph));
        RenumberRows();

        // Snapshot the opened steps using the same derivation Save uses
        // (row.Key.ToString() / Command / DelayMs) so the dirty check is an
        // apples-to-apples comparison immune to Room-string reformatting.
        _openedSteps = Waypoints
            .Select(r => (r.Key.ToString(), r.Command, r.DelayMs))
            .ToList();
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

    // Per-row ✎ button → opens the modeless WaypointActionEditDialog
    // pre-seeded with the row's current command + delay. On Save the returned
    // values are applied to the row in place. The dialog is the primary path
    // for editing per-waypoint actions; the in-dialog footer editor is gone.
    [RelayCommand]
    private async Task EditWaypointActionAsync(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        WaypointActionEditDialogViewModel vm = new(
            waypointLabel: row.DisplayLabel,
            command: row.Command,
            delayMs: row.DelayMs);
        WaypointActionEditResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<WaypointActionEditDialogViewModel, WaypointActionEditResult?>(vm);
        if (result is null) return;
        row.Command = result.Command;
        row.DelayMs = result.DelayMs;
        row.RefreshDisplay();
    }

    // Apply the inline command/delay edits to the selected waypoint row so
    // the list reflects them. Save consolidates the rows into the persisted
    // loop.
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

    // Rebuild SearchResults from query. Two input dialects, both mirroring
    // the Navigation search box: coordinate ("1/297", "1,297", "1 297", or
    // bare "297" across all maps) and room-name substring. Monster matches
    // are intentionally omitted.
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

    // Parse a coordinate token. Returns (map, room) when both numbers were
    // supplied (separator: /, ,, or whitespace), (null, room) for a bare
    // single number, or (null, null) for non-numeric input.
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

    // Add a waypoint at the end of the list. Priority: the dropdown's
    // highlighted row → the top dropdown row → the literal query parsed as a
    // key or matched as a unique name. Empty input is a no-op (the dropdown
    // is empty + the textbox has nothing to add).
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

    // Last-resort literal resolution — covers the case where the user typed
    // something that didn't show up in SearchResults (e.g. an exact key whose
    // debounce hadn't fired yet). Returns null when ambiguous so we don't
    // guess.
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

    // Inline validation message for the Save row. Empty when the dialog is in
    // a savable state; populated on the next Save click with whatever's
    // blocking it (empty name, too few waypoints, no BBS bound, etc.) so the
    // user sees feedback instead of a button that visibly does nothing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string _saveError = string.Empty;

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    [RelayCommand]
    private async Task SaveAsync()
    {
        string newName = (Name ?? string.Empty).Trim();
        if (newName.Length == 0)
        {
            SaveError = "Loop needs a name.";
            return;
        }
        if (Waypoints.Count < 2)
        {
            SaveError = "Cycles need at least 2 waypoints.";
            return;
        }
        if (_loops.SetName is null)
        {
            // LoopManager silently no-ops the Save call when no game-data
            // set is active — surface the cause instead of disappearing
            // the dialog with no feedback.
            SaveError = "No active game-data set — select one before saving loops.";
            return;
        }

        // Name-conflict guard: refuse if the new name would overwrite
        // a DIFFERENT loop. Renaming an existing loop to its own
        // current name (i.e. no rename, just a Save) is the intentional
        // overwrite case and is allowed.
        bool wouldOverwriteAnother = _loops.Loops.Any(l =>
            string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase)
            && (_isNew || !string.Equals(l.Name, _original.Name, StringComparison.OrdinalIgnoreCase)));
        if (wouldOverwriteAnother)
        {
            SaveError = $"A loop named '{newName}' already exists on this BBS — pick a different name.";
            return;
        }

        var waypoints = new List<LoopWaypoint>(Waypoints.Count);
        foreach (LoopWaypointRowViewModel row in Waypoints)
            waypoints.Add(new LoopWaypoint(row.Key, row.Command, row.DelayMs));

        bool renamed = !string.Equals(newName, _original.Name, StringComparison.OrdinalIgnoreCase);
        string oldName = _original.Name;

        _original.Name      = newName;
        _original.Notes     = Notes ?? string.Empty;
        _original.Waypoints = waypoints;

        // Rename → delete the old file (LoopManager keys by Loop.Name
        // on disk) before saving under the new name. (Skip for a
        // brand-new loop — there's nothing on disk yet to delete.)
        if (renamed && !_isNew) _loops.Delete(oldName);
        _loops.Save(_original);
        SaveError = string.Empty;

        // If we just edited the live running loop, reconcile the runner with
        // what we saved. Two cases:
        if (_runner is { } runner && IsEditingRunningLoop(runner, oldName))
        {
            if (_confirm is { } confirm && StepsEditedSinceOpen(waypoints))
            {
                // The runnable PATH changed — ask whether to restart with the
                // new definition. The user might be tweaking for next time and
                // not want their current lap disrupted, OR they might be fixing a
                // bug and want it applied immediately. Yes → stop+restart (which
                // adopts the new name too); No → leave the runner on the in-memory
                // pre-edit version — name and path both stay as they're actually
                // running, and a later Run picks up the new disk version.
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
            else if (renamed)
            {
                // Name/notes-only edit: the live path is identical to what we
                // saved, so renaming in place is truthful and doesn't disrupt the
                // lap. Push the new name onto the runner so the nav header + status
                // chip stop holding the old (often builder-generated) name.
                runner.RenameCurrentLoop(newName);
            }
        }

        CloseRequested?.Invoke(_original);
    }

    // True when runner's current loop is the one we just edited. Matched by
    // the pre-rename name so a rename during the edit still detects the
    // running-loop case.
    private static bool IsEditingRunningLoop(LoopRunner runner, string oldName)
        => runner.CurrentLoop is { } cur
        && string.Equals(cur.Name, oldName, StringComparison.OrdinalIgnoreCase);

    // True when the saved waypoint sequence (rooms, per-waypoint commands,
    // delays, in order) differs from what the dialog opened on — i.e. the user
    // actually changed the runnable cycle. A Save that only renames or edits
    // notes leaves this false, so it never offers a mid-lap restart (the runner
    // would drive the identical path). Name / notes still persist via Save.
    private bool StepsEditedSinceOpen(List<LoopWaypoint> current)
    {
        if (current.Count != _openedSteps.Count) return true;
        for (int i = 0; i < current.Count; i++)
        {
            (string room, string? command, int delayMs) = _openedSteps[i];
            if (!string.Equals(current[i].Room, room, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(current[i].Command ?? string.Empty, command ?? string.Empty,
                    StringComparison.Ordinal))
                return true;
            if (current[i].DelayMs != delayMs) return true;
        }
        return false;
    }

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

// Per-row VM for the editor's Waypoints ListBox. Carries the resolved room
// name (read-only) + the editable command + delay.
public sealed partial class LoopWaypointRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _displayLabel = string.Empty;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private int _delayMs;

    // True when this waypoint has a command attached — drives the row's command badge in the AXAML.
    public bool HasCommand => !string.IsNullOrEmpty(Command);

    // One-line summary for the row's cyan badge — combines Command with
    // DelayMs when both are set, so the user can see at a glance what the
    // waypoint will fire and how long the loop waits after. Examples: "rest",
    // "dep 100 · 1500ms".
    public string CommandSummary
    {
        get
        {
            if (string.IsNullOrEmpty(Command)) return string.Empty;
            return DelayMs > 0
                ? $"{Command} · {DelayMs}ms"
                : Command;
        }
    }

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
        OnPropertyChanged(nameof(CommandSummary));
    }

    private void RefreshDisplayFor(RoomGraphManager graph)
    {
        Room? room = graph.GetRoom(Key);
        DisplayLabel = room is null
            ? Key.ToString()
            : $"{room.DisplayName}  ·  {Key}";
    }
}
