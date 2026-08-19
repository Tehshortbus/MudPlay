using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.Services;

// Opens the on-use / proc message record for an item, modelessly, from the item
// edit dialog's "Message" section. This is the Items-side mirror of
// SpellRecordDialogService: a message anchored to an item (its Links carry an
// Items#N back-reference) is authored/edited from the item, not from the Messages
// tab — which now hides item-claimed records the same way it hides spell-claimed
// ones. Finds the record linked to the item (or a fresh pre-linked one when the
// item has none yet), builds the item's read-only facts as the dialog's Game Data
// tab, opens the shared MessageEditDialogViewModel, and persists the edit back to
// the store on Save. Single-instance: re-opening the shown item is a no-op; another
// swaps.
public sealed class ItemMessageDialogService
{
    private readonly GameDataCache _cache;
    private readonly MessageStore _messages;
    private readonly DialogService _dialogs;

    private MessageEditDialogViewModel? _openVm;
    private int _openItem;

    public ItemMessageDialogService(GameDataCache cache, MessageStore messages, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(dialogs);
        _cache = cache;
        _messages = messages;
        _dialogs = dialogs;
    }

    // One-line preview of the item's currently-attached message (first populated
    // perspective line), or "" when the item has no linked record. Drives the item
    // dialog's "Message" section summary + Add/Edit button label.
    public string SummaryFor(int itemNumber)
    {
        MessageRecord? m = FindByItem(itemNumber);
        if (m is null) return string.Empty;
        return FirstNonEmpty(m.AppliedMessage, m.CasterMessage, m.TargetMessage, m.WitnessMessage);
    }

    // Open the editor for the item's linked message. Returns the post-edit summary so
    // the item dialog's section refreshes in place. Null return only when the call is
    // rejected outright (bad number).
    public async Task<string?> OpenAsync(int itemNumber)
    {
        if (itemNumber <= 0) return null;

        // Re-opening the item already showing is a no-op — don't tear down edits.
        if (_openVm is not null && _openItem == itemNumber) return SummaryFor(itemNumber);

        string itemName = _cache.FindNameByNumber("Items", itemNumber) ?? $"Item #{itemNumber}";

        MessageRecord? match = FindByItem(itemNumber);
        bool isNew = match is null;
        MessageRecord record = match ?? new MessageRecord(
            Id:              string.Empty,
            Name:            itemName,
            Flags:           MessageFlags.None,
            RawFlagsHex:     0,
            CasterMessage:   string.Empty,
            TargetMessage:   string.Empty,
            WitnessMessage:  string.Empty,
            AppliedMessage:  string.Empty,
            AppliedEndsWith: string.Empty,
            Links:           new[] { new GameDataLink("Items", itemNumber) });

        IReadOnlyList<GameDataInfoRow> info = BuildItemInfoRows(itemNumber);

        MessageEditDialogViewModel vm = new(
            record,
            currentTier:     SettingsTier.Defaults,
            existingRecords: _messages.Messages,
            isNew:           isNew,
            cache:           _cache,
            gameDataInfo:    info);

        MessageEditDialogViewModel? previous = _openVm;
        _openVm = vm;
        _openItem = itemNumber;
        previous?.CancelCommand.Execute(null);

        MessageEditResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        }
        finally
        {
            if (ReferenceEquals(_openVm, vm)) { _openVm = null; _openItem = 0; }
        }

        if (result is not null)
        {
            // Id-keyed update-or-append into the store + persist (mirrors the browser's ApplyResult).
            int idx = -1;
            for (int i = 0; i < _messages.Messages.Count; i++)
                if (_messages.Messages[i].Id == result.Original.Id) { idx = i; break; }
            if (idx >= 0) _messages.Messages[idx] = result.Updated;
            else          _messages.Messages.Add(result.Updated);
            _messages.Save();
        }

        return SummaryFor(itemNumber);
    }

    // The one record whose Links back-reference this item (first match — an item
    // carries at most one on-use/proc message, same as a spell). Case-insensitive on
    // the table stem, matching every other Links consumer.
    private MessageRecord? FindByItem(int itemNumber) =>
        _messages.Messages.FirstOrDefault(m => m.Links is not null && m.Links.Any(l =>
            string.Equals(l.Table, "Items", StringComparison.OrdinalIgnoreCase) && l.Number == itemNumber));

    // The item's read-only MDB facts as Game Data rows — the dialog's Game Data tab,
    // so the author sees what the item does while writing its message. Built at the
    // neutral retail charm (50), matching the item dialog's own default.
    private IReadOnlyList<GameDataInfoRow> BuildItemInfoRows(int itemNumber)
    {
        ItemMdbView mdb = new ItemMdbViewBuilder(_cache, 50)
            .Build(itemNumber.ToString(CultureInfo.InvariantCulture));
        List<GameDataInfoRow> rows = new(mdb.OtherInfo.Count);
        foreach (KeyValuePair<string, string> kv in mdb.OtherInfo)
            rows.Add(new GameDataInfoRow(kv.Key, kv.Value));
        return rows;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return string.Empty;
    }
}
