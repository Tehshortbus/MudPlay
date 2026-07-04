using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Messages tab's per-record edit dialog. Edits
// one MessageRecord end-to-end: Name / Use-tier / four perspective line slots (Caster /
// Target / Witness / Applied + AppliedEndsWith) / Action / Effects flags / Response /
// Links. Commits on Save (Defaults tier writes back to MessageStore; other tiers are
// stubbed for the future SettingsResolver.WriteGameDataAt path) or discards on Cancel.
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

    // Verbatim response field — stored exactly as MegaMUD's UI would display it, including
    // literal ^M separators. No splitting happens here; the runtime consumer interprets
    // ^M / CR as multi-step boundaries when actually sending.
    [ObservableProperty] private string _response = string.Empty;
    [ObservableProperty] private MessageAction _action = MessageAction.Ignore;

    // Typed effect flags — bound to checkboxes in the dialog. Twelve
    // bits surfaced; the three MegaMUD-specific find-mode flags are
    // NOT exposed in the UI (the importer strips them at read time).
    [ObservableProperty] private bool _flagBlinded;
    [ObservableProperty] private bool _flagConfused;
    [ObservableProperty] private bool _flagPoisoned;
    [ObservableProperty] private bool _flagLosingHp;
    [ObservableProperty] private bool _flagMovementPrevented;
    [ObservableProperty] private bool _flagAttackPrevented;
    [ObservableProperty] private bool _flagDiseased;
    [ObservableProperty] private bool _flagHpRegenerating;
    [ObservableProperty] private bool _flagManaRegenerating;
    [ObservableProperty] private bool _flagEndsCombat;
    [ObservableProperty] private bool _flagLastActionFailed;
    [ObservableProperty] private bool _flagDisabled;

    public IReadOnlyList<MessageAction> AvailableActions { get; } =
        Enum.GetValues<MessageAction>().ToArray();

    public IReadOnlyList<TierOption> AvailableTiers { get; } = new[]
    {
        new TierOption(SettingsTier.Defaults,  "Defaults"),
        new TierOption(SettingsTier.Global,    "Global"),
        new TierOption(SettingsTier.Bbs,       "BBS"),
        new TierOption(SettingsTier.Character, "Character"),
    };

    // Editable Links list — see LinkRow for shape.
    public System.Collections.ObjectModel.ObservableCollection<LinkRow> LinkRows { get; } = new();

    public IReadOnlyList<string> LinkTables { get; } = new[] { "Spells", "Items", "Monsters" };

    [ObservableProperty] private string _addLinkTable = "Spells";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLinkStatus))]
    private string _addLinkNumber = string.Empty;

    public string AddLinkStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AddLinkNumber)) return "Pick a table + type a Number to add a link.";
            if (!int.TryParse(AddLinkNumber, out int n)) return $"'{AddLinkNumber}' is not a number.";
            string? name = _cache?.FindNameByNumber(AddLinkTable, n);
            return name is null
                ? $"{AddLinkTable}#{n} — no row with that Number in the active set."
                : $"Will add: {AddLinkTable}#{n} — {name}";
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

    // Tab the dialog opens on (0 = User Definitions, 1 = Game Data). A spell the player
    // can cast already has an authored cast message, so it opens on User Definitions where
    // the user's editable content lives. A spell with no message — cast by a room / item /
    // monster (e.g. a river's damage-on-entry spell) — opens on Game Data, the only
    // meaningful info for it. Plain Messages-tab edits (no Game Data tab) always stay on
    // tab 0.
    public int InitialTabIndex => (HasGameData && _isNew) ? 1 : 0;

    public string Title => _isNew ? "Message — (new)" : $"Message — {_original.Name}";

    // Placeholder-token legend shown under the Response field so an author editing a
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
            if (string.Equals(r.Id, projected, StringComparison.Ordinal)) return r;
        }
        return null;
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
        IReadOnlyList<GameDataInfoRow>? gameDataInfo = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(existingRecords);
        _original        = original;
        _existingRecords = existingRecords;
        _isNew           = isNew;
        _cache           = cache;
        GameDataInfo     = gameDataInfo ?? Array.Empty<GameDataInfoRow>();

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
        Response        = original.Response;
        Action          = original.Action;

        FlagBlinded           = original.Flags.HasFlag(MessageFlags.Blinded);
        FlagConfused          = original.Flags.HasFlag(MessageFlags.Confused);
        FlagPoisoned          = original.Flags.HasFlag(MessageFlags.Poisoned);
        FlagLosingHp          = original.Flags.HasFlag(MessageFlags.LosingHp);
        FlagMovementPrevented = original.Flags.HasFlag(MessageFlags.MovementPrevented);
        FlagAttackPrevented   = original.Flags.HasFlag(MessageFlags.AttackPrevented);
        FlagDiseased          = original.Flags.HasFlag(MessageFlags.Diseased);
        FlagHpRegenerating    = original.Flags.HasFlag(MessageFlags.HpRegenerating);
        FlagManaRegenerating  = original.Flags.HasFlag(MessageFlags.ManaRegenerating);
        FlagEndsCombat        = original.Flags.HasFlag(MessageFlags.EndsCombat);
        FlagLastActionFailed  = original.Flags.HasFlag(MessageFlags.LastActionFailed);
        FlagDisabled          = original.Flags.HasFlag(MessageFlags.Disabled);
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
            Action:          Action,
            Flags:           typed,
            RawFlagsHex:     raw,
            Response:        Response ?? string.Empty,
            CasterMessage:   CasterMessage   ?? string.Empty,
            TargetMessage:   TargetMessage   ?? string.Empty,
            WitnessMessage:  WitnessMessage  ?? string.Empty,
            AppliedMessage:  AppliedMessage  ?? string.Empty,
            AppliedEndsWith: AppliedEndsWith ?? string.Empty,
            Links:           LinkRows.Select(r => new GameDataLink(r.Table, r.Number)).ToList());

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
            if (string.Equals(existing.Table, AddLinkTable, StringComparison.Ordinal) &&
                existing.Number == n)
                return;
        }
        string? name = _cache?.FindNameByNumber(AddLinkTable, n);
        LinkRows.Add(new LinkRow(AddLinkTable, n, name));
        AddLinkNumber = string.Empty;
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
        if (FlagLosingHp)          f |= MessageFlags.LosingHp;
        if (FlagMovementPrevented) f |= MessageFlags.MovementPrevented;
        if (FlagAttackPrevented)   f |= MessageFlags.AttackPrevented;
        if (FlagDiseased)          f |= MessageFlags.Diseased;
        if (FlagHpRegenerating)    f |= MessageFlags.HpRegenerating;
        if (FlagManaRegenerating)  f |= MessageFlags.ManaRegenerating;
        if (FlagEndsCombat)        f |= MessageFlags.EndsCombat;
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
public sealed record GameDataInfoRow(string Label, string Value);

// One row in MessageEditDialogViewModel.LinkRows — pairs the back-reference's raw
// (Table, Number) with the game-data row's display Name resolved at dialog-open time.
public sealed record LinkRow(string Table, int Number, string? DisplayName)
{
    public string Label => DisplayName is null
        ? $"{Table}#{Number} (unknown)"
        : $"{Table}#{Number} — {DisplayName}";
}
