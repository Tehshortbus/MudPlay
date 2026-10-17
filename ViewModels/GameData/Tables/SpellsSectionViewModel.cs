using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
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
// Double-click a row → opens the spell's MessageRecord (the one linking Spells#N) in the tabbed
// MessageEditDialogViewModel, or a new record pre-linked to the spell when it has none; the Game
// Data tab shows the spell's imported fields either way.
//
// The Seed column and the "Differs from seed" filter show which spells' message records
// aren't plain seed records; "Compare with seed…" opens them beside the seed's and puts
// any the user picks back to it.
public sealed class SpellsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly MessageStore? _messages;
    private readonly DialogService? _dialogs;
    private readonly SpellAilmentIndex _ailments;

    public override string Id => "spells";
    public override string Title => "Spells";

    protected override string TableName => "Spells";

    // Wide record table — the user arranges its columns by dragging the headers.
    public override bool AllowColumnReorder => true;

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
        SeedColumn,
    };

    // How the spell's linked message record(s) differ from the shipped seed; blank when
    // they're plain seed records.
    private const string SeedColumn = "Seed";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "spell", "magery", "mana", "cast", "level", "code", "short", "target",
        // Ailment keywords the filter box understands (see RowMatches).
        "poison", "confuse", "blind", "hold", "ailment",
        "seed", "differs", "compare", "revert", "restore",
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

    // "Compare with seed…": the selected spells' message differences from the seed, or every
    // spell-linked difference (removed seed records included) when nothing is selected.
    // Concurrent executions allowed so a re-press while the dialog is open reaches the
    // handler, which raises the open window instead of opening another.
    public IAsyncRelayCommand CompareWithSeedCommand { get; }

    ICommand? IEditableTableSectionViewModel.SeedCompareCommand => CompareWithSeedCommand;

    private readonly BoolFilter _differsFromSeed = new(
        "Differs from seed", SeedColumn, v => !string.IsNullOrEmpty(v),
        "Only spells whose message isn't the plain seed record: your edited copy, flag / link " +
        "changes, a message you added for the spell, or a seed message you deleted.");

    // Spell-linked message differences and the Seed-column label per spell number. Built on
    // the UI thread whenever the catalogue changes and swapped in whole, because the row
    // build (ComputeRowCells) runs on a worker thread and must not read the live catalogue.
    private List<SeedDelta<MessageRecord>.Difference> _spellDifferences = [];
    private IReadOnlyDictionary<int, string> _seedStates = new Dictionary<int, string>();

    private readonly NotifyCollectionChangedEventHandler? _messagesHandler;

    // The open compare dialog, so a re-press raises it rather than opening a duplicate.
    private MessageSeedCompareDialogViewModel? _openCompare;

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
        CompareWithSeedCommand = new AsyncRelayCommand(
            CompareWithSeedAsync, () => _spellDifferences.Count > 0, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        FilterGroups.Add(new FilterGroup("Messages", bools: new[] { _differsFromSeed }));

        if (_messages is not null)
        {
            RefreshSeedStates();
            _messagesHandler = (_, _) => OnMessagesChanged();
            _messages.Messages.CollectionChanged += _messagesHandler;
        }
    }

    public override void Dispose()
    {
        if (_messages is not null && _messagesHandler is not null)
            _messages.Messages.CollectionChanged -= _messagesHandler;
        base.Dispose();
    }

    protected override IReadOnlyDictionary<string, string?>? ComputeRowCells(GameRow element)
    {
        if (!element.TryGetProperty("Number", out GameValue num) || !num.TryGetInt32(out int number))
            return null;
        return _seedStates.TryGetValue(number, out string? state)
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [SeedColumn] = state }
            : null;
    }

    private void RefreshSeedStates()
    {
        _spellDifferences = _messages is null
            ? []
            : _messages.SeedDifferences().Where(d => LinkedSpells(d).Any()).ToList();
        _seedStates = SeedStatesBySpell(_spellDifferences);
        CompareWithSeedCommand.NotifyCanExecuteChanged();
    }

    // A message edit anywhere can change a spell's Seed cell. Rebuild the rows only when
    // some spell's state actually moved, and keep the user's selected spell selected.
    private void OnMessagesChanged()
    {
        IReadOnlyDictionary<int, string> before = _seedStates;
        RefreshSeedStates();
        if (!IsLoaded || SameStates(before, _seedStates)) return;
        string? selected = SelectedRow?.Get("Number");
        Reload();
        if (selected is not null) SelectRowMatching(r => r.Get("Number") == selected);
    }

    private static bool SameStates(IReadOnlyDictionary<int, string> a, IReadOnlyDictionary<int, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? v) && v == kv.Value);

    // The Spells numbers a difference's message is linked to — either side's Links, since
    // an edited copy may have been re-linked.
    internal static IEnumerable<int> LinkedSpells(SeedDelta<MessageRecord>.Difference d)
        => new[] { d.Seed, d.Current }
            .Where(r => r?.Links is not null)
            .SelectMany(r => r!.Links!)
            .Where(l => string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Number)
            .Distinct();

    // The Seed-column text per spell: each kind of difference its messages carry, once each.
    internal static IReadOnlyDictionary<int, string> SeedStatesBySpell(IEnumerable<SeedDelta<MessageRecord>.Difference> differences)
    {
        Dictionary<int, SortedSet<SeedDifferenceKind>> kinds = new();
        foreach (SeedDelta<MessageRecord>.Difference d in differences)
            foreach (int spell in LinkedSpells(d))
            {
                if (!kinds.TryGetValue(spell, out SortedSet<SeedDifferenceKind>? set)) kinds[spell] = set = new();
                set.Add(d.Kind);
            }
        return kinds.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value.Select(SeedLabel)));
    }

    private static string SeedLabel(SeedDifferenceKind kind) => kind switch
    {
        SeedDifferenceKind.Edited   => "text edited",
        SeedDifferenceKind.Override => "fields edited",
        SeedDifferenceKind.Added    => "yours only",
        _                           => "removed",
    };

    private async Task CompareWithSeedAsync()
    {
        if (_dialogs is null || _messages is null) return;
        if (_openCompare is not null && _dialogs.RaiseIfOpen(_openCompare)) return;

        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });

        List<SeedDelta<MessageRecord>.Difference> differences =
            _messages.SeedDifferences().Where(d => LinkedSpells(d).Any()).ToList();
        if (selection.Count > 0)
        {
            HashSet<int> picked = new();
            foreach (GameDataRow row in selection)
                if (int.TryParse(row.Get("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    picked.Add(n);
            differences = differences.Where(d => LinkedSpells(d).Any(picked.Contains)).ToList();
            if (differences.Count == 0)
            {
                _dialogs.ShowInfo("Compare with seed",
                    "The selected spells' messages are plain seed records — nothing to compare. " +
                    "Clear the selection to compare every spell message that differs from the seed.");
                return;
            }
        }
        if (differences.Count == 0) return;

        MessageSeedCompareDialogViewModel vm = new(differences, _messages.ActiveSet);
        _openCompare = vm;
        MessageSeedCompareResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<MessageSeedCompareDialogViewModel, MessageSeedCompareResult>(vm);
        }
        finally
        {
            _openCompare = null;
        }
        if (result is null) return;

        int reverted = result.UseSeed.Count == 0 ? 0 : _messages.RevertToSeed(result.UseSeed);
        int stale = result.UseSeed.Count - reverted;
        AppServices.Current.Log.Info("Messages",
            $"Compare with seed: reverted {reverted} spell message(s) to the seed, kept {result.Kept} of yours" +
            (stale > 0 ? $"; {stale} changed since the comparison opened and were left as they are." : "."));
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

        // Each spell has at most ONE MessageRecord (one record per
        // spell name, every perspective slot a field on it); find it by
        // its Spells#N link.
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
            existingRecords: _messages.Messages,
            isNew:           isNew,
            cache:           _cache,
            gameDataInfo:    info,
            spellFormula:    formula,
            effectTree:      new SpellInfoRowsBuilder(_cache).BuildEffectTree(spellNumber));
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


    // The spell's Game Data tab content lives in the shared SpellInfoRowsBuilder now, so the
    // same record opens by Number from outside the browser (Room Info → SpellRecordDialogService)
    // as well as from a browser row here. ColumnFormatters is passed so the dialog's enum rows
    // format the same as the grid's.
    private IReadOnlyList<GameDataInfoRow> BuildSpellInfoRows(int spellNumber)
        => new SpellInfoRowsBuilder(_cache).Build(spellNumber);
}
