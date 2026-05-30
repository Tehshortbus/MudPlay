using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.GameData.Tables;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Macros tab. Read-only listing of the loaded
/// character's keybinds from <see cref="MacroStore"/>. Per master
/// plan, double-click a row opens the Phase 10 MacroEditDialog —
/// wiring lands in Phase 10 PR 10.3 once that dialog exists.
/// </summary>
public sealed partial class MacrosSectionViewModel : GameDataSectionViewModel
{
    private readonly MacroStore _store;
    private Control? _view;

    public override string Id => "macros";
    public override string Title => "Macros";

    public ObservableCollection<Macro> All => _store.Macros;
    public ObservableCollection<Macro> Filtered { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private Macro? _selected;

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new MacrosSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[] { Title, "macro", "key", "keybind" };

    public string StatusText
    {
        get
        {
            int total = All.Count;
            int visible = Filtered.Count;
            string countText = total == visible ? $"{total} macros" : $"{visible} / {total} macros";
            string selection = Selected is null ? "" : $"  ·  {Selected.Name}";
            return countText + selection;
        }
    }

    public MacrosSectionViewModel(MacroStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Macros.CollectionChanged += (_, _) => ApplyFilter();
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

        foreach (Macro m in All)
        {
            if (filter.Length == 0 ||
                m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Filtered.Add(m);
            }
        }
        OnPropertyChanged(nameof(StatusText));
    }
}
