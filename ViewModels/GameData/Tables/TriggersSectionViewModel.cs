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

/// <summary>
/// Game Data Browser → Triggers tab. Surfaces the active character's
/// user-defined triggers from <see cref="TriggerEngine"/>. Editable —
/// double-click a row opens the <see cref="TriggerEditDialogViewModel"/>;
/// save routes through <see cref="TriggerEngine.Replace"/>.
/// </summary>
public sealed class TriggersSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly TriggerEngine _engine;
    private readonly DialogService? _dialogs;

    /// <summary>
    /// Per-rebuild side table mapping the displayed <see cref="GameDataRow"/>
    /// back to the engine's live <see cref="Trigger"/> instance. The
    /// engine has no natural key (Name isn't constrained unique), and
    /// post-filter row indices don't map to engine indices, so we
    /// remember the reference at populate time.
    /// </summary>
    private readonly Dictionary<GameDataRow, Trigger> _rowToTrigger = new();

    public override string Id => "triggers";
    public override string Title => "Triggers";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Name", "Scope", "Match", "Pattern", "Response",
    };

    public override string SearchKeyColumn => "Name";

    /// <summary>Engine-backed table — every row lives only at the Char tier, so the "Use" badge would always read the same value.</summary>
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trigger", "pattern", "match", "response",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }

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
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);

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

    private void RemoveSelected()
    {
        if (SelectedRow is null) return;
        if (!_rowToTrigger.TryGetValue(SelectedRow, out Trigger? target)) return;
        _engine.Remove(target);
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

    /// <summary>
    /// Friendly column label for the scope enum — the underlying values
    /// are PascalCase (<c>GameMessages</c>, <c>ChatTelepath</c>) which
    /// reads awkwardly in a table.
    /// </summary>
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
}
