using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Messages tab. Surfaces the active game-data
/// set's Messages/Responses catalogue from <see cref="MessageStore"/>.
/// Records are paired per set: importing a MegaMUD <c>messages.md</c>
/// file lands as <c>Data/Global/Messages/{set-name}.json</c>, and
/// switching the active set swaps the catalogue in real time.
/// </summary>
public sealed class MessagesSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MessageStore _store;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolver;

    public override string Id => "messages";
    public override string Title => "Messages";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name", "Action", "Message", "EndsWith",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "message", "response", "condition", "pattern",
        "blinded", "poisoned", "paralyzed", "confused", "diseased", "regenerating",
    };

    /// <summary>Open the per-record edit dialog for the row currently double-clicked.</summary>
    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public MessagesSectionViewModel(MessageStore store, DialogService? dialogs = null, SettingsResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs;
        _resolver = resolver;
        _store.Messages.CollectionChanged += (_, _) => Reload();
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        Reload();
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        foreach (MessageRecord m in _store.Messages)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"]     = m.Name,
                ["Action"]   = m.Action.ToString(),
                ["Message"]  = m.Message,
                ["EndsWith"] = m.EndsWith,
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            // Messages live at the per-set Defaults tier by default;
            // record overrides at higher tiers go via SettingsResolver.
            if (_resolver is not null)
                row.SourceTier = _resolver.GetGameDataSourceTier("Messages", m.Id);
            rows.Add(row);
        }
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;

        // Match the row back to its source MessageRecord by Id —
        // synthesised from the row's Name/Message/EndsWith fields,
        // which is the same algorithm the importer uses.
        string id = MegaMudMessagesImporter.ComputeId(
            row.Get("Name")     ?? string.Empty,
            row.Get("Message")  ?? string.Empty,
            row.Get("EndsWith") ?? string.Empty);
        MessageRecord? original = _store.Messages.FirstOrDefault(m => m.Id == id);
        if (original is null) return;

        MessageEditDialogViewModel vm = new(original, row.SourceTier);
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
}
