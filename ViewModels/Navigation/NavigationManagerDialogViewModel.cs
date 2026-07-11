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

// Modeless "Manage" dialog hosting the user's saved Loops + marked Auto-Lair
// rooms. The bottom strip in the NavigationWindow is a pure status surface —
// naming / saving / deleting all flow through this dialog instead of
// crowding the build strip.
//
// Loops section: lists every loop on the active BBS. Each row exposes Edit
// (opens LoopEditorDialogViewModel so rename + notes + per-waypoint command
// edits all land in one place) and Delete (confirmed via ConfirmService).
// The dialog stays open across edits — closing it is the user's explicit
// action.
//
// Auto-Lair Mode section: lists saved LairSetups stored alongside loops in
// the shared AppPaths.GameDataSetLoopsFolder (lair files use the .lair.json
// suffix). Each row exposes Run / Load / Edit / Delete — same row shape as
// the Loops section. New setups happen via "Save lairs" in the rail's
// build-mode strip; this dialog is the CRUD surface for already-named
// setups.
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
    private readonly RoomSearchService? _search;
    private readonly AutoWalkManager? _walker;
    private readonly MovementController? _movement;
    private readonly Action? _onDraftConsumed;

    // Flat backing rows for the Loops pane — source the tree is grouped from + drives HasLoops.
    public ObservableCollection<ManagerLoopRow> Loops { get; } = new();

    // Flat backing rows for the Auto-Lair pane — source the tree is grouped from + drives HasLairSetups.
    public ObservableCollection<ManagerLairSetupRow> LairSetups { get; } = new();

    // Single folder-grouped tree mixing NavFolderNodeViewModel folders with
    // both ManagerLairSetupRow and ManagerLoopRow leaves — loops and lairs
    // share one list (and one on-disk folder layout), so the manager shows
    // them together. Lairs sort ahead of loops within a folder (added first).
    public ObservableCollection<object> WalkTree { get; } = new();

    // In-progress build session from the Navigation window, or null when the
    // user isn't in LoopBuild mode. When non-null the dialog's Draft section
    // is visible — the user gives the draft a name + clicks Save to persist
    // (Run alone is transient and never writes to disk).
    public LoopBuilderSessionViewModel? Draft { get; }

    // Editable name for the currently-running loop's "Save running" row.
    // Seeded from LoopRunner.CurrentLoop's name at construction; the user can
    // rename before persisting.
    [ObservableProperty] private string _runningLoopName = string.Empty;

    public bool HasLoops => Loops.Count > 0;
    public bool HasLairSetups => LairSetups.Count > 0;

    // True when the combined tree has any node (loop, lair, or empty folder) — drives tree-vs-placeholder visibility.
    public bool HasWalkTree => WalkTree.Count > 0;

    public bool HasDraft => Draft is not null;

    // True when the runner is currently driving a loop. The "Save running
    // loop" section in the dialog only shows when this is true; the user can
    // name + save the in-flight loop without stopping it.
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
        LogService? log = null,
        RoomSearchService? search = null,
        AutoWalkManager? walker = null,
        MovementController? movement = null)
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
        _search = search;
        _walker = walker;
        _movement = movement;
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

    // Empty + ancestor folders to seed both trees with, so user-created folders render before anything is filed under them.
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

    // Rebuild the single combined tree from both backing lists. Lairs are
    // added before loops so they sort ahead within each folder; the TreeView
    // picks a leaf DataTemplate by runtime row type.
    private void RebuildWalkTree()
    {
        var rows = new List<object>(LairSetups.Count + Loops.Count);
        rows.AddRange(LairSetups);
        rows.AddRange(Loops);
        NavTreeBuilder.Sync<object>(WalkTree, rows, FolderOfWalkRow, FolderSeed);
        OnPropertyChanged(nameof(HasWalkTree));
    }

    private static string FolderOfWalkRow(object row) => row switch
    {
        ManagerLoopRow l      => l.Source.Folder,
        ManagerLairSetupRow s => s.Source.Folder,
        _                     => string.Empty,
    };

    // ----- Loop row commands -----------------------------------------

    // Open the existing LoopEditorDialogViewModel for the selected loop. The
    // editor handles rename + notes + per-waypoint command edits and writes
    // back via LoopManager.Save; we just spawn it.
    [RelayCommand]
    private async Task EditLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        LoopEditorDialogViewModel vm = new(
            row.Source, _loops, _graph, _runner, _confirm);
        await _dialogs.OpenWindowAsync<LoopEditorDialogViewModel, Loop?>(vm);
    }

    // Start the selected loop immediately via the shared runner — the "run a
    // saved loop without opening the map" path. The dialog is modeless so it
    // stays open (the user closes it explicitly); the loop's progress
    // surfaces on the toolbar movement buttons + the Navigation window if
    // it's open. No-op when no runner is wired.
    [RelayCommand]
    private void RunLoop(ManagerLoopRow? row)
    {
        if (row is null) return;
        _runner?.Start(row.Source);
    }

    // Stage the selected loop as the active one without starting movement
    // (LoopRunner.Stage). The toolbar Start button picks the staged loop up
    // when the user presses it, so "Load" lets the user pre-select a loop
    // here and begin it later from the toolbar — no map needed. No-op when no
    // runner is wired.
    [RelayCommand]
    private void LoadLoop(ManagerLoopRow? row)
    {
        if (row is null) return;
        _runner?.Stage(row.Source);
    }

    // Open the LoopEditor dialog on a fresh empty loop. The editor flips its
    // title to "Create Loop" via the LoopEditorDialogViewModel.DialogTitle
    // binding; Save persists the new loop via LoopManager.Save and Cancel
    // discards it entirely. The Manage dialog stays open in the background and
    // refreshes the Loops list when the new loop saves (LoopManager fires
    // LoopsChanged).
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

    // Symmetric with NewLoopAsync — open the LairEditorDialogViewModel on a
    // fresh empty setup pre-named with the current timestamp so the user can
    // author a new Auto-Lair setup without first marking rooms on the map.
    // Markers can be added later via the editor or by re-loading + clicking
    // on the map.
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

    // Persist the currently-running loop (Run was used as a transient
    // try-out, the user decided to keep it). Uses the user-edited
    // RunningLoopName so the auto-generated "Loop HH-mm" placeholder can be
    // replaced before committing.
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

    // One-line status / error surfaced in the Manage dialog after an import
    // attempt. Empty until the user clicks "Import .mp" the first time;
    // populated with success ("Imported loop 'X' — review + Save in the
    // editor.") or failure (the importer's error reason).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    private string _importStatus = string.Empty;

    public bool HasImportStatus => !string.IsNullOrEmpty(ImportStatus);

    // Open a file picker, parse the chosen .mp, resolve against the active
    // graph, and either open the LoopEditor with the loop pre-filled (single
    // best candidate) OR pop the picker dialog for the user to disambiguate
    // (multi-candidate tie).
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

    // Persist the active build session under its current
    // LoopBuilderSessionViewModel.ProposedName. Clears the build session
    // afterwards (matching LoopBuilderSessionViewModel.Save's contract) and
    // invokes the consumed callback so the NavigationWindow exits LoopBuild
    // mode.
    [RelayCommand]
    private void SaveDraft()
    {
        if (Draft is null) return;
        if (Draft.Save() is null) return;
        _onDraftConsumed?.Invoke();
    }

    // Discard the active build session without persisting. Clears the click
    // list + asks the NavigationWindow to exit LoopBuild mode via the
    // consumed callback.
    [RelayCommand]
    private void DiscardDraft()
    {
        if (Draft is null) return;
        Draft.Clear();
        _onDraftConsumed?.Invoke();
    }

    // ----- Auto-Lair row commands ------------------------------------

    // Edit a saved Auto-Lair setup via the editor dialog.
    [RelayCommand]
    private async Task EditLairSetupAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        LairEditorDialogViewModel vm = new(
            row.Source, _lairSetups, _graph, _lairTimers, _confirm);
        await _dialogs.OpenWindowAsync<LairEditorDialogViewModel, LairSetup?>(vm);
    }

    // Delete a saved setup with confirmation.
    [RelayCommand]
    private async Task DeleteLairSetupAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync($"auto-lair setup \"{row.Source.Name}\"");
        if (!ok) return;
        _lairSetups.Delete(row.Source.Name);
    }

    // Run / Load are rail-only actions; the manager dialog is CRUD-only
    // (Edit + Delete) for saved setups, mirroring how the Loops section
    // works.

    // ----- folder commands -------------------------------------------
    // Folders are SHARED on-disk between loops and lairs (one Loops
    // directory tree), so folder CRUD here mutates both panes at once
    // via the coordinator. The move commands stay per-pane.

    // Prompt for a name and create a new (empty) folder at the root, or —
    // when invoked on a folder node — nested under it. The new directory
    // shows immediately in both panes via NavFolderManager.FoldersChanged.
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

    // Rename a folder (and everything beneath it). No-op if the target name already exists.
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

    // Delete a folder. Its loops / lairs / sub-folders are re-parented one
    // level up (nothing is destroyed); only the folder grouping goes away.
    [RelayCommand]
    private async Task DeleteFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_folders is null || node is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync(
            $"folder \"{node.Name}\" (its contents move up one level)");
        if (!ok) return;
        _folders.DeleteFolder(node.Path, moveContentsToParent: true);
    }

    // Move a loop into the folder identified by folder (empty = root). Used by drag-drop + context-menu move.
    public void MoveLoopToFolder(ManagerLoopRow? row, string? folder)
    {
        if (row is null) return;
        _loops.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    // Move an Auto-Lair setup into the folder identified by folder (empty = root).
    public void MoveLairToFolder(ManagerLairSetupRow? row, string? folder)
    {
        if (row is null) return;
        _lairSetups.Move(row.Source.Name, NavFolders.Normalize(folder));
    }

    // Context-menu "Move to folder…" for a loop — prompts for a destination path.
    [RelayCommand]
    private async Task MoveLoopAsync(ManagerLoopRow? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move loop", "Destination folder (blank = root).", row.Source.Folder);
        if (folder is null) return;
        MoveLoopToFolder(row, folder);
    }

    // Context-menu "Move to folder…" for an Auto-Lair setup — prompts for a destination path.
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

    // ----- walk-to search ------------------------------------------------

    // Footer search box text. Mirrors the Navigation window's room search —
    // type a name / coordinate, pick a dropdown row, then Run walks there and
    // closes the dialog. Only wired when a RoomSearchService + AutoWalkManager
    // were supplied (the live Manage flows); the transient import-only
    // instance leaves them null and the box stays hidden.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSearchCommand))]
    private string _searchQuery = string.Empty;

    // Current dropdown matches for SearchQuery.
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    // True while the dropdown has rows to show.
    public bool HasSearchResults => SearchResults.Count > 0;

    // True when the footer search box should render (search + walker wired).
    public bool HasWalkToSearch => _search is not null && _walker is not null;

    // Destination locked in when the user picks a dropdown row, so Run walks
    // to the exact room they chose rather than re-resolving the text. Cleared
    // whenever the user edits the box again.
    private RoomKey? _queuedDestination;

    // Set while we write the chosen room name back into the box, to keep the dropdown from reopening.
    private bool _suppressSearch;

    private Avalonia.Threading.DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    partial void OnSearchQueryChanged(string value)
    {
        if (_suppressSearch) return;
        // Editing the text invalidates a previously-picked row.
        _queuedDestination = null;
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

    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        string needle = query?.Trim() ?? string.Empty;
        if (_search is null || needle.Length < 1)
        {
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }
        foreach (RoomSearchResult m in _search.Search(needle, cap: 200).Take(50))
            SearchResults.Add(m);
        OnPropertyChanged(nameof(HasSearchResults));
    }

    // User clicked a dropdown row — lock its room in as the destination and
    // reflect the name in the box. Informational rows (a monster with no
    // recorded lair) carry no walkable target, so they no-op.
    [RelayCommand]
    private void SelectSearchResult(RoomSearchResult? result)
    {
        if (result is null || result.IsInformational) return;
        _queuedDestination = result.Key;
        _suppressSearch = true;
        SearchQuery = result.DisplayName;
        _suppressSearch = false;
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        RunSearchCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunSearch => HasWalkToSearch && !string.IsNullOrWhiteSpace(SearchQuery);

    // Walk to the searched room and close the dialog. Uses the row the user
    // picked if any; otherwise resolves the first walkable match of the typed
    // text. Stops any running loop / Auto-Lair first so the explicit walk-to
    // takes precedence over background automation.
    [RelayCommand(CanExecute = nameof(CanRunSearch))]
    private async Task RunSearch()
    {
        if (_walker is null) return;

        RoomKey? dest = _queuedDestination;
        if (dest is null)
        {
            string needle = SearchQuery?.Trim() ?? string.Empty;
            if (needle.Length == 0 || _search is null) return;
            dest = _search.Search(needle, cap: 50)
                          .FirstOrDefault(m => !m.IsInformational)?.Key;
        }
        if (dest is not { } target) return;

        _movement?.Stop();
        // Close this manager first, then hand off — the route picker (when a
        // shorter gated shortcut exists) opens as its own modeless window rather
        // than stacking on the closing dialog.
        Close();
        await RouteChoicePrompt.WalkAsync(AppServices.Current, target);
    }

    // ----- close -----------------------------------------------------

    [RelayCommand]
    private void Close()
    {
        _searchDebounce?.Stop();
        _loops.LoopsChanged -= RebuildLoops;
        _lairSetups.SetupsChanged -= RebuildLairSetups;
        if (_folders is not null) _folders.FoldersChanged -= OnFoldersChanged;
        CloseRequested?.Invoke(true);
    }
}

// Single saved-loop row shown in the manager.
public sealed record ManagerLoopRow(Loop Source)
{
    public string Name => Source.Name;
    public int WaypointCount => Source.Waypoints.Count;
    public string Notes => string.IsNullOrWhiteSpace(Source.Notes) ? "—" : Source.Notes!;
}

// Single saved Auto-Lair setup row shown in the manager.
public sealed record ManagerLairSetupRow(LairSetup Source)
{
    public string Name => Source.Name;
    public int MarkerCount => Source.Markers.Count;
    public string Notes => string.IsNullOrWhiteSpace(Source.Notes) ? "—" : Source.Notes!;
}
