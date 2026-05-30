using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Shared, source-agnostic base for every per-table tab in the Game
/// Data Browser. Owns the column list, the all-rows / filtered-rows
/// observable pair, the selected-row slot, the search box, and the
/// DataGrid view. Subclasses supply rows via <see cref="PopulateRows"/>
/// — JSON-backed tabs pull from <see cref="GameDataCache"/> (see
/// <see cref="JsonTableSectionViewModel"/>), engine-backed tabs
/// (Triggers / Aliases / Players / Macros / Messages) pull from their
/// runtime services.
/// </summary>
public abstract partial class GameDataTableSectionViewModel : GameDataSectionViewModel
{
    /// <summary>Trailing virtual column name shown on every grid — see <see cref="GameDataRow.SourceTier"/>.</summary>
    public const string UseColumnName = "Use";

    private Control? _view;

    /// <summary>
    /// Data columns in display order. Search hits, sort, and the
    /// right-pane row view all key off this list. The virtual
    /// <see cref="UseColumnName"/> tier column gets appended
    /// automatically by <see cref="DisplayColumns"/>.
    /// </summary>
    public abstract IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Columns rendered in the DataGrid: data columns + the trailing
    /// "Use" tier column. The view's column builder reads from this.
    /// </summary>
    public IReadOnlyList<string> DisplayColumns =>
        Columns.Concat(new[] { UseColumnName }).ToArray();

    /// <summary>Column the search box filters against by default (kept for status-bar display only).</summary>
    public abstract string SearchKeyColumn { get; }

    /// <summary>
    /// Optional per-column display formatters. Keys are column names in
    /// <see cref="Columns"/>; values transform the raw cell string into
    /// the human-readable form rendered in the grid (e.g. <c>1 → "Weapon"</c>,
    /// <c>5 → "Feet"</c>). Subclasses opt in by overriding; the search
    /// filter still runs against the raw value so numeric codes are
    /// findable both ways.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, Func<string?, string?>>? ColumnFormatters => null;

    /// <summary>Every row loaded from the source, original order.</summary>
    public ObservableCollection<GameDataRow> AllRows { get; } = new();

    /// <summary>Rows that survive the current <see cref="SearchText"/> filter.</summary>
    public ObservableCollection<GameDataRow> FilteredRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GameDataRow? _selectedRow;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new GameDataTableSectionView { DataContext = this };

    /// <summary>
    /// Bottom-strip status. Shows the 1-based position of the selected
    /// row out of the table total when something is selected
    /// (<c>"5 of 240 rows"</c>); otherwise just the row count, with the
    /// filtered / unfiltered split (<c>"3 / 240 rows"</c> when a search
    /// filter is active, <c>"240 rows"</c> otherwise).
    /// </summary>
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

    /// <summary>
    /// Subclass hook: append every visible row to <paramref name="rows"/>.
    /// Called on the first activation (see <see cref="OnActivated"/>) and
    /// on every <see cref="Reload"/> trigger.
    /// </summary>
    protected abstract void PopulateRows(ObservableCollection<GameDataRow> rows);

    /// <summary>
    /// Called by <see cref="GameDataBrowserViewModel"/> whenever this
    /// section becomes the selected one. Lets expensive sections
    /// (10k+ rows of MDB-derived JSON) defer their parse + row-build
    /// work until the user actually opens the tab. Base implementation
    /// is a no-op; <see cref="JsonTableSectionViewModel"/> overrides to
    /// trigger the first load.
    /// </summary>
    public virtual void OnActivated() { }

    /// <summary>
    /// Clear + re-populate <see cref="AllRows"/> and re-apply the filter.
    /// Subclasses call this when their source changes (set switch, engine
    /// CollectionChanged, profile reload, etc.).
    /// </summary>
    protected void Reload()
    {
        AllRows.Clear();
        FilteredRows.Clear();
        SelectedRow = null;
        PopulateRows(AllRows);
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        string filter = (SearchText ?? string.Empty).Trim();

        foreach (GameDataRow row in AllRows)
        {
            if (filter.Length == 0 || RowMatches(row, filter))
                FilteredRows.Add(row);
        }
    }

