using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Incomplete Messages tab's per-record edit
// dialog. Edits one MessageRecord end-to-end: Name / Use-tier / four perspective line
// slots (Caster / Target / Witness / Applied + AppliedEndsWith) / Effects flags / Links.
// A message is recognition only — no action, no response (those live in Triggers).
// Commits on Save (Defaults tier writes back to MessageStore; other tiers are stubbed
// for the future SettingsResolver.WriteGameDataAt path) or discards on Cancel.
//
// Validation runs live — StatusMessage + HasError flag the dialog when Name is blank,
// when no perspective line has any text (record would carry no matchable content), or
// when the projected Id would collide with another existing record's identity tuple.
// Save is gated on no errors.
public sealed partial class MessageEditDialogViewModel : ObservableObject, IDialogViewModel<MessageEditResult>
{
    public event Action<MessageEditResult?>? CloseRequested;

    private readonly MessageRecord _original;
    private readonly IReadOnlyCollection<MessageRecord> _existingRecords;
    private readonly bool _isNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Defaults;

    // ----- Five perspective line slots -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _casterMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _targetMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _witnessMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _appliedMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _appliedEndsWith = string.Empty;

    // Typed effect flags — bound to checkboxes in the dialog. Twelve
    // bits surfaced; the MegaMUD find-mode flags and the four retired inert
    // effect bits (LosingHp / HpRegenerating / ManaRegenerating / EndsCombat)
    // are NOT exposed in the UI.
    [ObservableProperty] private bool _flagBlinded;
    [ObservableProperty] private bool _flagConfused;
    [ObservableProperty] private bool _flagPoisoned;
    [ObservableProperty] private bool _flagMovementPrevented;
    [ObservableProperty] private bool _flagAttackPrevented;
    [ObservableProperty] private bool _flagDiseased;
    [ObservableProperty] private bool _flagLastActionFailed;
    [ObservableProperty] private bool _flagDisabled;

    // Per-source confusion fumble line(s), one wording per line — the textbox binding
    // it is shown only while Confused is checked. Not part of the record identity, so
    // editing it never re-Ids the record.
    [ObservableProperty] private string _confuseFumbleLine = string.Empty;

    // Engine-driven response sent when this record's spell is detected cast (the temp
    // death-spell recovery), '^M' = carriage return. Not part of the record identity.
    [ObservableProperty] private string _castResponse = string.Empty;

    // Pending per-field collisions surfaced when a spell number is linked whose
    // record already has content that differs from what's in the dialog (an
    // unrecognized line being committed). The inline resolver panel binds these;
    // empty when there are none (silent auto-fill only). See TryAutofillFromRecord.
    public ObservableCollection<LinkFillConflict> LinkFillConflicts { get; } = new();
    public bool HasLinkFillConflicts => LinkFillConflicts.Count > 0;

    public IReadOnlyList<TierOption> AvailableTiers { get; } = new[]
    {
        new TierOption(SettingsTier.Defaults,  "Defaults"),
        new TierOption(SettingsTier.Global,    "Global"),
        new TierOption(SettingsTier.Bbs,       "BBS"),
        new TierOption(SettingsTier.Character, "Character"),
    };

    // Editable Links list — see LinkRow for shape.
    public System.Collections.ObjectModel.ObservableCollection<LinkRow> LinkRows { get; } = new();

