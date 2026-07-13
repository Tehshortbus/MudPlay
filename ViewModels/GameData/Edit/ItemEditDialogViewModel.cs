using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
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

    // LIGHT items are owned by Auto-light — Auto-buy / Auto-sell never act on
    // them, so the dialog greys those two toggles for a light. The runtime
    // resolver enforces the same exclusion regardless; this just keeps the UI
    // honest about what the flags will do.
    public bool CanBuySell { get; }

    // Only surfaces (non-null) for a light, where the two toggles are greyed —
    // explains why. Null on a normal item so no tooltip shows on the enabled box.
    public string? BuySellTooltip =>
        CanBuySell ? null : "LIGHT items are managed by Auto-light — Auto-buy / Auto-sell don't apply.";

    // Auto-open only applies to container items — the engine sends `open <item>`
    // when a flagged container enters inventory, which is meaningless for a
    // non-container. The dialog greys the toggle off for anything that isn't a
    // container; the runtime resolver enforces the same container gate.
    public bool CanAutoOpen { get; }

    // Non-null only when Auto-open is greyed (non-container) — explains why.
    public string? AutoOpenTooltip =>
        CanAutoOpen ? null : "Auto-open applies to container items only.";

    // Guards the Auto-buy MaxToGet seed so it fires on a live user tick, not
    // during the ctor's initial load of a saved overlay.
    private readonly bool _initialized;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    // ----- Options flags -----
    [ObservableProperty] private bool _autoCollect;
    [ObservableProperty] private bool _autoDiscard;
    [ObservableProperty] private bool _autoOpen;
    [ObservableProperty] private bool _autoBuy;
    [ObservableProperty] private bool _autoSell;
    [ObservableProperty] private bool _autoStash;
    [ObservableProperty] private bool _cannotBeTaken;
    [ObservableProperty] private bool _mustHaveMinimum;
    [ObservableProperty] private bool _loyalItem;

    // ----- Navigation path provisioning (FujinTerm) -----
    // Master opt-in; when off, the three method sub-flags are greyed and never
    // fire. When a planned route needs this item to cross a gate or survive a
    // hazard and we lack it, CHECKED means "go get it (via the enabled methods),
    // then walk"; UNCHECKED surfaces the requirement in the route-picker instead.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathMethodsEnabled))]
    private bool _autoObtainForPath;
    [ObservableProperty] private bool _buyIfNeededForPath;
    [ObservableProperty] private bool _sourceFromDropsForPath;
    [ObservableProperty] private bool _provisionPartyForPath;

    // Method sub-flags only act while the master opt-in is set — the view greys
    // them out when it's off.
    public bool PathMethodsEnabled => AutoObtainForPath;

    // ----- Carry policy (overlay-editable) -----

    // "None" sentinel for the MegaMUD-parity blank state.
    [ObservableProperty] private string _minToKeep = string.Empty;

    // "All" is a legit MegaMUD sentinel here, so stored as a free string.
    [ObservableProperty] private string _maxToGet = string.Empty;

    // Right-pane "Other Info" key/value list (read-only MDB fields).
    public IReadOnlyList<KeyValuePair<string, string>> MdbInfo { get; }

    // Chest-contents readout (containers only) — the decoded loot table's
    // per-item drop chances plus a one-line yield summary. Empty for any item
    // that isn't a container wired to a loot textblock.
    public IReadOnlyList<ChestDropRow> ChestDrops { get; }
    public bool HasChestContents => ChestDrops.Count > 0;
    public string ChestSummary { get; }

    public IReadOnlyList<SettingsTier> AvailableTiers { get; } =
        Enum.GetValues<SettingsTier>().ToArray();

    public string Title => $"Item — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    public ItemEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        ItemOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<KeyValuePair<string, string>> mdbInfo,
        bool isLight = false,
        bool isContainer = false,
        ChestContents? chest = null)
    {
        WccNoStr     = wccNoStr;
        Name         = existing?.Name ?? mdbName;
        UseTier      = currentTier;
        MdbInfo      = mdbInfo;
        CanBuySell   = !isLight;
        CanAutoOpen  = isContainer;

        (ChestDrops, ChestSummary) = BuildChest(chest);

        AutoCollect     = existing?.AutoCollect     ?? false;
        AutoDiscard     = existing?.AutoDiscard     ?? false;
        // Container-gated: a non-container never shows (or persists) Auto-open,
        // even if a stale overlay carried the flag.
        AutoOpen        = isContainer && (existing?.AutoOpen ?? false);
        AutoBuy         = existing?.AutoBuy         ?? false;
        AutoSell        = existing?.AutoSell        ?? false;
        AutoStash       = existing?.AutoStash       ?? false;
        CannotBeTaken   = existing?.CannotBeTaken   ?? false;
        MustHaveMinimum = existing?.MustHaveMinimum ?? false;
        LoyalItem       = existing?.LoyalItem       ?? false;

        AutoObtainForPath      = existing?.AutoObtainForPath      ?? false;
        BuyIfNeededForPath     = existing?.BuyIfNeededForPath     ?? false;
        SourceFromDropsForPath = existing?.SourceFromDropsForPath ?? false;
        ProvisionPartyForPath  = existing?.ProvisionPartyForPath  ?? false;

        MinToKeep = existing?.MinToKeep ?? string.Empty;
        MaxToGet  = existing?.MaxToGet  ?? string.Empty;

        _initialized = true;
    }

    // Seed a MegaMUD-parity default cap the first time Auto-buy is ticked so the
    // engine buys a sane quantity rather than the whole affordable stock. An
    // existing (non-blank) cap is left untouched; the guard skips the ctor's
    // initial load so a saved "unbounded" (blank) cap isn't clobbered to 10.
    partial void OnAutoBuyChanged(bool value)
    {
        if (_initialized && value && string.IsNullOrWhiteSpace(MaxToGet))
            MaxToGet = "10";
    }

    // Turn the decoded loot table into display rows + a yield summary. Chances
    // render as whole-percent (a <1% drop clamps to "<1%" so a real-but-rare
    // item never reads as "0%"); the summary is "Yields N items" or "Yields
    // N–M items" when a draw can land on a message / failitem bracket.
    private static (IReadOnlyList<ChestDropRow> Rows, string Summary) BuildChest(ChestContents? chest)
    {
        if (chest is null || chest.Drops.Count == 0)
            return (Array.Empty<ChestDropRow>(), string.Empty);

        var rows = new List<ChestDropRow>(chest.Drops.Count);
        foreach (ChestDrop d in chest.Drops)
            rows.Add(new ChestDropRow(d.ItemId, d.ItemName, FormatChance(d.Probability)));

        string summary = chest.MinItems == chest.MaxItems
            ? $"Yields {chest.MinItems} {Plural(chest.MaxItems)}"
            : $"Yields {chest.MinItems}–{chest.MaxItems} items";
        return (rows, summary);
    }

    private static string FormatChance(double p)
    {
        int pct = (int)System.Math.Round(p * 100.0);
        if (pct <= 0 && p > 0.0) return "<1%";
        if (pct >= 100) return "100%";
        return pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string Plural(int n) => n == 1 ? "item" : "items";

    [RelayCommand]
    private void Save()
    {
        ItemOverlay overlay = new()
        {
            Name            = string.IsNullOrWhiteSpace(Name) ? null : Name,
            AutoCollect     = AutoCollect     ? true : null,
            AutoDiscard     = AutoDiscard     ? true : null,
            AutoOpen        = AutoOpen        ? true : null,
            AutoBuy         = AutoBuy         ? true : null,
            AutoSell        = AutoSell        ? true : null,
            AutoStash       = AutoStash       ? true : null,
            CannotBeTaken   = CannotBeTaken   ? true : null,
            MustHaveMinimum = MustHaveMinimum ? true : null,
            LoyalItem       = LoyalItem       ? true : null,
            AutoObtainForPath      = AutoObtainForPath ? true : null,
            // Method sub-flags only persist while the master opt-in is set — an
            // orphaned sub-flag would resolve to false anyway (the runtime gate
            // AND-s the master in), so drop it to keep the delta clean.
            BuyIfNeededForPath     = AutoObtainForPath && BuyIfNeededForPath     ? true : null,
            SourceFromDropsForPath = AutoObtainForPath && SourceFromDropsForPath ? true : null,
            ProvisionPartyForPath  = AutoObtainForPath && ProvisionPartyForPath  ? true : null,
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
