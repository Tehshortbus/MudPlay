using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Views.GameData.Tables;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.GameData.Tables;

// Shared, source-agnostic base for every per-table tab in the Game Data Browser. Owns the
// column list, the all-rows / filtered-rows observable pair, the selected-row slot, the
// search box, and the DataGrid view. Subclasses supply rows via PopulateRows — JSON-backed
// tabs pull from GameDataCache (see JsonTableSectionViewModel), engine-backed tabs
// (Triggers / Aliases / Players / Macros / Messages) pull from their runtime services.
public abstract partial class GameDataTableSectionViewModel : GameDataSectionViewModel
{
    // Trailing virtual column name shown on every grid — see GameDataRow.SourceTier.
    public const string UseColumnName = "Use";

    // Column key for the per-record "Toggles" summary — a compact list of the
    // user-settable flags this character has turned on for the row (the Monsters /
    // Items / Players tabs each populate it from their own overlay / customization).
    // Shared so the Columns entry and the computed-cell key can't drift apart.
    public const string TogglesColumn = "Toggles";

    private Control? _view;

    // Data columns in display order. Search hits, sort, and the right-pane row view all key
    // off this list. The virtual UseColumnName tier column gets appended automatically by
    // DisplayColumns.
    public abstract IReadOnlyList<string> Columns { get; }

    // True when the trailing virtual "Use" tier column should render. MDB-overlay tables
    // (Monsters / Items / Spells / Messages) keep it — the tier badge tells the user which
    // layer owns each row. Engine-backed tables (Macros / Triggers / Aliases / Players) hide
    // it: every row lives at one tier so the badge would always read the same value and just
    // adds visual noise.
    public virtual bool ShowUseColumn => true;

    // Optional friendly grid-header overrides, keyed by column name in Columns. When a column
    // is present here the DataGrid renders the mapped label as its header while the column
    // name still drives the value binding, search, sort, and formatters. Lets a table show
    // "AC" / "HP Regen" while keeping the raw MDB keys (ArmourClass / HPRegen) as the data
    // identity. Columns absent from the map fall back to their raw name.
    public virtual IReadOnlyDictionary<string, string>? ColumnHeaders => null;

    // ----- Column picker (per-character visible-column choices) -----------------
    // A dropdown at the tab's top-right lets the user check/uncheck which columns
    // show. The pool is every column this table MATERIALISES (the shown columns
    // plus the filter-only ones — e.g. a monster's per-element resists), so hidden
    // data facets can be surfaced without touching the underlying rows. Choices
    // persist per character (CharacterProfile.TableColumnLayouts, keyed by Title).

    // Show the picker button on this table. OFF here so the engine-backed utility
    // tabs (Macros / Triggers / Aliases / Players / Messages / Unrecognized Lines /
    // Quest Flags) keep their fixed columns; the MDB-derived tables turn it on (see
    // JsonTableSectionViewModel) since those carry the rich record columns worth
    // customising and the filter-only facets worth surfacing.
    public virtual bool ShowColumnPicker => false;

    // Stable key the layout persists under. Title is unique per tab.
    protected virtual string ColumnLayoutKey => Title;

    // Let the user drag column headers into their own order. The order persists with
    // the visible set: the saved layout list is kept in display order, so one list
    // carries both "which columns" and "in what order". Off by default; the big record
    // tables (Monsters / Items / Spells) turn it on.
    public virtual bool AllowColumnReorder => false;

    // The saved display order (visible keys, first to last); empty = canonical order.
    private List<string> _order = new();

    // Raised when the visible set changes (a toggle or a reset) so the view rebuilds
    // the DataGrid's columns.
    public event Action? ColumnsChanged;

    private ObservableCollection<TableColumnChoice>? _columnChoices;
    private bool _profileHookInstalled;
    // While a bulk update (reset / profile reload) sets many IsVisible values, the
    // per-item toggle callback is suppressed so we persist + rebuild the grid ONCE
    // at the end instead of once per column.
    private bool _bulkUpdating;

    // Picker items — one per materialised column, in canonical cell order (shown
    // columns first, then filter-only). Lazily built so Title + the loaded profile
    // are ready. The first data column is pinned (the row's identity) so the grid
    // can never be emptied.
    public ObservableCollection<TableColumnChoice> ColumnChoices
    {
        get
        {
            if (_columnChoices is null) BuildColumnChoices();
            return _columnChoices!;
        }
    }

