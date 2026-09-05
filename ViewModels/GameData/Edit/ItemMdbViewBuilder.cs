using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Builds the read-only "MDB view" the item edit dialog renders from the active set's Items.json
// row: a curated "Other Info" key/value list for the right pane plus the Details-section derived
// strings (weight, price, type label, slot label, bought/sold cross-reference). Extracted from
// the Items browser tab so a second caller (the Item Finder) can open the same record dialog.
//
// Depends only on the game-data cache and the player's charm (for shop pricing) — no view-model
// or dialog state — so it's safe to construct on demand wherever an item record needs rendering.
public sealed class ItemMdbViewBuilder
{
    private readonly GameDataCache _cache;
    private readonly int _charm;   // resolved: playerCharm > 0 ? playerCharm : 50

    public ItemMdbViewBuilder(GameDataCache cache, int playerCharm)
    {
        _cache = cache;
        _charm = playerCharm > 0 ? playerCharm : 50;
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
    // stores ArmourClass=20 → "2/0"). Bought/sold (left pane) renders EVERY shop reference in
    // Obtained From, one row per room each shop operates from (a shop can run from several).
    public ItemMdbView Build(string wccNoStr)
    {
        List<KeyValuePair<string, string>> otherInfo = new();
        List<ShopSaleRow> shops = new();
        List<DroppedByRow> droppedBy = new();
        List<PlacedInRow> placedIn = new();
        List<CastsSpellRow> castsSpells = new();
        bool isLight = false;
        bool isContainer = false;

        if (!int.TryParse(wccNoStr, out int wccNo))
            return new ItemMdbView(otherInfo, shops);

        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null)
            return new ItemMdbView(otherInfo, shops);

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
            isContainer = itemType == 8;
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

            // Standalone never-drop / delete-on-death MDB flag columns (0/1),
            // surfaced only when set. Distinct from the ability-code flags below
            // (LoyalItem etc.); these weren't shown at all before.
            if (ReadInt(el, "Not Droppable") != 0)
                otherInfo.Add(new KeyValuePair<string, string>("Not Droppable", "Yes"));
            if (ReadInt(el, "Destroy On Death") != 0)
                otherInfo.Add(new KeyValuePair<string, string>("Delete on Death", "Yes"));

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
                // CastsSp (43) — a spell the item delivers, on any item type. Rendered
                // as a clickable link to the cast spell's record (where its on-use / proc
                // wording lives, shared across every item casting it), with the spell's
                // effect / damage at the item's required level. A weapon %Spell (114) /
                // CastOnKill% (1114) that preceded it folds in as the proc trigger; a bare
                // CastsSp is a command-activated "use <item>" cast. val 0 is a no-op slot.
                if (code == 43)
                {
                    if (val > 0)
                    {
                        string castLabel = pendingTrigger is null
                            ? "Casts (on use)"
                            : $"Casts ({pendingPercent}%/{pendingTrigger})";
                        castsSpells.Add(new CastsSpellRow(
                            castLabel, val, ResolveSpellName(val), CastEffect(val, itemCastLevel)));
                    }
                    pendingPercent = 0;
                    pendingTrigger = null;
                    continue;
                }

                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
                string value = AbilityValueForDialog(code, val);
                otherInfo.Add(new KeyValuePair<string, string>(label, value));
            }

            // Dropped By — one clickable monster link per "Monster #N(X%)" token,
            // resolved to its Monsters.Name (+ drop-rate suffix). Its own linked
            // list rather than a joined string so each monster is clickable.
            droppedBy.AddRange(ResolveDroppedByLinks(obtainedFrom));

            // Placed In — one clickable room link per "Room {map}/{room}" token, a
            // static floor placement (the room's Placed list). Room-only items
            // (no shop / no monster / no giver) rendered nothing before this.
            placedIn.AddRange(ResolvePlacedInLinks(obtainedFrom));

            // Bought / sold — one clickable row per shop buy/sell location, each
            // with a "BUY: … SELL: …" line priced for the given charm under the
            // active realm's formula (the dialog re-runs this as its charm picker
            // moves). The location links to the host room's Rooms-tab record.
            double baseCopper = ShopPriceCalculator.ToCopper(ReadInt(el, "Price"), ReadInt(el, "Currency"));
            int charm = _charm;
            shops = ResolveShopSales(obtainedFrom, baseCopper, charm, _cache.ActiveRealm);

