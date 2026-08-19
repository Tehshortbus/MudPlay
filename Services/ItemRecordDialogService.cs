using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.Services;

// Opens the item record (edit) dialog by item Number, modelessly, from anywhere —
// today the Item Finder's double-click, which wants the record itself rather than
// the whole Game Data Browser. Mirrors the browser Items tab's open flow
// (ItemsSectionViewModel.OpenEditAsync) but keyed on a Number instead of a browser
// row, and shares the heavy read-only view assembly via ItemMdbViewBuilder. The
// small orchestration is duplicated on purpose: the browser path is coupled to its
// GameDataRow + Reload, this one resolves name/tier from the Number and has no grid
// to reload. Single-instance: re-opening the shown record is a no-op; another swaps.
public sealed class ItemRecordDialogService
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver _resolver;
    private readonly DialogService _dialogs;
    private readonly ItemOverlaySeedStore _overlaySeed;
    private readonly ItemSourceIndex _itemSources;

    private ItemEditDialogViewModel? _openItemVm;

    public ItemRecordDialogService(
        GameDataCache cache, SettingsResolver resolver, DialogService dialogs,
        ItemOverlaySeedStore overlaySeed, ItemSourceIndex itemSources)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(overlaySeed);
        ArgumentNullException.ThrowIfNull(itemSources);
        _cache = cache;
        _resolver = resolver;
        _dialogs = dialogs;
        _overlaySeed = overlaySeed;
        _itemSources = itemSources;
    }

    public async Task OpenAsync(int itemNumber)
    {
        if (itemNumber <= 0) return;
        string wcc = itemNumber.ToString(CultureInfo.InvariantCulture);

        // Re-opening the record already showing is a no-op — don't tear down edits.
        if (_openItemVm is not null && string.Equals(_openItemVm.WccNoStr, wcc, StringComparison.Ordinal))
            return;

        // 4-tier merged overlay over the realm-flavoured Defaults seed, and the tier
        // it currently resolves from (drives the dialog's tier picker).
        ItemOverlay seedDefaults = _overlaySeed.GetOverlay(itemNumber);
        ItemOverlay existing = _resolver.ResolveGameData<ItemOverlay>("Items", wcc, seedDefaults) ?? seedDefaults;
        SettingsTier currentTier = _resolver.GetGameDataSourceTier("Items", wcc);

        // Built at the neutral retail charm (50) to match the dialog's charm
        // picker default; the picker re-prices the shop rows via shopSalesForCharm.
        ItemMdbView mdb = new ItemMdbViewBuilder(_cache, 50).Build(wcc);
        GameDataCache cache = _cache;
        IReadOnlyList<ShopSaleRow> ShopsForCharm(int charm) =>
            new ItemMdbViewBuilder(cache, charm).Build(wcc).Shops;
        ChestContents? chest = ChestContentsReader.Read(_cache, itemNumber);
        IReadOnlyList<ItemSource>? containerSources = _itemSources.ContainersOf(itemNumber);
        IReadOnlyList<ItemGiver>? givers = _itemSources.GiversOf(itemNumber);

        // On-use / proc message editing — same surface the browser Items tab wires,
        // via the shared ItemMessageDialogService (item-claimed messages live with
        // the item, hidden from the Messages tab).
        ItemMessageDialogService? itemMsg = AppServices.Current?.ItemMessage;
        Func<Task<string?>>? editMsg = itemMsg is not null ? () => itemMsg.OpenAsync(itemNumber) : null;
        string? msgSummary = itemMsg?.SummaryFor(itemNumber);

        ItemEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          _cache.FindNameByNumber("Items", itemNumber) ?? string.Empty,
            existing:         existing,
            currentTier:      currentTier,
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
            if (ReferenceEquals(_openItemVm, vm)) _openItemVm = null;
        }
        if (result is null) return;

        // Defaults tier is read-only (MDB is source of truth) — fall back to Character.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;
        _resolver.WriteGameDataAt(tier, "Items", result.WccNoStr, result.Overlay);
    }
}