    /// <summary>
    /// A row matches the filter when *any* column's raw value contains
    /// the filter substring (case-insensitive). Raw values drive the
    /// match so numeric codes (e.g. <c>1</c>) are findable even when
    /// the grid renders them via a formatter (<c>"Weapon"</c>).
    /// </summary>
    private bool RowMatches(GameDataRow row, string filter)
    {
        foreach (string column in Columns)
        {
            string? value = row.Get(column);
            if (value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Also match against the Use-tier short label so the user can
        // filter by tier (e.g. typing "Char" surfaces every overridden row).
        return row.SourceTier.ToShortLabel().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Concrete base for MDB-derived tabs. Loads its rows from
/// <see cref="GameDataCache"/>'s active set on construction and on
/// every <see cref="GameDataCache.ActiveSetChanged"/>. Subclasses
/// supply <see cref="TableName"/> + <see cref="GameDataTableSectionViewModel.Columns"/> +
/// <see cref="GameDataTableSectionViewModel.SearchKeyColumn"/>.
/// </summary>
public abstract class JsonTableSectionViewModel : GameDataTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver? _resolver;

    /// <summary>Underlying table name in the active set (e.g. <c>"Monsters"</c>).</summary>
    protected abstract string TableName { get; }

    /// <summary>
    /// Column whose value identifies the record for tier-override
    /// lookup (default: the primary-key column, typically <c>"Number"</c>
    /// on MajorMUD MDB tables). Subclasses can override if the table's
    /// natural key isn't <c>"Number"</c>.
    /// </summary>
    protected virtual string OverrideKeyColumn => "Number";

    private bool _loaded;

    protected JsonTableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _resolver = resolver;
        // ActiveSetChanged invalidates whatever was loaded — but we only
        // re-parse if the tab has already been opened. Tabs that have never
        // been activated stay un-loaded until first activation, dodging the
        // upfront 10-tables-times-thousands-of-rows parse on browser open.
        _cache.ActiveSetChanged += _ =>
        {
            if (_loaded) Reload();
        };
        // NOTE: ctor does NOT call Reload() — that's lazy via OnActivated.
    }

    public override void OnActivated()
    {
        if (_loaded) return;
        _loaded = true;
        // Defer Reload to the next dispatcher tick so it runs *after* the
        // ContentControl constructs our View and the DataGrid builds its
        // columns (DataContextChanged handler in code-behind). Without
        // the defer, rows arrive on a 0-column grid; adding columns later
        // doesn't re-materialise rows — the tab renders blank on first
        // activation. The deferred Add()s emit CollectionChanged events
        // the DataGrid picks up correctly. Tests drain pending posts via
        // Dispatcher.UIThread.RunJobs() to force synchronous completion.
        Dispatcher.UIThread.Post(Reload);
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        JsonDocument? doc = _cache.GetRawTable(TableName);
        if (doc is null) return;

        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = ColumnFormatters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            GameDataRow row = GameDataRow.FromJson(el, Columns, formatters);
            // Per-row tier resolution: look up the record by its primary
            // key column value (typically Number) and ask the resolver
            // which tier owns the highest-priority override, if any.
            if (_resolver is not null)
            {
                string? key = row.Get(OverrideKeyColumn);
                if (!string.IsNullOrEmpty(key))
                    row.SourceTier = _resolver.GetGameDataSourceTier(TableName, key);
            }
            rows.Add(row);
        }
    }
}

/// <summary>
/// One row loaded from a game-data source. Holds the column-name →
/// string-rendered-value dictionary. Numbers / nulls / nested objects
/// are all collapsed to strings at parse time so the view only has to
/// deal with one shape.
/// </summary>
public sealed class GameDataRow
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    /// <summary>Data cells in display order; the trailing "Use" virtual cell is appended by the view.</summary>
    public IReadOnlyList<GameDataCell> Cells { get; }

    /// <summary>
    /// Highest-priority tier that owns this record. Drives the Game
    /// Data Browser's "Use" column label and the edit dialog's "Use:"
    /// dropdown initial value.
    /// </summary>
    public SettingsTier SourceTier { get; set; } = SettingsTier.Defaults;

    /// <summary>Short tier label rendered in the virtual "Use" column.</summary>
    public string UseLabel => SourceTier.ToShortLabel();

    private GameDataRow(IReadOnlyDictionary<string, string?> values, IReadOnlyList<GameDataCell> cells)
    {
        _values = values;
        Cells = cells;
    }

    /// <summary>Read a column value by name. Returns <c>null</c> if the column wasn't in the source row.</summary>
    public string? Get(string column)
        => _values.TryGetValue(column, out string? value) ? value : null;

    /// <summary>
    /// Build a row from a JSON element. Columns missing from the source
    /// render as <c>null</c> in the resulting row so subclasses see a
    /// uniform shape regardless of schema drift. The raw cell value
    /// drives <see cref="Get"/> (so search/filter sees the underlying
    /// data) while the optional <paramref name="formatters"/> map shapes
    /// the *displayed* value in <see cref="Cells"/>.
    /// </summary>
    public static GameDataRow FromJson(
        JsonElement element,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<GameDataCell> cells = new(columns.Count);

        foreach (string column in columns)
        {
            string? raw = ReadValue(element, column);
            values[column] = raw;
            string? display = (formatters is not null && formatters.TryGetValue(column, out Func<string?, string?>? fmt))
                ? fmt(raw)
                : raw;
            cells.Add(new GameDataCell(column, display));
        }
        return new GameDataRow(values, cells);
    }

    /// <summary>
    /// Build a row from an arbitrary column-name → raw-value dictionary
    /// (engine-backed tabs that don't have an MDB JSON source). The same
    /// formatter contract as <see cref="FromJson"/> applies — formatted
    /// strings render in the grid, raw strings drive search.
    /// </summary>
    public static GameDataRow FromDictionary(
        IReadOnlyDictionary<string, string?> source,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<GameDataCell> cells = new(columns.Count);

        foreach (string column in columns)
        {
            source.TryGetValue(column, out string? raw);
            values[column] = raw;
            string? display = (formatters is not null && formatters.TryGetValue(column, out Func<string?, string?>? fmt))
                ? fmt(raw)
                : raw;
            cells.Add(new GameDataCell(column, display));
        }
        return new GameDataRow(values, cells);
    }

    private static string? ReadValue(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out JsonElement el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String    => el.GetString(),
            JsonValueKind.Number    => el.ToString(),
            JsonValueKind.True      => "true",
            JsonValueKind.False     => "false",
            _                        => el.ToString(),
        };
    }
}

/// <summary>One column / value pair on a <see cref="GameDataRow"/>.</summary>
public sealed record GameDataCell(string Column, string? Value);
