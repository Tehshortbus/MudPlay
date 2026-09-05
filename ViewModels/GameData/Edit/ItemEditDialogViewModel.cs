using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

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

    // Opens the item's on-use / proc message record editor and returns the
    // refreshed one-line summary. Wired by the caller (browser Items tab / Item
    // Finder) to ItemMessageDialogService; null when no message surface is
    // available (e.g. a unit test builds the VM with no dialog service), which
    // hides the Message section.
    private readonly Func<Task<string?>>? _editAttachedMessage;

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

    // ----- Navigation path provisioning (MudPlay) -----
    // Single opt-in. When a planned route needs this item to cross a gate or
    // survive a hazard and we lack it, CHECKED means "go get it (every method:
    // party redistribute, shop buy, bank withdraw, or drop reroute), then walk";
    // UNCHECKED fails the sole-route case in place and surfaces the two-route
    // case in the route-picker instead.
    [ObservableProperty] private bool _autoObtainForPath;

    // ----- Carry policy (overlay-editable) -----

    // "None" sentinel for the MegaMUD-parity blank state.
    [ObservableProperty] private string _minToKeep = string.Empty;

    // "All" is a legit MegaMUD sentinel here, so stored as a free string.
    [ObservableProperty] private string _maxToGet = string.Empty;

    // ----- On-use / proc message -----

    // One-line preview of the message record anchored to this item (its "use
    // <item>" buff line or weapon-proc line), or "" when the item has none yet.
    // Refreshed in place after the editor closes.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachedMessage))]
    [NotifyPropertyChangedFor(nameof(AttachedMessageDisplay))]
    [NotifyPropertyChangedFor(nameof(AttachedMessageButtonText))]
    private string _attachedMessageSummary = string.Empty;

    // The Message section only shows when the caller supplied a message-editing
    // surface — an item-claimed message is edited from here rather than the
    // Messages tab, mirroring how a spell-claimed message is edited from Spells.
    public bool CanEditAttachedMessage => _editAttachedMessage is not null;
    public bool HasAttachedMessage => AttachedMessageSummary.Length > 0;
    public string AttachedMessageDisplay =>
        HasAttachedMessage ? AttachedMessageSummary : "No on-use message attached.";
    public string AttachedMessageButtonText =>
        HasAttachedMessage ? "Edit message…" : "Add message…";

    // Right-pane "Other Info" key/value list (read-only MDB fields).
    public IReadOnlyList<KeyValuePair<string, string>> MdbInfo { get; }

    // Bought/sold shop rows — each a clickable link to the host room's
    // Rooms-tab record, with a charm-priced buy/sell line beneath. Empty for an
    // item sold at no shop. Rebuilt when Charm moves (prices are charm-scaled).
    public ObservableCollection<ShopSaleRow> ShopSales { get; } = new();
    public bool HasShopSales => ShopSales.Count > 0;

    // Charm the bought/sold prices are shown at — a picker (default 50, the
    // neutral retail point) the user can drag to see how buy/sell shift, e.g. to
    // weigh a higher-charm party member selling instead. Rebuilds ShopSales.
    [ObservableProperty] private int _charm = 50;
    private readonly Func<int, IReadOnlyList<ShopSaleRow>>? _shopSalesForCharm;

    // Monsters that drop this item, each a clickable link to its record. Empty
    // when nothing drops it.
    public IReadOnlyList<DroppedByRow> DroppedBy { get; }
    public bool HasDroppedBy => DroppedBy.Count > 0;

    // Rooms whose static Placed list drops this item on the floor, each a
    // clickable link to the room's record (+ Queue-Walk). Empty when the item is
    // never floor-placed.
    public IReadOnlyList<PlacedInRow> PlacedIn { get; }
    public bool HasPlacedIn => PlacedIn.Count > 0;

    // Spells the item casts (use-cast / proc), each a clickable link to that spell's
    // record — where the cast's on-use / proc wording lives, shared across every item
    // casting it. Empty for an item that casts nothing.
    public IReadOnlyList<CastsSpellRow> CastsSpells { get; }
    public bool HasCastsSpells => CastsSpells.Count > 0;

    // Chest-contents readout (containers only) — the decoded loot table's
    // per-item drop chances plus a one-line yield summary. Empty for any item
    // that isn't a container wired to a loot textblock.
    public IReadOnlyList<ChestDropRow> ChestDrops { get; }
    public bool HasChestContents => ChestDrops.Count > 0;
    public string ChestSummary { get; }

    // Reverse container sourcing — the chests this item can be found in, each a
    // clickable link to that container's Items-tab record with its per-open drop
    // chance. Empty when no container yields the item.
    public IReadOnlyList<ChestDropRow> FoundInContainers { get; }
    public bool HasFoundInContainers => FoundInContainers.Count > 0;

    // Textblock `giveitem` sourcing — the monsters / rooms that hand this item
    // over (quest turn-ins, room CMD rewards, merchant gives), each a clickable
    // link with an optional requirement note. Empty when nothing gives it.
    public IReadOnlyList<ItemGiverRow> Givers { get; }
    public bool HasGivers => Givers.Count > 0;

    public IReadOnlyList<SettingsTier> AvailableTiers { get; }

    // The overlay Save would produce from the installed defaults — see the ctor.
    // On Save an overlay equal to this means the edit matches the seed, so the
    // caller clears the tier's redundant override (or resets, at the Defaults tier).
    private readonly ItemOverlay _defaultsBaseline;

    public string Title => $"Item — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    // Re-price the bought/sold rows at the picker's charm — the shop membership
    // is unchanged, only the buy/sell figures. The callback re-runs the shop
    // resolution for the new charm (the caller wires it to ItemMdbViewBuilder).
    partial void OnCharmChanged(int value)
    {
        if (_shopSalesForCharm is null) return;
        ShopSales.Clear();
        foreach (ShopSaleRow row in _shopSalesForCharm(value)) ShopSales.Add(row);
    }

    public ItemEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        ItemOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<KeyValuePair<string, string>> mdbInfo,
        IReadOnlyList<ShopSaleRow> shops,
        IReadOnlyList<SettingsTier>? writableTiers = null,
        ItemOverlay? installedDefaults = null,
        bool isLight = false,
        bool isContainer = false,
        ChestContents? chest = null,
        IReadOnlyList<ItemSource>? containerSources = null,
        IReadOnlyList<ItemGiver>? givers = null,
        Func<int, IReadOnlyList<ShopSaleRow>>? shopSalesForCharm = null,
        IReadOnlyList<DroppedByRow>? droppedBy = null,
        IReadOnlyList<PlacedInRow>? placedIn = null,
        IReadOnlyList<CastsSpellRow>? castsSpells = null,
        Func<Task<string?>>? editAttachedMessage = null,
        string? attachedMessageSummary = null)
    {
        WccNoStr     = wccNoStr;
        Name         = existing?.Name ?? mdbName;
        // Writable tiers + "Installed defaults" (the reset option, last). The default
        // selection is always a writable tier, never Defaults — see the Monster dialog.
        IReadOnlyList<SettingsTier> writable = writableTiers is { Count: > 0 }
            ? writableTiers
            : new[] { SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global };
        AvailableTiers = writable.Append(SettingsTier.Defaults).ToArray();
        UseTier      = writable.Contains(currentTier) ? currentTier : writable[0];
        MdbInfo      = mdbInfo;
        _shopSalesForCharm = shopSalesForCharm;
        foreach (ShopSaleRow row in shops) ShopSales.Add(row);
        DroppedBy    = droppedBy ?? Array.Empty<DroppedByRow>();
        PlacedIn     = placedIn  ?? Array.Empty<PlacedInRow>();
        CastsSpells  = castsSpells ?? Array.Empty<CastsSpellRow>();
        CanBuySell   = !isLight;
        CanAutoOpen  = isContainer;

        (ChestDrops, ChestSummary) = BuildChest(chest);
        FoundInContainers = BuildContainerSources(containerSources);
        Givers = BuildGivers(givers);

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

        AutoObtainForPath = existing?.AutoObtainForPath ?? false;

        MinToKeep = existing?.MinToKeep ?? string.Empty;
        MaxToGet  = existing?.MaxToGet  ?? string.Empty;

        _editAttachedMessage   = editAttachedMessage;
        AttachedMessageSummary = attachedMessageSummary ?? string.Empty;

        // What Compose produces from the installed defaults (same container gate on
        // AutoOpen the fields use) — an edit equal to this is a no-op vs the seed.
        _defaultsBaseline = Compose(
            installedDefaults?.Name ?? mdbName,
            installedDefaults?.AutoCollect ?? false,
            installedDefaults?.AutoDiscard ?? false,
            isContainer && (installedDefaults?.AutoOpen ?? false),
            installedDefaults?.AutoBuy ?? false,
            installedDefaults?.AutoSell ?? false,
            installedDefaults?.AutoStash ?? false,
            installedDefaults?.CannotBeTaken ?? false,
            installedDefaults?.MustHaveMinimum ?? false,
            installedDefaults?.LoyalItem ?? false,
            installedDefaults?.AutoObtainForPath ?? false,
            installedDefaults?.MinToKeep ?? string.Empty,
            installedDefaults?.MaxToGet ?? string.Empty);

        _initialized = true;
    }

    // Open the item's on-use / proc message editor, then refresh the section's
    // summary from whatever the editor committed.
    [RelayCommand]
    private async Task EditAttachedMessage()
    {
        if (_editAttachedMessage is null) return;
        string? summary = await _editAttachedMessage();
        AttachedMessageSummary = summary ?? string.Empty;
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

    // Reverse container sourcing → clickable Items-tab links. Reuses ChestDropRow
    // (identical shape: an item link + a formatted per-open chance) — here the
    // linked item is the container and the chance is this item's drop rate from it.
    private static IReadOnlyList<ChestDropRow> BuildContainerSources(IReadOnlyList<ItemSource>? sources)
    {
        if (sources is null || sources.Count == 0) return Array.Empty<ChestDropRow>();
        var rows = new List<ChestDropRow>(sources.Count);
        foreach (ItemSource s in sources)
            rows.Add(new ChestDropRow(s.ContainerItemId, s.ContainerName, FormatChance(s.Probability)));
        return rows;
    }

    private static IReadOnlyList<ItemGiverRow> BuildGivers(IReadOnlyList<ItemGiver>? givers)
    {
        if (givers is null || givers.Count == 0) return Array.Empty<ItemGiverRow>();
        var rows = new List<ItemGiverRow>(givers.Count);
        foreach (ItemGiver g in givers)
            rows.Add(new ItemGiverRow(g));
        return rows;
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
        ItemOverlay overlay = Compose(
            Name, AutoCollect, AutoDiscard, AutoOpen, AutoBuy, AutoSell, AutoStash,
            CannotBeTaken, MustHaveMinimum, LoyalItem, AutoObtainForPath, MinToKeep, MaxToGet);

        // Record value-equality against the defaults baseline: true means the edit
        // matches the seed, so the caller clears the tier's redundant override.
        bool equalsInstalledDefaults = overlay.Equals(_defaultsBaseline);
        CloseRequested?.Invoke(new ItemEditResult(WccNoStr, overlay, UseTier, equalsInstalledDefaults));
    }

    // Single overlay construction shared by Save (live fields) and the ctor's
    // defaults-baseline capture, so the two compare apples-to-apples.
    private static ItemOverlay Compose(
        string name, bool autoCollect, bool autoDiscard, bool autoOpen, bool autoBuy,
        bool autoSell, bool autoStash, bool cannotBeTaken, bool mustHaveMinimum,
        bool loyalItem, bool autoObtainForPath, string minToKeep, string maxToGet)
        => new()
        {
            Name              = string.IsNullOrWhiteSpace(name) ? null : name,
            AutoCollect       = autoCollect     ? true : null,
            AutoDiscard       = autoDiscard     ? true : null,
            AutoOpen          = autoOpen        ? true : null,
            AutoBuy           = autoBuy         ? true : null,
            AutoSell          = autoSell        ? true : null,
            AutoStash         = autoStash       ? true : null,
            CannotBeTaken     = cannotBeTaken   ? true : null,
            MustHaveMinimum   = mustHaveMinimum ? true : null,
            LoyalItem         = loyalItem       ? true : null,
            AutoObtainForPath = autoObtainForPath ? true : null,
            MinToKeep         = string.IsNullOrWhiteSpace(minToKeep) ? null : minToKeep,
            MaxToGet          = string.IsNullOrWhiteSpace(maxToGet)  ? null : maxToGet,
        };

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Returned by ItemEditDialogViewModel on Save. WccNoStr is the item's WCC No as a string
// — primary key for the overlay write; Overlay is the user's edited overlay payload
// (boolean flags written only when true); Tier is the tier the overlay should be written at.
public sealed record ItemEditResult(
    string       WccNoStr,
    ItemOverlay  Overlay,
    SettingsTier Tier,
    bool         EqualsInstalledDefaults);