    // A message always attributes to a Spells row — this dialog exists to author
    // the message text a spell record lacks (the MDB imports every spell field
    // EXCEPT its caster/target/witness/applied/wears-off lines). Item on-use and
    // monster abilities both resolve to a spell in the Spells table, so the
    // add-link table is a fixed "Spells" (no picker); AddLink builds its link from it.
    private const string LinkTable = "Spells";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLinkStatus))]
    private string _addLinkNumber = string.Empty;

    public string AddLinkStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AddLinkNumber)) return "Type a spell number to add a link.";
            if (!int.TryParse(AddLinkNumber, out int n)) return $"'{AddLinkNumber}' is not a number.";
            string? name = _cache?.FindNameByNumber(LinkTable, n);
            return name is null
                ? $"{LinkTable}#{n} — no row with that Number in the active set."
                : $"Will add: {LinkTable}#{n} — {name}";
        }
    }

    private readonly GameDataCache? _cache;

    // Optional read-only "Game Data" tab content — the source row's imported fields
    // (label / value), shown alongside the editable message when the dialog is opened for
    // a game-data row (e.g. a spell). Empty for the plain Messages-tab edit, which hides
    // the tab.
    public IReadOnlyList<GameDataInfoRow> GameDataInfo { get; }

    // True when the Game Data tab has content to show.
    public bool HasGameData => GameDataInfo.Count > 0;

    // Interactive damage calculator (level + resist pickers), non-null only for a
    // damage spell. Sits at the top of the Game Data tab.
    public SpellDamageCalcViewModel? DamageCalc { get; }

    // Drives the calculator panel's visibility.
    public bool HasDamageCalc => DamageCalc is not null;

    // Tab the dialog opens on (0 = User Definitions, 1 = Game Data). A spell the player
    // can cast already has an authored cast message, so it opens on User Definitions where
    // the user's editable content lives. A spell with no message — cast by a room / item /
    // monster (e.g. a river's damage-on-entry spell) — opens on Game Data, the only
    // meaningful info for it. Plain Messages-tab edits (no Game Data tab) always stay on
    // tab 0.
    public int InitialTabIndex => (HasGameData && _isNew) ? 1 : 0;

    public string Title => _isNew ? "Message — (new)" : $"Message — {_original.Name}";

    // Placeholder-token legend shown below the line fields so an author editing a
    // message line can see which bracket pins which capture slot (the meaning surfaces on
    // hover). Sourced from the matcher itself so the editor and the runtime interpreter
    // never drift.
    public IReadOnlyList<Game.Spells.MessagePlaceholder> Placeholders =>
        Game.Spells.CasterMessageMatcher.Placeholders;

    // The Id the record would have at save time given the current Name + all five line
    // slots. Used internally for duplicate detection; not surfaced in the UI.
    public string ProjectedId
        => MessageRecord.ComputeId(
            Name            ?? string.Empty,
            CasterMessage   ?? string.Empty,
            TargetMessage   ?? string.Empty,
            WitnessMessage  ?? string.Empty,
            AppliedMessage  ?? string.Empty,
            AppliedEndsWith ?? string.Empty);

    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Name is required.";
        bool hasAnyLine =
            !string.IsNullOrWhiteSpace(CasterMessage)  ||
            !string.IsNullOrWhiteSpace(TargetMessage)  ||
            !string.IsNullOrWhiteSpace(WitnessMessage) ||
            !string.IsNullOrWhiteSpace(AppliedMessage);
        if (!hasAnyLine) return "At least one perspective line (Caster / Target / Witness / Applied) is required.";
        if (FindDuplicate() is { } dup)
            return $"Another record already has this identity (Name + all four lines): '{dup.Name}'.";
        return null;
    }

    private MessageRecord? FindDuplicate()
    {
        string projected = ProjectedId;
        foreach (MessageRecord r in _existingRecords)
        {
            if (!_isNew && string.Equals(r.Id, _original.Id, StringComparison.Ordinal)) continue;
            if (!string.Equals(r.Id, projected, StringComparison.Ordinal)) continue;
            // Same Name + all four lines — but a record anchored to a DIFFERENT game-data
            // row (another spell/item) is a legitimate alias, not a duplicate: the game
            // shows the same text for several spells (three separate 'disease' spells all
            // read "You are diseased"). Only a record that shares THIS one's links (or, like
            // it, carries none) is a true duplicate worth blocking.
            if (LinksEqual(r)) return r;
        }
        return null;
    }

    // The record's back-references (Table#Number, table stem lower-cased) as a set,
    // compared to the links currently in the editor. Two link-less records match.
    private bool LinksEqual(MessageRecord r)
    {
        HashSet<string> mine = LinkRows
            .Select(l => $"{l.Table.ToLowerInvariant()}#{l.Number}")
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> theirs = (r.Links ?? Array.Empty<GameDataLink>())
            .Select(l => $"{l.Table.ToLowerInvariant()}#{l.Number}")
            .ToHashSet(StringComparer.Ordinal);
        return mine.SetEquals(theirs);
    }

    public bool HasError => GetValidationError() is not null;
    public bool CanSave  => !HasError;

    // Validation error to surface (red) under the header, or empty when the record is
    // valid — in the valid case the placeholder legend takes this slot instead. The
    // projected Id is no longer shown here.
    public string StatusMessage => GetValidationError() ?? string.Empty;

    public MessageEditDialogViewModel(
        MessageRecord original,
        SettingsTier currentTier,
        IReadOnlyCollection<MessageRecord> existingRecords,
        bool isNew,
        GameDataCache? cache = null,
        IReadOnlyList<GameDataInfoRow>? gameDataInfo = null,
        MudPlay.Game.Spells.SpellFormulaInput? spellFormula = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(existingRecords);
        _original        = original;
        _existingRecords = existingRecords;
        _isNew           = isNew;
        _cache           = cache;
        GameDataInfo     = gameDataInfo ?? Array.Empty<GameDataInfoRow>();

        // A damage spell drives an interactive level/resist damage calculator on
        // the Game Data tab (see SpellDamageCalcViewModel); the redundant static
        // damage rows are suppressed upstream in SpellInfoRowsBuilder.
        if (spellFormula is { } f && MudPlay.Game.Spells.SpellDamageCalculator.IsDamageSpell(f))
            DamageCalc = new SpellDamageCalcViewModel(f);

        if (original.Links is { Count: > 0 } links)
        {
            foreach (GameDataLink link in links)
            {
                string? name = cache?.FindNameByNumber(link.Table, link.Number);
                LinkRows.Add(new LinkRow(link.Table, link.Number, name));
            }
        }

        Name            = original.Name;
        UseTier         = currentTier;
        CasterMessage   = original.CasterMessage;
        TargetMessage   = original.TargetMessage;
        WitnessMessage  = original.WitnessMessage;
        AppliedMessage  = original.AppliedMessage;
        AppliedEndsWith = original.AppliedEndsWith;
        ConfuseFumbleLine = original.ConfuseFumbleLine;
        CastResponse      = original.CastResponse;

        LoadFlags(original.Flags);
    }

    private void LoadFlags(MessageFlags flags)
    {
        FlagBlinded           = flags.HasFlag(MessageFlags.Blinded);
        FlagConfused          = flags.HasFlag(MessageFlags.Confused);
        FlagPoisoned          = flags.HasFlag(MessageFlags.Poisoned);
        FlagMovementPrevented = flags.HasFlag(MessageFlags.MovementPrevented);
        FlagAttackPrevented   = flags.HasFlag(MessageFlags.AttackPrevented);
        FlagDiseased          = flags.HasFlag(MessageFlags.Diseased);
        FlagLastActionFailed  = flags.HasFlag(MessageFlags.LastActionFailed);
        FlagDisabled          = flags.HasFlag(MessageFlags.Disabled);
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        MessageFlags typed = AssembleFlags();
        ushort reservedBits = (ushort)(_original.RawFlagsHex & ReservedBitsMask);
        ushort raw = (ushort)((ushort)typed | reservedBits);

        MessageRecord updated = new(
            Id:              MessageRecord.ComputeId(
                                 Name, CasterMessage, TargetMessage, WitnessMessage,
                                 AppliedMessage, AppliedEndsWith),
            Name:            Name,
            Flags:           typed,
            RawFlagsHex:     raw,
            CasterMessage:   CasterMessage   ?? string.Empty,
            TargetMessage:   TargetMessage   ?? string.Empty,
            WitnessMessage:  WitnessMessage  ?? string.Empty,
            AppliedMessage:  AppliedMessage  ?? string.Empty,
            AppliedEndsWith: AppliedEndsWith ?? string.Empty,
            Links:           LinkRows.Select(r => new GameDataLink(r.Table, r.Number)).ToList(),
            ConfuseFumbleLine: ConfuseFumbleLine ?? string.Empty,
            CastResponse:      CastResponse ?? string.Empty);

        CloseRequested?.Invoke(new MessageEditResult(_original, updated, UseTier));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void AddLink()
    {
        if (!int.TryParse(AddLinkNumber, out int n)) return;
        foreach (LinkRow existing in LinkRows)
        {
            if (string.Equals(existing.Table, LinkTable, StringComparison.Ordinal) &&
                existing.Number == n)
                return;
        }
        string? name = _cache?.FindNameByNumber(LinkTable, n);
        LinkRows.Add(new LinkRow(LinkTable, n, name));
        AddLinkNumber = string.Empty;
        TryAutofillFromRecord(n);
    }

    // When a spell number is linked whose record already carries message text,
    // pull that text in: empty slots fill silently; a slot the dialog already
    // holds (the unrecognized line being committed) that DIFFERS from the record
    // surfaces as a per-field collision in the inline resolver. Name fills when
    // blank; on a fresh commit the record's Effects flags are adopted (a candidate
    // starts with none). No matching record ⇒ no-op.
    private void TryAutofillFromRecord(int spellNumber)
    {
        MessageRecord? rec = _existingRecords.FirstOrDefault(r =>
            !ReferenceEquals(r, _original)
            && r.Links is { } ls
            && ls.Any(l => string.Equals(l.Table, LinkTable, StringComparison.OrdinalIgnoreCase)
                           && l.Number == spellNumber));
        if (rec is null) return;

        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(rec.Name)) Name = rec.Name;
        if (_isNew) LoadFlags(rec.Flags);

        LinkFillConflicts.Clear();
        MergeField("Caster",         rec.CasterMessage,   () => CasterMessage,   v => CasterMessage = v);
        MergeField("Target",         rec.TargetMessage,   () => TargetMessage,   v => TargetMessage = v);
        MergeField("Witness",        rec.WitnessMessage,  () => WitnessMessage,  v => WitnessMessage = v);
        MergeField("Applied",        rec.AppliedMessage,  () => AppliedMessage,  v => AppliedMessage = v);
        MergeField("Wears off",      rec.AppliedEndsWith, () => AppliedEndsWith, v => AppliedEndsWith = v);
        MergeField("Confuse fumble", rec.ConfuseFumbleLine, () => ConfuseFumbleLine, v => ConfuseFumbleLine = v);
        MergeField("Cast response",  rec.CastResponse,    () => CastResponse,    v => CastResponse = v);
        OnPropertyChanged(nameof(HasLinkFillConflicts));
    }

    // Silent-fill when the dialog's slot is empty; skip when equal or when the
    // record's value is empty (nothing to overwrite the unrecognized line with);
    // otherwise record it as a collision for the user to resolve.
    private void MergeField(string label, string? recordValue, Func<string> get, Action<string> set)
    {
        string current = get() ?? string.Empty;
        string incoming = recordValue ?? string.Empty;
        if (string.IsNullOrEmpty(current)) { if (incoming.Length > 0) set(incoming); return; }
        if (string.Equals(current, incoming, StringComparison.Ordinal)) return;
        if (incoming.Length == 0) return;
        LinkFillConflicts.Add(new LinkFillConflict(label, incoming, current, set));
    }

    // Resolve every pending collision to its chosen source, then clear the panel.
    [RelayCommand]
    private void ApplyLinkFill()
    {
        foreach (LinkFillConflict c in LinkFillConflicts)
            c.Apply(c.UseRecord ? c.RecordValue : c.UnrecognizedValue);
        LinkFillConflicts.Clear();
        OnPropertyChanged(nameof(HasLinkFillConflicts));
    }

    [RelayCommand]
    private void RemoveLink(LinkRow? row)
    {
        if (row is null) return;
        LinkRows.Remove(row);
    }

    private MessageFlags AssembleFlags()
    {
        MessageFlags f = MessageFlags.None;
        if (FlagBlinded)           f |= MessageFlags.Blinded;
        if (FlagConfused)          f |= MessageFlags.Confused;
        if (FlagPoisoned)          f |= MessageFlags.Poisoned;
        if (FlagMovementPrevented) f |= MessageFlags.MovementPrevented;
        if (FlagAttackPrevented)   f |= MessageFlags.AttackPrevented;
        if (FlagDiseased)          f |= MessageFlags.Diseased;
        if (FlagLastActionFailed)  f |= MessageFlags.LastActionFailed;
        if (FlagDisabled)          f |= MessageFlags.Disabled;
        return f;
    }

    // Single bit preserved across save — the reserved 0x0800 the legacy MegaMUD format
    // defines but doesn't otherwise use. Anything outside the typed-flag mask is stripped
    // on save.
    private const ushort ReservedBitsMask = 0x0800;
}

