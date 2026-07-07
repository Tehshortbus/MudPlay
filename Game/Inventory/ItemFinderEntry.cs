using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// One equippable item projected into the searchable columns the Item Finder
// lists — slot, type, the wear requirements (level / strength), the worn-stat
// totals (HP / mana / regens / damage / accuracy / crits / hit-magic / backstab /
// AC / DR), and — for weapons, when a character swing context is supplied — the
// modelled 10-round swing average. The numbers come from the same aggregation
// Character Info uses (CharacterCalculator.AggregateItemRow), so a row's
// AC/DR/bonuses match what the item grants once worn. Row is retained so the
// finder's class / level / alignment filter can defer to ItemEquipFilter.CanEquip.
//
// Build the whole catalog with BuildCatalog — it enumerates the active set's
// Items table once, keeps the rows that resolve to an EquipmentSlot, and returns
// them sorted by slot then name (the finder's default order). Two facts the
// aggregation summary doesn't surface — the Abil-135 min-level wear gate and
// Abil-116 backstab capability — are read in a single extra Abil pass per item.
public sealed record ItemFinderEntry
{
    // Items.ItemType codes the catalog keys on (weapons surface their base damage;
    // armour surfaces its tier). Other equippable types (jewellery, lights) have neither.
    private const int ArmourItemType = 0;
    private const int WeaponItemType = 1;

    // Abil-N codes whose facts the worn-stat summary doesn't expose.
    private const int MinLevelAbil = 135;   // AbilVal holds the level gate.
    private const int BackstabAbil = 116;   // presence ⇒ the weapon can backstab.

    // Item name — the catalog's secondary sort key and the grid's Name column.
    public required string Name { get; init; }

    // The slot the item occupies; the catalog's primary grouping / sort key.
    public required EquipmentSlot Slot { get; init; }

    // Short slot label for the grid (e.g. "Off-Hand", "Finger (1)").
    public required string SlotLabel { get; init; }

    // Numeric slot rank (EquipmentSlot order) for slot-column sorting.
    public int SlotOrder => (int)Slot;

    // Weapon-type label ("1H Sharp" …), or null when the item isn't a weapon.
    public string? WeaponTypeLabel { get; init; }

    // Armour-type label ("Platemail" …), or null when the item isn't armour.
    public string? ArmourTypeLabel { get; init; }

    // Weapon-type code, or -1 when the item isn't a weapon.
    public int WeaponType { get; init; }

    // Armour-type code, or -1 when the item isn't armour.
    public int ArmourType { get; init; }

    // The combined type label shown in the grid — weapon type, else armour type.
    public string TypeLabel => WeaponTypeLabel ?? ArmourTypeLabel ?? string.Empty;

    // Minimum character level to wear it (Abil-135), 0 when ungated.
    public int LevelReq { get; init; }

    // Strength the item requires (weapon StrReq), 0 when none.
    public int StrReq { get; init; }

    // Weapon base minimum damage, 0 for non-weapons.
    public int MinDmg { get; init; }

    // Weapon base maximum damage, 0 for non-weapons.
    public int MaxDmg { get; init; }

    // Accuracy the item grants (base Accy + the Abil-22/105/106 sum).
    public int Accuracy { get; init; }

    // Critical-hit bonus (Abil-58).
    public int Crits { get; init; }

    // Hit-magic level (Abil-28/142 sum).
    public int HitMagic { get; init; }

    // True when the item carries a backstab-accuracy ability (Abil-116).
    public bool CanBackstab { get; init; }

    // Backstab accuracy bonus (Abil-116).
    public int BsAccuracy { get; init; }

    // Backstab minimum-damage bonus (Abil-117).
    public int BsMin { get; init; }

    // Backstab maximum-damage bonus (Abil-118).
    public int BsMax { get; init; }

    // Total armour class (base ArmourClass/10 + Abil-2/10).
    public double Ac { get; init; }

    // Total damage resist (base DamageResist/10 + Abil-7/10).
    public double Dr { get; init; }

    // Max-HP bonus (Abil-88).
    public int Hp { get; init; }

