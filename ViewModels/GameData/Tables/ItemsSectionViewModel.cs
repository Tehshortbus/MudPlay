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
            boughtSold   = ResolveBoughtSold(obtainedFrom);

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

            // Weapon-only block — each sub-row is suppressed when its
            // underlying field is zero, so weapons without Min/Max
            // damage or Speed don't carry empty placeholder rows.
            if (itemType == 1)
            {
                AddIfPresent(otherInfo, "Weapon Type",
                    FormatWeaponTypeOrEmpty(ReadInt(el, "WeaponType")));
                AddIfPresent(otherInfo, "Weapon Damage",
                    FormatRangeOrEmpty(ReadInt(el, "Min"), ReadInt(el, "Max")));
                AddIfPresent(otherInfo, "Weapon Speed",
                    ReadInt(el, "Speed") is int s and > 0
                        ? s.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty);

                // BSable — explicit Yes/No row for weapons. Per MMUD
                // Explorer's frmMain weapon filter (Case 116: bBSAble = True),
                // a weapon is backstab-eligible iff any Abil-N slot holds
                // code 116. Showing this unconditionally on weapons lets
                // the user tell at a glance whether the item is BS-usable
                // without scanning the abilities list.
                otherInfo.Add(new KeyValuePair<string, string>("BSable",
                    HasAbility(el, 116) ? "Yes" : "No"));
            }
            else if (itemType == 0)
            {
                AddIfPresent(otherInfo, "Armour Type",
                    FormatArmourTypeOrEmpty(ReadInt(el, "ArmourType")));
            }

            // Common stat block. Each row is suppressed entirely when
            // its source field is zero — the dialog hides "None"
            // requirements rather than rendering them as visual noise.
            AddIfPresent(otherInfo, "Accuracy Bonus", FormatSignedOrEmpty(ReadInt(el, "Accy")));
            AddIfPresent(otherInfo, "AC Bonus",       FormatAcBonusOrEmpty(el));
            int strReq = ReadInt(el, "StrReq");
            if (strReq > 0)
                AddIfPresent(otherInfo, "Required Strength",
                    strReq.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
                // Code 116 (BSable) is surfaced as an explicit Yes/No row
                // above (weapon block); suppress the duplicate here.
                if (code == 116) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
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

    /// <summary>Signed value, or empty when zero — used by the "skip when None" row helpers.</summary>
    private static string FormatSignedOrEmpty(int n) => n == 0 ? string.Empty : FormatSigned(n);

    /// <summary>
    /// MegaMUD's Weapon-Type labels use "2-Handed Sharp" not "2H Sharp".
    /// Returns empty when the WeaponType code is 0 (i.e. not a weapon /
    /// no weapon-class assigned) so the caller suppresses the row.
    /// </summary>
    private static string FormatWeaponTypeOrEmpty(int code) => code switch
    {
        0 => string.Empty,
        1 => "2-Handed Blunt",
        2 => "1-Handed Sharp",
        3 => "2-Handed Sharp",
        _ => MmudEnums.FormatWeaponType(code.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? string.Empty,
    };

    /// <summary>
    /// Armour-Type label, or empty when 0. Note the stock data has
    /// ArmourType=0 mapping to "Natural" in <see cref="MmudEnums"/>; for
    /// the dialog we treat 0 as "no armour type" → suppress the row,
    /// matching the user's preference to hide None-valued requirements.
    /// </summary>
    private static string FormatArmourTypeOrEmpty(int code) => code == 0
        ? string.Empty
        : MmudEnums.FormatArmourType(code.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? string.Empty;

    /// <summary>"5-12" pair when either is non-zero, or empty when both are zero.</summary>
    private static string FormatRangeOrEmpty(int min, int max) =>
        (min == 0 && max == 0) ? string.Empty : $"{min}-{max}";

    /// <summary>AC Bonus in MegaMUD's slash-pair form (ArmourClass ÷ 10), or empty when both are zero.</summary>
    private static string FormatAcBonusOrEmpty(JsonElement el)
    {
        int ac = ReadInt(el, "ArmourClass");
        int dr = ReadInt(el, "DamageResist");
        if (ac == 0 && dr == 0) return string.Empty;
        return $"{ac / 10}/{dr / 10}";
    }

    /// <summary>Add a (label, value) row only when value is non-empty. Centralises the None-suppression rule.</summary>
    private static void AddIfPresent(List<KeyValuePair<string, string>> list, string label, string value)
    {
        if (!string.IsNullOrEmpty(value))
            list.Add(new KeyValuePair<string, string>(label, value));
    }

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    /// <summary>True when any <c>Abil-N</c> (N = 0..19) on the row equals <paramref name="code"/>.</summary>
    private static bool HasAbility(JsonElement el, int code)
    {
        for (int i = 0; i < 20; i++)
            if (ReadInt(el, $"Abil-{i}") == code) return true;
        return false;
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

    /// <summary>Renders "5 Silver" / "1 Gold" from the (Price, Currency) pair.</summary>
    private static string FormatPrice(JsonElement el)
    {
        string price = ReadString(el, "Price");
        if (string.IsNullOrWhiteSpace(price) || price == "0") return "Free";
        string currency = MmudEnums.FormatCurrency(ReadString(el, "Currency")) ?? string.Empty;
        return string.IsNullOrEmpty(currency) ? price : $"{price} {currency}";
    }

    /// <summary>
    /// Bought/sold: enumerate every <c>Shop #N</c> / <c>Shop(flag) #N</c>
    /// reference in <c>Obtained From</c>, look each shop's host room up
    /// via <c>Shops.AssignedTo</c> + <c>Rooms.json</c>, and render one
    /// line per shop in the form:
    /// <code>
    ///   {RoomName}              - {map}/{room}
    ///   {RoomName} (SELL)       - {map}/{room}      // for Shop(sell) #N
    ///   {RoomName} (NO GEN)     - {map}/{room}      // for Shop(nogen) #N
    /// </code>
    /// Plain <c>Shop #N</c> (no flag) is normal buy + sell, no suffix.
    /// Falls back to the raw shop token when the shop / room isn't in
    /// the active set.
    /// </summary>
    private string ResolveBoughtSold(string obtainedFrom)
    {
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return string.Empty;
        List<string> lines = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            if (!trimmed.StartsWith("Shop", StringComparison.Ordinal)) continue;

            // Extract optional flag (e.g. "sell" from "Shop(sell) #89")
            // and the numeric id.
            int hashIdx = trimmed.IndexOf('#');
            if (hashIdx < 0) continue;
            string prefix = trimmed[..hashIdx];          // "Shop", "Shop(sell)", "Shop(nogen)"
            string numText = trimmed[(hashIdx + 1)..].TrimStart();
            int paren = numText.IndexOf('(');
            if (paren >= 0) numText = numText[..paren];
            if (!int.TryParse(numText.Trim(), out int shopId)) continue;

            string? flag = ExtractShopFlag(prefix);

            // Resolve shop → room.
            (string? roomName, int mapNo, int roomNo) = LookupShopRoom(shopId);
            string suffix = flag is null ? string.Empty : $" ({flag.ToUpperInvariant()})";
            string locator = mapNo > 0 ? $"{mapNo}/{roomNo}" : "?";
            string name = string.IsNullOrEmpty(roomName) ? $"Shop #{shopId}" : roomName;

            lines.Add($"{name}{suffix} - {locator}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>"Shop" → null; "Shop(sell)" → "sell"; "Shop(nogen)" → "no gen".</summary>
    private static string? ExtractShopFlag(string prefix)
    {
        int open = prefix.IndexOf('(');
        if (open < 0) return null;
        int close = prefix.IndexOf(')', open + 1);
        if (close <= open + 1) return null;
        string raw = prefix[(open + 1)..close].Trim();
        // Friendly-ify the few known flags; leave anything else as the raw token.
        return raw switch
        {
            "sell"  => "SELL",
            "nogen" => "NO GEN",
            _        => raw,
        };
    }

    /// <summary>
    /// Resolves a Shop.Number → (Room.Name, map, room) via the active set's
    /// Shops.json (AssignedTo = "Room {map}/{room}") + Rooms.json. Returns
    /// (null, 0, 0) when any lookup misses.
    /// </summary>
    private (string? RoomName, int Map, int Room) LookupShopRoom(int shopId)
    {
        JsonDocument? shopsDoc = _cache.GetRawTable("Shops");
        if (shopsDoc is null) return (null, 0, 0);

        string? assigned = null;
        foreach (JsonElement el in shopsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != shopId) continue;
            assigned = ReadString(el, "Assigned To");
            break;
        }
        if (string.IsNullOrWhiteSpace(assigned)) return (null, 0, 0);

        // AssignedTo format: "Room {map}/{room}" (e.g. "Room 1/2334").
        if (!assigned.StartsWith("Room ", StringComparison.Ordinal)) return (null, 0, 0);
        string remainder = assigned[5..].Trim();
        int slash = remainder.IndexOf('/');
        if (slash <= 0) return (null, 0, 0);
        if (!int.TryParse(remainder[..slash], out int mapNo)) return (null, 0, 0);
        if (!int.TryParse(remainder[(slash + 1)..], out int roomNo)) return (null, 0, 0);

        JsonDocument? roomsDoc = _cache.GetRawTable("Rooms");
        if (roomsDoc is null) return (null, mapNo, roomNo);

        foreach (JsonElement el in roomsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Map Number",  out JsonElement m)) continue;
            if (!el.TryGetProperty("Room Number", out JsonElement r)) continue;
            if (m.ValueKind != JsonValueKind.Number || r.ValueKind != JsonValueKind.Number) continue;
            if (m.GetInt32() != mapNo || r.GetInt32() != roomNo) continue;
            string name = ReadString(el, "Name");
            return (string.IsNullOrEmpty(name) ? null : name, mapNo, roomNo);
        }
        return (null, mapNo, roomNo);
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
