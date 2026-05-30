using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Aliases tab. Surfaces the active character's
/// user-defined aliases from <see cref="AliasEngine"/> — the
/// outgoing-text mirror of the Triggers tab.
/// </summary>
public sealed partial class AliasesSectionViewModel : GameDataSectionViewModel
{
    private readonly AliasEngine _engine;
    private Control? _view;

    public override string Id => "aliases";
    public override string Title => "Aliases";

    public ObservableCollection<Alias> AllAliases => _engine.Aliases;
    public ObservableCollection<Alias> FilteredAliases { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private Alias? _selectedAlias;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new AliasesSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[] { Title, "alias", "shortcut", "command" };

    public string StatusText
    {
        get
        {
            int total = AllAliases.Count;
            int visible = FilteredAliases.Count;
            string countText = total == visible ? $"{total} aliases" : $"{visible} / {total} aliases";
            string selection = SelectedAlias is null ? "" : $"  ·  {SelectedAlias.Name}";
            return countText + selection;
        }
    }

    public AliasesSectionViewModel(AliasEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _engine.Aliases.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void ApplyFilter()
    {
        FilteredAliases.Clear();
        string filter = (SearchText ?? string.Empty).Trim();

        foreach (Alias a in AllAliases)
        {
            if (filter.Length == 0 ||
                a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Expansion.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAliases.Add(a);
            }
        }
        OnPropertyChanged(nameof(StatusText));
    }
}