// Result returned from MessageEditDialogViewModel on Save.
public sealed record MessageEditResult(
    MessageRecord Original,
    MessageRecord Updated,
    SettingsTier  Tier);

// One Use-dropdown row — friendly label for a SettingsTier.
public sealed record TierOption(SettingsTier Value, string Label);

// One row on the dialog's read-only Game Data tab — a field label and its rendered value
// from the source game-data row.
// One row of a spell/item's read-only Game Data tab: a Label and its Value text.
// Links, when present, are the clickable record references rendered in place of
// the plain Value (Value still holds the same names as text — the fallback the
// template shows when there are no links, and what tests read).
public sealed record GameDataInfoRow(string Label, string Value, IReadOnlyList<GameDataRecordLink>? Links = null)
{
    public bool HasLinks => Links is { Count: > 0 };
}

// One row in MessageEditDialogViewModel.LinkRows — pairs the back-reference's raw
// (Table, Number) with the game-data row's display Name resolved at dialog-open time.
public sealed record LinkRow(string Table, int Number, string? DisplayName)
{
    public string Label => DisplayName is null
        ? $"{Table}#{Number} (unknown)"
        : $"{Table}#{Number} — {DisplayName}";
}

// One field collision surfaced when a linked spell's record and the dialog (the
// unrecognized line being committed) both have text for the same slot. UseRecord
// (default true) is the user's per-field pick; Apply writes the chosen value back
// into the dialog field via the captured setter.
public sealed partial class LinkFillConflict : ObservableObject
{
    private readonly Action<string> _apply;

    public string FieldLabel { get; }
    public string RecordValue { get; }
    public string UnrecognizedValue { get; }

    [ObservableProperty] private bool _useRecord = true;

    public LinkFillConflict(string fieldLabel, string recordValue, string unrecognizedValue, Action<string> apply)
    {
        FieldLabel        = fieldLabel;
        RecordValue       = recordValue;
        UnrecognizedValue = unrecognizedValue;
        _apply            = apply;
    }

    public void Apply(string value) => _apply(value);
}
