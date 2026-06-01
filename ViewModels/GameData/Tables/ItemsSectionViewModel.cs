using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Items tab. Renders the imported MajorMUD
/// <c>Items</c> table — drives equipment validation on the Phase 9
/// Workshop EQUIP grid, shop-price lookups for the Phase 13 Cash
/// auto-deposit math, and ability-effect tooltips throughout.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim (per
/// <c>data-v1.11p.mdb</c>): <c>Number</c> is the canonical item ID,
/// <c>Encum</c> is encumbrance, <c>Accy</c> is to-hit modifier,
/// <c>StrReq</c> is strength prerequisite. Numeric enum cells
/// (<c>ItemType</c>, <c>Worn</c>, <c>WeaponType</c>, <c>ArmourType</c>,
/// <c>Currency</c>) are formatted via <see cref="MmudEnums"/> so the
/// grid shows "Weapon" / "Feet" / "1H Sharp" / "Gold" rather than the
/// raw integers.
/// </remarks>
public sealed class ItemsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolverRef;

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

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "item", "weapon", "armor", "armour", "worn", "slot",
        "encumbrance", "price", "currency", "ability",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = MmudEnums.FormatItemType,
            ["Worn"]       = MmudEnums.FormatWornSlot,
            ["WeaponType"] = MmudEnums.FormatWeaponType,
            ["ArmourType"] = MmudEnums.FormatArmourType,
            ["Currency"]   = MmudEnums.FormatCurrency,
        };

    public IAsyncRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public ItemsSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

        // 4-tier merged overlay — Char → BBS → Global → Defaults. Defaults
        // baseline is currently a blank ItemOverlay; once the items.md
        // decoder + ItemOverlaySeedStore land (follow-on commit) the
        // baseline switches to the realm-flavoured seed lookup.
        ItemOverlay existing = _resolverRef?.ResolveGameData<ItemOverlay>(
            "Items", wcc, new ItemOverlay())
            ?? new ItemOverlay();

        // MDB-derived display fields that don't roundtrip through the
        // overlay — the dialog renders them as read-only.
        ItemMdbView mdb = BuildMdbView(wcc);

        ItemEditDialogViewModel vm = new(
            wccNoStr:     wcc,
            mdbName:      row.Get("Name") ?? string.Empty,
            existing:     existing,
            currentTier:  row.SourceTier,
            mdbInfo:      mdb.OtherInfo,
            weight:       mdb.Weight,
            price:        mdb.Price,
            itemTypeText: mdb.ItemTypeText,
            bodyLocation: mdb.BodyLocation,
            boughtSold:   mdb.BoughtSold);

        ItemEditResult? result = await _dialogs.OpenWindowAsync<ItemEditDialogViewModel, ItemEditResult>(vm);
        if (result is null) return;

        // Defaults tier is read-only (MDB is the source of truth) — fall
        // back to Character if the user picks it. Same guard MonstersTab uses.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;
        _resolverRef?.WriteGameDataAt(tier, "Items", result.WccNoStr, result.Overlay);

        Reload();
    }

    /// <summary>
    /// Builds the read-only views the dialog needs from the active set's
    /// <c>Items.json</c> row: a curated "Other Info" key/value list for
    /// the right pane plus the Details-section derived strings (weight,
    /// price, type label, slot label, bought/sold cross-reference).
    /// </summary>
    private ItemMdbView BuildMdbView(string wccNoStr)
    {
        List<KeyValuePair<string, string>> otherInfo = new();
        string weight = string.Empty, price = string.Empty;
        string itemTypeText = string.Empty, bodyLocation = string.Empty;
        string boughtSold = string.Empty;

        if (!int.TryParse(wccNoStr, out int wccNo))
            return new ItemMdbView(otherInfo, weight, price, itemTypeText, bodyLocation, boughtSold);

        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null)
            return new ItemMdbView(otherInfo, weight, price, itemTypeText, bodyLocation, boughtSold);

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != wccNo) continue;

            // Details-section derived strings.
            weight       = ReadString(el, "Encum");
            price        = FormatPrice(el);
            itemTypeText = MmudEnums.FormatItemType(ReadString(el, "ItemType"))   ?? string.Empty;
            bodyLocation = MmudEnums.FormatWornSlot(ReadString(el, "Worn"))       ?? string.Empty;
            boughtSold   = ReadString(el, "Obtained From");

            // Curated Other Info pane — the headline stats a user wants
            // to glance at without scrolling. Anything zero / missing
            // renders as "None" so the pane stays uncluttered.
            otherInfo.Add(new KeyValuePair<string, string>("WCC No",          wccNoStr));
            otherInfo.Add(new KeyValuePair<string, string>("Armour Type",     MmudEnums.FormatArmourType(ReadString(el, "ArmourType")) ?? "None"));
            otherInfo.Add(new KeyValuePair<string, string>("Accuracy Bonus",  NoneIfZero(ReadString(el, "Accy"))));
            otherInfo.Add(new KeyValuePair<string, string>("AC Bonus",        FormatAcBonus(el)));
            otherInfo.Add(new KeyValuePair<string, string>("Required Strength", NoneIfZero(ReadString(el, "StrReq"))));
            otherInfo.Add(new KeyValuePair<string, string>("Weapon Type",     MmudEnums.FormatWeaponType(ReadString(el, "WeaponType")) ?? "None"));
            otherInfo.Add(new KeyValuePair<string, string>("Speed",           NoneIfZero(ReadString(el, "Speed"))));
            otherInfo.Add(new KeyValuePair<string, string>("Min Damage",      NoneIfZero(ReadString(el, "Min"))));
            otherInfo.Add(new KeyValuePair<string, string>("Max Damage",      NoneIfZero(ReadString(el, "Max"))));
            otherInfo.Add(new KeyValuePair<string, string>("Use Count",       ReadString(el, "UseCount")));
            otherInfo.Add(new KeyValuePair<string, string>("Gettable",        ReadString(el, "Gettable") == "1" ? "Yes" : "No"));
            break;
        }
        return new ItemMdbView(otherInfo, weight, price, itemTypeText, bodyLocation, boughtSold);
    }

    private static string ReadString(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.Null      => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String    => v.GetString() ?? string.Empty,
            JsonValueKind.Number    => v.ToString(),
            JsonValueKind.True      => "true",
            JsonValueKind.False     => "false",
            _                        => v.ToString(),
        };
    }

    private static string NoneIfZero(string raw)
        => string.IsNullOrWhiteSpace(raw) || raw == "0" ? "None" : raw;

    /// <summary>Renders "5 Silver" / "1 Gold" from the (Price, Currency) pair.</summary>
    private static string FormatPrice(JsonElement el)
    {
        string price = ReadString(el, "Price");
        if (string.IsNullOrWhiteSpace(price) || price == "0") return "None";
        string currency = MmudEnums.FormatCurrency(ReadString(el, "Currency")) ?? string.Empty;
        return string.IsNullOrEmpty(currency) ? price : $"{price} {currency}";
    }

    /// <summary>"{ArmourClass}/{DamageResist}" — MegaMUD's slash-pair display.</summary>
    private static string FormatAcBonus(JsonElement el)
    {
        string ac = ReadString(el, "ArmourClass");
        string dr = ReadString(el, "DamageResist");
        bool acZero = string.IsNullOrWhiteSpace(ac) || ac == "0";
        bool drZero = string.IsNullOrWhiteSpace(dr) || dr == "0";
        if (acZero && drZero) return "None";
        return $"{(acZero ? "0" : ac)}/{(drZero ? "0" : dr)}";
    }

    /// <summary>Bundle returned by <see cref="BuildMdbView"/>.</summary>
    private sealed record ItemMdbView(
        IReadOnlyList<KeyValuePair<string, string>> OtherInfo,
        string Weight,
        string Price,
        string ItemTypeText,
        string BodyLocation,
        string BoughtSold);
}
