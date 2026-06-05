using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// Text input for the "add waypoint" row. Accepts either a raw
    /// room key (<c>1/297</c>) or a room name (case-insensitive
    /// substring match against the active graph). Empty until the
    /// user types.
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

    /// <summary>
    /// Add a waypoint at the end of the list from the query box.
    /// Accepts either a raw room key (<c>"1/297"</c>) or a room name
    /// — exact match first, single substring match as fallback.
    /// </summary>
    [RelayCommand]
    private void AddWaypoint()
    {
        string query = (NewWaypointQuery ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            AddWaypointError = "Enter a room key (1/297) or room name.";
            return;
        }

        RoomKey? resolved = ResolveQuery(query);
        if (resolved is null)
        {
            AddWaypointError = $"No graph match for '{query}'.";
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
        AddWaypointError = string.Empty;
    }

    /// <summary>
    /// Best-effort lookup from a free-form query: raw <c>map/room</c>
    /// first, exact name match next, single substring match last.
    /// Returns null when nothing matches OR multiple rooms share the
    /// same substring (forcing the user to disambiguate).
    /// </summary>
    private RoomKey? ResolveQuery(string query)
    {
        if (RoomKey.TryParseWire(query, out RoomKey key)
            && _graph.GetRoom(key) is not null)
            return key;

        // Name-based search through the active graph. Exact match
        // wins outright; otherwise we need exactly one substring hit
        // (case-insensitive) to commit without ambiguity.
        RoomKey? exact = null;
        List<RoomKey> substrings = new();
        foreach (Room r in _graph.Rooms)
        {
            if (string.Equals(r.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                if (exact is not null) return null;   // multiple exact matches
                exact = r.Key;
            }
            else if (r.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
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
