using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Items tab. Renders the imported MajorMUD Items table — drives equipment
// validation on the Workshop EQUIP grid, shop-price lookups for the Cash auto-deposit math,
// and ability-effect tooltips throughout.
//
// Column names mirror the MajorMUD MDB schema verbatim (per data-v1.11p.mdb): Number is the
// canonical item ID, Encum is encumbrance, Accy is to-hit modifier, StrReq is strength
// prerequisite. Numeric enum cells (ItemType, Worn, WeaponType, ArmourType, Currency) are
// formatted via LookupEnums so the grid shows "Weapon" / "Feet" / "1H Sharp" / "Gold" rather
// than the raw integers.
public sealed class ItemsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolverRef;
    private readonly ItemOverlaySeedStore? _overlaySeed;
    private readonly PlayerStats? _playerStats;
    private readonly ItemSourceIndex? _itemSources;

    // The item menu currently open (null when none). Only one exists at a time:
    // double-clicking another row closes this one and opens the new item, rather
    // than stacking a second modeless window.
    private ItemEditDialogViewModel? _openItemVm;

    public override string Id => "items";
    public override string Title => "Items";

    protected override string TableName => "Items";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "ItemType",
        "Worn",
        "WeaponType",
        "ArmourType",
        "Min",
        "Max",
        "ArmourClass",
        "DamageResist",
        "Speed",
        "Accy",
        "StrReq",
        "Encum",
        "Price",
        "Currency",
    };

    public override string SearchKeyColumn => "Name";

    public override string? FilterHint =>
        "Type text to match name / columns, or a flag keyword to show only items with that " +
        "flag set: collect, discard, open, buy, sell, stash.";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "item", "weapon", "armor", "armour", "worn", "slot",
        "encumbrance", "price", "currency", "ability",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = LookupEnums.FormatItemType,
            ["Worn"]       = LookupEnums.FormatWornSlot,
            ["WeaponType"] = LookupEnums.FormatWeaponType,
            ["ArmourType"] = LookupEnums.FormatArmourType,
            ["Currency"]   = LookupEnums.FormatCurrency,
        };

    public IAsyncRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public ItemsSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null,
        ItemOverlaySeedStore? overlaySeed = null,
        PlayerStats? playerStats = null,
        ItemSourceIndex? itemSources = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _overlaySeed = overlaySeed;
        _playerStats = playerStats;
        _itemSources = itemSources;
        // AllowConcurrentExecutions: the first double-click parks at the open
        // dialog's await, so without this the command reports IsRunning and
        // CanExecute=false — a second double-click on another row would be
        // silently dropped instead of swapping the open item menu.
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(
            OpenEditAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    // Recognized flag keywords → the ItemOverlay flag they filter on. Typing one of
    // these (exact, case-insensitive) narrows the table to items with that auto-* /
    // stash flag set instead of the normal column / name substring match, so "collect"
    // shows only auto-collect items, "discard" only auto-discard, and so on.
    private static readonly IReadOnlyDictionary<string, Func<ItemOverlay, bool?>> FlagKeywords =
        new Dictionary<string, Func<ItemOverlay, bool?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["collect"] = o => o.AutoCollect,
            ["discard"] = o => o.AutoDiscard,
            ["open"]    = o => o.AutoOpen,
            ["buy"]     = o => o.AutoBuy,
            ["sell"]    = o => o.AutoSell,
            ["stash"]   = o => o.AutoStash,
        };

    protected override bool RowMatches(GameDataRow row, string filter)
    {
        // A recognized flag keyword filters by the item's resolved auto-* / stash
        // overlay flag; anything else falls back to the column / tier substring match.
        if (FlagKeywords.TryGetValue(filter, out Func<ItemOverlay, bool?>? flag))
            return ResolveOverlay(row) is { } overlay && flag(overlay) == true;
        return base.RowMatches(row, filter);
    }

    // Resolve a row's 4-tier ItemOverlay (Char → BBS → Global → seed Defaults) — the
    // flags the runtime engines actually see for this item — for the flag filter.
    private ItemOverlay? ResolveOverlay(GameDataRow row)
    {
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return null;
        ItemOverlay seed = (_overlaySeed is not null && int.TryParse(wcc, out int n))
            ? _overlaySeed.GetOverlay(n)
            : new ItemOverlay();
        return _resolverRef?.ResolveGameData<ItemOverlay>("Items", wcc, seed) ?? seed;
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

        // Re-double-clicking the item already showing in the open menu is a
        // no-op — don't tear down the user's in-progress edits to reopen the
        // same record.
        if (_openItemVm is not null &&
            string.Equals(_openItemVm.WccNoStr, wcc, StringComparison.Ordinal))
            return;

        // 4-tier merged overlay — Char → BBS → Global → Defaults. The
        // Defaults-tier baseline comes from the realm-flavoured
        // ItemOverlaySeedStore (decoded from Items.md), so the dialog
        // opens showing exactly what the runtime engines will see for
        // this item before any user override.
        ItemOverlay seedDefaults =
            (_overlaySeed is not null && int.TryParse(wcc, out int seedNum))
                ? _overlaySeed.GetOverlay(seedNum)
                : new ItemOverlay();
        ItemOverlay existing = _resolverRef?.ResolveGameData<ItemOverlay>(
            "Items", wcc, seedDefaults)
            ?? seedDefaults;

        // MDB-derived display rows that don't roundtrip through the overlay —
        // the dialog renders them as the read-only "Other Info" pane. Built at
        // the neutral retail charm (50) so it matches the dialog's charm picker
        // default; the picker re-prices the shop rows via shopSalesForCharm.
        ItemMdbView mdb = new ItemMdbViewBuilder(_cache, 50).Build(wcc);
        GameDataCache cache = _cache;
        IReadOnlyList<ShopSaleRow> ShopsForCharm(int charm) =>
            new ItemMdbViewBuilder(cache, charm).Build(wcc).Shops;

        // Container loot table (null for a non-chest) — the dialog shows it as a
        // read-only "Chest Contents" section below "Other Info".
        int.TryParse(wcc, out int itemNum);
        ChestContents? chest = itemNum > 0
            ? ChestContentsReader.Read(_cache, itemNum)
            : null;

        // Reverse acquisition sources the shop/drop panes don't cover: containers
        // this item is found in, and monster/room textblock `giveitem` awards.
        IReadOnlyList<ItemSource>? containerSources =
            itemNum > 0 ? _itemSources?.ContainersOf(itemNum) : null;
        IReadOnlyList<ItemGiver>? givers =
            itemNum > 0 ? _itemSources?.GiversOf(itemNum) : null;

        // On-use / proc message editing — item-claimed message records live with
        // the item now (hidden from the Messages tab), so the dialog opens their
        // editor via the shared ItemMessageDialogService. Null in a headless test
        // (no AppServices) — the Message section then hides itself.
        ItemMessageDialogService? itemMsg = AppServices.Current?.ItemMessage;
        Func<Task<string?>>? editMsg = (itemMsg is not null && itemNum > 0)
            ? () => itemMsg.OpenAsync(itemNum)
            : null;
        string? msgSummary = (itemMsg is not null && itemNum > 0) ? itemMsg.SummaryFor(itemNum) : null;

        ItemEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          row.Get("Name") ?? string.Empty,
            existing:         existing,
            currentTier:      row.SourceTier,
            mdbInfo:          mdb.OtherInfo,
            shops:            mdb.Shops,
            isLight:          mdb.IsLight,
            isContainer:      mdb.IsContainer,
            chest:            chest,
            containerSources: containerSources,
            givers:           givers,
            shopSalesForCharm: ShopsForCharm,
            droppedBy:        mdb.DroppedBy,
            editAttachedMessage:    editMsg,
            attachedMessageSummary: msgSummary);

        // Replace any open item menu with the new one: a double-click on another
        // row swaps the shown item instead of opening a second window. Closing
        // the previous dialog discards its uncommitted edits — switching items is
        // a navigate gesture, not a save (the user reaches for OK to commit).
        ItemEditDialogViewModel? previous = _openItemVm;
        _openItemVm = vm;
        previous?.CancelCommand.Execute(null);

        ItemEditResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<ItemEditDialogViewModel, ItemEditResult>(vm);
        }
        finally
        {
            if (ReferenceEquals(_openItemVm, vm))
                _openItemVm = null;
        }
        if (result is null) return;

        // Defaults tier is read-only (MDB is the source of truth) — fall
        // back to Character if the user picks it. Same guard MonstersTab uses.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;
        _resolverRef?.WriteGameDataAt(tier, "Items", result.WccNoStr, result.Overlay);

        Reload();
    }

    // Test seam: the rendered "Other Info" rows for a given item Number, so the use-cast
    // effect / damage rendering can be pinned without standing up a dialog. Mirrors what the
    // edit dialog's read-only pane shows.
    internal IReadOnlyList<KeyValuePair<string, string>> BuildOtherInfoForTests(string itemNumber)
        => new ItemMdbViewBuilder(_cache, _playerStats?.Charm ?? 0).Build(itemNumber).OtherInfo;

    // Test seam: the rendered clickable bought/sold shop rows for a given item.
    internal IReadOnlyList<ShopSaleRow> BuildShopSalesForTests(string itemNumber)
        => new ItemMdbViewBuilder(_cache, _playerStats?.Charm ?? 0).Build(itemNumber).Shops;

    // Test seam: the clickable "Dropped by" monster links for a given item.
    internal IReadOnlyList<Edit.DroppedByRow> BuildDroppedByForTests(string itemNumber)
        => new ItemMdbViewBuilder(_cache, 50).Build(itemNumber).DroppedBy ?? System.Array.Empty<Edit.DroppedByRow>();
}