            break;
        }
        return new ItemMdbView(otherInfo, shops, isLight, isContainer, droppedBy, placedIn, castsSpells);
    }

    // Placed In: one clickable room row per "Room {map}/{room}" token in Obtained
    // From — the reverse of a room's Placed column (the item numbers it drops on
    // the floor). Deduped by room; a drop-rate suffix (rare on a fixed placement)
    // is tolerated and stripped. Links to the room's Rooms-tab record, like the
    // bought/sold shop rows.
    private List<PlacedInRow> ResolvePlacedInLinks(string obtainedFrom)
    {
        List<PlacedInRow> rows = new();
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return rows;
        HashSet<(int, int)> seen = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string t = token.Trim();
            if (!t.StartsWith("Room ", StringComparison.Ordinal)) continue;
            string rest = t[5..].Trim();
            int paren = rest.IndexOf('(');
            if (paren >= 0) rest = rest[..paren].Trim();   // drop any "(X%)"
            int slash = rest.IndexOf('/');
            if (slash <= 0) continue;
            if (!int.TryParse(rest[..slash].Trim(), out int mapNo)) continue;
            if (!int.TryParse(rest[(slash + 1)..].Trim(), out int roomNo)) continue;
            if (!seen.Add((mapNo, roomNo))) continue;
            string? name = ResolveRoomName(mapNo, roomNo);
            string locator = $"{mapNo}/{roomNo}";
            string label = string.IsNullOrEmpty(name) ? $"Room {locator}" : $"{name} - {locator}";
            rows.Add(new PlacedInRow(label, mapNo, roomNo));
        }
        return rows;
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

    // Ability codes that are boolean PRESENCE flags — the code appearing on the
    // item IS the whole signal; the paired AbilVal is noise (usually 0). Rendering
    // the raw value gave a misleading "LoyalItem: 0" (reads as "not loyal" when the
    // item IS loyal), so these surface presence instead.
    private static readonly HashSet<int> FlagAbilityCodes = new()
    {
        100,   // LoyalItem
        119,   // Del@Maint
        149,   // Remove@Maint
        154,   // Visible@Maint
        156,   // QuestItem
        1115,  // NoFirstKillDrop
        1117,  // NotSellable
        1118,  // NoRandomRegen
        1119,  // Del@Ganghouse
    };

    private string AbilityValueForDialog(int code, int rawValue)
    {
        // Codes whose value is a record number in another table (42/43 → Spells,
        // 59 → Classes, etc.) resolve to that row's Name. The static code → table
        // map lives in LookupEnums; resolution stays here because it needs the cache.
        if (LookupEnums.ReferencedTable(code) is { } table)
            return ResolveTableRef(table, rawValue);
        // Presence flags read "Yes" — the raw AbilVal (often 0) is meaningless.
        if (FlagAbilityCodes.Contains(code))
            return "Yes";
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
    // each shop's host room up via Shops.AssignedTo + Rooms.json, and build one clickable row
    // per shop whose location reads:
    //   {RoomName}              - {map}/{room}
    //   {RoomName} (SELL)       - {map}/{room}      // for Shop(sell) #N
    //   {RoomName} (NO GEN)     - {map}/{room}      // for Shop(nogen) #N
    // Plain Shop #N (no flag) is normal buy + sell, no suffix. Falls back to the raw shop
    // token (non-clickable) when the shop / room isn't in the active set.
    private List<ShopSaleRow> ResolveShopSales(string obtainedFrom, double baseCopper, int charm, RealmType realm)
    {
        List<ShopSaleRow> rows = new();
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return rows;

        // SELL ignores shop markup, so it's identical at every shop for a given
        // charm — compute it once and reuse on each shop's price line.
        double sellCopper = ShopPriceCalculator.SellCopper(baseCopper, charm, realm);

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

            // Resolve shop → markup + every room it operates from.
            (int markup, var shopRooms) = LookupShopRooms(shopId);
            string suffix = flag is null ? string.Empty : $" ({flag.ToUpperInvariant()})";

            // Priced line under the shop — only meaningful when the item carries a
            // value (Free items have no buy/sell figure). BUY uses the shop's markup,
            // which is the same across all of its rooms; SELL was computed once above.
            string price = string.Empty;
            if (baseCopper > 0)
            {
                double buyCopper = ShopPriceCalculator.BuyCopper(baseCopper, markup, charm);
                // Charm is shown in the dialog's picker now, so the line drops the
                // "@Ncha" prefix and shows just the buy/sell figures for it.
                price = $"BUY: {ShopPriceCalculator.FormatCopper(buyCopper)}   " +
                        $"SELL: {ShopPriceCalculator.FormatCopper(sellCopper)}";
            }

            // One row per room the shop runs from — each is a distinct buy location.
            // A shop that resolves no rooms still surfaces by id so the record isn't
            // silently empty.
            if (shopRooms.Count == 0)
            {
                rows.Add(new ShopSaleRow($"Shop #{shopId}{suffix} - ?", price, 0, 0));
                continue;
            }
            foreach ((string? roomName, int mapNo, int roomNo) in shopRooms)
            {
                string locator = mapNo > 0 ? $"{mapNo}/{roomNo}" : "?";
                string name = string.IsNullOrEmpty(roomName) ? $"Shop #{shopId}" : roomName;
                rows.Add(new ShopSaleRow($"{name}{suffix} - {locator}", price, mapNo, roomNo));
            }
        }
        return rows;
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

    // Resolves a Shop.Number → (markup%, all its assigned rooms) via the active set's
    // Shops.json (Assigned To = "Room {map}/{room}[, Room {map}/{room}...]", Markup% =
    // the shop's buy surcharge) + Rooms.json. A shop can host out of SEVERAL rooms —
    // each is a real place the item is buyable, so ALL are returned, one buy location
    // apiece (report paradigm-20260818-080337: the silverbark canoe's Boat Launch runs
    // from two docks — Arlysia City Docks and the Pier — but only the first showed).
    // Returns an empty room list when the shop id or its Assigned To misses.
    private (int Markup, List<(string? RoomName, int Map, int Room)> Rooms) LookupShopRooms(int shopId)
    {
        var rooms = new List<(string?, int, int)>();
        JsonDocument? shopsDoc = _cache.GetRawTable("Shops");
        if (shopsDoc is null) return (0, rooms);

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
        if (string.IsNullOrWhiteSpace(assigned)) return (markup, rooms);

        foreach (string roomToken in assigned.Split(','))
        {
            string t = roomToken.Trim();
            if (!t.StartsWith("Room ", StringComparison.Ordinal)) continue;
            string remainder = t[5..].Trim();
            int slash = remainder.IndexOf('/');
            if (slash <= 0) continue;
            if (!int.TryParse(remainder[..slash], out int mapNo)) continue;
            if (!int.TryParse(remainder[(slash + 1)..], out int roomNo)) continue;
            rooms.Add((ResolveRoomName(mapNo, roomNo), mapNo, roomNo));
        }
        return (markup, rooms);
    }

    // Map/room → the room's Name from the active set's Rooms.json, or null when
    // the table is absent or the room isn't found.
    private string? ResolveRoomName(int mapNo, int roomNo)
    {
        JsonDocument? roomsDoc = _cache.GetRawTable("Rooms");
        if (roomsDoc is null) return null;
        foreach (JsonElement el in roomsDoc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Map Number",  out JsonElement m)) continue;
            if (!el.TryGetProperty("Room Number", out JsonElement r)) continue;
            if (m.ValueKind != JsonValueKind.Number || r.ValueKind != JsonValueKind.Number) continue;
            if (m.GetInt32() != mapNo || r.GetInt32() != roomNo) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }

    // One clickable DroppedByRow per "Monster #N(X%)" token in Obtained From,
    // resolved to its Monsters.Name (+ drop-rate suffix), de-duplicated by label.
    private List<DroppedByRow> ResolveDroppedByLinks(string obtainedFrom)
    {
        List<DroppedByRow> rows = new();
        if (string.IsNullOrWhiteSpace(obtainedFrom)) return rows;
        HashSet<string> seen = new();
        foreach (string token in obtainedFrom.Split(','))
        {
            string trimmed = token.Trim();
            if (!trimmed.StartsWith("Monster #", StringComparison.Ordinal)) continue;
            string rest = trimmed[9..]; // skip "Monster #"
            int paren = rest.IndexOf('(');
            string numText = paren >= 0 ? rest[..paren] : rest;
            // Keep the drop-rate suffix ("(10%)") so the reader sees how likely the
            // drop is, e.g. "Prismatic Dragon(10%)".
            string percent = paren >= 0 ? rest[paren..].Trim() : string.Empty;
            if (!int.TryParse(numText.Trim(), out int monsterId)) continue;
            string? name = LookupMonsterName(monsterId);
            if (string.IsNullOrEmpty(name)) continue;
            string label = name + percent;
            if (seen.Add(label)) rows.Add(new DroppedByRow(label, monsterId));
        }
        return rows;
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
}

// Bundle returned by ItemMdbViewBuilder.Build. Shops is the clickable bought/sold list
// (each row links to the host room's Rooms-tab record). IsLight flags an
// ItemType==6 light so the edit dialog can grey Auto-buy / Auto-sell
// (Auto-light owns lights); IsContainer flags an ItemType==8 container so it
// can grey Auto-open (only containers can be opened).
public sealed record ItemMdbView(
    IReadOnlyList<KeyValuePair<string, string>> OtherInfo,
    IReadOnlyList<ShopSaleRow> Shops,
    bool IsLight = false,
    bool IsContainer = false,
    IReadOnlyList<DroppedByRow>? DroppedBy = null,
    IReadOnlyList<PlacedInRow>? PlacedIn = null,
    IReadOnlyList<CastsSpellRow>? CastsSpells = null);
