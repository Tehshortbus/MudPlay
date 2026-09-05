using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

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
    public override string Title => "Incomplete Messages";

    // Description banner (renders under the title): explains the two kinds of record that
    // land here — spell-linked messages still missing a required line (the fill-from-game
    // worklist) and orphan records tied to no spell/item. A complete spell/item message is
    // edited from its own tab and stays hidden.
    public override string? BannerText =>
        "Two kinds of message need attention here. A spell-linked record still missing a " +
        "required line — caster, target, witness, applied, wears-off, or (on a Confused " +
        "record) the fumble line — is listed with the gaps in the Missing column; fill them " +
        "in from the game, or type {null} / {void} / {empty} in a line the spell simply " +
        "doesn't have. A record tied to no spell or item shows as an orphan awaiting a link. " +
        "Spells with no message record at all are listed separately in the Spell coverage report.";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Spell #", "Name", "Missing", "Lines", "Preview",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "messages", "incomplete", "unfiltered", "missing", "worklist", "orphan",
        "spell", "number", "condition", "pattern", "caster", "target", "witness", "applied",
        "wears-off", "fumble", "blinded", "poisoned", "paralyzed", "confused", "diseased",
    };

    // Open the per-record edit dialog for the row currently double-clicked.
    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    // Far-right toolbar action: export the user's message edits (vs the shipped seed) to a
    // Markdown file on the Desktop, so a curated line can be folded back into the seed.
    public IRelayCommand UploadEditsCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand     => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;
    ICommand? IEditableTableSectionViewModel.ExportCommand  => UploadEditsCommand;
    string?   IEditableTableSectionViewModel.ExportLabel    => "Upload edits";

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
        UploadEditsCommand    = new RelayCommand(UploadEdits);

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
        HashSet<int> spellNumbers = _cache?.RowNumbers("Spells") ?? new HashSet<int>();
        HashSet<int> itemNumbers = _cache?.RowNumbers("Items") ?? new HashSet<int>();

        foreach (MessageRecord m in _store.Messages)
        {
            // A disabled record is switched off wholesale — it recognizes nothing and is
            // parked deliberately, so it's neither a worklist item nor an orphan to chase.
            if (m.Flags.HasFlag(MessageFlags.Disabled)) continue;

            string missing;
            if (IsClaimedByExistingSpell(m, spellNumbers))
            {
                // A spell's message is edited from the Spells section, so a COMPLETE one is
                // hidden here (listing the same record under both tabs is confusing). An
                // INCOMPLETE one — a required perspective/applied slot still blank — surfaces
                // as a worklist item: the "fill these in from in-game" list.
                IReadOnlyList<string> gaps = MissingSlots(m);
                if (gaps.Count == 0) continue;
                missing = string.Join(", ", gaps);
            }
            else if (IsClaimedByExistingItem(m, itemNumbers))
            {
                // An item-claimed message (its "use <item>" buff line or weapon-proc line) is
                // edited from the item dialog's Message section, so it never surfaces here.
                continue;
            }
            else
            {
                // Tied to no spell/item in this set — an orphan awaiting a link (renamed-away
                // spells, standalone detectors, records whose only link is orphaned).
                missing = "not linked to a spell/item";
            }

            // Lines column = compact tag string showing which perspective slots ARE populated,
            // e.g. "C T W A•" (Caster+Target+Witness + Applied pair). Missing column = the
            // still-blank required slots. Preview column = first non-empty line for a quick read.
            // Spell # column = the linked Spell record number(s), blank when tied to no spell.
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Spell #"] = SpellNumbers(m),
                ["Name"]    = m.Name,
                ["Missing"] = missing,
                ["Lines"]   = BuildLineTags(m),
                ["Preview"] = FirstNonEmptyLine(m),
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            row.Tag = m;
            if (_resolver is not null)
                row.SourceTier = _resolver.GetGameDataSourceTier("Messages", m.Id);
            rows.Add(row);
        }
    }

    // The linked Spell record number(s) for the leading "Spell #" column — the Number of
    // every Spells back-reference (comma-joined when a record aliases several spells),
    // blank when the record is tied to no spell. Shown whether or not the spell exists in
    // the active set (an orphaned link still names the number it points at).
    internal static string SpellNumbers(MessageRecord m)
    {
        if (m.Links is null) return string.Empty;
        List<int>? nums = null;
        foreach (GameDataLink link in m.Links)
            if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase))
                (nums ??= new()).Add(link.Number);
        return nums is null ? string.Empty : string.Join(", ", nums);
    }

    // The required message slots for a spell-linked record, in fill order: the three
    // perspective lines + the applied/wear-off pair, plus the confuse-fumble line when the
    // record is flagged Confused. A slot counts as FILLED when it holds any text — real
    // wording OR one of the {null}/{void}/{empty} "no such line" sentinels (the user's
    // explicit "this spell has no wording here"), so a sentinel drops the slot off this
    // list exactly as real text would. Returns the labels of the still-blank slots; empty
    // when the record is fully triaged. Drives both the Incomplete row inclusion and its
    // "Missing" column.
    internal static IReadOnlyList<string> MissingSlots(MessageRecord m)
    {
        List<string> gaps = new(6);
        if (string.IsNullOrWhiteSpace(m.CasterMessage))   gaps.Add("Caster");
        if (string.IsNullOrWhiteSpace(m.TargetMessage))   gaps.Add("Target");
        if (string.IsNullOrWhiteSpace(m.WitnessMessage))  gaps.Add("Witness");
        if (string.IsNullOrWhiteSpace(m.AppliedMessage))  gaps.Add("Applied");
        if (string.IsNullOrWhiteSpace(m.AppliedEndsWith)) gaps.Add("Wears-off");
        if (m.Flags.HasFlag(MessageFlags.Confused) && string.IsNullOrWhiteSpace(m.ConfuseFumbleLine))
            gaps.Add("Fumble");
        return gaps;
    }

    // True when the record is claimed by a spell present in the active set — a Links
    // back-reference to a Spells row whose Number exists. Such records are edited from the
    // Spells section, so a COMPLETE one is hidden here (an INCOMPLETE one still surfaces as
    // a worklist item — see PopulateRows). An orphaned Spells link (spell not in this set)
    // does NOT claim it, so it stays listed here as an orphan (its only reachable editor).
    internal static bool IsClaimedByExistingSpell(MessageRecord m, HashSet<int> spellNumbers)
    {
        if (m.Links is null) return false;
        foreach (GameDataLink link in m.Links)
            if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                && spellNumbers.Contains(link.Number))
                return true;
        return false;
    }

    // True when the record is claimed by an item present in the active set — a Links
    // back-reference to an Items row whose Number exists. Such records (on-use buffs,
    // weapon procs) are edited from the item dialog's Message section, so the Messages
    // tab hides them. An orphaned Items link (item not in this set) does NOT claim it.
    internal static bool IsClaimedByExistingItem(MessageRecord m, HashSet<int> itemNumbers)
    {
        if (m.Links is null) return false;
        foreach (GameDataLink link in m.Links)
            if (string.Equals(link.Table, "Items", StringComparison.OrdinalIgnoreCase)
                && itemNumbers.Contains(link.Number))
                return true;
        return false;
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

        // When the record is anchored to a spell present in the active set, populate the
        // read-only Game Data tab (spell facts + damage calculator) the same way the Spells
        // tab does — so an incomplete record can be filled with the spell's details in view.
        IReadOnlyList<GameDataInfoRow>? info = null;
        Game.Spells.SpellFormulaInput? formula = null;
        if (_cache is not null && TryLinkedSpellNumber(original, out int spellNumber))
        {
            info = new SpellInfoRowsBuilder(_cache).Build(spellNumber);
            formula = new Game.Spells.KnownSpellCatalog(_cache).GetFormulaByNumber(spellNumber);
        }

        MessageEditDialogViewModel vm = new(
            original,
            row.SourceTier,
            _store.Messages,
            isNew: false,
            cache: _cache,
            gameDataInfo: info,
            spellFormula: formula);
        MessageEditResult? result = await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        ApplyResult(result);
    }

    // First Spells back-reference whose Number exists in the active set (so the Game Data
    // tab has real content to show). False for orphan links / non-spell records.
    private bool TryLinkedSpellNumber(MessageRecord m, out int spellNumber)
    {
        spellNumber = 0;
        if (m.Links is null || _cache is null) return false;
        HashSet<int> spellNumbers = _cache.RowNumbers("Spells");
        foreach (GameDataLink link in m.Links)
            if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                && spellNumbers.Contains(link.Number))
            {
                spellNumber = link.Number;
                return true;
            }
        return false;
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
            Flags:           MessageFlags.None,
            RawFlagsHex:     0,
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

    // Diff the active set's live catalogue against the bundled (shipped) seed for its realm
    // and write every ADDED / CHANGED record to a Markdown file on the Desktop — the
    // user's message edits, keyed by spell / item, for the dev to fold back into the seed.
    // No-op (logged) when no set is active or the seed can't be read.
    private void UploadEdits()
    {
        AppServices svc = AppServices.Current;
        string? set = _store.ActiveSet;
        if (string.IsNullOrWhiteSpace(set))
        {
            svc.Log.Warn("Messages", "Upload edits: no active game-data set — nothing to export.");
            return;
        }

        string realm = GameDataRealm.Resolve(set);
        List<MessageRecord> baseline;
        try
        {
            baseline = JsonStore.Load<List<MessageRecord>>(AppPaths.BundledMessagesSeedFile(realm))
                       ?? new List<MessageRecord>();
        }
        catch (Exception ex)
        {
            svc.Log.Error("Messages", $"Upload edits: couldn't read the bundled '{realm}' seed: {ex.Message}");
            return;
        }

        IReadOnlyList<MessageEditExporter.RecordEdit> edits = MessageEditExporter.Diff(_store.Messages, baseline);
        DateTime now = DateTime.Now;
        string content = MessageEditExporter.Render(edits, realm, set, now);
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = Path.Combine(desktop, MessageEditExporter.FileName(realm, now));
            File.WriteAllText(path, content);
            svc.Log.Info("Messages",
                $"Upload edits: wrote {edits.Count} message edit(s) for realm '{realm}' to {path}.");
        }
        catch (Exception ex)
        {
            svc.Log.Error("Messages", $"Upload edits: failed to write the export file: {ex.Message}");
        }
    }
}
