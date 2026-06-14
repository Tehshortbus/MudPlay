using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Game.Map.MpFile;
using FujinTerm.Models.Profile;
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
/// Auto-Lair Mode section: lists saved <see cref="LairSetup"/>s
/// stored alongside loops in the shared
/// <see cref="AppPaths.BbsLoopsFolder"/> (lair files use the
/// <c>.lair.json</c> suffix). Each row exposes Run / Load / Edit /
/// Delete — same row shape as the Loops section. New setups happen
/// via "Save lairs" in the rail's build-mode strip; this dialog is
/// the CRUD surface for already-named setups.
/// </para>
/// </remarks>
public sealed partial class NavigationManagerDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly LoopManager _loops;
    private readonly LairManager _lairSetups;
    private readonly LairTimerStore _lairTimers;
    private readonly RoomGraphManager _graph;
    private readonly ConfirmService _confirm;
    private readonly DialogService _dialogs;
    private readonly NavFolderManager? _folders;
    private readonly LoopRunner? _runner;
    private readonly MpFileImporter? _mpImporter;
    private readonly LogService? _log;
    private readonly Action? _onDraftConsumed;

    /// <summary>Flat backing rows for the Loops pane — source the tree is grouped from + drives <see cref="HasLoops"/>.</summary>
    public ObservableCollection<ManagerLoopRow> Loops { get; } = new();

    /// <summary>Flat backing rows for the Auto-Lair pane — source the tree is grouped from + drives <see cref="HasLairSetups"/>.</summary>
    public ObservableCollection<ManagerLairSetupRow> LairSetups { get; } = new();

    /// <summary>
    /// Single folder-grouped tree mixing <see cref="NavFolderNodeViewModel"/>
    /// folders with both <see cref="ManagerLairSetupRow"/> and
    /// <see cref="ManagerLoopRow"/> leaves — loops and lairs share one
    /// list (and one on-disk folder layout), so the manager shows them
    /// together. Lairs sort ahead of loops within a folder (added first).
    /// </summary>
    public ObservableCollection<object> WalkTree { get; } = new();

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
    public bool HasLairSetups => LairSetups.Count > 0;

    /// <summary>True when the combined tree has any node (loop, lair, or empty folder) — drives tree-vs-placeholder visibility.</summary>
    public bool HasWalkTree => WalkTree.Count > 0;

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
        LairManager lairSetups,
        LairTimerStore lairTimers,
        RoomGraphManager graph,
        ConfirmService confirm,
        DialogService dialogs,
        NavFolderManager? folders = null,
        LoopBuilderSessionViewModel? draft = null,
        Action? onDraftConsumed = null,
        LoopRunner? runner = null,
        MpFileImporter? mpImporter = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairSetups);
        ArgumentNullException.ThrowIfNull(lairTimers);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(dialogs);
        _loops = loops;
        _lairSetups = lairSetups;
        _lairTimers = lairTimers;
        _graph = graph;
        _confirm = confirm;
        _dialogs = dialogs;
        _folders = folders;
        _runner = runner;
        _mpImporter = mpImporter;
        _log = log;
        Draft = draft;
        _onDraftConsumed = onDraftConsumed;
        _runningLoopName = runner?.CurrentLoop?.Name ?? string.Empty;

        _loops.LoopsChanged += RebuildLoops;
        _lairSetups.SetupsChanged += RebuildLairSetups;
        // Empty-folder creation produces no loop/lair change, so both
        // trees rebuild on the coordinator's own folder event too.
        if (_folders is not null) _folders.FoldersChanged += OnFoldersChanged;
        RebuildLoops();
        RebuildLairSetups();
    }

    private void OnFoldersChanged()
    {
        RebuildLoops();
        RebuildLairSetups();
    }

    /// <summary>Empty + ancestor folders to seed both trees with, so user-created folders render before anything is filed under them.</summary>
    private IEnumerable<string> FolderSeed =>
        _folders?.AllFolders ?? Enumerable.Empty<string>();

    private void RebuildLoops()
    {
        Loops.Clear();
        foreach (Loop loop in _loops.Loops.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            Loops.Add(new ManagerLoopRow(loop));
        OnPropertyChanged(nameof(HasLoops));
        RebuildWalkTree();
    }

    private void RebuildLairSetups()
    {
        LairSetups.Clear();
        foreach (LairSetup setup in _lairSetups.Setups)
            LairSetups.Add(new ManagerLairSetupRow(setup));
        OnPropertyChanged(nameof(HasLairSetups));
        RebuildWalkTree();
    }

    /// <summary>
    /// Rebuild the single combined tree from both backing lists. Lairs
    /// are added before loops so they sort ahead within each folder; the
    /// TreeView picks a leaf DataTemplate by runtime row type.
    /// </summary>
    private void RebuildWalkTree()
    {
        var rows = new List<object>(LairSetups.Count + Loops.Count);
        rows.AddRange(LairSetups);
        rows.AddRange(Loops);
        NavTreeBuilder.Sync<object>(WalkTree, rows, FolderOfWalkRow, NameOfWalkRow, FolderSeed);
        OnPropertyChanged(nameof(HasWalkTree));
    }

    private static string FolderOfWalkRow(object row) => row switch
    {
        ManagerLoopRow l      => l.Source.Folder,
        ManagerLairSetupRow s => s.Source.Folder,
        _                     => string.Empty,
    };

    private static string NameOfWalkRow(object row) => row switch
    {
        ManagerLoopRow l      => l.Name,
        ManagerLairSetupRow s => s.Name,
        _                     => string.Empty,
    };

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
    /// Start the selected loop immediately via the shared runner — the
    /// "run a saved loop without opening the map" path. The dialog is
    /// modeless so it stays open (the user closes it explicitly); the
    /// loop's progress surfaces on the toolbar movement buttons + the
    /// Navigation window if it's open. No-op when no runner is wired.
    /// </summary>
    [RelayCommand]
    private void RunLoop(ManagerLoopRow? row)
    {
        if (row is null) return;
        _runner?.Start(row.Source);
    }

    /// <summary>
    /// Stage the selected loop as the active one without starting
    /// movement (<see cref="LoopRunner.Stage"/>). The toolbar Start
    /// button picks the staged loop up when the user presses it, so
    /// "Load" lets the user pre-select a loop here and begin it later
    /// from the toolbar — no map needed. No-op when no runner is wired.
    /// </summary>
    [RelayCommand]
    private void LoadLoop(ManagerLoopRow? row)
    {
        if (row is null) return;
        _runner?.Stage(row.Source);
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
    /// Symmetric with <see cref="NewLoopAsync"/> — open the
    /// <see cref="LairEditorDialogViewModel"/> on a fresh empty setup
    /// pre-named with the current timestamp so the user can author a
    /// new Auto-Lair setup without first marking rooms on the map.
    /// Markers can be added later via the editor or by re-loading +
    /// clicking on the map.
    /// </summary>
    [RelayCommand]
    private async Task NewLairAsync()
    {
        LairSetup draft = new(
            name: $"Lairs {DateTime.Now:HH-mm-ss}",
            markers: Array.Empty<LairMarker>());
        LairEditorDialogViewModel vm = new(
            draft, _lairSetups, _graph, _lairTimers, _confirm, isNew: true);
        await _dialogs.OpenWindowAsync<LairEditorDialogViewModel, LairSetup?>(vm);
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

    // ----- .mp importer ----------------------------------------------

    /// <summary>
    /// One-line status / error surfaced in the Manage dialog after an
    /// import attempt. Empty until the user clicks "Import .mp" the
    /// first time; populated with success ("Imported loop 'X' — review
    /// + Save in the editor.") or failure (the importer's error
    /// reason).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    private string _importStatus = string.Empty;

    public bool HasImportStatus => !string.IsNullOrEmpty(ImportStatus);

    /// <summary>
    /// Open a file picker, parse the chosen <c>.mp</c>, resolve
    /// against the active graph, and either open the LoopEditor with
    /// the loop pre-filled (single best candidate) OR pop the picker
    /// dialog for the user to disambiguate (multi-candidate tie).
    /// </summary>
    [RelayCommand]
    private async Task ImportMpAsync()
    {
        if (_mpImporter is null)
        {
            ImportStatus = "Importer not available — file a bug.";
            return;
        }
        if (Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                { MainWindow: { } main })
        {
            ImportStatus = "Couldn't access the main window for the file picker.";
            return;
        }

        IReadOnlyList<IStorageFile> picked = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Pick a MegaMUD .mp loop file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MegaMUD path / loop") { Patterns = new[] { "*.mp", "*.MP" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0) return;
        string path = picked[0].Path.LocalPath;

        MpLoopFile file;
        try
        {
            file = MpFileParser.ParseFile(path);
        }
        catch (MpFileFormatException ex)
        {
            ImportStatus = $"Import failed: {ex.Message}";
            _log?.Warn("MpImporter", $"parse failed for {path}: {ex.Message}");
            return;
        }

        MpImportResolution resolution = _mpImporter.Resolve(file);
        if (resolution.Failed)
        {
            ImportStatus = $"Import failed: {resolution.Error}";
            return;
        }

        RoomKey anchor;
        if (resolution.HasUniqueBest)
        {
            anchor = resolution.BestCandidates[0].AnchorKey;
        }
        else
        {
            MpAnchorPickerDialogViewModel pickerVm = new(file, resolution.BestCandidates, _graph);
            RoomKey? userChoice = await _dialogs
                .OpenWindowAsync<MpAnchorPickerDialogViewModel, RoomKey?>(pickerVm);
            if (userChoice is not { } chosen)
            {
                ImportStatus = "Import cancelled.";
                return;
            }
            anchor = chosen;
        }

        Loop? built = _mpImporter.BuildLoop(file, anchor);
        if (built is null)
        {
            ImportStatus = "Import failed: the chosen anchor didn't actually close the loop.";
            return;
        }

        // Open the editor pre-filled in create mode so the user can
        // rename / tweak / add commands before saving.
        LoopEditorDialogViewModel editor = new(
            built, _loops, _graph, _runner, _confirm, isNew: true);
        await _dialogs.OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(editor);

        ImportStatus = $"Parsed '{file.Label}' ({file.Steps.Count} steps) — review and Save in the editor.";
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

    /// <summary>Edit a saved Auto-Lair setup via the editor dialog.</summary>
    [RelayCommand]
    private async Task EditLairSetupAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        LairEditorDialogViewModel vm = new(
            row.Source, _lairSetups, _graph, _lairTimers, _confirm);
        await _dialogs.OpenWindowAsync<LairEditorDialogViewModel, LairSetup?>(vm);
    }

    /// <summary>Delete a saved setup with confirmation.</summary>
    [RelayCommand]
    private async Task DeleteLairSetupAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync($"auto-lair setup \"{row.Source.Name}\"");
        if (!ok) return;
        _lairSetups.Delete(row.Source.Name);
    }

    // Run / Load are rail-only actions per UX direction; the manager
    // dialog is CRUD-only (Edit + Delete) for saved setups, mirroring
    // how the Loops section works.

    // ----- folder commands -------------------------------------------
    // Folders are SHARED on-disk between loops and lairs (one Loops
    // directory tree), so folder CRUD here mutates both panes at once
    // via the coordinator. The move commands stay per-pane.

    /// <summary>
    /// Prompt for a name and create a new (empty) folder at the root,
    /// or — when invoked on a folder node — nested under it. The new
    /// directory shows immediately in both panes via
    /// <see cref="NavFolderManager.FoldersChanged"/>.
    /// </summary>
    [RelayCommand]
    private async Task NewFolderAsync(NavFolderNodeViewModel? parent)
    {
        if (_folders is null) return;
        string? name = await PromptFolderNameAsync(
            "New folder", "Name the new folder (use / to nest).");
        if (string.IsNullOrEmpty(name)) return;
        string full = parent is null ? name : NavFolders.Combine(parent.Path, name);
        _folders.CreateFolder(full);
    }

    /// <summary>Rename a folder (and everything beneath it). No-op if the target name already exists.</summary>
    [RelayCommand]
    private async Task RenameFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_folders is null || node is null) return;
        string? name = await PromptFolderNameAsync(
            "Rename folder", "New name for this folder.", node.Name);
        if (string.IsNullOrEmpty(name)) return;
        // Rename swaps only the last segment unless the user typed a
        // path; rebase onto the same parent so a bare name moves in place.
        string target = name.Contains(NavFolders.Separator)
            ? name
            : NavFolders.Combine(NavFolders.Parent(node.Path), name);
        _folders.RenameFolder(node.Path, target);
    }

    /// <summary>
    /// Delete a folder. Its loops / lairs / sub-folders are re-parented
    /// one level up (nothing is destroyed); only the folder grouping
    /// goes away.
    /// </summary>
    [RelayCommand]
    private async Task DeleteFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_folders is null || node is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync(
            $"folder \"{node.Name}\" (its contents move up one level)");
        if (!ok) return;
        _folders.DeleteFolder(node.Path, moveContentsToParent: true);
    }

    /// <summary>Move a loop into the folder identified by <paramref name="folder"/> (empty = root). Used by drag-drop + context-menu move.</summary>
    public void MoveLoopToFolder(ManagerLoopRow? row, string? folder)
    {
        if (row is null) return;
        _loops.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    /// <summary>Move an Auto-Lair setup into the folder identified by <paramref name="folder"/> (empty = root).</summary>
    public void MoveLairToFolder(ManagerLairSetupRow? row, string? folder)
    {
        if (row is null) return;
        _lairSetups.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    /// <summary>Context-menu "Move to folder…" for a loop — prompts for a destination path.</summary>
    [RelayCommand]
    private async Task MoveLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move loop", "Destination folder (blank = root).", row.Source.Folder);
        if (folder is null) return;
        MoveLoopToFolder(row, folder);
    }

    /// <summary>Context-menu "Move to folder…" for an Auto-Lair setup — prompts for a destination path.</summary>
    [RelayCommand]
    private async Task MoveLairAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move setup", "Destination folder (blank = root).", row.Source.Folder);
        if (folder is null) return;
        MoveLairToFolder(row, folder);
    }

    private async Task<string?> PromptFolderNameAsync(string title, string prompt, string initial = "")
    {
        NavFolderNameDialogViewModel vm = new(title, prompt, initial);
        return await _dialogs.OpenWindowAsync<NavFolderNameDialogViewModel, string?>(vm);
    }

    // ----- close -----------------------------------------------------

    [RelayCommand]
    private void Close()
    {
        _loops.LoopsChanged -= RebuildLoops;
        _lairSetups.SetupsChanged -= RebuildLairSetups;
        if (_folders is not null) _folders.FoldersChanged -= OnFoldersChanged;
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

/// <summary>Single saved Auto-Lair setup row shown in the manager.</summary>
public sealed record ManagerLairSetupRow(LairSetup Source)
{
    public string Name => Source.Name;
    public int MarkerCount => Source.Markers.Count;
    public string Notes => string.IsNullOrWhiteSpace(Source.Notes) ? "—" : Source.Notes!;
}
