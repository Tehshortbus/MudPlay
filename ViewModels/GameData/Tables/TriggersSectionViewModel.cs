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

// Game Data Browser → Triggers tab. Surfaces the active character's user-defined triggers from
// TriggerEngine. Editable — double-click a row opens the TriggerEditDialogViewModel; save routes
// through TriggerEngine.Replace.
public sealed class TriggersSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly TriggerEngine _engine;
    private readonly DialogService? _dialogs;

    // Per-rebuild side table mapping the displayed GameDataRow back to the engine's live Trigger
    // instance. The engine has no natural key (Name isn't constrained unique), and post-filter
    // row indices don't map to engine indices, so we remember the reference at populate time.
    private readonly Dictionary<GameDataRow, Trigger> _rowToTrigger = new();

    public override string Id => "triggers";
    public override string Title => "Triggers";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Name", "Location", "Scope", "Match", "Pattern", "Response",
    };

    public override string SearchKeyColumn => "Name";

    // Engine-backed table — every row lives only at the Char tier, so the "Use" badge would
    // always read the same value.
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trigger", "pattern", "match", "response",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand     => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public TriggersSectionViewModel(TriggerEngine engine, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _dialogs = dialogs;
        _handler = (_, _) => Reload();
        _engine.Triggers.CollectionChanged += _handler;

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
        _engine.Triggers.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        _rowToTrigger.Clear();
        foreach (Trigger t in _engine.Triggers)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]  = t.Enabled ? "✓" : "",
                ["Name"]     = t.Name,
                ["Location"] = FormatLocation(t.Location),
                ["Scope"]    = FormatScope(t.Scope),
                ["Match"]    = t.MatchType.ToString(),
                ["Pattern"]  = t.Pattern,
                ["Response"] = string.IsNullOrEmpty(t.Response) ? "(CR)" : t.Response,
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            _rowToTrigger[row] = t;
            rows.Add(row);
        }
    }

    private async Task AddAsync()
    {
        if (_dialogs is null) return;
        Trigger blank = new(
            Name:      string.Empty,
            Enabled:   true,
            Scope:     TriggerScope.GameMessages,
            MatchType: TriggerMatchType.Literal,
            Pattern:   string.Empty,
            Response:  string.Empty);
        TriggerEditDialogViewModel vm = new(blank, isNew: true);
        Trigger? created = await _dialogs.OpenWindowAsync<TriggerEditDialogViewModel, Trigger>(vm);
        if (created is null) return;
        _engine.Add(created);
    }

    private async Task RemoveSelectedAsync()
    {
        // Snapshot the multi-selection before mutating the engine —
        // Remove triggers Reload which clears SelectedRows mid-loop.
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;
        string what = selection.Count == 1 ? "this trigger" : $"{selection.Count} triggers";
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync(what)) return;

        List<Trigger> targets = new();
        foreach (GameDataRow row in selection)
        {
            if (_rowToTrigger.TryGetValue(row, out Trigger? t)) targets.Add(t);
        }
        foreach (Trigger t in targets) _engine.Remove(t);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (!_rowToTrigger.TryGetValue(row, out Trigger? original)) return;

        TriggerEditDialogViewModel vm = new(original, isNew: false);
        Trigger? updated = await _dialogs.OpenWindowAsync<TriggerEditDialogViewModel, Trigger>(vm);
        if (updated is null) return;
        _engine.Replace(original, updated);
    }

    // Friendly column label for the scope enum — the underlying values are PascalCase
    // (GameMessages, ChatTelepath) which reads awkwardly in a table.
    private static string FormatScope(TriggerScope scope) => scope switch
    {
        TriggerScope.GameMessages  => "Game messages",
        TriggerScope.ChatAny       => "Chat (any)",
        TriggerScope.ChatSay       => "Say",
        TriggerScope.ChatYell      => "Yell",
        TriggerScope.ChatGossip    => "Gossip",
        TriggerScope.ChatTelepath  => "Telepath",
        TriggerScope.ChatGangpath  => "Gangpath",
        TriggerScope.ChatBroadcast => "Broadcast",
        TriggerScope.SystemLog     => "System log",
        _                          => scope.ToString(),
    };

    // Friendly column label for the storage-location enum.
    private static string FormatLocation(TriggerLocation loc) => loc switch
    {
        TriggerLocation.GameData => "Game data",
        TriggerLocation.Profile  => "Profile",
        _                        => loc.ToString(),
    };
}
