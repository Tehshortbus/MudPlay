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
    private readonly LoopRunner? _runner;
    private readonly Action? _onDraftConsumed;

    public ObservableCollection<ManagerLoopRow> Loops { get; } = new();
    public ObservableCollection<ManagerLairRow> Lairs { get; } = new();

    /// <summary>
    /// In-progress build session from the Navigation window, or null
    /// when the user isn't in LoopBuild mode. When non-null the
    /// dialog's Draft section is visible — the user gives the draft
    /// a name + clicks Save to persist (Run alone is transient and
    /// never writes to disk per UX direction).
    /// </summary>
    public LoopBuilderSessionViewModel? Draft { get; }

    /// <summary>
    /// Editable name for the currently-running loop's "Save running"
    /// row. Seeded from <see cref="LoopRunner.CurrentLoop"/>'s name
    /// at construction; the user can rename before persisting.
    /// </summary>
    [ObservableProperty] private string _runningLoopName = string.Empty;

    public bool HasLoops => Loops.Count > 0;
    public bool HasLairs => Lairs.Count > 0;
    public bool HasDraft => Draft is not null;

    /// <summary>
    /// True when the runner is currently driving a loop. The "Save
    /// running loop" section in the dialog only shows when this is
    /// true; the user can name + save the in-flight loop without
    /// stopping it.
    /// </summary>
    public bool HasRunningLoop => _runner?.CurrentLoop is not null;

    public NavigationManagerDialogViewModel(
        LoopManager loops,
        AutoLairManager autoLair,
        RoomGraphManager graph,
        ConfirmService confirm,
        DialogService dialogs,
        LoopBuilderSessionViewModel? draft = null,
        Action? onDraftConsumed = null,
        LoopRunner? runner = null)
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
        _runner = runner;
        Draft = draft;
        _onDraftConsumed = onDraftConsumed;
        _runningLoopName = runner?.CurrentLoop?.Name ?? string.Empty;

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
        LoopEditorDialogViewModel vm = new(
            row.Source, _loops, _graph, _runner, _confirm);
        await _dialogs.OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
    }

    /// <summary>
    /// Open the LoopEditor dialog on a fresh empty loop. The editor
    /// flips its title to "Create Loop" via the
    /// <see cref="LoopEditorDialogViewModel.DialogTitle"/> binding;
    /// Save persists the new loop via <see cref="LoopManager.Save"/>
    /// and Cancel discards it entirely. The Manage dialog stays open
    /// in the background and refreshes the Loops list when the new
    /// loop saves (LoopManager fires LoopsChanged).
    /// </summary>
    [RelayCommand]
    private async Task NewLoopAsync()
    {
        Loop draft = new(
            name: $"Loop {DateTime.Now:HH-mm-ss}",
            waypoints: Array.Empty<LoopWaypoint>());
        LoopEditorDialogViewModel vm = new(
            draft, _loops, _graph, _runner, _confirm, isNew: true);
        await _dialogs.OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
    }

    /// <summary>
    /// Stub for the Auto-Lair editor — symmetric with
    /// <see cref="NewLoopAsync"/> per UX direction (both "New"
    /// buttons should spawn the same kind of away-from-the-map
    /// editor). The lair-side editor lands in a later PR; for now
    /// the button is wired so the layout is final but the click
    /// shows a placeholder log line. Right-click on the map remains
    /// the working path for marking a lair until then.
    /// </summary>
    [RelayCommand]
    private void NewLair()
    {
        // Intentionally a no-op; lair editor dialog ships later.
        // Button kept visible so the layout matches the Loops
        // section side by side.
    }

    /// <summary>
    /// Persist the currently-running loop (Run was used as a
    /// transient try-out, the user decided to keep it). Uses the
    /// user-edited <see cref="RunningLoopName"/> so the auto-
    /// generated "Loop HH-mm" placeholder can be replaced before
    /// committing.
    /// </summary>
    [RelayCommand]
    private void SaveRunningLoop()
    {
        if (_runner?.CurrentLoop is not { } running) return;
        string saveName = (RunningLoopName ?? string.Empty).Trim();
        if (saveName.Length == 0) saveName = running.Name;

        Loop snapshot = new(saveName, running.Waypoints)
        {
            Notes = running.Notes ?? string.Empty,
        };
        _loops.Save(snapshot);
        // Re-stamp the live runner's loop name so subsequent edits
        // / saves identify the same record on disk.
        running.Name = saveName;
        OnPropertyChanged(nameof(HasRunningLoop));
    }

    [RelayCommand]
    private async Task DeleteLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync($"loop \"{row.Source.Name}\"");
        if (!ok) return;
        _loops.Delete(row.Source.Name);
    }

    // ----- Draft (in-progress build) commands ------------------------

    /// <summary>
    /// Persist the active build session under its current
    /// <see cref="LoopBuilderSessionViewModel.ProposedName"/>. Clears
    /// the build session afterwards (matching
    /// <see cref="LoopBuilderSessionViewModel.Save"/>'s contract) and
    /// invokes the consumed callback so the NavigationWindow exits
    /// LoopBuild mode.
    /// </summary>
    [RelayCommand]
    private void SaveDraft()
    {
        if (Draft is null) return;
        if (Draft.Save() is null) return;
        _onDraftConsumed?.Invoke();
    }

    /// <summary>
    /// Discard the active build session without persisting. Clears
    /// the click list + asks the NavigationWindow to exit LoopBuild
    /// mode via the consumed callback.
    /// </summary>
    [RelayCommand]
    private void DiscardDraft()
    {
        if (Draft is null) return;
        Draft.Clear();
        _onDraftConsumed?.Invoke();
    }

    // ----- Auto-Lair row commands ------------------------------------

    [RelayCommand]
    private void UnmarkLair(ManagerLairRow? row)
    {
        if (row is null) return;
        _autoLair.Unmark(row.Key);
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
