using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Modeless "Manage" dialog hosting the user's saved Loops + marked
/// Auto-Lair rooms. Per UX direction the bottom strip in the
/// NavigationWindow is a pure status surface — naming / saving /
/// deleting all flow through this dialog instead of crowding the
/// build strip.
/// </summary>
/// <remarks>
/// <para>
/// Loops section: lists every loop on the active BBS. Each row
/// exposes Edit (opens <see cref="LoopEditorDialogViewModel"/> so
/// rename + notes + per-waypoint command edits all land in one place)
/// and Delete (confirmed via <see cref="ConfirmService"/>). The
/// dialog stays open across edits — closing it is the user's
/// explicit action.
/// </para>
/// <para>
/// Auto-Lair section: lists every room currently marked for the
/// Auto-Lair scheduler. Each row exposes Unmark. New marks happen
/// from the map right-click context menu (Phase 7's existing
/// workflow); the dialog is read-modify, not author-from-scratch.
/// </para>
/// </remarks>
public sealed partial class NavigationManagerDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly LoopManager _loops;
    private readonly AutoLairManager _autoLair;
    private readonly RoomGraphManager _graph;
    private readonly ConfirmService _confirm;
    private readonly DialogService _dialogs;

    public ObservableCollection<ManagerLoopRow> Loops { get; } = new();
    public ObservableCollection<ManagerLairRow> Lairs { get; } = new();

    public bool HasLoops => Loops.Count > 0;
    public bool HasLairs => Lairs.Count > 0;

    public NavigationManagerDialogViewModel(
        LoopManager loops,
        AutoLairManager autoLair,
        RoomGraphManager graph,
        ConfirmService confirm,
        DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(dialogs);
        _loops = loops;
        _autoLair = autoLair;
        _graph = graph;
        _confirm = confirm;
        _dialogs = dialogs;

        _loops.LoopsChanged += RebuildLoops;
        _autoLair.MarkedChanged += RebuildLairs;
        RebuildLoops();
        RebuildLairs();
    }

    private void RebuildLoops()
    {
        Loops.Clear();
        foreach (Loop loop in _loops.Loops.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            Loops.Add(new ManagerLoopRow(loop));
        OnPropertyChanged(nameof(HasLoops));
    }

    private void RebuildLairs()
    {
        Lairs.Clear();
        foreach (RoomKey key in _autoLair.Marked.OrderBy(k => k.Map).ThenBy(k => k.Room))
        {
            string label = _graph.GetRoom(key) is { } room
                ? $"{room.DisplayName}  ·  {key}"
                : key.ToString();
            Lairs.Add(new ManagerLairRow(key, label));
        }
        OnPropertyChanged(nameof(HasLairs));
    }

    // ----- Loop row commands -----------------------------------------

    /// <summary>
    /// Open the existing <see cref="LoopEditorDialogViewModel"/> for
    /// the selected loop. The editor handles rename + notes +
    /// per-waypoint command edits and writes back via
    /// <see cref="LoopManager.Save"/>; we just spawn it.
    /// </summary>
    [RelayCommand]
    private async Task EditLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        LoopEditorDialogViewModel vm = new(row.Source, _loops, _graph);
        await _dialogs.OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
    }

    [RelayCommand]
    private async Task DeleteLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync($"loop \"{row.Source.Name}\"");
        if (!ok) return;
        _loops.Delete(row.Source.Name);
    }

    // ----- Auto-Lair row commands ------------------------------------

    [RelayCommand]
    private void UnmarkLair(ManagerLairRow? row)
    {
        if (row is null) return;
        _autoLair.Unmark(row.Key);
    }

    [RelayCommand]
    private async Task ClearAllLairsAsync()
    {
        if (Lairs.Count == 0) return;
        bool ok = await _confirm.ConfirmDeleteAsync(
            $"all {Lairs.Count} marked Auto-Lair room(s)");
        if (!ok) return;
        _autoLair.Clear();
    }

    // ----- close -----------------------------------------------------

    [RelayCommand]
    private void Close()
    {
        _loops.LoopsChanged -= RebuildLoops;
        _autoLair.MarkedChanged -= RebuildLairs;
        CloseRequested?.Invoke(true);
    }
}

/// <summary>Single saved-loop row shown in the manager.</summary>
public sealed record ManagerLoopRow(Loop Source)
{
    public string Name => Source.Name;
    public int WaypointCount => Source.Waypoints.Count;
    public string Notes => string.IsNullOrWhiteSpace(Source.Notes) ? "—" : Source.Notes!;
}

/// <summary>Single marked Auto-Lair room shown in the manager.</summary>
public sealed record ManagerLairRow(RoomKey Key, string DisplayLabel);
