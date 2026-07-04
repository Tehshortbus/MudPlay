using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Aliases tab. Surfaces the active character's user-defined aliases from
// AliasEngine — the outgoing-text mirror of the Triggers tab. Editable — double-click a row
// opens the AliasEditDialogViewModel; save routes through AliasEngine.Replace.
public sealed class AliasesSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly AliasEngine _engine;
    private readonly DialogService? _dialogs;

    // Per-rebuild side table mapping the displayed GameDataRow back to the engine's live Alias
    // instance. Same trick the Triggers tab uses — first-word is unique at save time but
    // FilteredRows indices skew under search, so we remember the reference at populate time.
    private readonly Dictionary<GameDataRow, Alias> _rowToAlias = new();

    public override string Id => "aliases";
    public override string Title => "Aliases";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Name", "Expansion",
    };

    public override string SearchKeyColumn => "Name";

    // Engine-backed table — see GameDataTableSectionViewModel.ShowUseColumn.
    public override bool ShowUseColumn => false;

    // Surfaced banner — tells the user that aliases only expand from the Conversation window's
    // input field today, not from the terminal canvas. The canvas would need client-side
    // line-mode (local echo + telnet ECHO negotiation) to participate; that work is
    // intentionally deferred until usage demand justifies it.
    public override string? BannerText =>
        "Aliases fire only when you press Enter in the Conversation window's input field. " +
        "Typing in the main terminal sends each keystroke directly to the game and bypasses alias expansion.";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "alias", "shortcut", "command",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand     => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public AliasesSectionViewModel(AliasEngine engine, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _dialogs = dialogs;
        _handler = (_, _) => Reload();
        _engine.Aliases.CollectionChanged += _handler;

        OpenEditAsyncCommand  = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        AddAsyncCommand       = new AsyncRelayCommand(AddAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedRow is not null);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
                RemoveSelectedCommand.NotifyCanExecuteChanged();
        };

        Reload();
    }

    public override void Dispose()
    {
        _engine.Aliases.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        _rowToAlias.Clear();
        foreach (Alias a in _engine.Aliases)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]   = a.Enabled ? "✓" : "",
                ["Name"]      = a.Name,
                ["Expansion"] = a.Expansion,
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            _rowToAlias[row] = a;
            rows.Add(row);
        }
    }

    private async Task AddAsync()
    {
        if (_dialogs is null) return;
        Alias blank = new(Name: string.Empty, Enabled: true, Expansion: string.Empty);
        AliasEditDialogViewModel vm = new(blank, _engine, isNew: true);
        Alias? created = await _dialogs.OpenWindowAsync<AliasEditDialogViewModel, Alias>(vm);
        if (created is null) return;
        _engine.Add(created);
    }

    private async Task RemoveSelectedAsync()
    {
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;
        string what = selection.Count == 1 ? "this alias" : $"{selection.Count} aliases";
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync(what)) return;

        List<Alias> targets = new();
        foreach (GameDataRow row in selection)
        {
            if (_rowToAlias.TryGetValue(row, out Alias? t)) targets.Add(t);
        }
        foreach (Alias t in targets) _engine.Remove(t);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (!_rowToAlias.TryGetValue(row, out Alias? original)) return;

        AliasEditDialogViewModel vm = new(original, _engine, isNew: false);
        Alias? updated = await _dialogs.OpenWindowAsync<AliasEditDialogViewModel, Alias>(vm);
        if (updated is null) return;
        _engine.Replace(original, updated);
    }
}
