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
/// Shared base for every per-table tab in the Game Data Browser. Loads
/// the active set's table from <see cref="GameDataCache"/>, normalises
/// each row to a string-string dictionary keyed by column name, and
/// exposes a search-filtered view plus a selected-row slot. Subclasses
/// supply the table name + the column list to display + the search-key
/// column; the shared view (<see cref="GameDataTableSectionView"/>)
/// renders the result.
/// </summary>
/// <remarks>
/// PR 5.5 introduces this base alongside the first concrete consumer
/// (Monsters). Every subsequent per-table PR (5.6 / 5.7 / 5.12-5.18 /
/// 5.20-5.22) is just a ~30-line subclass — table name + columns +
/// search key, plus the section's Id / Title / SearchableLabels.
/// </remarks>
public abstract partial class GameDataTableSectionViewModel : GameDataSectionViewModel
{
    private readonly GameDataCache _cache;
    private Control? _view;

    /// <summary>Underlying table name in the active set (e.g. <c>"Monsters"</c>).</summary>
    protected abstract string TableName { get; }

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

    /// <summary>Every row loaded from the active set, original order.</summary>
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

    protected GameDataTableSectionViewModel(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => Reload();
        Reload();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void Reload()
    {
        AllRows.Clear();
        FilteredRows.Clear();
        SelectedRow = null;

        JsonDocument? doc = _cache.GetRawTable(TableName);
        if (doc is null) { OnPropertyChanged(nameof(StatusText)); return; }

        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = ColumnFormatters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            AllRows.Add(GameDataRow.FromJson(el, Columns, formatters));
        }
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
/// One row loaded from a game-data JSON table. Holds the column-name →
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
