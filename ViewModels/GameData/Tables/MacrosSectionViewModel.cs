using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Macros tab. Surfaces the loaded character's keybinds from MacroStore.
// Editable — double-click a row opens the MacroEditDialogViewModel; save routes through
// MacroStore.Replace.
public sealed class MacrosSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MacroStore _store;
    private readonly DialogService? _dialogs;
    private readonly KeybindingStore? _keybindings;

    public override string Id => "macros";
    public override string Title => "Macros";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Key", "Command",
    };

    public override string SearchKeyColumn => "Command";

    // Engine-backed table — every row lives only at the Char tier, so the "Use" badge would
    // always read the same value and just adds noise.
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "macro", "key", "keybind", "shortcut",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand     => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public MacrosSectionViewModel(MacroStore store, DialogService? dialogs = null, KeybindingStore? keybindings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs;
        _keybindings = keybindings;
        _handler = (_, _) => Reload();
        _store.Macros.CollectionChanged += _handler;
        OpenEditAsyncCommand   = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        AddAsyncCommand        = new AsyncRelayCommand(AddAsync);
        RemoveSelectedCommand  = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedRow is not null);

        // The Remove button's CanExecute depends on the current SelectedRow —
        // re-evaluate every time the selection changes.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
                RemoveSelectedCommand.NotifyCanExecuteChanged();
        };

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

    private async Task AddAsync()
    {
        if (_dialogs is null) return;
        // Seed a blank macro — the dialog forces the user to pick a key
        // before save (CanSave gates the Save button on a valid chord).
        Macro blank = new(Key: string.Empty, Ctrl: false, Shift: false, Alt: false,
                          Command: string.Empty, Enabled: true);
        if (_keybindings is null) return;
        MacroEditDialogViewModel vm = new(blank, _store, _keybindings);
        Macro? created = await _dialogs.OpenWindowAsync<MacroEditDialogViewModel, Macro>(vm);
        if (created is null) return;
        _store.Add(created);
        Reload();
    }

    private async Task RemoveSelectedAsync()
    {
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;
        string what = selection.Count == 1 ? "this macro" : $"{selection.Count} macros";
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync(what)) return;

        List<Macro> targets = new();
        foreach (GameDataRow row in selection)
        {
            string? chord = row.Get("Key");
            if (string.IsNullOrEmpty(chord)) continue;
            foreach (Macro m in _store.Macros)
            {
                if (m.KeyChordLabel == chord) { targets.Add(m); break; }
            }
        }
        if (targets.Count == 0) return;
        foreach (Macro t in targets) _store.Remove(t);
        Reload();
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

        if (_keybindings is null) return;
        MacroEditDialogViewModel vm = new(original, _store, _keybindings);
        Macro? updated = await _dialogs.OpenWindowAsync<MacroEditDialogViewModel, Macro>(vm);
        if (updated is null) return;

        _store.Replace(original, updated);
        Reload();
    }
}
