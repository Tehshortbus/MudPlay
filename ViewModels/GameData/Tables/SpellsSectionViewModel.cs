using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Spells tab. Renders the imported MajorMUD Spells table — fuel for the
// CastingDirector + the Settings → Spells / Party spell pickers + the Workshop Spell Book.
//
// Column names mirror the MajorMUD MDB schema verbatim. Short is the cast-name shortcode (e.g.
// "star"), ReqLevel is the cast prerequisite, Diff is the cast-difficulty score. Magery, AttType,
// and Targets render via LookupEnums ("Mage" / "Cold" / "Full Area" / etc.).
//
// Double-click a row → opens every MessageRecord that links Spells#N for that row's spell number
// in its own modeless MessageEditDialogViewModel. Multi-message spells (e.g. apostrophe / wording
// variants of one effect line) stack as cascaded windows the user drags apart if needed. Zero
// matches surfaces a one-shot info dialog naming the spell so the user sees the gap rather than a
// silent no-op.
public sealed class SpellsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly MessageStore? _messages;
    private readonly DialogService? _dialogs;
    private readonly SpellAilmentIndex _ailments;

    public override string Id => "spells";
    public override string Title => "Spells";

    protected override string TableName => "Spells";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "Short",
        "Magery",
        "MageryLVL",
        "ReqLevel",
        "ManaCost",
        "EnergyCost",
        "Diff",
        "AttType",
        "Targets",
        "MinBase",
        "MaxBase",
        "Dur",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "spell", "magery", "mana", "cast", "level", "code", "short", "target",
        // Ailment keywords the filter box understands (see RowMatches).
        "poison", "confuse", "blind", "hold", "ailment",
    };

    public override string? FilterHint =>
        "Filter by name, or type an ailment — poison / confuse / blind / hold — to list every spell that applies it.";

    // Enum-column formatters live on the shared SpellInfoRowsBuilder so the grid and the
    // dialog's Game Data tab always render enum columns the same way.
    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters
        => SpellInfoRowsBuilder.ColumnFormatters;

    // Double-click handler — opens every Message linked to this spell.
    public IAsyncRelayCommand<GameDataRow?> OpenLinkedMessagesCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenLinkedMessagesCommand;

    public SpellsSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        MessageStore? messages = null,
        DialogService? dialogs = null) : base(cache, resolver)
    {
        _cache    = cache;
        _messages = messages;
        _dialogs  = dialogs;
        _ailments = new SpellAilmentIndex(cache);
        OpenLinkedMessagesCommand = new AsyncRelayCommand<GameDataRow?>(OpenLinkedMessagesAsync);
    }

    // Extend the base name/text filter with ailment-keyword matching: typing an
    // exact ailment word (poison / confuse / blind / hold) also surfaces every
    // spell that APPLIES it, read from the spell's ability codes (following the
    // EndCast chain) rather than just spells with the word in their name.
    protected override bool RowMatches(GameDataRow row, string filter)
    {
        if (base.RowMatches(row, filter)) return true;
        if (SpellAilmentIndex.AilmentCodes.ContainsKey(filter)
            && int.TryParse(row.Get("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            && _ailments.Applies(number, filter))
            return true;
        return false;
    }

    private async Task OpenLinkedMessagesAsync(GameDataRow? row)
    {
        if (row is null || _messages is null || _dialogs is null) return;

        string? numText = row.Get("Number");
        if (!int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int spellNumber))
            return;
        string spellName = row.Get("Name") ?? string.Empty;

        // Post-restructure: each spell has at most ONE MessageRecord
        // (the wcc generator emits one record per spell-name with all
        // perspective slots populated as fields on the same record).
        // Find that one record by Spells#N link and open the editor —
        // or fall back to the "no message" InfoDialog if the spell
        // has no record at all.
        MessageRecord? match = _messages.Messages
            .FirstOrDefault(m => m.Links is not null && m.Links.Any(l =>
                string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                && l.Number == spellNumber));

        // Always open the tabbed dialog: the Message tab edits the player-cast
        // message (if any), and the Game Data tab shows the spell's imported
        // fields. A message only exists for spells the player casts — spells
        // cast by rooms / items / textblocks have none, so for those we open a
        // new record pre-linked to the spell (Message tab ready to author) while
        // the Game Data tab still surfaces what the spell does.
        IReadOnlyList<GameDataInfoRow> info = BuildSpellInfoRows(spellNumber);

        MessageRecord record;
        bool isNew;
        if (match is not null)
        {
            record = match;
            isNew = false;
        }
        else
        {
            record = new MessageRecord(
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
            isNew = true;
        }

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
        MessageEditResult? result = await _dialogs
            .OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;
        ApplyResult(result);
    }

    // Mirror of MessagesSectionViewModel.ApplyResult — Id-keyed update-or-append into the store +
    // persist.
    private void ApplyResult(MessageEditResult result)
    {
        if (_messages is null) return;
        int idx = -1;
        for (int i = 0; i < _messages.Messages.Count; i++)
        {
            if (_messages.Messages[i].Id == result.Original.Id) { idx = i; break; }
        }
        if (idx >= 0) _messages.Messages[idx] = result.Updated;
        else          _messages.Messages.Add(result.Updated);
        _messages.Save();
    }


    // Test seam — exercises BuildSpellInfoRows (the dialog's Game Data tab content) without
    // standing up a dialog.
    internal IReadOnlyList<GameDataInfoRow> BuildSpellInfoRowsForTests(int spellNumber)
        => BuildSpellInfoRows(spellNumber);

    // The spell's Game Data tab content lives in the shared SpellInfoRowsBuilder now, so the
    // same record opens by Number from outside the browser (Room Info → SpellRecordDialogService)
    // as well as from a browser row here. ColumnFormatters is passed so the dialog's enum rows
    // format the same as the grid's.
    private IReadOnlyList<GameDataInfoRow> BuildSpellInfoRows(int spellNumber)
        => new SpellInfoRowsBuilder(_cache).Build(spellNumber);
}
