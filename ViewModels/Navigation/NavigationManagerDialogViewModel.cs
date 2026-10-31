using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Navigation;

// Modeless "Manage" dialog hosting the user's saved Loops + marked Auto-Lair
// rooms. The bottom strip in the NavigationWindow is a pure status surface —
// naming / saving / deleting all flow through this dialog instead of
// crowding the build strip.
//
// Loops section: lists every loop of the active game-data set. Each row exposes Edit
// (opens LoopEditorDialogViewModel so rename + notes + per-waypoint command
// edits all land in one place) and Delete (confirmed via ConfirmService).
// The dialog stays open across edits — closing it is the user's explicit
// action.
//
// Auto-Lair Mode section: lists saved LairSetups stored alongside loops in
// the shared AppPaths.GameDataSetLoopsFolder (lair files use the .lair.json
// suffix). Each row exposes Run / Load / Edit / Delete — same row shape as
// the Loops section. New setups come from Save on the chip row in the
// build-mode strip, or from this dialog's New Lair button (an empty setup
// to fill in the editor).
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
    private readonly AutoLairManager? _autoLair;
    private readonly FavoritesStore? _favorites;
    private readonly MovementFilter? _movementFilter;
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

    // Flat, uniform-height projection of WalkTree the tab actually renders (a
    // virtualized ListBox over these rows). A nested TreeView makes an expanded
    // folder one giant item that wrecks the virtualized scrollbar and drifts on
    // expand; a flat list of same-height rows keeps it stable. Kept in sync by
    // RebuildWalkTree + the expand/collapse splice in ToggleFolder — see NavFlatList.
    private readonly NavFlatList _walkFlat = new();
    public ObservableCollection<NavFlatRow> WalkRows => _walkFlat.Rows;

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

    // ----- Filter (combined loops + lairs tree) ----------------------
    // Debounced + flat-while-filtering, exactly like the Navigation rail, so a
    // broad match over the seeded lists doesn't stall. The resting tree starts
    // collapsed (fast tab-swap); a filter shows a flat, virtualized match list.
    [ObservableProperty] private string _walkFilter = string.Empty;

    // Any loops/lairs exist to filter — drives the filter box's visibility,
    // independent of the current filter.
    public bool HasWalkItems => HasLoops || HasLairSetups;

    // Filtering matched nothing though the BBS does have loops/lairs — distinct
    // from the "nothing saved yet" empty state so the placeholder text stays right.
    public bool HasNoWalkMatches => HasWalkItems && WalkTree.Count == 0;

    private static readonly TimeSpan FilterDebounceDelay = TimeSpan.FromMilliseconds(150);
    private readonly HashSet<string> _walkExpandOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _walkWasFiltering;
    private Avalonia.Threading.DispatcherTimer? _walkFilterDebounce;

    partial void OnWalkFilterChanged(string value)
    {
        _walkFilterDebounce ??= new Avalonia.Threading.DispatcherTimer { Interval = FilterDebounceDelay };
        _walkFilterDebounce.Stop();
        _walkFilterDebounce.Tick -= OnWalkFilterDebounceTick;
        _walkFilterDebounce.Tick += OnWalkFilterDebounceTick;
        _walkFilterDebounce.Start();
    }

    private void OnWalkFilterDebounceTick(object? sender, EventArgs e)
    {
        _walkFilterDebounce?.Stop();
        RebuildWalkTree();
    }

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
        MovementController? movement = null,
        AutoLairManager? autoLair = null,
        FavoritesStore? favorites = null,
        bool startOnGotoTab = false,
        MovementFilter? movementFilter = null)
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
        _autoLair = autoLair;
        _favorites = favorites;
        _movementFilter = movementFilter;
        Draft = draft;
        _onDraftConsumed = onDraftConsumed;
        _runningLoopName = runner?.CurrentLoop?.Name ?? string.Empty;

        _loops.LoopsChanged += RebuildLoops;
        _lairSetups.SetupsChanged += RebuildLairSetups;
        // Empty-folder creation produces no loop/lair change, so both
        // trees rebuild on the coordinator's own folder event too.
        if (_folders is not null) _folders.FoldersChanged += OnFoldersChanged;
        // GOTO tab: favourites + their own (profile-backed) folder set. Their
        // Changed event covers both favourite and folder mutations.
        if (_favorites is not null) _favorites.Changed += RebuildFavorites;
        RebuildLoops();
        RebuildLairSetups();
        RebuildFavorites();
        SelectTab(startOnGotoTab);
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
        OnPropertyChanged(nameof(HasWalkItems));
        RebuildWalkTree();
    }

    private void RebuildLairSetups()
    {
        LairSetups.Clear();
        foreach (LairSetup setup in _lairSetups.Setups)
            LairSetups.Add(new ManagerLairSetupRow(setup));
        OnPropertyChanged(nameof(HasLairSetups));
        OnPropertyChanged(nameof(HasWalkItems));
        RebuildWalkTree();
    }

    // Rebuild the single combined tree from both backing lists. Lairs are
    // added before loops so they sort ahead within each folder; the flat row
    // list picks a leaf template by runtime row type.
    private void RebuildWalkTree()
    {
        string filter = (WalkFilter ?? string.Empty).Trim();
        bool filtering = filter.Length > 0;
        var rows = new List<object>(LairSetups.Count + Loops.Count);
        if (!filtering)
        {
            rows.AddRange(LairSetups);
            rows.AddRange(Loops);
        }
        else
        {
            bool favQuery = FavoriteFilter.IsFavoriteQuery(filter);
            foreach (ManagerLairSetupRow s in LairSetups)
                if (s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (favQuery && s.Source.Favorite)) rows.Add(s);
            foreach (ManagerLoopRow l in Loops)
                if (l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (favQuery && l.Source.Favorite)) rows.Add(l);
        }

        // Collapsed at rest (fast tab-swap); while filtering, build without
        // folders so every match is a visible row instead of hiding inside a
        // collapsed folder.
        Func<object, string?> folderOf = filtering ? (static _ => null) : FolderOfWalkRow;
        IEnumerable<string> folders = filtering ? Array.Empty<string>() : FolderSeed;
        NavTreeBuilder.Sync<object>(WalkTree, rows, folderOf, folders,
            defaultExpanded: false, _walkExpandOverrides,
            harvest: !_walkWasFiltering, forceExpandAll: false);
        _walkWasFiltering = filtering;
        _walkFlat.Rebuild(WalkTree);
        OnPropertyChanged(nameof(HasWalkTree));
        OnPropertyChanged(nameof(HasNoWalkMatches));
    }

    private static string FolderOfWalkRow(object row) => row switch
    {
        ManagerLoopRow l      => l.Source.Folder,
        ManagerLairSetupRow s => s.Source.Folder,
        _                     => string.Empty,
    };

    // ----- GOTO favourites (second tab) ------------------------------
    // Favourites are saved rooms with their OWN profile-backed folder set,
    // separate from the on-disk loop/lair folders. This tab is the CRUD surface
    // the pre-rework window used to host; the rail's GOTO collapsible mirrors the
    // same store live.

    // Flat backing rows for the GOTO pane — source the tree is grouped from.
    public ObservableCollection<FavoriteRowViewModel> Favorites { get; } = new();

    // Folder-grouped GOTO tree (NavFolderNodeViewModel folders + FavoriteRowViewModel leaves).
    public ObservableCollection<object> FavoriteTree { get; } = new();

    // Flat projection of FavoriteTree the GOTO tab renders — see _walkFlat / NavFlatList.
    private readonly NavFlatList _favoriteFlat = new();
    public ObservableCollection<NavFlatRow> FavoriteRows => _favoriteFlat.Rows;

    // Expand/collapse a folder in either flat list, splicing its rows in/out so the
    // scroll position — and the folder row you clicked — stays put. Routed by which
    // list holds the folder so one command serves both tabs' lists.
    [RelayCommand]
    private void ToggleFolder(NavFolderNodeViewModel? folder)
    {
        if (folder is null) return;
        if (_favoriteFlat.Contains(folder)) _favoriteFlat.ToggleFolder(folder);
        else if (_walkFlat.Contains(folder)) _walkFlat.ToggleFolder(folder);
    }

    public bool HasFavorites => Favorites.Count > 0;
    public bool HasFavoriteTree => FavoriteTree.Count > 0;

    // ----- Filter (GOTO favourites tree) -----------------------------
    [ObservableProperty] private string _gotoFilter = string.Empty;

    // Filtering matched nothing though favourites exist — distinct from the
    // "no favourites yet" empty state.
    public bool HasNoGotoMatches => HasFavorites && FavoriteTree.Count == 0;

    private readonly HashSet<string> _gotoExpandOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _gotoWasFiltering;
    private Avalonia.Threading.DispatcherTimer? _gotoFilterDebounce;

    partial void OnGotoFilterChanged(string value)
    {
        _gotoFilterDebounce ??= new Avalonia.Threading.DispatcherTimer { Interval = FilterDebounceDelay };
        _gotoFilterDebounce.Stop();
        _gotoFilterDebounce.Tick -= OnGotoFilterDebounceTick;
        _gotoFilterDebounce.Tick += OnGotoFilterDebounceTick;
        _gotoFilterDebounce.Start();
    }

    private void OnGotoFilterDebounceTick(object? sender, EventArgs e)
    {
        _gotoFilterDebounce?.Stop();
        RebuildFavoriteTree();
    }

    // Show the GOTO tab only when a FavoritesStore was supplied (the live Manage
    // flow); the transient import-only instance leaves it null.
    public bool HasGotoTab => _favorites is not null;

    // Which tab is showing: 0 = Loops & Auto-Lairs, 1 = Go To. Two-way so user
    // clicks stay in sync; driven by the entry point via SelectTab (toolbar Start
    // opens on Go To, the map's Manage button on Loops).
    [ObservableProperty] private int _selectedTabIndex;

    // Land on the Go To tab (when it exists) or the Loops tab.
    public void SelectTab(bool gotoTab) => SelectedTabIndex = gotoTab && HasGotoTab ? 1 : 0;

    private void RebuildFavorites()
    {
        Favorites.Clear();
        if (_favorites is not null)
        {
            var entries = new List<FavoriteRowViewModel>();
            foreach (FavoriteRoom f in _favorites.All)
            {
                RoomKey key = new(f.Map, f.Room);
                string label = !string.IsNullOrWhiteSpace(f.Label)
                    ? f.Label!
                    : _graph.GetRoom(key) is { } r ? r.Name : key.ToString();
                entries.Add(new FavoriteRowViewModel(key, label, _favorites.FolderOf(key), f.Starred));
            }
            entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            foreach (FavoriteRowViewModel e in entries) Favorites.Add(e);
        }
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasFolders));
        RebuildFavoriteTree();
    }

    private void RebuildFavoriteTree()
    {
        string filter = (GotoFilter ?? string.Empty).Trim();
        bool filtering = filter.Length > 0;
        bool favQuery = FavoriteFilter.IsFavoriteQuery(filter);
        IEnumerable<FavoriteRowViewModel> rows = filtering
            ? Favorites.Where(f => f.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || (favQuery && f.IsStarred))
            : Favorites;
        // Collapsed at rest; flat while filtering so the tree virtualizes (see
        // RebuildWalkTree).
        Func<FavoriteRowViewModel, string?> folderOf = filtering ? (static _ => null) : static r => r.Folder;
        IEnumerable<string> folders = filtering
            ? Array.Empty<string>()
            : _favorites?.AllFolders ?? Enumerable.Empty<string>();
        NavTreeBuilder.Sync(FavoriteTree, rows, folderOf, folders,
            defaultExpanded: false, _gotoExpandOverrides,
            harvest: !_gotoWasFiltering, forceExpandAll: false);
        _gotoWasFiltering = filtering;
        _favoriteFlat.Rebuild(FavoriteTree);
        OnPropertyChanged(nameof(HasFavoriteTree));
        OnPropertyChanged(nameof(HasNoGotoMatches));
    }

    // GOTO folder CRUD — mutates the favourites' profile-backed folder set.
    [RelayCommand]
    private async Task NewGotoFolderAsync(NavFolderNodeViewModel? parent)
    {
        if (_favorites is null) return;
        string? name = await PromptFolderNameAsync(
            "New folder", "Name the new folder (use / to nest).");
        if (string.IsNullOrEmpty(name)) return;
        string full = parent is null ? name : NavFolders.Combine(parent.Path, name);
        _favorites.AddFolder(full);
    }

    [RelayCommand]
    private async Task RenameGotoFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_favorites is null || node is null) return;
        string? name = await PromptFolderNameAsync(
            "Rename folder", "New name for this folder.", node.Name);
        if (string.IsNullOrEmpty(name)) return;
        _favorites.RenameFolder(node.Path, NavFolders.RenameTarget(node.Path, name));
    }

    [RelayCommand]
    private async Task DeleteGotoFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_favorites is null || node is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync(
            $"folder \"{node.Name}\" (its favourites move up one level)");
        if (!ok) return;
        _favorites.RemoveFolder(node.Path, moveContentsToParent: true);
    }

    // Move a favourite into folder (empty = root).
    public void MoveFavoriteToFolder(FavoriteRowViewModel? row, string? folder)
    {
        if (row is null || _favorites is null) return;
        _favorites.MoveFavorite(row.Key, NavFolders.Normalize(folder));
    }

    // True while any GOTO folder exists — gates the "Move to folder…" affordance
    // (there's nowhere to move a favourite with no folders).
    public bool HasFolders => _favorites is { } f && f.AllFolders.Count > 0;

    [RelayCommand]
    private async Task MoveFavoriteAsync(FavoriteRowViewModel? row)
    {
        if (row is null || _favorites is null) return;
        FolderPickerDialogViewModel vm = new(_favorites.AllFolders, row.Folder);
        string? folder = await _dialogs
            .OpenWindowAsync<FolderPickerDialogViewModel, string?>(vm);
        if (folder is null) return;   // cancelled (root is "")
        MoveFavoriteToFolder(row, folder);
    }

    // Full edit — name + map + room. Re-points the favourite at a different room
    // when the coordinate changed (favourites are keyed by room), else relabels.
    [RelayCommand]
    private async Task EditFavoriteAsync(FavoriteRowViewModel? row)
    {
        if (row is null || _favorites is null) return;
        bool alreadyStarred = _favorites.IsStarred(row.Key);
        FavoriteEditDialogViewModel vm = new(
            row.Label, row.Key.Map, row.Key.Room,
            (m, r) => _graph.GetRoom(new RoomKey(m, r))?.Name,
            starred: alreadyStarred,
            // The cap is "other" starred favourites — exclude this one so a starred
            // 10th can still toggle freely.
            canStarWhenUnset: _favorites.StarredCount - (alreadyStarred ? 1 : 0) < FavoritesStore.MaxStarred);
        FavoriteEditResult? res = await _dialogs
            .OpenWindowAsync<FavoriteEditDialogViewModel, FavoriteEditResult?>(vm);
        if (res is null) return;
        RoomKey newKey = new(res.Map, res.Room);
        // Edit re-keys via Remove+Add (dropping the star), so apply the star to the
        // final key after the edit lands.
        _favorites.Edit(row.Key, newKey, res.Label);
        _favorites.SetStarred(newKey, res.Starred);
    }

    [RelayCommand]
    private async Task DeleteFavoriteAsync(FavoriteRowViewModel? row)
    {
        if (row is null || _favorites is null) return;
        bool ok = await _confirm.ConfirmDeleteAsync($"favourite \"{row.Label}\"");
        if (!ok) return;
        _favorites.Remove(row.Key);
    }

    // Walk to a favourite — stops background automation, closes the manager, then
    // hands off to the route picker (same terminal-walk shape as the footer search).
    [RelayCommand]
    private Task WalkToFavoriteAsync(FavoriteRowViewModel? row) => WalkToFavorite(row, RunStartMode.Normal);

    // Go ▾ → Run / Sprint: the same walk with Auto-Combat off, or in Sprint Mode.
    [RelayCommand]
    private Task WalkToFavoriteCombatOffAsync(FavoriteRowViewModel? row) => WalkToFavorite(row, RunStartMode.CombatOff);

    [RelayCommand]
    private Task WalkToFavoriteSprintAsync(FavoriteRowViewModel? row) => WalkToFavorite(row, RunStartMode.Sprint);

    private async Task WalkToFavorite(FavoriteRowViewModel? row, RunStartMode mode)
    {
        if (row is null || _walker is null) return;
        // Before the Stop: a run frozen by the master switch is not given up for
        // a walk that won't start.
        if (AppServices.Current.RefuseStartForMasterSwitch("Walk")) return;
        _movement?.Stop();
        Close();
        await RouteChoicePrompt.WalkAsync(AppServices.Current, row.Key, startMode: mode);
    }

    // "Add" — pick a room by loose-match name search OR map/room number, then
    // save it as a favourite (at the root; move it into a folder afterwards).
    [RelayCommand]
    private async Task AddFavoriteAsync()
    {
        if (_favorites is null || _search is null) return;
        AddFavoriteDialogViewModel picker = new(_search);
        RoomKey? chosen = await _dialogs
            .OpenWindowAsync<AddFavoriteDialogViewModel, RoomKey?>(picker);
        if (chosen is { } key) _favorites.Add(key);
    }

    // "Restore defaults" on the Go To tab: the bundled starter GOTOs this character
    // doesn't have, added back. Asked first, since on a list the user has pruned
    // it can add hundreds.
    [RelayCommand]
    private async Task RestoreDefaultGotosAsync()
    {
        if (_favorites is null) return;
        const string Title = "Restore default GOTOs";
        int missing = _favorites.MissingStarterCount;
        if (missing == 0)
        {
            await _confirm.ConfirmAsync(Title,
                "This character already has every default GOTO for its game data (or no game data is loaded). "
                + "There is nothing to add.", "OK");
            return;
        }
        bool ok = await _confirm.ConfirmAsync(Title,
            $"Add the {missing} default GOTO(s) this character doesn't have? "
            + "Your own GOTOs and folders are kept as they are.", "Add them");
        if (ok) _favorites.AddStarters();
    }

    // The same for the packaged loops. Loops belong to the game data, so what is
    // put back is put back for every character on it.
    [RelayCommand]
    private async Task RestoreDefaultLoopsAsync()
    {
        const string Title = "Restore default loops";
        int missing = _loops.SetName is { } loaded ? NavSeedBootstrapper.MissingLoopCount(loaded) : 0;
        if (_loops.SetName is not { } set || missing == 0)
        {
            await _confirm.ConfirmAsync(Title,
                "This game data already has every packaged loop (or no game data is loaded). "
                + "There is nothing to put back.", "OK");
            return;
        }
        bool ok = await _confirm.ConfirmAsync(Title,
            $"Put back the {missing} packaged loop(s) that '{set}' no longer has? Loops are shared by every "
            + "character on this game data. The loops that are there, edited or not, are kept as they are.",
            "Put them back");
        if (!ok) return;
        try
        {
            NavSeedBootstrapper.RestoreLoops(set, _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("NavSeed", $"Restore defaults on set '{set}' stopped part-way: {ex.Message}");
        }
        _loops.TakeInOutsideChanges();
    }

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
    private void RunLoop(ManagerLoopRow? row) => StartLoop(row, RunStartMode.Normal);

    // Go ▾ → Run / Sprint: start the loop with Auto-Combat off, or in Sprint Mode, for
    // the walk to its first waypoint.
    [RelayCommand]
    private void RunLoopCombatOff(ManagerLoopRow? row) => StartLoop(row, RunStartMode.CombatOff);

    [RelayCommand]
    private void RunLoopSprint(ManagerLoopRow? row) => StartLoop(row, RunStartMode.Sprint);

    private void StartLoop(ManagerLoopRow? row, RunStartMode mode)
    {
        if (row is null || _runner is null) return;
        if (AppServices.Current.RefuseStartForMasterSwitch("Loop")) return;
        ApplyStartMode(mode);
        _runner.Start(row.Source);
    }

    private static void ApplyStartMode(RunStartMode mode)
    {
        if (mode != RunStartMode.Normal) AppServices.Current.ApplyRunStartMode?.Invoke(mode);
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
            draft, _lairSetups, _graph, _lairTimers, _confirm, isNew: true, log: _log);
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
        // The loop is the rooms of the set it was started on, and isn't saved into
        // another's. A switch of game data stops a run, so this is for one something
        // started again from before it.
        OpenedOnGameData startedOn = new(_runner.LastRunLoopGameData);
        if (startedOn.Refusal("loop", _loops.SetName) is { } refusal)
        {
            ImportStatus = _runningLoopSaveRefusal = refusal;
            _log?.Info("Navigation",
                $"running loop '{running.Name}' not saved: {startedOn.LogReason(_loops.SetName)}");
            return;
        }
        // The refusal isn't left standing over a save that went through. The line
        // is the import's too, so whatever else it says stays.
        if (_runningLoopSaveRefusal is { } said && ImportStatus == said) ImportStatus = string.Empty;
        _runningLoopSaveRefusal = null;
        string saveName = (RunningLoopName ?? string.Empty).Trim();
        if (saveName.Length == 0) saveName = running.Name;

        Loop snapshot = new(saveName, running.Waypoints)
        {
            Notes = running.Notes ?? string.Empty,
            // The settings the run has are the ones this character keeps for the
            // saved loop; left out, the save would put both back to off.
            OwnSettings = running.OwnSettings,
        };
        _loops.Save(snapshot);
        GuideActions.Report(GuideActions.LoopSaved);
        // Re-stamp the live runner's loop name so subsequent edits
        // / saves identify the same record on disk.
        running.Name = saveName;
        OnPropertyChanged(nameof(HasRunningLoop));
    }

    // ----- .mp importer ----------------------------------------------

    // One-line status / error surfaced in the Manage dialog after an import
    // attempt, and the reason a running loop's Save was refused. Empty until the
    // user clicks "Import .mp" the first time or such a Save is refused.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    private string _importStatus = string.Empty;

    public bool HasImportStatus => !string.IsNullOrEmpty(ImportStatus);

    // The refusal SaveRunningLoop last put on that line, so the save that goes
    // through can take it off again without wiping an import's status.
    private string? _runningLoopSaveRefusal;

    // Pick a .mp file and open the import review: the file decoded beside our
    // translation, editable, accepted or rejected there.
    [RelayCommand]
    private async Task ImportMpAsync()
    {
        GuideActions.Report(GuideActions.MpImportStarted);
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
        MegaMudRoomsFile? roomsMd;
        try
        {
            file = MpFileParser.ParseFile(path);
            roomsMd = MegaMudRoomsFile.FindBeside(path);
        }
        catch (Exception ex) when (ex is MpFileFormatException or IOException or UnauthorizedAccessException)
        {
            ImportStatus = $"Can't read {Path.GetFileName(path)}: {ex.Message}";
            _log?.Warn("MpImporter", $"read failed for {path}: {ex.Message}");
            return;
        }

        AppServices? app = AppServices.CurrentOrNull;
        MpImportReviewViewModel review = new(path, file, roomsMd, _mpImporter, _graph, _loops, _movementFilter, _log,
            app?.MapComparison, app is null ? null : app.NavigateToRoom);
        Loop? accepted = await _dialogs.OpenWindowAsync<MpImportReviewViewModel, Loop?>(review);
        ImportStatus = accepted is null
            ? $"Import of {Path.GetFileName(path)} cancelled."
            : $"Imported '{accepted.Name}' ({accepted.Waypoints.Count} rooms).";
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
        Draft.Discard();
        _onDraftConsumed?.Invoke();
    }

    // ----- Auto-Lair row commands ------------------------------------

    // Edit a saved Auto-Lair setup via the editor dialog.
    [RelayCommand]
    private async Task EditLairSetupAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        LairEditorDialogViewModel vm = new(
            row.Source, _lairSetups, _graph, _lairTimers, _confirm, log: _log);
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

    // Run a saved Auto-Lair setup immediately — loads its markers into the
    // AutoLairManager (wiping any current ones) and starts the scheduler, the
    // "run a saved lair without opening the map" path that mirrors RunLoop.
    // The map-mode transition the Navigation rail does on Run is window-only
    // sugar and deliberately skipped here — this dialog isn't the map.
    [RelayCommand]
    private void RunLairSetup(ManagerLairSetupRow? row) => StartLairSetup(row, RunStartMode.Normal);

    // Go ▾ → Run / Sprint: start the setup with Auto-Combat off, or in Sprint Mode,
    // for the trip to its first lair.
    [RelayCommand]
    private void RunLairSetupCombatOff(ManagerLairSetupRow? row) => StartLairSetup(row, RunStartMode.CombatOff);

    [RelayCommand]
    private void RunLairSetupSprint(ManagerLairSetupRow? row) => StartLairSetup(row, RunStartMode.Sprint);

    private void StartLairSetup(ManagerLairSetupRow? row, RunStartMode mode)
    {
        if (row is null || _autoLair is null) return;
        if (AppServices.Current.RefuseStartForMasterSwitch("Auto-Lair")) return;
        LoadLairMarkers(row.Source);
        ApplyStartMode(mode);
        _autoLair.Start();
    }

    // Stage a saved setup's markers without starting the scheduler — lets the
    // user load a lair here and begin it later. Mirrors LoadLoop.
    [RelayCommand]
    private void LoadLairSetup(ManagerLairSetupRow? row)
    {
        if (row is null || _autoLair is null) return;
        LoadLairMarkers(row.Source);
    }

    // Shared body for RunLairSetup / LoadLairSetup: stop any in-flight loop /
    // lair, then swap the AutoLairManager's marker set to this setup's.
    private void LoadLairMarkers(LairSetup setup)
    {
        if (_autoLair is null) return;
        if (_runner is not null && _runner.State != LoopState.Idle)
            _runner.Stop("auto-lair setup loaded");
        if (_autoLair.IsActive)
            _autoLair.Stop("auto-lair setup loaded");

        _autoLair.Clear();
        foreach (LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
    }

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
        if (_folders is not { } folders) return;
        string? name = await PromptFolderNameAsync(
            "New folder", "Name the new folder (use / to nest).",
            whyNot: n => folders.WhyNotFolder(NavFolders.Combine(parent?.Path, n)));
        if (string.IsNullOrEmpty(name)) return;
        string full = parent is null ? name : NavFolders.Combine(parent.Path, name);
        folders.CreateFolder(full);
    }

    // Rename a folder (and everything beneath it). No-op if the target name already exists.
    [RelayCommand]
    private async Task RenameFolderAsync(NavFolderNodeViewModel? node)
    {
        if (_folders is not { } folders || node is null) return;
        string? name = await PromptFolderNameAsync(
            "Rename folder", "New name for this folder.", node.Name,
            n => folders.WhyNotFolder(NavFolders.RenameTarget(node.Path, n)));
        if (string.IsNullOrEmpty(name)) return;
        folders.RenameFolder(node.Path, NavFolders.RenameTarget(node.Path, name));
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

    // Move a loop into the folder identified by folder (empty = root).
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
            "Move loop", "Destination folder (blank = root).", row.Source.Folder,
            _folders is { } folders ? folders.WhyNotFolder : null);
        if (folder is null) return;
        MoveLoopToFolder(row, folder);
    }

    // Context-menu "Move to folder…" for an Auto-Lair setup — prompts for a destination path.
    [RelayCommand]
    private async Task MoveLairAsync(ManagerLairSetupRow? row)
    {
        if (row is null) return;
        string? folder = await PromptFolderNameAsync(
            "Move setup", "Destination folder (blank = root).", row.Source.Folder,
            _folders is { } folders ? folders.WhyNotFolder : null);
        if (folder is null) return;
        MoveLairToFolder(row, folder);
    }

    private async Task<string?> PromptFolderNameAsync(string title, string prompt, string initial = "",
        Func<string, string?>? whyNot = null)
    {
        NavFolderNameDialogViewModel vm = new(title, prompt, initial, whyNot);
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
    [NotifyCanExecuteChangedFor(nameof(RunSearchCombatOffCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSearchSprintCommand))]
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
        RunSearchCombatOffCommand.NotifyCanExecuteChanged();
        RunSearchSprintCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunSearch => HasWalkToSearch && !string.IsNullOrWhiteSpace(SearchQuery);

    // Walk to the searched room and close the dialog. Uses the row the user
    // picked if any; otherwise resolves the first walkable match of the typed
    // text. Stops any running loop / Auto-Lair first so the explicit walk-to
    // takes precedence over background automation.
    [RelayCommand(CanExecute = nameof(CanRunSearch))]
    private Task RunSearch() => WalkToSearch(RunStartMode.Normal);

    // Go ▾ → Run / Sprint: the same walk with Auto-Combat off, or in Sprint Mode.
    [RelayCommand(CanExecute = nameof(CanRunSearch))]
    private Task RunSearchCombatOff() => WalkToSearch(RunStartMode.CombatOff);

    [RelayCommand(CanExecute = nameof(CanRunSearch))]
    private Task RunSearchSprint() => WalkToSearch(RunStartMode.Sprint);

    private async Task WalkToSearch(RunStartMode mode)
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

        if (AppServices.Current.RefuseStartForMasterSwitch("Walk")) return;
        _movement?.Stop();
        // Close this manager first, then hand off — the route picker (when a
        // shorter gated shortcut exists) opens as its own modeless window rather
        // than stacking on the closing dialog.
        Close();
        await RouteChoicePrompt.WalkAsync(AppServices.Current, target, startMode: mode);
    }

    // ----- close -----------------------------------------------------

    [RelayCommand]
    private void Close()
    {
        _searchDebounce?.Stop();
        _walkFilterDebounce?.Stop();
        _gotoFilterDebounce?.Stop();
        _loops.LoopsChanged -= RebuildLoops;
        _lairSetups.SetupsChanged -= RebuildLairSetups;
        if (_folders is not null) _folders.FoldersChanged -= OnFoldersChanged;
        if (_favorites is not null) _favorites.Changed -= RebuildFavorites;
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
