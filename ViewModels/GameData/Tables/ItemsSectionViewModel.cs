using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

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
        PlayerStats? playerStats = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _overlaySeed = overlaySeed;
        _playerStats = playerStats;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

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
        // the dialog renders them as the read-only "Other Info" pane.
        ItemMdbView mdb = BuildMdbView(wcc);

        ItemEditDialogViewModel vm = new(
            wccNoStr:     wcc,
            mdbName:      row.Get("Name") ?? string.Empty,
            existing:     existing,
            currentTier:  row.SourceTier,
            mdbInfo:      mdb.OtherInfo,
            isLight:      mdb.IsLight);

        ItemEditResult? result = await _dialogs.OpenWindowAsync<ItemEditDialogViewModel, ItemEditResult>(vm);
        if (result is null) return;

        // Defaults tier is read-only (MDB is the source of truth) — fall
        // back to Character if the user picks it. Same guard MonstersTab uses.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;
        _resolverRef?.WriteGameDataAt(tier, "Items", result.WccNoStr, result.Overlay);

        Reload();
    }

    // Builds the read-only views the dialog needs from the active set's Items.json row: a
    // curated "Other Info" key/value list for the right pane plus the Details-section derived
    // strings (weight, price, type label, slot label, bought/sold cross-reference).
    //
    // "Other Info" mirrors MegaMUD's Game Item Details right-pane ordering (verified against
    // stock items 172 / 203 / 283 / 304 / 438 / 741 / 784). Layout — anything unset is
    // suppressed unless MegaMUD shows the row even when empty (in which case we render "None"):
    //   1. WCC No
    //   2. Game Max (from Limit) — when > 0
    //   3. Uses Per Day (from UseCount) — when > 0 (-1 / 0 suppressed)
    //   4. Weapon block (ItemType==Weapon): Weapon Type, Weapon Damage, Weapon Speed
    //   5. Armour block (ItemType==Armour): Armour Type
    //   6. Accuracy Bonus / AC Bonus / Required Strength — always
    //   7. Also Used By — one row per non-zero ClassRest-N
    //   8. Negates — one row per non-zero NegateSpell-N (resolved to Spells.Name)
    //   9. Ability rows — one per Abil-N with code > 0, even when AbilVal is 0 (so
    //      "Del@Maint: 0" surfaces). Stat-bonus codes render signed (+5); value/threshold
    //      codes render raw. LearnSpell + CastSpell resolve their values to Spells.Name.
    //  10. Dropped By — comma-joined Monsters parsed from Obtained From.
    // AC Bonus follows MegaMUD's convention of ArmourClass/10 + "/" + DamageResist/10 (stock
    // stores ArmourClass=20 → "2/0"). Bought/sold (left pane) picks the FIRST shop reference
    // from Obtained From and resolves it to Shop.Name.
    private ItemMdbView BuildMdbView(string wccNoStr)
    {
        List<KeyValuePair<string, string>> otherInfo = new();
        bool isLight = false;

        if (!int.TryParse(wccNoStr, out int wccNo))
            return new ItemMdbView(otherInfo);

        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null)
            return new ItemMdbView(otherInfo);

        // Spell-effect renderer for weapon use-cast / proc rows. An item's
        // cast spell scales to the item's required level (ability code 135 =
        // MinLevel) — that level is the spell's effective base level when the
        // spell is delivered by the item rather than learned by a character.
        // Per-level-only spells (no flat base, scaling purely via Min/MaxInc)
        // therefore render zero at level 0 and only surface real damage once
        // evaluated at the item's required level. The TextBlock cast-index is a
        // full-table scan, so build it lazily on the first cast-bearing item.
        KnownSpellCatalog catalog = new(_cache);
        IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>>? tbIndex = null;
        string CastEffect(int spellNumber, int castLevel)
        {
            if (catalog.GetFormulaByNumber(spellNumber) is not { } f) return string.Empty;
            tbIndex ??= catalog.BuildCastByTextblockIndex();
            IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> idx = tbIndex;
            string rendered = SpellEffectFormatter.Format(
                f, level: castLevel,
                resolveChain: catalog.GetFormulaByNumber,
                resolveSpellName: catalog.GetSpellNameByNumber,
                resolveTextblockCasts: tb => idx.TryGetValue(tb, out IReadOnlyList<KnownSpell>? list)
                    ? list : System.Array.Empty<KnownSpell>(),
                resolveMonsterName: n => _cache.FindNameByNumber("Monsters", n));
            return rendered == "—" ? string.Empty : rendered;
        }

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != wccNo) continue;

            int itemType = ReadInt(el, "ItemType");
            isLight = itemType == 6;
            string obtainedFrom = ReadString(el, "Obtained From");

            // ----- Other Info pane (right pane) -----
            otherInfo.Add(new KeyValuePair<string, string>("WCC No", wccNoStr));

            // Weight (Encum) — item type and body slot are obvious from the
            // rows/columns above, so only weight moves to this pane.
            string weight = ReadString(el, "Encum");
            if (!string.IsNullOrEmpty(weight))
                otherInfo.Add(new KeyValuePair<string, string>("Weight", weight));

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

                // BSable — explicit Yes/No row for weapons. A weapon is backstab-eligible iff
                // any Abil-N slot holds code 116. Showing this unconditionally on weapons lets
                // the user tell at a glance whether the item is BS-usable without scanning the
                // abilities list.
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
            //
            // On weapons, a CastsSp (43) is interpreted via the code that
            // precedes it: a %Spell (114) folds in as a per-swing proc
            // ("Casts (25%/swing)"), a CastOnKill% (1114) as a kill proc
            // ("Casts (25%/kill)"). A 43 with no pending modifier is a
            // command-activated cast ("Casts (on use)"). The modifier codes
            // (114 / 1114) are consumed silently and never emit their own rows.
            bool isWeapon = itemType == 1;
            int pendingPercent = 0;
            string? pendingTrigger = null;   // "swing" | "kill"

            // Item required level (ability code 135 = MinLevel) is the base
            // level the item's cast spell scales to. There's no dedicated
            // level column on item records — it's encoded as an ability pair.
            int itemCastLevel = 0;
            for (int i = 0; i < 20; i++)
            {
                if (ReadInt(el, $"Abil-{i}") == 135) { itemCastLevel = ReadInt(el, $"AbilVal-{i}"); break; }
            }
            for (int i = 0; i < 20; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                // Code 116 (BSable) is surfaced as an explicit Yes/No row
                // above (weapon block); suppress the duplicate here.
                if (code == 116) continue;
                int val = ReadInt(el, $"AbilVal-{i}");

                if (isWeapon && code == 114)       // %Spell → modifies the next CastsSp
                {
                    pendingPercent = val;
                    pendingTrigger = "swing";
                    continue;
                }
                if (isWeapon && code == 1114)      // CastOnKill% → modifies the next CastsSp
                {
                    pendingPercent = val;
                    pendingTrigger = "kill";
                    continue;
                }
                if (isWeapon && code == 43)        // CastsSp — fold any pending modifier
                {
                    string castLabel = pendingTrigger is null
                        ? "Casts (on use)"
                        : $"Casts ({pendingPercent}%/{pendingTrigger})";
                    otherInfo.Add(new KeyValuePair<string, string>(castLabel, ResolveSpellName(val)));

                    // Indented sub-row showing the cast spell's effect /
                    // damage at the item's required level (e.g. "Dmg 10–40").
                    // Suppressed when the spell yields no figure or isn't in the set.
                    string castEffect = CastEffect(val, itemCastLevel);
                    if (castEffect.Length > 0)
                        otherInfo.Add(new KeyValuePair<string, string>("  Effect", castEffect));

                    pendingPercent = 0;
                    pendingTrigger = null;
                    continue;
                }

                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
                string value = AbilityValueForDialog(code, val);
                otherInfo.Add(new KeyValuePair<string, string>(label, value));

                // A CastsSp (43) on a non-weapon item — potion / wand / scroll —
                // is a use-activated cast. The weapon branch above renders the
                // cast spell's effect inline; do the same here so "use this item"
                // effects surface the damage / heal they do, not just the spell
                // name. (Weapon CastsSp is consumed by the isWeapon branch and
                // never reaches this row.)
                if (code == 43)
                {
                    string castEffect = CastEffect(val, itemCastLevel);
                    if (castEffect.Length > 0)
                        otherInfo.Add(new KeyValuePair<string, string>("  Effect", castEffect));
                }
            }

            // Dropped By — extract Monster #N(X%) tokens, resolve each
            // to its Monsters.Name, comma-join. Skip when the item has
            // no Monster references in Obtained From.
            string droppedBy = ResolveDroppedBy(obtainedFrom);
            if (!string.IsNullOrEmpty(droppedBy))
                otherInfo.Add(new KeyValuePair<string, string>("Dropped By", droppedBy));

            // Bought / sold — shop buy/sell locations, each followed by an
            // "@<charm>cha BUY: … SELL: …" line priced for the current
            // character's charm (or the neutral retail charm of 50 when it's
            // unknown) under the active realm's formula.
            double baseCopper = ShopPriceCalculator.ToCopper(ReadInt(el, "Price"), ReadInt(el, "Currency"));
            int charm = (_playerStats?.Charm ?? 0) > 0 ? _playerStats!.Charm : 50;
            string boughtSold = ResolveBoughtSold(obtainedFrom, baseCopper, charm, _cache.ActiveRealm);
            if (!string.IsNullOrEmpty(boughtSold))
                otherInfo.Add(new KeyValuePair<string, string>("Bought / sold", boughtSold));

            break;
        }
        return new ItemMdbView(otherInfo, isLight);
    }

    // ----- Ability-row formatting helpers -----

    // Ability codes whose value is a stat bonus and should render signed ("+5") rather than
    // raw ("5"). The remainder render raw — they encode thresholds, counts, ids, etc. where a
    // sign would be misleading.
    private static readonly HashSet<int> SignedAbilityCodes = new()
    {
        1, 2, 3, 4, 5, 7, 8, 17, 18, 22, 27, 29, 30, 31, 32, 33,
        34, 36, 37, 38, 39, 40, 41, 44, 45, 46, 47, 48, 49, 51,
        58, 65, 66, 67, 68, 69, 70, 145, 187,
    };

    private string AbilityValueForDialog(int code, int rawValue)
    {
        // Codes whose value is a record number in another table (42/43 → Spells,
        // 59 → Classes, etc.) resolve to that row's Name. The static code → table
        // map lives in LookupEnums; resolution stays here because it needs the cache.
        if (LookupEnums.ReferencedTable(code) is { } table)
            return ResolveTableRef(table, rawValue);
        return SignedAbilityCodes.Contains(code)
            ? FormatSigned(rawValue)
            : rawValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Resolve an ability value that is a record number in table to that row's Name. 0 →
    // "None"; an absent row — or a table without a Name column (e.g. TextBlocks) — falls back
    // to the raw number.
    private string ResolveTableRef(string table, int value)
    {
        if (value == 0) return "None";
        string? name = _cache.FindNameByNumber(table, value);
        return string.IsNullOrEmpty(name)
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : name;
    }

    private static string FormatSigned(int n) => n > 0
        ? "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Signed value, or empty when zero — used by the "skip when None" row helpers.
    private static string FormatSignedOrEmpty(int n) => n == 0 ? string.Empty : FormatSigned(n);

    // MegaMUD's Weapon-Type labels use "2-Handed Sharp" not "2H Sharp". Returns empty when the
    // WeaponType code is 0 (i.e. not a weapon / no weapon-class assigned) so the caller
    // suppresses the row.
    private static string FormatWeaponTypeOrEmpty(int code) => code switch
    {
        0 => string.Empty,
        1 => "2-Handed Blunt",
        2 => "1-Handed Sharp",
        3 => "2-Handed Sharp",
        _ => LookupEnums.FormatWeaponType(code.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? string.Empty,
    };

    // Armour-Type label, or empty when 0. Note the stock data has ArmourType=0 mapping to
    // "Natural" in LookupEnums; for the dialog we treat 0 as "no armour type" → suppress the
    // row, matching the user's preference to hide None-valued requirements.
    private static string FormatArmourTypeOrEmpty(int code) => code == 0
        ? string.Empty
        : LookupEnums.FormatArmourType(code.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? string.Empty;

    // "5-12" pair when either is non-zero, or empty when both are zero.
    private static string FormatRangeOrEmpty(int min, int max) =>
        (min == 0 && max == 0) ? string.Empty : $"{min}-{max}";

    // AC Bonus in MegaMUD's slash-pair form (ArmourClass ÷ 10), or empty when both are zero.
    private static string FormatAcBonusOrEmpty(JsonElement el)
    {
        int ac = ReadInt(el, "ArmourClass");
        int dr = ReadInt(el, "DamageResist");
        if (ac == 0 && dr == 0) return string.Empty;
        return $"{ac / 10}/{dr / 10}";
    }

    // Add a (label, value) row only when value is non-empty. Centralises the None-suppression rule.
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

    // True when any Abil-N (N = 0..19) on the row equals code.
    private static bool HasAbility(JsonElement el, int code)
    {
        for (int i = 0; i < 20; i++)
            if (ReadInt(el, $"Abil-{i}") == code) return true;
        return false;
    }

    // Look up a spell's Name by its Number; falls back to the raw id when absent.
    private string ResolveSpellName(int spellNumber)
    {
        if (spellNumber == 0) return "None";
        string? name = _cache.FindNameByNumber("Spells", spellNumber);
        return string.IsNullOrEmpty(name)
            ? spellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : name;
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

    // Bought/sold: enumerate every Shop #N / Shop(flag) #N reference in Obtained From, look
    // each shop's host room up via Shops.AssignedTo + Rooms.json, and render one line per shop
    // in the form:
    //   {RoomName}              - {map}/{room}
    //   {RoomName} (SELL)       - {map}/{room}      // for Shop(sell) #N
    //   {RoomName} (NO GEN)     - {map}/{room}      // for Shop(nogen) #N
    // Plain Shop #N (no flag) is normal buy + sell, no suffix. Falls back to the raw shop
    // token when the shop / room isn't in the active set.
    private string ResolveBoughtSold(string obtainedFrom, double baseCopper, int charm, RealmType realm)
    {
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return string.Empty;

        // SELL ignores shop markup, so it's identical at every shop for a given
        // charm — compute it once and reuse on each shop's price line.
        double sellCopper = ShopPriceCalculator.SellCopper(baseCopper, charm, realm);

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

            // Resolve shop → room + markup.
            (string? roomName, int mapNo, int roomNo, int markup) = LookupShopRoom(shopId);
            string suffix = flag is null ? string.Empty : $" ({flag.ToUpperInvariant()})";
            string locator = mapNo > 0 ? $"{mapNo}/{roomNo}" : "?";
            string name = string.IsNullOrEmpty(roomName) ? $"Shop #{shopId}" : roomName;

            lines.Add($"{name}{suffix} - {locator}");

            // Priced line under the shop — only meaningful when the item
            // carries a value (Free items have no buy/sell figure).
            if (baseCopper > 0)
            {
                double buyCopper = ShopPriceCalculator.BuyCopper(baseCopper, markup, charm);
                lines.Add($"@{charm}cha BUY: {ShopPriceCalculator.FormatCopper(buyCopper)} " +
                          $"SELL: {ShopPriceCalculator.FormatCopper(sellCopper)}");
            }
        }
        return string.Join("\n", lines);
    }

    // "Shop" → null; "Shop(sell)" → "sell"; "Shop(nogen)" → "no gen".
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

    // Resolves a Shop.Number → (Room.Name, map, room, markup%) via the active set's Shops.json
    // (AssignedTo = "Room {map}/{room}", Markup% = the shop's buy surcharge) + Rooms.json.
    // Returns (null, 0, 0, 0) when the shop lookup misses.
    private (string? RoomName, int Map, int Room, int Markup) LookupShopRoom(int shopId)
    {
        JsonDocument? shopsDoc = _cache.GetRawTable("Shops");
        if (shopsDoc is null) return (null, 0, 0, 0);

        string? assigned = null;
        int markup = 0;
        foreach (JsonElement el in shopsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != shopId) continue;
            assigned = ReadString(el, "Assigned To");
            markup = ReadInt(el, "Markup%");
            break;
        }
        if (string.IsNullOrWhiteSpace(assigned)) return (null, 0, 0, markup);

        // AssignedTo format: "Room {map}/{room}" (e.g. "Room 1/2334"); a shop
        // can host out of several rooms, listed comma-separated ("Room 1/169,
        // Room 1/291"). Resolve the first — it's the canonical coordinate the
        // game reports and what the bought/sold line shows.
        string firstRoom = assigned.Split(',')[0].Trim();
        if (!firstRoom.StartsWith("Room ", StringComparison.Ordinal)) return (null, 0, 0, markup);
        string remainder = firstRoom[5..].Trim();
        int slash = remainder.IndexOf('/');
        if (slash <= 0) return (null, 0, 0, markup);
        if (!int.TryParse(remainder[..slash], out int mapNo)) return (null, 0, 0, markup);
        if (!int.TryParse(remainder[(slash + 1)..], out int roomNo)) return (null, 0, 0, markup);

        JsonDocument? roomsDoc = _cache.GetRawTable("Rooms");
        if (roomsDoc is null) return (null, mapNo, roomNo, markup);

        foreach (JsonElement el in roomsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Map Number",  out JsonElement m)) continue;
            if (!el.TryGetProperty("Room Number", out JsonElement r)) continue;
            if (m.ValueKind != JsonValueKind.Number || r.ValueKind != JsonValueKind.Number) continue;
            if (m.GetInt32() != mapNo || r.GetInt32() != roomNo) continue;
            string name = ReadString(el, "Name");
            return (string.IsNullOrEmpty(name) ? null : name, mapNo, roomNo, markup);
        }
        return (null, mapNo, roomNo, markup);
    }

    // Comma-joined list of monster names parsed from Obtained From's "Monster #N(X%)" tokens.
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
        if (monsterId <= 0) return null;
        string? name = _cache.FindNameByNumber("Monsters", monsterId);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // Classes.Number → Classes.Name; falls back to "Class N" when absent.
    private string ResolveClassName(int classId)
    {
        if (classId == 0) return "None";
        string? name = _cache.FindNameByNumber("Classes", classId);
        return string.IsNullOrEmpty(name) ? $"Class {classId}" : name;
    }

    // Test seam: the rendered "Other Info" rows for a given item Number, so the use-cast
    // effect / damage rendering can be pinned without standing up a dialog. Mirrors what the
    // edit dialog's read-only pane shows.
    internal IReadOnlyList<KeyValuePair<string, string>> BuildOtherInfoForTests(string itemNumber)
        => BuildMdbView(itemNumber).OtherInfo;

    // Bundle returned by BuildMdbView. IsLight flags an ItemType==6 light so the
    // edit dialog can grey Auto-buy / Auto-sell (Auto-light owns lights).
    private sealed record ItemMdbView(
        IReadOnlyList<KeyValuePair<string, string>> OtherInfo,
        bool IsLight = false);
}
