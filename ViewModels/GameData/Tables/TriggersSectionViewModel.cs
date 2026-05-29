using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Triggers tab. Surfaces the active character's
/// user-defined triggers from <see cref="TriggerEngine"/>. Unlike the
/// MDB-derived tabs, the data source here is the loaded
/// <see cref="Models.Profile.CharacterProfile"/>, not
/// <see cref="GameDataCache"/>.
/// </summary>
/// <remarks>
/// PR 5.10 ships the listing surface; the editor dialog opened from
/// row double-click lands once every table's listing is in place.
/// </remarks>
public sealed partial class TriggersSectionViewModel : GameDataSectionViewModel
{
    private readonly TriggerEngine _engine;
    private Control? _view;

    public override string Id => "triggers";
    public override string Title => "Triggers";

    public ObservableCollection<Trigger> AllTriggers => _engine.Triggers;

    public ObservableCollection<Trigger> FilteredTriggers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private Trigger? _selectedTrigger;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new TriggersSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trigger", "pattern", "match",
    };

    public string StatusText
    {
        get
        {
            int total = AllTriggers.Count;
            int visible = FilteredTriggers.Count;
            string countText = total == visible ? $"{total} triggers" : $"{visible} / {total} triggers";
            string selection = SelectedTrigger is null ? "" : $"  ·  {SelectedTrigger.Name}";
            return countText + selection;
        }
    }

    public TriggersSectionViewModel(TriggerEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _engine.Triggers.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    private void ApplyFilter()
    {
        FilteredTriggers.Clear();
        string filter = (SearchText ?? string.Empty).Trim();

        foreach (Trigger t in AllTriggers)
        {
            if (filter.Length == 0 ||
                t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                t.Pattern.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTriggers.Add(t);
            }
        }
        OnPropertyChanged(nameof(StatusText));
    }
}
