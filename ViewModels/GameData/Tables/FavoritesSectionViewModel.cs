using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Favorites tab. Surfaces the loaded character's
/// favourites from <see cref="FavoritesManager"/>. PR 5.21 ships a
/// flat list (with the path column showing the folder hierarchy);
/// the tree-view layout the Phase 7 Goto / Loop dialogs use is a
/// follow-up.
/// </summary>
public sealed partial class FavoritesSectionViewModel : GameDataSectionViewModel
{
    private readonly FavoritesManager _favs;
    private Control? _view;

    public override string Id => "favorites";
    public override string Title => "Favorites";

    public ObservableCollection<Favorite> All => _favs.Favorites;
    public ObservableCollection<Favorite> Filtered { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private Favorite? _selected;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new FavoritesSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[] { Title, "favorite", "shortcut", "room" };

    public string StatusText
    {
        get
        {
            int total = All.Count;
            int visible = Filtered.Count;
            string countText = total == visible ? $"{total} favorites" : $"{visible} / {total} favorites";
            string selection = Selected is null ? "" : $"  ·  {Selected.Name}";
            return countText + selection;
        }
    }

    public FavoritesSectionViewModel(FavoritesManager favs)
    {
        ArgumentNullException.ThrowIfNull(favs);
        _favs = favs;
        _favs.Favorites.CollectionChanged += (_, _) => ApplyFilter();
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

        foreach (Favorite f in All)
        {
            if (filter.Length == 0 ||
                f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Filtered.Add(f);
            }
        }
        OnPropertyChanged(nameof(StatusText));
    }
}
