using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Players tab. Surfaces the rows held by
/// <see cref="PlayerDatabase"/>. Unlike the MDB-derived tabs, the
/// data source is observation + manual edits, not an imported table.
/// </summary>
public sealed partial class PlayersSectionViewModel : GameDataSectionViewModel
{
    private readonly PlayerDatabase _db;
    private Control? _view;

    public override string Id => "players";
    public override string Title => "Players";

    public ObservableCollection<PlayerRecord> All => _db.Players;
    public ObservableCollection<PlayerRecord> Filtered { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PlayerRecord? _selected;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new PlayersSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "player", "name", "class", "race", "alignment",
    };

    public string StatusText
    {
        get
        {
            int total = All.Count;
            int visible = Filtered.Count;
            string countText = total == visible ? $"{total} players" : $"{visible} / {total} players";
            string selection = Selected is null ? "" : $"  ·  {Selected.Name}";
            return countText + selection;
        }
    }

    public PlayersSectionViewModel(PlayerDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _db.Players.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        string filter = (SearchText ?? string.Empty).Trim();

        foreach (PlayerRecord p in All)
        {
            if (filter.Length == 0 ||
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (p.Class?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.Race?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                Filtered.Add(p);
            }
        }
        OnPropertyChanged(nameof(StatusText));
    }
}
