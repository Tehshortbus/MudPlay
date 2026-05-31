using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Macros tab. Surfaces the loaded character's
/// keybinds from <see cref="MacroStore"/>. Editable — double-click a
/// row opens the <see cref="MacroEditDialogViewModel"/>; save routes
/// through <see cref="MacroStore.Replace"/>.
/// </summary>
public sealed class MacrosSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MacroStore _store;
    private readonly DialogService? _dialogs;

    public override string Id => "macros";
    public override string Title => "Macros";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Key", "Command",
    };

    public override string SearchKeyColumn => "Command";

    /// <summary>Engine-backed table — every row lives only at the Char tier, so the "Use" badge would always read the same value and just adds noise.</summary>
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "macro", "key", "keybind", "shortcut",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public MacrosSectionViewModel(MacroStore store, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs;
        _handler = (_, _) => Reload();
        _store.Macros.CollectionChanged += _handler;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        Reload();
    }

    public override void Dispose()
    {
        _store.Macros.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (Macro m in _store.Macros)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = m.Enabled ? "✓" : "",
                ["Key"]     = m.KeyChordLabel,
                ["Command"] = m.Command,
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? chord = row.Get("Key");
        if (string.IsNullOrEmpty(chord)) return;

        // Locate the live record by chord — IsDuplicate already
        // prevents two macros sharing one chord at save time.
        Macro? original = null;
        foreach (Macro m in _store.Macros)
        {
            if (m.KeyChordLabel == chord)
            {
                original = m;
                break;
            }
        }
        if (original is null) return;

        MacroEditDialogViewModel vm = new(original, _store);
        Macro? updated = await _dialogs.OpenWindowAsync<MacroEditDialogViewModel, Macro>(vm);
        if (updated is null) return;

        _store.Replace(original, updated);
        Reload();
    }
}