    // HP-regen percent bonus (Abil-123).
    public int HpRegen { get; init; }

    // Max-mana bonus (Abil-69).
    public int Mana { get; init; }

    // Mana-regen percent bonus (Abil-145).
    public int ManaRegen { get; init; }

    // Mean swings per round over a 10-round simulation for the live character
    // wielding this weapon (energy carried forward each round). 0 for non-weapons
    // and when the finder is opened without a usable character context.
    public double AvgSwings { get; init; }

    // The backing Items row — kept so the finder's character filter can call
    // ItemEquipFilter.CanEquip against the live class / level / alignment. Valid
    // for the lifetime of the cached Items JsonDocument.
    public required JsonElement Row { get; init; }

    // ----- grid display (blank-on-zero so the dense grid stays readable) -----

    // Weapon damage as "min-max", blank for non-weapons.
    public string DamageText => MinDmg != 0 || MaxDmg != 0 ? $"{MinDmg}-{MaxDmg}" : string.Empty;
    public string LevelReqText => Plain(LevelReq);
    public string StrReqText => Plain(StrReq);
    public string AccuracyText => Signed(Accuracy);
    public string CritsText => Signed(Crits);
    public string HitMagicText => Signed(HitMagic);
    public string BsAccuracyText => Signed(BsAccuracy);
    public string BsDamageText => BsMin != 0 || BsMax != 0 ? $"{BsMin}-{BsMax}" : string.Empty;
    public string AcText => Decimal(Ac);
    public string DrText => Decimal(Dr);
    public string HpText => Signed(Hp);
    public string HpRegenText => Signed(HpRegen);
    public string ManaText => Signed(Mana);
    public string ManaRegenText => Signed(ManaRegen);
    public string AvgSwingsText => AvgSwings > 0 ? AvgSwings.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;

    private static string Plain(int v) => v != 0 ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private static string Signed(int v) => v != 0 ? v.ToString("+0;-0", CultureInfo.InvariantCulture) : string.Empty;
    private static string Decimal(double v) => v != 0 ? v.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;

    // Project every equippable item in the active set's Items table into a
    // catalog, sorted by slot then name. Items with no resolvable slot (non-equip
    // types, Worn 0) are skipped, as are items the realm doesn't actually put in
    // play (see IsObtainable). Empty when no Items table is loaded. When a swing
    // context is supplied, each weapon carries its modelled 10-round swing average.
    public static IReadOnlyList<ItemFinderEntry> BuildCatalog(GameDataCache cache, SwingContext? swing = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        JsonDocument? doc = cache.GetRawTable("Items");
        if (doc is null) return Array.Empty<ItemFinderEntry>();

        var list = new List<ItemFinderEntry>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = GetString(row, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!IsObtainable(row)) continue;
            if (EquipmentSlotMap.SlotForItem(row) is not { } slot) continue;

            int itemType = GetInt(row, "ItemType");
            bool isWeapon = itemType == WeaponItemType;
            bool isArmour = itemType == ArmourItemType;

            // The slot tag picks weapon-base vs off-hand vs generic-worn handling
            // inside the aggregation — the same tag InventoryManager emits.
            string slotTag = isWeapon ? "Weapon Hand"
                : slot == EquipmentSlot.OffHand ? "Off-Hand"
                : "Worn";

            EquipmentStatSummary t = CharacterCalculator.AggregateItemRow(row, name, slotTag).Totals;
            (int levelReq, bool backstab) = ScanAbilFacts(row);

            int weaponType = isWeapon ? GetInt(row, "WeaponType") : -1;
            int armourType = isArmour ? GetInt(row, "ArmourType") : -1;

            int strReq = GetInt(row, "StrReq");
            double avgSwings = isWeapon && swing is { } ctx
                ? ctx.AvgSwingsFor(GetInt(row, "Speed"), strReq)
                : 0;

            list.Add(new ItemFinderEntry
            {
                Name = name,
                Slot = slot,
                SlotLabel = EquipmentSlotMap.Label(slot),
                WeaponTypeLabel = isWeapon
                    ? LookupEnums.FormatWeaponType(weaponType.ToString(CultureInfo.InvariantCulture))
                    : null,
                ArmourTypeLabel = isArmour
                    ? LookupEnums.FormatArmourType(armourType.ToString(CultureInfo.InvariantCulture))
                    : null,
                WeaponType = weaponType,
                ArmourType = armourType,
                LevelReq = levelReq,
                StrReq = strReq,
                MinDmg = isWeapon ? t.WeaponMin : 0,
                MaxDmg = isWeapon ? t.WeaponMax : 0,
                Accuracy = t.TotalWornAccy + t.PlusAccuracy,
                Crits = t.PlusCrits,
                HitMagic = t.PlusHitMagic,
                CanBackstab = backstab,
                BsAccuracy = t.PlusBSAccuracy,
                BsMin = t.PlusBSMin,
                BsMax = t.PlusBSMax,
                Ac = t.PlusAC,
                Dr = t.PlusDR,
                Hp = t.PlusMaxHp,
                HpRegen = t.HpRegenPercent,
                Mana = t.PlusMaxMana,
                ManaRegen = t.MpRegenPercent,
                AvgSwings = avgSwings,
                Row = row,
            });
        }

