using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Spells tab. Renders the imported MajorMUD
/// <c>Spells</c> table — fuel for the Phase 13 CastingDirector + the
/// Settings → Spells / Party spell pickers + the Phase 9 Workshop
/// Spell Book.
/// </summary>
/// <remarks>
/// <para>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>Short</c>
/// is the cast-name shortcode (e.g. <c>"star"</c>), <c>ReqLevel</c> is
/// the cast prerequisite, <c>Diff</c> is the cast-difficulty score.
/// <c>Magery</c>, <c>AttType</c>, and <c>Targets</c> render via
/// <see cref="MmudEnums"/> ("Mage" / "Cold" / "Full Area" / etc.).
/// </para>
/// <para>
/// Double-click a row → opens every <see cref="MessageRecord"/> that
/// links <c>Spells#N</c> for that row's spell number in its own
/// modeless <see cref="MessageEditDialogViewModel"/>. Multi-message
/// spells (e.g. apostrophe / wording variants of one effect line)
/// stack as cascaded windows the user drags apart if needed. Zero
/// matches surfaces a one-shot info dialog naming the spell so the
/// user sees the gap rather than a silent no-op.
/// </para>
/// </remarks>
public sealed class SpellsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly MessageStore? _messages;
    private readonly DialogService? _dialogs;

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
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magery"]  = MmudEnums.FormatMagery,
            ["AttType"] = MmudEnums.FormatSpellAttackType,
            ["Targets"] = MmudEnums.FormatSpellTargets,
        };

    /// <summary>Double-click handler — opens every Message linked to this spell.</summary>
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
        OpenLinkedMessagesCommand = new AsyncRelayCommand<GameDataRow?>(OpenLinkedMessagesAsync);
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

        if (match is null)
        {
            _dialogs.ShowInfo(
                "No associated Messages Found",
                $"No Messages link \"{spellName}\" (Spell #{spellNumber}).");
            return;
        }

        MessageEditDialogViewModel vm = new(
            match,
            currentTier:     SettingsTier.Defaults,
            existingRecords: _messages.Messages,
            isNew:           false,
            cache:           _cache);
        MessageEditResult? result = await _dialogs
            .OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;
        ApplyResult(result);
    }

    /// <summary>
    /// Mirror of <c>MessagesSectionViewModel.ApplyResult</c> — Id-keyed
    /// update-or-append into the store + persist.
    /// </summary>
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
}
