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

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<LoopWaypointRowViewModel> Waypoints { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LoopWaypointRowViewModel? _selectedRow;

    [ObservableProperty] private string _selectedCommand = string.Empty;
    [ObservableProperty] private int _selectedDelayMs = DefaultCommandDelayMs;

    private const int DefaultCommandDelayMs = 1200;

    /// <summary>True when a waypoint row is selected for command edit.</summary>
    public bool HasSelection => SelectedRow is not null;

    public LoopEditorDialogViewModel(Loop loop, LoopManager loops, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _original = loop;
        _loops = loops;
        _graph = graph;
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

    // ----- dialog commit ---------------------------------------------

    [RelayCommand]
    private void Save()
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

        CloseRequested?.Invoke(_original);
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