    private void BuildColumnChoices()
    {
        _order = LoadSavedOrder();
        HashSet<string> saved    = LoadSavedVisible();
        HashSet<string> defaults = new(Columns, StringComparer.OrdinalIgnoreCase);
        bool haveSaved = saved.Count > 0;

        ObservableCollection<TableColumnChoice> choices = new();
        bool first = true;
        foreach (string key in ValueColumns)   // Columns first, then FilterOnlyColumns
        {
            bool pinned = first;
            first = false;
            bool visible = pinned || (haveSaved ? saved.Contains(key) : defaults.Contains(key));
            choices.Add(new TableColumnChoice(key, HeaderLabel(key), pinned, visible, OnColumnToggled));
        }
        _columnChoices = choices;

        if (!_profileHookInstalled && AppServices.Current is { } svc)
        {
            _profileHookInstalled = true;
            svc.Profile.ProfileLoaded += OnProfileLoadedReloadColumns;
        }
    }

    private string HeaderLabel(string key)
        => ColumnHeaders is { } h && h.TryGetValue(key, out string? friendly) ? friendly : key;

    // Columns to render, each paired with its index into the row's Cells (materialised
    // in ValueColumns order, so display order never misaligns a cell). Saved order
    // first; a visible column the saved order doesn't name (just switched on, or new
    // in this version) follows at the end in canonical order. Drives the grid build.
    public IReadOnlyList<VisibleColumn> BuildVisibleColumns()
    {
        List<VisibleColumn> result = new();
        IReadOnlyList<string> value = ValueColumns;
        IEnumerable<TableColumnChoice> ordered = ColumnChoices.Where(c => c.IsVisible);
        if (AllowColumnReorder && _order.Count > 0)
            ordered = ordered.OrderBy(c => IndexOfColumn(_order, c.Key) is var i and >= 0 ? i : int.MaxValue);
        foreach (TableColumnChoice c in ordered)
        {
            int idx = IndexOfColumn(value, c.Key);
            if (idx >= 0) result.Add(new VisibleColumn(c.Key, idx, HeaderLabel(c.Key)));
        }
        return result;
    }

