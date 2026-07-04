using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Messages tab. Surfaces the active game-data set's Messages/Responses
// catalogue from MessageStore. Records are paired per set: seeded from the wcc-derived
// universal seed (Data/Global/Messages.seed.json), persisted per set at
// Data/game data/{set}/messages.json on first edit. Switching the active set swaps the
// catalogue in real time.
public sealed class MessagesSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MessageStore _store;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolver;
    private readonly GameDataCache? _cache;

    public override string Id => "messages";
    public override string Title => "Messages";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name", "Action", "Lines", "Preview",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "message", "response", "condition", "pattern",
        "blinded", "poisoned", "paralyzed", "confused", "diseased", "regenerating",
    };

    // Open the per-record edit dialog for the row currently double-clicked.
    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand     => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public MessagesSectionViewModel(
        MessageStore store,
        DialogService? dialogs = null,
        SettingsResolver? resolver = null,
        GameDataCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs;
        _resolver = resolver;
        _cache = cache;
        _handler = (_, _) => Reload();
        _store.Messages.CollectionChanged += _handler;
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
        _store.Messages.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (MessageRecord m in _store.Messages)
        {
            // Lines column = compact tag string showing which perspective
            // slots are populated, e.g. "C T W A•" (Caster+Target+Witness
            // + Applied pair). Preview column = first non-empty line for
            // a quick at-a-glance read.
            string lines = BuildLineTags(m);
            string preview = FirstNonEmptyLine(m);
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"]    = m.Name,
                ["Action"]  = m.Action.ToString(),
                ["Lines"]   = lines,
                ["Preview"] = preview,
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            row.Tag = m;
            if (_resolver is not null)
                row.SourceTier = _resolver.GetGameDataSourceTier("Messages", m.Id);
            rows.Add(row);
        }
    }

    private static string BuildLineTags(MessageRecord m)
    {
        List<string> tags = new(4);
        if (!string.IsNullOrEmpty(m.CasterMessage))  tags.Add("C");
        if (!string.IsNullOrEmpty(m.TargetMessage))  tags.Add("T");
        if (!string.IsNullOrEmpty(m.WitnessMessage)) tags.Add("W");
        if (!string.IsNullOrEmpty(m.AppliedMessage)) tags.Add(string.IsNullOrEmpty(m.AppliedEndsWith) ? "A" : "A•");
        return string.Join(" ", tags);
    }

    private static string FirstNonEmptyLine(MessageRecord m)
    {
        if (!string.IsNullOrEmpty(m.CasterMessage))  return m.CasterMessage;
        if (!string.IsNullOrEmpty(m.TargetMessage))  return m.TargetMessage;
        if (!string.IsNullOrEmpty(m.WitnessMessage)) return m.WitnessMessage;
        if (!string.IsNullOrEmpty(m.AppliedMessage)) return m.AppliedMessage;
        return string.Empty;
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (row.Tag is not MessageRecord original) return;

        MessageEditDialogViewModel vm = new(
            original,
            row.SourceTier,
            _store.Messages,
            isNew: false,
            cache: _cache);
        MessageEditResult? result = await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        ApplyResult(result);
    }

    private void ApplyResult(MessageEditResult result)
    {
        // For now Save targets the per-set MessageStore (treated as the
        // Defaults tier for messages). Future: non-Defaults tier writes
        // via SettingsResolver.WriteGameDataAt("Messages", id, record)
        // — wiring lands once a runtime overlay-aware reader exists.
        int idx = -1;
        for (int i = 0; i < _store.Messages.Count; i++)
        {
            if (_store.Messages[i].Id == result.Original.Id) { idx = i; break; }
        }
        if (idx >= 0) _store.Messages[idx] = result.Updated;
        else          _store.Messages.Add(result.Updated);
        _store.Save();
    }

    // Add-button handler — opens the edit dialog with a fresh blank record. Save through
    // ApplyResult appends it to the store; Cancel discards. isNew: true tells the dialog to
    // skip the self-duplicate-check exemption.
    private async Task AddAsync()
    {
        if (_dialogs is null) return;
        MessageRecord blank = new(
            Id:              string.Empty,
            Name:            string.Empty,
            Action:          MessageAction.Ignore,
            Flags:           MessageFlags.None,
            RawFlagsHex:     0,
            Response:        string.Empty,
            CasterMessage:   string.Empty,
            TargetMessage:   string.Empty,
            WitnessMessage:  string.Empty,
            AppliedMessage:  string.Empty,
            AppliedEndsWith: string.Empty,
            Links:           Array.Empty<GameDataLink>());

        MessageEditDialogViewModel vm = new(
            blank,
            currentTier:     SettingsTier.Defaults,
            existingRecords: _store.Messages,
            isNew:           true,
            cache:           _cache);
        MessageEditResult? result = await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;
        ApplyResult(result);
    }

    // Remove the selected row's record from the store.
    private async Task RemoveSelectedAsync()
    {
        // Snapshot the multi-selection (or fall back to the single
        // SelectedRow when nothing has been multi-selected) before
        // mutating the store — Remove triggers CollectionChanged →
        // Reload, which clears SelectedRows mid-loop and would
        // truncate the operation.
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;
        string what = selection.Count == 1 ? "this message" : $"{selection.Count} messages";
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync(what)) return;

        List<MessageRecord> targets = new();
        foreach (GameDataRow row in selection)
        {
            if (row.Tag is MessageRecord target) targets.Add(target);
        }
        if (targets.Count == 0) return;
        foreach (MessageRecord t in targets) _store.Messages.Remove(t);
        _store.Save();
    }
}
