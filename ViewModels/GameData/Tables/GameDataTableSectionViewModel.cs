using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
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
    private Control? _view;

    /// <summary>
    /// Columns to surface, in display order. Search hits, sort, and
    /// the right-pane row view all key off this list.
    /// </summary>
    public abstract IReadOnlyList<string> Columns { get; }

    /// <summary>Column the search box filters against (substring match).</summary>
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

    /// <summary>Bottom-strip status — row count + selected row pointer.</summary>
    public string StatusText
    {
        get
        {
            int total = AllRows.Count;
            int visible = FilteredRows.Count;
            string countText = total == visible ? $"{total} rows" : $"{visible} / {total} rows";
            string selection = SelectedRow is null ? "" : $"  ·  {SearchKeyColumn} = {SelectedRow.Get(SearchKeyColumn)}";
            return countText + selection;
        }
    }

    /// <summary>
    /// Subclass hook: append every visible row to <paramref name="rows"/>.
    /// Called on construction and on every <see cref="Reload"/> trigger.
    /// </summary>
    protected abstract void PopulateRows(ObservableCollection<GameDataRow> rows);

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
            if (filter.Length == 0 ||
                (row.Get(SearchKeyColumn)?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                FilteredRows.Add(row);
            }
        }
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

    /// <summary>Underlying table name in the active set (e.g. <c>"Monsters"</c>).</summary>
    protected abstract string TableName { get; }

    protected JsonTableSectionViewModel(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => Reload();
        Reload();
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        JsonDocument? doc = _cache.GetRawTable(TableName);
        if (doc is null) return;

        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = ColumnFormatters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            rows.Add(GameDataRow.FromJson(el, Columns, formatters));
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

    public IReadOnlyList<GameDataCell> Cells { get; }

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
