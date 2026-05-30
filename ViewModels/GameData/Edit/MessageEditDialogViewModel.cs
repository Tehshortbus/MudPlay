using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// View-model for the Game Data Browser → Messages tab's per-record
/// edit dialog. Mirrors MMUD Explorer's "Game Message Details" dialog:
/// editable Name / Use-tier / Message / EndsWith / Response, the 15-bit
/// effect-flag checkboxes, and the 7-value Action radio. Commits on
/// Save (Defaults tier writes back to <see cref="MessageStore"/>;
/// other tiers go via <see cref="SettingsResolver.WriteGameDataAt"/>)
/// or discards on Cancel.
/// </summary>
public sealed partial class MessageEditDialogViewModel : ObservableObject, IDialogViewModel<MessageEditResult>
{
    public event Action<MessageEditResult?>? CloseRequested;

    private readonly MessageRecord _original;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Defaults;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _endsWith = string.Empty;
    [ObservableProperty] private string _response = string.Empty;
    [ObservableProperty] private MessageAction _action = MessageAction.Ignore;

    // Typed effect flags — bound to checkboxes in the dialog.
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

    // Find/use flags — separate group in MMUD Explorer's dialog.
    [ObservableProperty] private bool _flagFindInText;
    [ObservableProperty] private bool _flagFindInConversations;
    [ObservableProperty] private bool _flagUseWhenChasing;
    [ObservableProperty] private bool _flagDisabled;

    public IReadOnlyList<MessageAction> AvailableActions { get; } =
        Enum.GetValues<MessageAction>().ToArray();

    public IReadOnlyList<SettingsTier> AvailableTiers { get; } =
        Enum.GetValues<SettingsTier>().ToArray();

    public string Title => $"Message — {(_original.Name.Length > 0 ? _original.Name : "(new)")}";

    public MessageEditDialogViewModel(MessageRecord original, SettingsTier currentTier)
    {
        _original = original;
        Name      = original.Name;
        UseTier   = currentTier;
        Message   = original.Message;
        EndsWith  = original.EndsWith;
        Response  = string.Join('\n', original.ResponseCommands);
        Action    = original.Action;

        // Hydrate the typed flag checkboxes from the record's flags.
        FlagBlinded             = original.Flags.HasFlag(MessageFlags.Blinded);
        FlagConfused            = original.Flags.HasFlag(MessageFlags.Confused);
        FlagPoisoned            = original.Flags.HasFlag(MessageFlags.Poisoned);
        FlagLosingHp            = original.Flags.HasFlag(MessageFlags.LosingHp);
        FlagMovementPrevented   = original.Flags.HasFlag(MessageFlags.MovementPrevented);
        FlagAttackPrevented     = original.Flags.HasFlag(MessageFlags.AttackPrevented);
        FlagDiseased            = original.Flags.HasFlag(MessageFlags.Diseased);
        FlagHpRegenerating      = original.Flags.HasFlag(MessageFlags.HpRegenerating);
        FlagManaRegenerating    = original.Flags.HasFlag(MessageFlags.ManaRegenerating);
        FlagEndsCombat          = original.Flags.HasFlag(MessageFlags.EndsCombat);
        FlagLastActionFailed    = original.Flags.HasFlag(MessageFlags.LastActionFailed);
        FlagFindInText          = original.Flags.HasFlag(MessageFlags.FindInText);
        FlagFindInConversations = original.Flags.HasFlag(MessageFlags.FindInConversations);
        FlagUseWhenChasing      = original.Flags.HasFlag(MessageFlags.UseWhenChasing);
        FlagDisabled            = original.Flags.HasFlag(MessageFlags.Disabled);
    }

    [RelayCommand]
    private void Save()
    {
        MessageFlags typed = AssembleFlags();
        // Preserve any reserved bits (e.g. 0x0800) the importer
        // recorded on the original so the record round-trips losslessly.
        ushort reservedBits = (ushort)(_original.RawFlagsHex & ~AllKnownFlagsMask);
        ushort raw = (ushort)((ushort)typed | reservedBits);

        string[] responseCommands = (Response ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        MessageRecord updated = new(
            Id:               MegaMudMessagesImporter.ComputeId(Name, Message, EndsWith),
            Name:             Name,
            Message:          Message,
            EndsWith:         EndsWith,
            Action:           Action,
            Flags:            typed,
            RawFlagsHex:      raw,
            ResponseCommands: responseCommands);

        CloseRequested?.Invoke(new MessageEditResult(_original, updated, UseTier));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private MessageFlags AssembleFlags()
    {
        MessageFlags f = MessageFlags.None;
        if (FlagBlinded)             f |= MessageFlags.Blinded;
        if (FlagConfused)            f |= MessageFlags.Confused;
        if (FlagPoisoned)            f |= MessageFlags.Poisoned;
        if (FlagLosingHp)            f |= MessageFlags.LosingHp;
        if (FlagMovementPrevented)   f |= MessageFlags.MovementPrevented;
        if (FlagAttackPrevented)     f |= MessageFlags.AttackPrevented;
        if (FlagDiseased)            f |= MessageFlags.Diseased;
        if (FlagHpRegenerating)      f |= MessageFlags.HpRegenerating;
        if (FlagManaRegenerating)    f |= MessageFlags.ManaRegenerating;
        if (FlagEndsCombat)          f |= MessageFlags.EndsCombat;
        if (FlagLastActionFailed)    f |= MessageFlags.LastActionFailed;
        if (FlagFindInText)          f |= MessageFlags.FindInText;
        if (FlagFindInConversations) f |= MessageFlags.FindInConversations;
        if (FlagUseWhenChasing)      f |= MessageFlags.UseWhenChasing;
        if (FlagDisabled)            f |= MessageFlags.Disabled;
        return f;
    }

    private const ushort AllKnownFlagsMask =
        (ushort)MessageFlags.Blinded             | (ushort)MessageFlags.Confused            |
        (ushort)MessageFlags.Poisoned            | (ushort)MessageFlags.LosingHp            |
        (ushort)MessageFlags.MovementPrevented   | (ushort)MessageFlags.AttackPrevented     |
        (ushort)MessageFlags.Diseased            | (ushort)MessageFlags.HpRegenerating      |
        (ushort)MessageFlags.FindInConversations | (ushort)MessageFlags.ManaRegenerating    |
        (ushort)MessageFlags.FindInText          | (ushort)MessageFlags.EndsCombat          |
        (ushort)MessageFlags.LastActionFailed    | (ushort)MessageFlags.UseWhenChasing      |
        (ushort)MessageFlags.Disabled;
}

/// <summary>
/// Result returned from <see cref="MessageEditDialogViewModel"/> on Save.
/// Carries both the original (for replacement matching) and the
/// updated record, plus the tier the user chose to write at.
/// </summary>
/// <param name="Original">The record as it was before editing — used to
/// find and replace the row in the underlying store.</param>
/// <param name="Updated">The record carrying the user's edits.</param>
/// <param name="Tier">The tier the user picked from the Use dropdown.
/// Defaults tier writes back to <see cref="MessageStore"/>; other tiers
/// land via <see cref="SettingsResolver.WriteGameDataAt"/>.</param>
public sealed record MessageEditResult(
    MessageRecord Original,
    MessageRecord Updated,
    SettingsTier  Tier);
