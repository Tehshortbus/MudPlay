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
    /// <remarks>
    /// "Other Info" is type-aware — mirrors MegaMUD's behaviour where the
    /// right-pane fields vary by ItemType. Weapon-only fields (Speed /
    /// damage range / Weapon Type) only appear for weapons; Armour-only
    /// fields (Armour Type) only appear for armour; the
    /// <c>Abil-N / AbilVal-N</c> pairs (0..19) are iterated and rendered
    /// with their <see cref="AbilityNames"/> label so bonuses like
    /// "Magical: 1" / "LearnSp: burning aura" appear inline without
    /// hardcoding each one. <c>LearnSpell</c> (ability code 42) gets
    /// special-cased to look the value up as a Spells.Number and render
    /// the spell's Name instead of the raw id.
    /// </remarks>
    private ItemMdbView BuildMdbView(string wccNoStr)
    {
        List<KeyValuePair<string, string>> otherInfo = new();
        string weight = string.Empty, price = string.Empty;
        string itemTypeText = string.Empty, bodyLocation = "None";
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

            int itemType = ReadInt(el, "ItemType");
            int worn     = ReadInt(el, "Worn");

            // Details-section derived strings.
            weight       = ReadString(el, "Encum");
            price        = FormatPrice(el);
            itemTypeText = MmudEnums.FormatItemType(ReadString(el, "ItemType")) ?? string.Empty;
            bodyLocation = worn == 0 ? "None" : (MmudEnums.FormatWornSlot(ReadString(el, "Worn")) ?? "None");
            boughtSold   = ResolveShops(ReadString(el, "Obtained From"));

            // ----- Other Info pane: type-aware ordering -----
            otherInfo.Add(new KeyValuePair<string, string>("WCC No", wccNoStr));

            // Weapon-only block.
            if (itemType == 1)
            {
                otherInfo.Add(new KeyValuePair<string, string>("Weapon Type",
                    MmudEnums.FormatWeaponType(ReadString(el, "WeaponType")) ?? "None"));
                otherInfo.Add(new KeyValuePair<string, string>("Damage",
                    FormatRange(ReadString(el, "Min"), ReadString(el, "Max"))));
                otherInfo.Add(new KeyValuePair<string, string>("Speed",
                    NoneIfZero(ReadString(el, "Speed"))));
            }

            // Armour-only block.
            if (itemType == 0)
            {
                otherInfo.Add(new KeyValuePair<string, string>("Armour Type",
                    MmudEnums.FormatArmourType(ReadString(el, "ArmourType")) ?? "None"));
            }

            // Consumables — Scroll / Food / Drink / Light / Special — list
            // "Uses Per Day" (MegaMUD's user-facing name for UseCount).
            // -1 = unlimited; 0 = not applicable; positive = print as-is.
            string uc = ReadString(el, "UseCount");
            if (uc is not (null or "" or "0"))
            {
                otherInfo.Add(new KeyValuePair<string, string>("Uses Per Day",
                    uc == "-1" ? "Unlimited" : uc));
            }

            // Common stat fields — shown for every item type. "None" when
            // unset so the row stays present (useful for at-a-glance
            // visual parity with MegaMUD).
            otherInfo.Add(new KeyValuePair<string, string>("Accuracy Bonus",
                NoneIfZero(ReadString(el, "Accy"))));
            otherInfo.Add(new KeyValuePair<string, string>("AC Bonus",
                FormatAcBonus(el)));
            otherInfo.Add(new KeyValuePair<string, string>("Required Strength",
                NoneIfZero(ReadString(el, "StrReq"))));

            // Ability pairs — Abil-0..19 / AbilVal-0..19. Each non-zero
            // code becomes one row labelled with its AbilityNames mapping
            // (e.g. "Magical: 1", "Strength: +5"). LearnSpell (code 42)
            // resolves the AbilVal to a Spells.Name lookup so the row
            // reads "LearnSpell: burning aura" rather than "LearnSpell: 71".
            for (int i = 0; i < 20; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                string label = AbilityNames.GetName(code) ?? $"Abil{code}";
                string value = code == 42 ? ResolveSpellName(val) : val.ToString(System.Globalization.CultureInfo.InvariantCulture);
                otherInfo.Add(new KeyValuePair<string, string>(label, value));
            }
            break;
        }
        return new ItemMdbView(otherInfo, weight, price, itemTypeText, bodyLocation, boughtSold);
    }

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    /// <summary>"5-12" pair display; "None" when both are zero / missing.</summary>
    private static string FormatRange(string min, string max)
    {
        bool minZero = string.IsNullOrWhiteSpace(min) || min == "0";
        bool maxZero = string.IsNullOrWhiteSpace(max) || max == "0";
        if (minZero && maxZero) return "None";
        return $"{(minZero ? "0" : min)}-{(maxZero ? "0" : max)}";
    }

    /// <summary>
    /// Items.json's "Obtained From" carries shop references as
    /// "Shop #N, Shop #M, ..." — resolve each #N to "Shop.Name" via the
    /// active set's Shops.json. Falls back to the raw text when the
    /// Shops table isn't loaded or a shop isn't found.
    /// </summary>
    private string ResolveShops(string obtainedFrom)
    {
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return string.Empty;
        JsonDocument? shopsDoc = _cache.GetRawTable("Shops");
        if (shopsDoc is null) return obtainedFrom;

        // Build a quick id → name map for any tokens of the form "Shop #N".
        Dictionary<int, string> shopNames = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            if (!trimmed.StartsWith("Shop #", StringComparison.Ordinal)) continue;
            if (int.TryParse(trimmed.AsSpan(6), out int id)) shopNames[id] = string.Empty;
        }
        if (shopNames.Count == 0) return obtainedFrom;

        foreach (JsonElement el in shopsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            int shopNum = numProp.GetInt32();
            if (!shopNames.ContainsKey(shopNum)) continue;
            shopNames[shopNum] = ReadString(el, "Name");
        }

        List<string> parts = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            if (trimmed.StartsWith("Shop #", StringComparison.Ordinal)
                && int.TryParse(trimmed.AsSpan(6), out int id)
                && shopNames.TryGetValue(id, out string? name)
                && !string.IsNullOrEmpty(name))
            {
                parts.Add(name);
            }
            else if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }
        return string.Join(", ", parts);
    }

    /// <summary>Look up a spell's Name by its Number; falls back to the raw id when absent.</summary>
    private string ResolveSpellName(int spellNumber)
    {
        if (spellNumber == 0) return "None";
        JsonDocument? spellsDoc = _cache.GetRawTable("Spells");
        if (spellsDoc is null) return spellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (JsonElement el in spellsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != spellNumber) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? spellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
        }
        return spellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
