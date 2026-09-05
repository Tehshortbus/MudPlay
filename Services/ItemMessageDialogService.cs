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
// edit dialog's "Message" section.
//
// An item that CASTS a spell (a CastsSp / Abil-43 slot — weapon use-bless, wand bolt,
// proc weapon) carries its on-use / proc wording on the CAST SPELL's message record
// (Spells#N), NOT the item: many weapons share one cast spell and therefore one set of
// messages, so editing from any of them edits the same record. For those items this
// service delegates straight to SpellRecordDialogService on the item's PrimaryCastSpell.
//
// An item that casts nothing (a worn trinket whose only "message" is a wield/remove
// event) keeps the legacy item-anchored path: a record whose Links carry an Items#N
// back-reference, edited from the item and hidden from the Messages tab. Single-instance:
// re-opening the shown item is a no-op; another swaps.
public sealed class ItemMessageDialogService
{
    private readonly GameDataCache _cache;
    private readonly MessageStore _messages;
    private readonly DialogService _dialogs;
    private readonly SpellRecordDialogService _spellRecords;

    private MessageEditDialogViewModel? _openVm;
    private int _openItem;

    public ItemMessageDialogService(
        GameDataCache cache, MessageStore messages, DialogService dialogs,
        SpellRecordDialogService spellRecords)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(spellRecords);
        _cache = cache;
        _messages = messages;
        _dialogs = dialogs;
        _spellRecords = spellRecords;
    }

    // One-line preview of the item's on-use / proc message (first populated
    // perspective/applied line), or "" when there is none. Drives the item dialog's
    // "Message" section summary + Add/Edit button label. Resolves via the item's
    // PrimaryCastSpell (the shared Spells#N record) when it casts a spell, else its
    // legacy item-anchored record.
    public string SummaryFor(int itemNumber)
    {
        if (ItemCastSpells.PrimaryCastSpell(_cache, itemNumber) is int spell)
            return _spellRecords.SummaryFor(spell);
        MessageRecord? m = FindByItem(itemNumber);
        if (m is null) return string.Empty;
        return FirstNonEmpty(m.AppliedMessage, m.CasterMessage, m.TargetMessage, m.WitnessMessage);
    }

    // Open the editor for the item's on-use / proc message. Returns the post-edit summary so
    // the item dialog's section refreshes in place. Null return only when the call is
    // rejected outright (bad number).
    public async Task<string?> OpenAsync(int itemNumber)
    {
        if (itemNumber <= 0) return null;

        // A casting item's message lives on the cast SPELL's record — open that (shared
        // across every item casting the same spell), not an item-anchored one.
        if (ItemCastSpells.PrimaryCastSpell(_cache, itemNumber) is int spell)
            return await _spellRecords.OpenAsync(spell);

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
