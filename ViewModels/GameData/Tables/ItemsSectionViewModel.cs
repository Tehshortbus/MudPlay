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
    /// "Other Info" mirrors MegaMUD's Game Item Details right-pane ordering
    /// (verified against stock items 172 / 203 / 283 / 304 / 438 / 741 /
    /// 784). Layout — anything unset is suppressed unless MegaMUD shows
    /// the row even when empty (in which case we render "None"):
    /// <list type="number">
    ///   <item>WCC No</item>
    ///   <item>Game Max (from <c>Limit</c>) — when &gt; 0</item>
    ///   <item>Uses Per Day (from <c>UseCount</c>) — when &gt; 0 (-1 / 0 suppressed)</item>
    ///   <item>Weapon block (<c>ItemType==Weapon</c>): Weapon Type, Weapon Damage, Weapon Speed</item>
    ///   <item>Armour block (<c>ItemType==Armour</c>): Armour Type</item>
    ///   <item>Accuracy Bonus / AC Bonus / Required Strength — always</item>
    ///   <item>Also Used By — one row per non-zero <c>ClassRest-N</c></item>
    ///   <item>Negates — one row per non-zero <c>NegateSpell-N</c> (resolved to Spells.Name)</item>
    ///   <item>Ability rows — one per <c>Abil-N</c> with code &gt; 0, even when AbilVal is 0
    ///         (so "Del@Maint: 0" surfaces). Stat-bonus codes render signed (+5);
    ///         value/threshold codes render raw. <c>LearnSpell</c> + <c>CastSpell</c>
    ///         resolve their values to Spells.Name.</item>
    ///   <item>Dropped By — comma-joined Monsters parsed from <c>Obtained From</c>.</item>
    /// </list>
    /// AC Bonus follows MegaMUD's convention of <c>ArmourClass/10</c> + "/" +
    /// <c>DamageResist/10</c> (stock stores ArmourClass=20 → "2/0").
    /// Bought/sold (left pane) picks the FIRST shop reference from
    /// <c>Obtained From</c> and resolves it to <c>Shop.Name</c>.
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
            string obtainedFrom = ReadString(el, "Obtained From");

            // ----- Details section (left pane) -----
            weight       = ReadString(el, "Encum");
            price        = FormatPrice(el);
            itemTypeText = MmudEnums.FormatItemType(ReadString(el, "ItemType")) ?? string.Empty;
            bodyLocation = worn == 0 ? "None" : (MmudEnums.FormatWornSlot(ReadString(el, "Worn")) ?? "None");
            boughtSold   = ResolveFirstShop(obtainedFrom);

            // ----- Other Info pane (right pane) -----
            otherInfo.Add(new KeyValuePair<string, string>("WCC No", wccNoStr));

            int limit = ReadInt(el, "Limit");
            if (limit > 0)
                otherInfo.Add(new KeyValuePair<string, string>("Game Max", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Uses Per Day — only when positive. -1 (unlimited / "use
            // ability with no limit") is suppressed entirely, matching
            // MegaMUD; 0 is suppressed (not applicable).
            int useCount = ReadInt(el, "UseCount");
            if (useCount > 0)
                otherInfo.Add(new KeyValuePair<string, string>("Uses Per Day",
                    useCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Weapon-only block.
            if (itemType == 1)
            {
                otherInfo.Add(new KeyValuePair<string, string>("Weapon Type",
                    FormatWeaponTypeForDialog(ReadString(el, "WeaponType"))));
                otherInfo.Add(new KeyValuePair<string, string>("Weapon Damage",
                    FormatRange(ReadString(el, "Min"), ReadString(el, "Max"))));
                otherInfo.Add(new KeyValuePair<string, string>("Weapon Speed",
                    NoneIfZero(ReadString(el, "Speed"))));
            }
            else if (itemType == 0)
            {
                otherInfo.Add(new KeyValuePair<string, string>("Armour Type",
                    MmudEnums.FormatArmourType(ReadString(el, "ArmourType")) ?? "None"));
            }

            // Common stat block.
            otherInfo.Add(new KeyValuePair<string, string>("Accuracy Bonus",
                FormatSignedOrNone(ReadInt(el, "Accy"))));
            otherInfo.Add(new KeyValuePair<string, string>("AC Bonus",
                FormatAcBonus(el)));
            otherInfo.Add(new KeyValuePair<string, string>("Required Strength",
                NoneIfZero(ReadString(el, "StrReq"))));

            // Class restrictions — "Also Used By: Mage" etc. One row per
            // non-zero ClassRest-N (10 slots).
            for (int i = 0; i < 10; i++)
            {
                int classId = ReadInt(el, $"ClassRest-{i}");
                if (classId == 0) continue;
                otherInfo.Add(new KeyValuePair<string, string>("Also Used By",
                    ResolveClassName(classId)));
            }

            // Negated spells — one row per non-zero NegateSpell-N (10 slots).
            for (int i = 0; i < 10; i++)
            {
                int spellId = ReadInt(el, $"NegateSpell-{i}");
                if (spellId == 0) continue;
                otherInfo.Add(new KeyValuePair<string, string>("Negates",
                    ResolveSpellName(spellId)));
            }

            // Ability pairs — Abil-0..19 / AbilVal-0..19. Render each
            // non-zero code (even when value is 0 — that's how MegaMUD
            // surfaces "Del@Maint: 0"). Code 42 (LearnSpell) and 43
            // (CastSpell) resolve their values to spell names; code 59
            // (ClassOK) resolves to a class name. Stat-bonus codes get
            // signed display ("+5"), threshold/value codes render raw.
            for (int i = 0; i < 20; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                string label = AbilityLabelForDialog(code);
                string value = AbilityValueForDialog(code, val);
                otherInfo.Add(new KeyValuePair<string, string>(label, value));
            }

            // Dropped By — extract Monster #N(X%) tokens, resolve each
            // to its Monsters.Name, comma-join. Skip when the item has
            // no Monster references in Obtained From.
            string droppedBy = ResolveDroppedBy(obtainedFrom);
            if (!string.IsNullOrEmpty(droppedBy))
                otherInfo.Add(new KeyValuePair<string, string>("Dropped By", droppedBy));

            break;
        }
        return new ItemMdbView(otherInfo, weight, price, itemTypeText, bodyLocation, boughtSold);
    }

    // ----- Ability-row formatting helpers -----

    /// <summary>
    /// Per-code label override for the dialog. AbilityNames is the canonical
    /// table; this dictionary swaps in MegaMUD-display names where they differ
    /// (e.g. ability 43 stores as "CastSpell" but MegaMUD's dialog shows
    /// "CastsSp"). Codes not listed fall through to <see cref="AbilityNames"/>.
    /// </summary>
    private static readonly Dictionary<int, string> AbilityLabelOverrides = new()
    {
        [43]  = "CastsSp",
        [59]  = "ClassOk",
        [70]  = "Spellcasting",
        [114] = "%Spell",
        [119] = "Del@Maint",
        [145] = "ManaRgn",
    };

    /// <summary>
    /// Ability codes whose value is a stat bonus and should render signed
    /// ("+5") rather than raw ("5"). The remainder render raw — they encode
    /// thresholds, counts, ids, etc. where a sign would be misleading.
    /// </summary>
    private static readonly HashSet<int> SignedAbilityCodes = new()
    {
        1, 2, 3, 4, 5, 7, 8, 17, 18, 22, 27, 29, 30, 31, 32, 33,
        34, 36, 37, 38, 39, 40, 41, 44, 45, 46, 47, 48, 49, 51,
        58, 65, 66, 67, 68, 69, 70, 145, 187,
    };

    private string AbilityLabelForDialog(int code)
    {
        if (AbilityLabelOverrides.TryGetValue(code, out string? overridden)) return overridden;
        return AbilityNames.GetName(code) ?? $"Abil{code}";
    }

    private string AbilityValueForDialog(int code, int rawValue) => code switch
    {
        42 => ResolveSpellName(rawValue),  // LearnSpell — value is a Spells.Number
        43 => ResolveSpellName(rawValue),  // CastSpell  — same
        59 => ResolveClassName(rawValue),  // ClassOK    — value is a Classes.Number
        _  => SignedAbilityCodes.Contains(code)
              ? FormatSigned(rawValue)
              : rawValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string FormatSigned(int n) => n > 0
        ? "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Like <see cref="FormatSigned"/> but renders 0 as "None".</summary>
    private static string FormatSignedOrNone(int n) => n == 0 ? "None" : FormatSigned(n);

    /// <summary>MegaMUD's Weapon-Type labels use "2-Handed Sharp" not "2H Sharp".</summary>
    private static string FormatWeaponTypeForDialog(string raw) => raw switch
    {
        "0" => "1-Handed Blunt",
        "1" => "2-Handed Blunt",
        "2" => "1-Handed Sharp",
        "3" => "2-Handed Sharp",
        _   => MmudEnums.FormatWeaponType(raw) ?? "None",
    };

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

    /// <summary>
    /// MegaMUD's slash-pair AC display, divided by 10 to match the dialog
    /// convention (stock stores ArmourClass=20 → "2/0", ArmourClass=60 →
    /// "6/0"). Both zero / missing → "None".
    /// </summary>
    private static string FormatAcBonus(JsonElement el)
    {
        int ac = ReadInt(el, "ArmourClass");
        int dr = ReadInt(el, "DamageResist");
        if (ac == 0 && dr == 0) return "None";
        return $"{ac / 10}/{dr / 10}";
    }

    /// <summary>
    /// Bought/sold (left pane) picks the FIRST shop reference in
    /// <c>Obtained From</c> and renders the shop's <c>Name</c>. Falls back
    /// to empty when the field has no shop tokens or the Shops table isn't
    /// loaded. Accepts both <c>"Shop #N"</c> and <c>"Shop(sell) #N"</c>
    /// token formats.
    /// </summary>
    private string ResolveFirstShop(string obtainedFrom)
    {
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return string.Empty;
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            int hash = trimmed.IndexOf('#');
            if (hash < 0) continue;
            if (!trimmed.StartsWith("Shop", StringComparison.Ordinal)) continue;
            // Strip a trailing "(N%)" / "(sell)" tail before parsing.
            string numText = trimmed[(hash + 1)..];
            int paren = numText.IndexOf('(');
            if (paren >= 0) numText = numText[..paren];
            if (!int.TryParse(numText.Trim(), out int shopId)) continue;
            return LookupShopName(shopId) ?? trimmed;
        }
        return string.Empty;
    }

    /// <summary>Comma-joined list of monster names parsed from Obtained From's "Monster #N(X%)" tokens.</summary>
    private string ResolveDroppedBy(string obtainedFrom)
    {
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return string.Empty;
        List<string> names = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            if (!trimmed.StartsWith("Monster #", StringComparison.Ordinal)) continue;
            string rest = trimmed[9..]; // skip "Monster #"
            int paren = rest.IndexOf('(');
            string numText = paren >= 0 ? rest[..paren] : rest;
            if (!int.TryParse(numText.Trim(), out int monsterId)) continue;
            string? name = LookupMonsterName(monsterId);
            if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
        }
        return string.Join(", ", names);
    }

    private string? LookupShopName(int shopId)
    {
        JsonDocument? doc = _cache.GetRawTable("Shops");
        if (doc is null) return null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != shopId) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }

    private string? LookupMonsterName(int monsterId)
    {
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != monsterId) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }

    /// <summary>Classes.Number → Classes.Name; falls back to "Class N" when absent.</summary>
    private string ResolveClassName(int classId)
    {
        if (classId == 0) return "None";
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return $"Class {classId}";
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != classId) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? $"Class {classId}" : name;
        }
        return $"Class {classId}";
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
