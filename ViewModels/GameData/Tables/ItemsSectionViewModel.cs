using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Batch;
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

    // Wide record table — the user arranges its columns by dragging the headers.
    public override bool AllowColumnReorder => true;

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
        TogglesColumn,   // our configured auto-* / carry flags for this item
    };

    public override string? FilterHint =>
        "Type text to match name, item type, worn slot, or weapon / armour type " +
        "(e.g. \"weapon\", \"feet\", \"plate\"), or an auto-toggle word to show only " +
        "items with that flag set: get / collect, drop / discard, open, buy, sell, " +
        "stash, keep, loyal, notake, path.";

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

    public IRelayCommand BatchEditAsyncCommand { get; }
    ICommand? IEditableTableSectionViewModel.BatchEditCommand => BatchEditAsyncCommand;
    string? IEditableTableSectionViewModel.BatchEditLabel => "Batch edit";

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
        BatchEditAsyncCommand = new AsyncRelayCommand(BatchEditAsync);
    }

    // Batch edit every selected item: fold the chosen flags / carry policy onto each
    // record's existing overlay at the chosen tier, or reset them all at Defaults.
    private async Task BatchEditAsync()
    {
        if (_dialogs is null || _resolverRef is not { } resolver) return;
        List<GameDataRow> rows = SelectedRows
            .Where(r => !string.IsNullOrEmpty(r.Get("Number")))
            .ToList();
        if (rows.Count < 2) return;

        ItemBatchEditDialogViewModel vm = new(rows.Count, resolver.WritableTiers());
        ItemBatchResult? result = await _dialogs.OpenWindowAsync<ItemBatchEditDialogViewModel, ItemBatchResult>(vm);
        if (result is null) return;

        if (result.Tier == SettingsTier.Defaults)
        {
            bool ok = await AppServices.Current.Confirm.ConfirmAsync(
                "Reset to installed defaults",
                $"Clear every override on {rows.Count} selected items and restore the installed defaults?");
            if (!ok) return;
            foreach (GameDataRow row in rows)
                resolver.ResetGameDataRecord("Items", row.Get("Number")!);
            Reload();
            return;
        }

        if (!result.Changes.AnyFieldChosen) return;

        foreach (GameDataRow row in rows)
        {
            string wcc = row.Get("Number")!;
            ItemOverlay seedDefaults =
                (_overlaySeed is not null && int.TryParse(wcc, out int seedNum))
                    ? _overlaySeed.GetOverlay(seedNum)
                    : new ItemOverlay();
            ItemOverlay existing = resolver.ResolveGameData<ItemOverlay>("Items", wcc, seedDefaults);
            ItemOverlay updated = result.Changes.ApplyTo(existing);
            await GameDataOverrideApplier.ApplyAsync(
                resolver, AppServices.Current.Confirm, "Items", wcc,
                result.Tier, updated, updated.Equals(seedDefaults));
        }
        Reload();
    }

    // Recognized flag keywords → the ItemOverlay flag they filter on. Typing one of
    // these (exact, case-insensitive) narrows the table to items with that flag set
    // instead of the normal column / name substring match, so "get" (or "collect")
    // shows only auto-collect items, "drop" (or "discard") only auto-discard, and so
    // on. Covers every user-settable flag, with the in-game verb as a synonym where one
    // fits (get / drop). Keep this in sync with the flags rendered in the Toggles column.
    internal static readonly IReadOnlyDictionary<string, Func<ItemOverlay, bool?>> FlagKeywords =
        new Dictionary<string, Func<ItemOverlay, bool?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["collect"]  = o => o.AutoCollect,
            ["get"]      = o => o.AutoCollect,
            ["discard"]  = o => o.AutoDiscard,
            ["drop"]     = o => o.AutoDiscard,
            ["open"]     = o => o.AutoOpen,
            ["buy"]      = o => o.AutoBuy,
            ["sell"]     = o => o.AutoSell,
            ["detour"]   = o => o.SellDetour,
            ["sell-detour"] = o => o.SellDetour,
            ["stash"]    = o => o.AutoStash,
            ["keep"]     = o => o.MustHaveMinimum,
            ["keep-min"] = o => o.MustHaveMinimum,
            ["loyal"]    = o => o.LoyalItem,
            ["notake"]   = o => o.CannotBeTaken,
            ["no-take"]  = o => o.CannotBeTaken,
            ["path"]     = o => o.AutoObtainForPath,
            ["path-get"] = o => o.AutoObtainForPath,
            ["obtain"]   = o => o.AutoObtainForPath,
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
        return string.IsNullOrEmpty(wcc) ? null : ResolveOverlayByNumber(wcc);
    }

    // 4-tier merge for a raw item Number (Char → BBS → Global → seed Defaults). Shared by
    // the flag filter (via ResolveOverlay) and the Toggles column's per-row build.
    private ItemOverlay ResolveOverlayByNumber(string wcc)
    {
        ItemOverlay seed = (_overlaySeed is not null && int.TryParse(wcc, out int n))
            ? _overlaySeed.GetOverlay(n)
            : new ItemOverlay();
        return _resolverRef?.ResolveGameData<ItemOverlay>("Items", wcc, seed) ?? seed;
    }

    // The Toggles column: a compact summary of the auto-* / carry flags this character
    // has turned on for the item, resolved from the same 4-tier overlay the engines read.
    // Null (blank cell) when the item has no overlay layer or no flags set.
    protected override IReadOnlyDictionary<string, string?>? ComputeRowCells(GameRow element)
    {
        if (_resolverRef is null) return null;
        string? wcc = ReadNumber(element);
        if (string.IsNullOrEmpty(wcc)) return null;
        ItemOverlay o = ResolveOverlayByNumber(wcc);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TogglesColumn] = FormatToggleSummary(
                (o.AutoCollect       == true, "Collect"),
                (o.AutoDiscard       == true, "Discard"),
                (o.AutoOpen          == true, "Open"),
                (o.AutoBuy           == true, "Buy"),
                (o.AutoSell          == true, "Sell"),
                (o.SellDetour        == true, "Sell-detour"),
                (o.AutoStash         == true, "Stash"),
                (o.CannotBeTaken     == true, "No-take"),
                (o.MustHaveMinimum   == true, "Keep-min"),
                (o.LoyalItem         == true, "Loyal"),
                (o.AutoObtainForPath == true, "Path-get")),
        };
    }

    // Read the item's Number as a string from the raw MDB element (numeric in the MDB, but
    // tolerate a string kind too). null when the field is missing or an unexpected shape.
    private static string? ReadNumber(GameRow el)
    {
        if (!el.TryGetProperty("Number", out GameValue v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
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
        // editor via the shared ItemMessageDialogService. Null without
        // AppServices — the Message section then hides itself.
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
            writableTiers:    _resolverRef?.WritableTiers(),
            installedDefaults: seedDefaults,
            isLight:          mdb.IsLight,
            isContainer:      mdb.IsContainer,
            chest:            chest,
            containerSources: containerSources,
            givers:           givers,
            shopSalesForCharm: ShopsForCharm,
            droppedBy:        mdb.DroppedBy,
            placedIn:         mdb.PlacedIn,
            roomCommands:     mdb.RoomCommands,
            castsSpells:      mdb.CastsSpells,
            summons:          mdb.Summons,
            teleportsTo:      mdb.TeleportsTo,
            editAttachedMessage:    editMsg,
            attachedMessageSummary: msgSummary,
            exitUses:         new ItemMdbViewBuilder(_cache, 50).ExitUsesOf(itemNum));

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

        // Installed-defaults reset (confirm + wipe all tiers), redundant-override
        // cleanup (edit == seed → clear the tier), or a normal write — one shared path.
        if (_resolverRef is { } resolver && AppServices.Current is { } app)
            await GameDataOverrideApplier.ApplyAsync(
                resolver, app.Confirm, "Items", result.WccNoStr,
                result.Tier, result.Overlay, result.EqualsInstalledDefaults);

        Reload();
    }
}
