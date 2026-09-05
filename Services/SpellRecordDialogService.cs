using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.Services;

// Opens the spell record (Message / Game-Data) dialog by spell Number, modelessly, from
// anywhere — the Navigation Room Info panel's room-spell link. Mirrors the Spells browser
// tab's open flow (SpellsSectionViewModel.OpenLinkedMessagesAsync): find the message linked
// to the spell (or a fresh pre-linked record for spells with no player-cast message), build
// the Game Data tab via the shared SpellInfoRowsBuilder, open the tabbed dialog, and persist
// the message edit back to the store on Save. Single-instance: re-opening the shown spell is
// a no-op; another swaps.
public sealed class SpellRecordDialogService
{
    private readonly GameDataCache _cache;
    private readonly MessageStore _messages;
    private readonly DialogService _dialogs;

    private MessageEditDialogViewModel? _openVm;
    private int _openSpell;

    public SpellRecordDialogService(GameDataCache cache, MessageStore messages, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(dialogs);
        _cache = cache;
        _messages = messages;
        _dialogs = dialogs;
    }

    // One-line preview of the message record linked to this spell (first populated
    // perspective/applied line), or "" when no record anchors to it. Drives the item
    // dialog's on-use "Message" section summary when the on-use ties here via CastsSp.
    public string SummaryFor(int spellNumber)
    {
        if (spellNumber <= 0) return string.Empty;
        MessageRecord? m = _messages.Messages.FirstOrDefault(r => r.Links is not null && r.Links.Any(l =>
            string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase) && l.Number == spellNumber));
        if (m is null) return string.Empty;
        foreach (string v in new[] { m.AppliedMessage, m.CasterMessage, m.TargetMessage, m.WitnessMessage })
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return string.Empty;
    }

    // Returns the post-edit summary so an item's Message section (on-use tied here via
    // CastsSp) refreshes in place; callers that only open the dialog can ignore it.
    public async Task<string?> OpenAsync(int spellNumber)
    {
        if (spellNumber <= 0) return null;

        // Re-opening the spell already showing is a no-op — don't tear down edits.
        if (_openVm is not null && _openSpell == spellNumber) return SummaryFor(spellNumber);

        string spellName = _cache.FindNameByNumber("Spells", spellNumber) ?? $"Spell #{spellNumber}";

        // The one message record linked to this spell (player-cast wording), or a fresh
        // pre-linked record when the spell has none (room / item / textblock casts) — the
        // Message tab is then ready to author while the Game Data tab still shows what it does.
        MessageRecord? match = _messages.Messages
            .FirstOrDefault(m => m.Links is not null && m.Links.Any(l =>
                string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                && l.Number == spellNumber));

        bool isNew = match is null;
        MessageRecord record = match ?? new MessageRecord(
            Id:              string.Empty,
            Name:            spellName,
            Flags:           MessageFlags.None,
            RawFlagsHex:     0,
            CasterMessage:   string.Empty,
            TargetMessage:   string.Empty,
            WitnessMessage:  string.Empty,
            AppliedMessage:  string.Empty,
            AppliedEndsWith: string.Empty,
            Links:           new[] { new GameDataLink("Spells", spellNumber) });

        IReadOnlyList<GameDataInfoRow> info = new SpellInfoRowsBuilder(_cache).Build(spellNumber);
        MudPlay.Game.Spells.SpellFormulaInput? formula =
            new MudPlay.Game.Spells.KnownSpellCatalog(_cache).GetFormulaByNumber(spellNumber);

        MessageEditDialogViewModel vm = new(
            record,
            currentTier:     SettingsTier.Defaults,
            existingRecords: _messages.Messages,
            isNew:           isNew,
            cache:           _cache,
            gameDataInfo:    info,
            spellFormula:    formula);

        MessageEditDialogViewModel? previous = _openVm;
        _openVm = vm;
        _openSpell = spellNumber;
        previous?.CancelCommand.Execute(null);

        MessageEditResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        }
        finally
        {
            if (ReferenceEquals(_openVm, vm)) { _openVm = null; _openSpell = 0; }
        }
        if (result is null) return SummaryFor(spellNumber);

        // Id-keyed update-or-append into the store + persist (mirrors the browser's ApplyResult).
        int idx = -1;
        for (int i = 0; i < _messages.Messages.Count; i++)
            if (_messages.Messages[i].Id == result.Original.Id) { idx = i; break; }
        if (idx >= 0) _messages.Messages[idx] = result.Updated;
        else          _messages.Messages.Add(result.Updated);
        _messages.Save();

        return SummaryFor(spellNumber);
    }
}
