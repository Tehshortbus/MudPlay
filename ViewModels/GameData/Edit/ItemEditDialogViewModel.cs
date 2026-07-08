using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Items tab's per-record edit dialog. Surfaces
// the editable overlay fields (Use-tier, Name, the 11 Options checkboxes, Min/Max carry
// policy, IfNeededDo action) alongside a read-only MDB info pane on the right.
//
// The Details section holds only the overlay-editable carry-policy fields (Min/Max);
// every read-only MDB fact — weight, item type, body slot, and the charm-priced
// bought/sold shop list — lives in the right-pane "Other Info". MDB-canonical stats
// (ItemType, Worn slot, ArmourType, ArmourClass, etc.) are deliberately not
// user-overridable — every BBS supplies a concrete MDB so the MDB is the source of
// truth; only behaviour fields flow through the overlay.
public sealed partial class ItemEditDialogViewModel : ObservableObject, IDialogViewModel<ItemEditResult>
{
    public event Action<ItemEditResult?>? CloseRequested;

    public string WccNoStr { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    // ----- Options flags -----
    [ObservableProperty] private bool _autoCollect;
    [ObservableProperty] private bool _autoDiscard;
    [ObservableProperty] private bool _autoFind;
    [ObservableProperty] private bool _autoOpen;
    [ObservableProperty] private bool _autoBuy;
    [ObservableProperty] private bool _autoSell;
    [ObservableProperty] private bool _autoStash;
    [ObservableProperty] private bool _cannotBeTaken;
    [ObservableProperty] private bool _mustHaveMinimum;
    [ObservableProperty] private bool _loyalItem;

    // ----- Carry policy (overlay-editable) -----

    // "None" sentinel for the MegaMUD-parity blank state.
    [ObservableProperty] private string _minToKeep = string.Empty;

    // "All" is a legit MegaMUD sentinel here, so stored as a free string.
    [ObservableProperty] private string _maxToGet = string.Empty;

    // Right-pane "Other Info" key/value list (read-only MDB fields).
    public IReadOnlyList<KeyValuePair<string, string>> MdbInfo { get; }

    public IReadOnlyList<SettingsTier> AvailableTiers { get; } =
        Enum.GetValues<SettingsTier>().ToArray();

    public string Title => $"Item — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    public ItemEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        ItemOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<KeyValuePair<string, string>> mdbInfo)
    {
        WccNoStr     = wccNoStr;
        Name         = existing?.Name ?? mdbName;
        UseTier      = currentTier;
        MdbInfo      = mdbInfo;

        AutoCollect     = existing?.AutoCollect     ?? false;
        AutoDiscard     = existing?.AutoDiscard     ?? false;
        AutoFind        = existing?.AutoFind        ?? false;
        AutoOpen        = existing?.AutoOpen        ?? false;
        AutoBuy         = existing?.AutoBuy         ?? false;
        AutoSell        = existing?.AutoSell        ?? false;
        AutoStash       = existing?.AutoStash       ?? false;
        CannotBeTaken   = existing?.CannotBeTaken   ?? false;
        MustHaveMinimum = existing?.MustHaveMinimum ?? false;
        LoyalItem       = existing?.LoyalItem       ?? false;

        MinToKeep = existing?.MinToKeep ?? string.Empty;
        MaxToGet  = existing?.MaxToGet  ?? string.Empty;
    }

    [RelayCommand]
    private void Save()
    {
        ItemOverlay overlay = new()
        {
            Name            = string.IsNullOrWhiteSpace(Name) ? null : Name,
            AutoCollect     = AutoCollect     ? true : null,
            AutoDiscard     = AutoDiscard     ? true : null,
            AutoFind        = AutoFind        ? true : null,
            AutoOpen        = AutoOpen        ? true : null,
            AutoBuy         = AutoBuy         ? true : null,
            AutoSell        = AutoSell        ? true : null,
            AutoStash       = AutoStash       ? true : null,
            CannotBeTaken   = CannotBeTaken   ? true : null,
            MustHaveMinimum = MustHaveMinimum ? true : null,
            LoyalItem       = LoyalItem       ? true : null,
            MinToKeep       = string.IsNullOrWhiteSpace(MinToKeep) ? null : MinToKeep,
            MaxToGet        = string.IsNullOrWhiteSpace(MaxToGet)  ? null : MaxToGet,
        };

        CloseRequested?.Invoke(new ItemEditResult(WccNoStr, overlay, UseTier));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Returned by ItemEditDialogViewModel on Save. WccNoStr is the item's WCC No as a string
// — primary key for the overlay write; Overlay is the user's edited overlay payload
// (boolean flags written only when true); Tier is the tier the overlay should be written at.
public sealed record ItemEditResult(
    string       WccNoStr,
    ItemOverlay  Overlay,
    SettingsTier Tier);