        list.Sort(static (a, b) =>
        {
            int c = a.SlotOrder.CompareTo(b.SlotOrder);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // One Abil-0..19 pass for the two facts the worn-stat summary leaves out: the
    // min-level wear gate (135, value = level) and backstab capability (116 present).
    private static (int LevelReq, bool Backstab) ScanAbilFacts(JsonElement row)
    {
        int levelReq = 0;
        bool backstab = false;
        for (int i = 0; i < 20; i++)
        {
            int code = GetInt(row, $"Abil-{i}");
            if (code == 0) continue;
            if (code == MinLevelAbil) levelReq = GetInt(row, $"AbilVal-{i}");
            else if (code == BackstabAbil) backstab = true;
        }
        return (levelReq, backstab);
    }

    // The MDB conversion stamps "In Game" = 1 on every item the realm actually
    // spawns, sells, drops, or gates behind a quest, and 0 on sysop-only,
    // unimplemented, or duplicate test rows (e.g. "bow of silver", the "large
    // rock" placeholders, "longsword1..5") — even ones a shop merely references
    // as no-generate / buy-only. Those can't be acquired in play, so the finder
    // omits them. The check is defensive: a set predating the flag (property
    // absent, or non-numeric) is treated as obtainable rather than blanked out.
    private static bool IsObtainable(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return true;
        if (!row.TryGetProperty("In Game", out JsonElement el)) return true;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v)) return true;
        return v != 0;
    }

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }

    private static string? GetString(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    // Live-character inputs the finder needs to model each weapon's swing rate —
    // everything CombatCalculator.CalcSwings consumes except the per-weapon speed
    // and strength requirement, which vary per row. Built once when the finder
    // opens (player stats + class combat level + encumbrance); left null when no
    // character is loaded, in which case the Swings column stays blank.
    public readonly record struct SwingContext(
        int CombatLevel, int Level, int Agility, int Strength,
        int CurrentEncum, int MaxEncum, RealmType Realm)
    {
        // Swing math only makes sense once the character has a real level and a
        // resolved class combat level; below that the model divides toward garbage.
        public bool IsUsable => Level > 0 && CombatLevel > 0;

        // Mean swings per round across the 10-round energy-carry simulation for a
        // weapon of the given speed / strength requirement, or 0 when unmodellable.
        public double AvgSwingsFor(int weaponSpeed, int weaponStrReq)
        {
            if (!IsUsable || weaponSpeed <= 0) return 0;
            SwingCalcResult res = CombatCalculator.CalcSwings(
                CombatLevel, Level, weaponSpeed, Agility, Strength, weaponStrReq,
                CurrentEncum, MaxEncum, realmType: Realm);
            int[] rounds = res.SwingsPerRound;
            if (rounds.Length == 0) return 0;
            int total = 0;
            foreach (int s in rounds) total += s;
            return (double)total / rounds.Length;
        }
    }
}