    private static int IndexOfColumn(IReadOnlyList<string> list, string key)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // The user dragged a header: keys are the data columns in their new display order.
    // Saved at once; no rebuild — the grid already shows the new order.
    public void SetColumnOrder(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!AllowColumnReorder) return;
        _order = keys.ToList();
        PersistVisible();
    }

    private void OnColumnToggled()
    {
        if (_bulkUpdating) return;
        PersistVisible();
        ColumnsChanged?.Invoke();
    }

    // Restore this table to its built-in default columns and forget the saved layout
    // so it tracks future default changes rather than pinning to today's set.
    [RelayCommand]
    private void ResetColumns()
    {
        if (_columnChoices is null) return;
        HashSet<string> defaults = new(Columns, StringComparer.OrdinalIgnoreCase);
        _bulkUpdating = true;
        try
        {
            foreach (TableColumnChoice c in _columnChoices)
                if (!c.Pinned) c.IsVisible = defaults.Contains(c.Key);
        }
        finally { _bulkUpdating = false; }
        _order = new();
        ClearSavedVisible();
        ColumnsChanged?.Invoke();
    }

    private void OnProfileLoadedReloadColumns(Models.Profile.CharacterProfile _)
    {
        if (_columnChoices is null) return;
        _order = LoadSavedOrder();
        HashSet<string> saved    = LoadSavedVisible();
        HashSet<string> defaults = new(Columns, StringComparer.OrdinalIgnoreCase);
        bool haveSaved = saved.Count > 0;
        _bulkUpdating = true;
        try
        {
            foreach (TableColumnChoice c in _columnChoices)
                if (!c.Pinned) c.IsVisible = haveSaved ? saved.Contains(c.Key) : defaults.Contains(c.Key);
        }
        finally { _bulkUpdating = false; }
        // Reflecting the freshly-loaded profile — rebuild the grid but don't persist
        // (nothing the user changed).
        ColumnsChanged?.Invoke();
    }

    // A saved layout counts only on a table whose columns the user can still change. The
    // picker once sat on every tab, so a profile can hold a layout for a table that has since
    // lost it; honouring that would hide every column added later with no way to bring it back.
    private bool HonoursSavedLayout => ShowColumnPicker || AllowColumnReorder;

    private HashSet<string> LoadSavedVisible()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (HonoursSavedLayout
            && AppServices.Current?.Profile.Current?.TableColumnLayouts is { } map
            && map.TryGetValue(ColumnLayoutKey, out List<string>? cols) && cols is not null)
            foreach (string c in cols) set.Add(c);
        return set;
    }

    private List<string> LoadSavedOrder()
    {
        if (HonoursSavedLayout
            && AppServices.Current?.Profile.Current?.TableColumnLayouts is { } map
            && map.TryGetValue(ColumnLayoutKey, out List<string>? cols) && cols is not null)
            return cols.ToList();
        return new List<string>();
    }

    // Saved in display order (see AllowColumnReorder) — a toggle keeps the order the
    // user arranged rather than snapping back to canonical.
    private void PersistVisible()
    {
        if (AppServices.Current?.Profile is not { Current: { } profile } profileService) return;
        List<string> visible = _columnChoices is null
            ? new List<string>()
            : BuildVisibleColumns().Select(c => c.Key).ToList();
        _order = visible;
        (profile.TableColumnLayouts ??= new())[ColumnLayoutKey] = visible;
        profileService.Save();
    }

    private void ClearSavedVisible()
    {
        if (AppServices.Current?.Profile is not { Current: { } profile } profileService) return;
        if (profile.TableColumnLayouts?.Remove(ColumnLayoutKey) == true)
            profileService.Save();
    }

    public override void Dispose()
    {
        if (_profileHookInstalled && AppServices.Current is { } svc)
        {
            svc.Profile.ProfileLoaded -= OnProfileLoadedReloadColumns;
            _profileHookInstalled = false;
        }
        base.Dispose();
    }

    // Optional tooltip for the row-filter box — a section overrides it to advertise a
    // richer query it accepts (e.g. Items' auto-* / stash flag keywords). Null = no tip.
    public virtual string? FilterHint => null;

    // Optional muted-info banner shown directly under the tab header, for a section's
    // one-liner note to the user (e.g. Aliases: where an alias expands and where it
    // doesn't). null hides the banner row.
    public virtual string? BannerText => null;

    // Optional per-column display formatters. Keys are column names in Columns; values
    // transform the raw cell string into the human-readable form rendered in the grid
    // (e.g. 1 → "Weapon", 5 → "Feet"). Subclasses opt in by overriding; the search filter
    // still runs against the raw value so numeric codes are findable both ways.
    protected virtual IReadOnlyDictionary<string, Func<string?, string?>>? ColumnFormatters => null;

    // Every row loaded from the source, original order. Set in one shot on Reload rather than
    // appended row-by-row so the DataGrid sees a single PropertyChanged + re-bind instead of N
    // CollectionChanged events. Critical at 27k-row table sizes (Rooms in MajorMUD v1.11p),
    // where per-row notifications were the hot loop.
    private ObservableCollection<GameDataRow> _allRows = new();
    public ObservableCollection<GameDataRow> AllRows
    {
        get => _allRows;
        private set => SetProperty(ref _allRows, value);
    }

    // Rows that survive the current SearchText filter.
    private ObservableCollection<GameDataRow> _filteredRows = new();
    public ObservableCollection<GameDataRow> FilteredRows
    {
        get => _filteredRows;
        private set => SetProperty(ref _filteredRows, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GameDataRow? _selectedRow;

    // Live mirror of the DataGrid's multi-selection set. The view's code-behind syncs this
    // whenever RowsGrid.SelectionChanged fires (Avalonia's DataGrid exposes SelectedItems as a
    // non-bindable IList, so the sync is imperative). RemoveSelected handlers iterate this set
    // instead of just SelectedRow so Ctrl-/Shift-selecting multiple rows + clicking Remove
    // drops them all in one go.
    public System.Collections.ObjectModel.ObservableCollection<GameDataRow> SelectedRows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new GameDataTableSectionView { DataContext = this };

    // Bottom-strip status. Shows the 1-based position of the selected row out of the table
    // total when something is selected ("5 of 240 rows"); otherwise just the row count, with
    // the filtered / unfiltered split ("3 / 240 rows" when a search filter is active,
    // "240 rows" otherwise).
    public string StatusText
    {
        get
        {
            int total = AllRows.Count;
            int visible = FilteredRows.Count;

            if (SelectedRow is not null)
            {
                int index = FilteredRows.IndexOf(SelectedRow);
                if (index >= 0) return $"{index + 1} of {total} rows";
            }

            return total == visible ? $"{total} rows" : $"{visible} / {total} rows";
        }
    }

    // Subclass hook: append every visible row to rows. Called on the first activation (see
    // OnActivated) and on every Reload trigger. Uses IList (not ObservableCollection) so the
    // row-build can run against a plain List — caller wraps the finished list in a fresh
    // ObservableCollection once for the bulk-replace.
    protected abstract void PopulateRows(IList<GameDataRow> rows);

    // Extra columns to carry in each row's value/display maps but NOT render as grid
    // columns — used for filter-only fields (e.g. Monsters' Alignment dropdown reads
    // "Align" without the table showing an Alignment column, and the AC / DR filters
    // read the raw fields even though the grid shows them combined). The grid still
    // builds its columns from Columns alone; these are appended after them so the
    // visible columns keep their cell indices (the sort comparer is index-based).
    protected virtual IReadOnlyList<string> FilterOnlyColumns => System.Array.Empty<string>();

    // Columns whose values are materialised on each row = visible columns plus any
    // filter-only ones. Cached; visible columns come first so their indices are stable.
    private IReadOnlyList<string>? _valueColumns;
    protected IReadOnlyList<string> ValueColumns => _valueColumns ??=
        FilterOnlyColumns.Count == 0
            ? Columns
            : System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(Columns, FilterOnlyColumns));

    // Called on the UI thread once AllRows is materialised (both Reload and the async
    // LoadAsync path). Subclasses rebuild data-derived UI state here — e.g. category-filter
    // option lists computed from the loaded rows. PopulateRows itself runs on a worker thread
    // under LoadAsync, so it must never touch observable collections; this hook is where that
    // work belongs.
    protected virtual void OnRowsLoaded() { }

    // Called by GameDataBrowserViewModel whenever this section becomes the selected one. Lets
    // expensive sections (10k+ rows of MDB-derived JSON) defer their parse + row-build work
    // until the user actually opens the tab. Base implementation is a no-op;
    // JsonTableSectionViewModel overrides to trigger the first load.
    public virtual void OnActivated() { }

    // True once Reload or LoadAsync has completed at least once. Subclasses that re-react to
    // source changes (set switch, profile reload) check this to skip work for cold tabs the
    // user has never opened.
    protected bool IsLoaded { get; private set; }

    // Rebuild AllRows + FilteredRows from the source and reapply the filter. Builds a fresh
    // List via PopulateRows, wraps it in a new ObservableCollection, and assigns to the
    // properties — one PropertyChanged each instead of N CollectionChanged events. Subclasses
    // call this when their source changes (set switch, engine CollectionChanged, profile
    // reload).
    protected void Reload()
    {
        SelectedRow = null;
        List<GameDataRow> rows = new();
        PopulateRows(rows);
        AllRows = new ObservableCollection<GameDataRow>(rows);
        OnRowsLoaded();
        ApplyFilter();
        IsLoaded = true;
        OnPropertyChanged(nameof(StatusText));
    }

    // Async variant of Reload: row-build runs on a worker thread, then bulk-replace AllRows
    // back on the UI thread once the parse is done. Keeps the UI responsive on big tables
    // (Rooms reaches 27k+ rows in MajorMUD v1.11p, more on custom-edit realms).
    // JsonTableSectionViewModel.OnActivated fires this through the dispatcher post.
    internal async Task LoadAsync()
    {
        List<GameDataRow> rows = await Task.Run(() =>
        {
            List<GameDataRow> list = new();
            PopulateRows(list);
            return list;
        });
        // Task.Run resumes the continuation on the captured
        // SynchronizationContext (Avalonia dispatcher in app context,
        // none outside it — either way the property writes happen on a
        // single thread per call).
        SelectedRow = null;
        AllRows = new ObservableCollection<GameDataRow>(rows);
        OnRowsLoaded();
        ApplyFilter();
        IsLoaded = true;
        OnPropertyChanged(nameof(StatusText));

        // Cross-section navigation might have queued a selection while
        // rows were still loading (Shops → Rooms double-click hits this
        // path). Apply it now that AllRows is materialised.
        if (_pendingRowSelector is { } selector)
        {
            _pendingRowSelector = null;
            ApplyRowSelector(selector);
        }
    }

    // Pending row predicate from a cross-section navigation request that arrived before this
    // section had loaded its rows. Cleared after LoadAsync applies it.
    private Func<GameDataRow, bool>? _pendingRowSelector;

    // Select the first row matching predicate. Queues the predicate when rows haven't loaded
    // yet — the selection applies as soon as LoadAsync finishes.
    public void SelectRowMatching(Func<GameDataRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!IsLoaded || AllRows.Count == 0)
        {
            _pendingRowSelector = predicate;
            return;
        }
        ApplyRowSelector(predicate);
    }

    // Fired after a programmatic SelectRowMatching lands on a row. The view subscribes to
    // bring the row into view (Avalonia DataGrid doesn't auto-scroll on SelectedItem source
    // changes — we have to call ScrollIntoView explicitly).
    public event Action<GameDataRow>? ScrollToRowRequested;

    private void ApplyRowSelector(Func<GameDataRow, bool> predicate)
    {
        // Prefer matches that survive the current filter; fall back to
        // the full row set when none do so the navigation never lands
        // on "no selection" with the row sitting just past the filter.
        // When a match is found in AllRows but the filter is hiding it,
        // clear the filter so the user actually sees the selected row.
        foreach (GameDataRow row in FilteredRows)
        {
            if (predicate(row))
            {
                SelectedRow = row;
                ScrollToRowRequested?.Invoke(row);
                return;
            }
        }
        foreach (GameDataRow row in AllRows)
        {
            if (predicate(row))
            {
                // Filter is hiding the row — clear it so the target is
                // visible. The selection latches on the now-unfiltered
                // row set.
                if (!string.IsNullOrEmpty(SearchText)) SearchText = string.Empty;
                SelectedRow = row;
                ScrollToRowRequested?.Invoke(row);
                return;
            }
        }
    }

    // The Filter… box searches when Enter is pressed (ApplySearch), not on every
    // keystroke — a full pass over a big table per character made typing lag. Emptying
    // the box (its clear button, or a navigation that clears it) re-shows every row at
    // once: that pass is free, the unfiltered view aliases AllRows.
    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    // Enter in the Filter… box.
    [RelayCommand]
    private void ApplySearch()
    {
        ApplyFilter();
        GuideActions.Report(GuideActions.BrowserFiltered);
        OnPropertyChanged(nameof(StatusText));
    }

    // Callable by subclasses so a richer filter surface (e.g. the Monsters
    // multi-field panel) can re-filter when one of its fields changes.
    protected void ApplyFilter()
    {
        string filter = (SearchText ?? string.Empty).Trim();
        if (filter.Length == 0 && !HasExtraFilter)
        {
            // Unfiltered — alias the AllRows collection directly so the
            // DataGrid sees identical content with zero extra allocation.
            // Both collections are treated as immutable snapshots between
            // Reloads, so sharing is safe.
            FilteredRows = AllRows;
            return;
        }

        // Snapshot the active panel filters once for the pass, instead of re-walking
        // every group's filters for every row.
        _activeRanges = AllRangeFilters.Where(r => r.IsActive).ToArray();
        _activeBools = AllBoolFilters.Where(b => b.IsActive).ToArray();
        _activeCategories = AllCategoryFilters.Where(c => c.IsActive).ToArray();

        List<GameDataRow> matched = new();
        foreach (GameDataRow row in AllRows)
        {
            if (RowMatches(row, filter))
                matched.Add(row);
        }
        FilteredRows = new ObservableCollection<GameDataRow>(matched);
    }

    private RangeFilter[] _activeRanges = Array.Empty<RangeFilter>();
    private BoolFilter[] _activeBools = Array.Empty<BoolFilter>();
    private CategoryFilter[] _activeCategories = Array.Empty<CategoryFilter>();

    // ----- Curation filter panel (subclasses populate FilterGroups; empty = no panel) -----
    // A sidebar beside the grid: min/max ranges, checkboxes, and dropdowns, grouped
    // into labelled sections. The panel CURATES the list (which rows qualify) as a
    // deliberate act — editing a control is a PENDING edit that does nothing until
    // "Apply" is pressed (CommitPanelFilters copies each box's value over). Distinct
    // from the "Filter…" text box (applied on Enter), which FINDS a specific row within the
    // curated list. Empty by default, so tables with no groups render no sidebar.
    public ObservableCollection<FilterGroup> FilterGroups { get; } = new();
    public bool HasFilterPanel => FilterGroups.Count > 0;

    private IEnumerable<RangeFilter> AllRangeFilters => FilterGroups.SelectMany(g => g.Ranges);
    private IEnumerable<BoolFilter> AllBoolFilters => FilterGroups.SelectMany(g => g.Bools);
    private IEnumerable<CategoryFilter> AllCategoryFilters => FilterGroups.SelectMany(g => g.Categories);

    // Commit every pending panel edit and re-filter. The "Apply" button.
    [RelayCommand]
    private void ApplyFilters()
    {
        CommitPanelFilters();
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void CommitPanelFilters()
    {
        foreach (RangeFilter r in AllRangeFilters) r.Commit();
        foreach (BoolFilter b in AllBoolFilters) b.Commit();
        foreach (CategoryFilter c in AllCategoryFilters) c.Commit();
    }

    // Drives ApplyFilter's "run even with an empty text box" path (see ApplyFilter).
    protected bool HasExtraFilter
    {
        get
        {
            foreach (RangeFilter r in AllRangeFilters) if (r.IsActive) return true;
            foreach (BoolFilter b in AllBoolFilters) if (b.IsActive) return true;
            foreach (CategoryFilter c in AllCategoryFilters) if (c.IsActive) return true;
            return false;
        }
    }

    // Empties every panel filter (and the text box) and re-filters. The "Reset" button.
    [RelayCommand]
    private void ResetFilters()
    {
        foreach (RangeFilter r in AllRangeFilters) r.Clear();
        foreach (BoolFilter b in AllBoolFilters) b.Clear();
        foreach (CategoryFilter c in AllCategoryFilters) c.Clear();
        SearchText = string.Empty;   // OnSearchTextChanged re-applies (no-op if already empty)
        CommitPanelFilters();        // make the cleared boxes take effect
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    // Panel filters (range + bool + category), all AND'd together. Empty groups pass
    // everything, so non-panel tables are unaffected. Range filters test the leading
    // integer of the raw cell; bool filters test the raw value via their predicate;
    // category filters match the rendered display value.
    private bool PassesPanelFilters(GameDataRow row)
    {
        foreach (RangeFilter r in _activeRanges)
            if (!TryLeadingInt(row.Get(r.Column), out int v) || !r.Passes(v))
                return false;
        foreach (BoolFilter b in _activeBools)
            if (!b.Passes(row.Get(b.Column)))
                return false;
        foreach (CategoryFilter c in _activeCategories)
            if (!c.Passes(row.GetDisplay(c.Column)))
                return false;
        return true;
    }

    // A row matches the text filter when any column's raw value contains the substring
    // (case-insensitive), so numeric codes are findable even when the grid renders them
    // via a formatter. Virtual so a tab with a richer match (Rooms' "map,room" query)
    // can intercept before this substring pass.

    // Leading signed integer of a cell string — "12345" → 12345, "2hp@90s" → 2,
    // "10/42/8" → 10, "1–11" → 1. False when there's no leading number.
    private static bool TryLeadingInt(string? s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        int i = s[0] == '-' ? 1 : 0;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        if (i == start) return false;
        if (!int.TryParse(s.AsSpan(start, i - start), out value)) return false;
        if (s[0] == '-') value = -value;
        return true;
    }

    protected virtual bool RowMatches(GameDataRow row, string filter)
    {
        if (!PassesPanelFilters(row)) return false;
        // Empty text box + active panel: the panel alone decides the match.
        if (filter.Length == 0) return true;
        // One string per row holds everything the box searches — each shown column's raw
        // value plus every cell's display label — built the first time the row is
        // searched and reused after, so a search is one substring test per row. The
        // display labels make a visible enum value ("Weapon", "Plate", "Lawful Good")
        // searchable, not just its raw MDB code; the raw values keep numeric text
        // ("300000") matching a thousands-grouped cell. A separator no one types keeps a
        // hit from spanning two cells.
        row.SearchIndex ??= BuildSearchIndex(row);
        if (row.SearchIndex.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        // Also match against the Use-tier short label so the user can
        // filter by tier (e.g. typing "Char" surfaces every overridden row).
        return row.SourceTier.ToShortLabel().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildSearchIndex(GameDataRow row)
    {
        System.Text.StringBuilder sb = new();
        foreach (string column in Columns)
            if (row.Get(column) is { Length: > 0 } value) sb.Append(value).Append('\u0001');
        for (int i = 0; i < row.CellCount; i++)
            if (row.DisplayAt(i) is { Length: > 0 } display) sb.Append(display).Append('\u0001');
        return sb.ToString();
    }

    // Join the labels of the enabled toggles into one compact, comma-separated cell
    // ("Collect, Stash"); null when none are on so the grid renders it blank. Backs the
    // shared "Toggles" column — a per-record readout of the user-settable flags this
    // character has configured, so they're visible at a glance instead of only filterable.
    internal static string? FormatToggleSummary(params (bool On, string Label)[] toggles)
    {
        string joined = string.Join(", ", toggles.Where(t => t.On).Select(t => t.Label));
        return joined.Length == 0 ? null : joined;
    }
}

// Concrete base for MDB-derived tabs. Loads its rows from GameDataCache's active set on
// construction and on every GameDataCache.ActiveSetChanged. Subclasses supply TableName +
// Columns + SearchKeyColumn.
public abstract class JsonTableSectionViewModel : GameDataTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver? _resolver;

    // JSON-backed sections all belong in the browser's MDB-derived tables group.
    public override bool ShowInTableGroup => true;

    // MDB-derived tables carry the rich record columns (and filter-only facets like a
    // monster's per-element resists) worth customising, so they get the column picker;
    // engine-backed utility tabs keep it off.
    public override bool ShowColumnPicker => true;

    // Underlying table name in the active set (e.g. "Monsters").
    protected abstract string TableName { get; }

    // Row-level filter applied while populating from the raw table. Default keeps every
    // row; a section (e.g. Monsters, which leaves out the rows the game marks out of play)
    // overrides to include only the rows it wants without duplicating the whole populate loop.
    protected virtual bool IncludeRow(GameRow element) => true;

    // Column whose value identifies the record for tier-override lookup (default: the
    // primary-key column, typically "Number" on MajorMUD MDB tables). Subclasses can override
    // if the table's natural key isn't "Number".
    protected virtual string OverrideKeyColumn => "Number";

    // Stored as a field so Dispose can unsubscribe — without this the
    // GameDataCache singleton's event roots every JsonTableSectionViewModel
    // ever created (leaking section VMs + their cached row collections +
    // their lazy-built Views across every browser open).
    private readonly Action<string?> _activeSetHandler;

    protected JsonTableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _resolver = resolver;
        // ActiveSetChanged invalidates whatever was loaded — but we only
        // re-parse if the tab has already been opened. Tabs that have never
        // been activated stay un-loaded until first activation, dodging the
        // upfront 10-tables-times-thousands-of-rows parse on browser open.
        _activeSetHandler = _ =>
        {
            if (IsLoaded) Reload();
        };
        _cache.ActiveSetChanged += _activeSetHandler;
        // NOTE: ctor does NOT call Reload() — that's lazy via OnActivated.
    }

    public override void Dispose()
    {
        _cache.ActiveSetChanged -= _activeSetHandler;
        base.Dispose();
    }

    public override void OnActivated()
    {
        if (IsLoaded) return;
        // Defer to the next dispatcher tick so the parse runs *after* the
        // ContentControl constructs our View and the DataGrid builds its
        // columns (DataContextChanged handler in code-behind). Without
        // the defer, rows would arrive on a 0-column grid and never
        // materialise — the tab would render blank on first activation.
        // LoadAsync flips IsLoaded once the rows land.
        Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
        => AddTableRows(rows, TableName, IncludeRow, ComputeRowCells);

    // Append the rows of one table that pass `include`, shaped with this section's
    // columns and formatters. PopulateRows uses it for the section's own table; a derived
    // view that gathers rows from more than one table (Unobtainable) calls it once per table.
    protected void AddTableRows(
        IList<GameDataRow> rows,
        string tableName,
        Func<GameRow, bool> include,
        Func<GameRow, IReadOnlyDictionary<string, string?>?> computeCells)
    {
        GameTable? doc = _cache.GetTable(tableName);
        if (doc is null) return;

        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = ColumnFormatters;
        foreach (GameRow el in doc.Rows)
        {
            if (!include(el)) continue;
            // Sections may inject synthesised cells that aren't backed
            // by a real MDB field (e.g. Races / Classes synthesise an
            // "Abilities" column from Abil-N / AbilVal-N pairs).
            IReadOnlyDictionary<string, string?>? computed = computeCells(el);
            GameDataRow row = GameDataRow.FromRow(el, ValueColumns, formatters, computed);
            // Per-row tier resolution: look up the record by its primary
            // key column value (typically Number) and ask the resolver
            // which tier owns the highest-priority override, if any.
            if (_resolver is not null)
            {
                string? key = row.Get(OverrideKeyColumn);
                if (!string.IsNullOrEmpty(key))
                    row.SourceTier = _resolver.GetGameDataSourceTier(tableName, key);
            }
            rows.Add(row);
        }
    }

    // Optional per-row computed-cell hook. Returned values are merged into the row, taking
    // precedence over any same-named cell of the table. Use this for columns that aren't backed by
    // a real MDB field — e.g. the Race / Class tabs synthesise an "Abilities" column from each
    // row's Abil-N / AbilVal-N pairs. Default implementation returns null (no extras).
    protected virtual IReadOnlyDictionary<string, string?>? ComputeRowCells(GameRow element) => null;
}

// One row loaded from a game-data source: each column's value as a string. Numbers /
// nulls / nested objects are all collapsed to strings at parse time so the view only
// has to deal with one shape.
//
// Kept lean, because a table is tens of thousands of these (57,000 rooms): a row is
// two arrays, the raw values and the displayed ones (the same array when no formatter
// changed anything), indexed by a column map every row of the table shares. A
// dictionary and a cell object per value per row was 100 MB for Rooms alone, and
// building it kept the garbage collector pausing the whole client while a table
// loaded. Cell objects are made only for the rows the grid actually shows.
public sealed class GameDataRow
{
    private readonly RowShape _shape;
    private readonly string?[] _raw;
    private readonly string?[] _display;
    private GameDataCell[]? _cells;

    // Data cells in display order; the trailing "Use" virtual cell is appended by the
    // view. Built on first use: the grid binds these for the rows it realizes. Code
    // that walks every row (sorting, the search index) reads DisplayAt instead.
    public IReadOnlyList<GameDataCell> Cells => _cells ??= BuildCells();

    public int CellCount => _display.Length;

    // The displayed (formatter-applied) value of the cell at index, in Cells order.
    public string? DisplayAt(int index) => _display[index];

    // Highest-priority tier that owns this record. Drives the Game Data Browser's "Use" column
    // label and the edit dialog's "Use:" dropdown initial value.
    public SettingsTier SourceTier { get; set; } = SettingsTier.Defaults;

    // Short tier label rendered in the virtual "Use" column.
    public string UseLabel => SourceTier.ToShortLabel();

    // The Filter… box's search text for this row (see RowMatches) — built on first search
    // and kept, since a row's values don't change once loaded; a reload builds new rows.
    public string? SearchIndex { get; set; }

    // Opaque per-section payload: a direct handle back to the source record after the user
    // double-clicks. Lets a section avoid the Id-from-cells dance when display columns don't
    // include the identity fields (the Messages table shows "Lines/Preview" summaries, not
    // the raw message text the Id is hashed from).
    public object? Tag { get; set; }

    private GameDataRow(RowShape shape, string?[] raw, string?[] display)
    {
        _shape = shape;
        _raw = raw;
        _display = display;
    }

    // Read a column value by name. Returns null if the column wasn't in the source row.
    public string? Get(string column)
        => _shape.Index.TryGetValue(column, out int i) ? _raw[i] : null;

    // Read a column's *display* value (formatter-applied), as shown in the grid.
    // Category filters match on this so their dropdowns show "Living"/"Undead" or
    // "Lawful Good" rather than the raw MDB codes; range filters use Get (raw
    // numeric) instead.
    public string? GetDisplay(string column)
        => _shape.Index.TryGetValue(column, out int i) ? _display[i] : null;

    private GameDataCell[] BuildCells()
    {
        GameDataCell[] cells = new GameDataCell[_display.Length];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new GameDataCell(_shape.Columns[i], _display[i]);
        return cells;
    }

    // Build a row from a table row. Columns missing from the source render as null in the
    // resulting row so subclasses see a uniform shape regardless of schema drift. The raw cell
    // value drives Get (so search/filter sees the underlying data) while the optional
    // formatters map shapes the displayed value in Cells.
    public static GameDataRow FromRow(
        GameRow element,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null,
        IReadOnlyDictionary<string, string?>? computedCells = null)
    {
        string?[] raw = new string?[columns.Count];
        for (int i = 0; i < raw.Length; i++)
        {
            // Computed cells take precedence over the table's own when present —
            // sections use this to surface synthesised columns ("Abilities")
            // that aren't backed by a real MDB field.
            string column = columns[i];
            raw[i] = computedCells is not null && computedCells.TryGetValue(column, out string? computed)
                ? computed
                : ReadValue(element, column);
        }
        return new GameDataRow(RowShape.Of(columns), raw, Format(raw, columns, formatters));
    }

    // Build a row from an arbitrary column-name → raw-value dictionary (engine-backed tabs
    // that don't have an MDB table behind them). The same formatter contract as FromRow applies —
    // formatted strings render in the grid, raw strings drive search.
    public static GameDataRow FromDictionary(
        IReadOnlyDictionary<string, string?> source,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null)
    {
        string?[] raw = new string?[columns.Count];
        for (int i = 0; i < raw.Length; i++)
        {
            source.TryGetValue(columns[i], out string? value);
            raw[i] = value;
        }
        return new GameDataRow(RowShape.Of(columns), raw, Format(raw, columns, formatters));
    }

    // The displayed values: raw itself unless a formatter gives a column something else.
    private static string?[] Format(
        string?[] raw, IReadOnlyList<string> columns, IReadOnlyDictionary<string, Func<string?, string?>>? formatters)
    {
        if (formatters is null || formatters.Count == 0) return raw;
        string?[]? display = null;
        for (int i = 0; i < raw.Length; i++)
        {
            if (!formatters.TryGetValue(columns[i], out Func<string?, string?>? format)) continue;
            string? shown = format(raw[i]);
            if (ReferenceEquals(shown, raw[i])) continue;
            display ??= (string?[])raw.Clone();
            display[i] = shown;
        }
        return display ?? raw;
    }

    // A number comes back as the file wrote it ("7.0" stays "7.0"); the table hands
    // out one shared string per small whole number, which is what most cells are.
    private static string? ReadValue(GameRow row, string column)
    {
        if (!row.TryGetProperty(column, out GameValue el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String    => el.GetString(),
            JsonValueKind.True      => "true",
            JsonValueKind.False     => "false",
            _                        => el.ToString(),
        };
    }

    // A table's columns and where each sits, shared by all its rows. Keyed by the
    // column list itself: a section hands every row the same list.
    private sealed class RowShape
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<string>, RowShape> Shapes = new();

        public IReadOnlyList<string> Columns { get; }
        public Dictionary<string, int> Index { get; }

        private RowShape(IReadOnlyList<string> columns)
        {
            Columns = columns;
            Index = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++) Index.TryAdd(columns[i], i);
        }

        public static RowShape Of(IReadOnlyList<string> columns) => Shapes.GetValue(columns, c => new RowShape(c));
    }
}

// One column / value pair on a GameDataRow.
public sealed record GameDataCell(string Column, string? Value);
