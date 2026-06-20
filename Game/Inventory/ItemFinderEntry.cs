using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// One equippable item projected into the searchable columns the Item Finder lists —
/// slot, type, the wear requirements (level / strength), and the worn-stat totals
/// (HP / mana / regens / damage / accuracy / crits / hit-magic / backstab / AC / DR).
/// The numbers come from the same aggregation Character Info uses
/// (<see cref="CharacterCalculator.AggregateItemRow"/>), so a row's AC/DR/bonuses
/// match what the item grants once worn. <see cref="Row"/> is retained so the finder's
/// class / level / alignment filter can defer to <see cref="ItemEquipFilter.CanEquip"/>.
/// </summary>
/// <remarks>
/// Build the whole catalog with <see cref="BuildCatalog"/> — it enumerates the active
/// set's <c>Items</c> table once, keeps the rows that resolve to an
/// <see cref="EquipmentSlot"/>, and returns them sorted by slot then name (the finder's
/// default order). Two facts the aggregation summary doesn't surface — the
/// <c>Abil-135</c> min-level wear gate and <c>Abil-116</c> backstab capability — are
/// read in a single extra Abil pass per item.
/// </remarks>
public sealed record ItemFinderEntry
{
    // Items.ItemType codes the catalog keys on (weapons surface their base damage;
    // armour surfaces its tier). Other equippable types (jewellery, lights) have neither.
    private const int ArmourItemType = 0;
    private const int WeaponItemType = 1;

    // Abil-N codes whose facts the worn-stat summary doesn't expose.
    private const int MinLevelAbil = 135;   // AbilVal holds the level gate.
    private const int BackstabAbil = 116;   // presence ⇒ the weapon can backstab.

    /// <summary>Item name — the catalog's secondary sort key and the grid's Name column.</summary>
    public required string Name { get; init; }

    /// <summary>The slot the item occupies; the catalog's primary grouping / sort key.</summary>
    public required EquipmentSlot Slot { get; init; }

    /// <summary>Short slot label for the grid (e.g. <c>"Off-Hand"</c>, <c>"Finger (1)"</c>).</summary>
    public required string SlotLabel { get; init; }

    /// <summary>Numeric slot rank (<see cref="EquipmentSlot"/> order) for slot-column sorting.</summary>
    public int SlotOrder => (int)Slot;

    /// <summary>Weapon-type label (<c>"1H Sharp"</c> …), or null when the item isn't a weapon.</summary>
    public string? WeaponTypeLabel { get; init; }

    /// <summary>Armour-type label (<c>"Platemail"</c> …), or null when the item isn't armour.</summary>
    public string? ArmourTypeLabel { get; init; }

    /// <summary>Weapon-type code, or -1 when the item isn't a weapon.</summary>
    public int WeaponType { get; init; }

    /// <summary>Armour-type code, or -1 when the item isn't armour.</summary>
    public int ArmourType { get; init; }

    /// <summary>The combined type label shown in the grid — weapon type, else armour type.</summary>
    public string TypeLabel => WeaponTypeLabel ?? ArmourTypeLabel ?? string.Empty;

    /// <summary>Minimum character level to wear it (<c>Abil-135</c>), 0 when ungated.</summary>
    public int LevelReq { get; init; }

    /// <summary>Strength the item requires (weapon <c>StrReq</c>), 0 when none.</summary>
    public int StrReq { get; init; }

    /// <summary>Weapon base minimum damage, 0 for non-weapons.</summary>
    public int MinDmg { get; init; }

    /// <summary>Weapon base maximum damage, 0 for non-weapons.</summary>
    public int MaxDmg { get; init; }

    /// <summary>Accuracy the item grants (base <c>Accy</c> + the <c>Abil-22/105/106</c> sum).</summary>
    public int Accuracy { get; init; }

    /// <summary>Critical-hit bonus (<c>Abil-58</c>).</summary>
    public int Crits { get; init; }

    /// <summary>Hit-magic level (<c>Abil-28/142</c> sum).</summary>
    public int HitMagic { get; init; }

    /// <summary>True when the item carries a backstab-accuracy ability (<c>Abil-116</c>).</summary>
    public bool CanBackstab { get; init; }

    /// <summary>Backstab accuracy bonus (<c>Abil-116</c>).</summary>
    public int BsAccuracy { get; init; }

    /// <summary>Backstab minimum-damage bonus (<c>Abil-117</c>).</summary>
    public int BsMin { get; init; }

    /// <summary>Backstab maximum-damage bonus (<c>Abil-118</c>).</summary>
    public int BsMax { get; init; }

    /// <summary>Total armour class (base <c>ArmourClass</c>/10 + <c>Abil-2/10</c>).</summary>
    public double Ac { get; init; }

    /// <summary>Total damage resist (base <c>DamageResist</c>/10 + <c>Abil-7</c>/10).</summary>
    public double Dr { get; init; }

    /// <summary>Max-HP bonus (<c>Abil-88</c>).</summary>
    public int Hp { get; init; }

    /// <summary>HP-regen percent bonus (<c>Abil-123</c>).</summary>
    public int HpRegen { get; init; }

    /// <summary>Max-mana bonus (<c>Abil-69</c>).</summary>
    public int Mana { get; init; }

    /// <summary>Mana-regen percent bonus (<c>Abil-145</c>).</summary>
    public int ManaRegen { get; init; }

    /// <summary>
    /// The backing <c>Items</c> row — kept so the finder's character filter can call
    /// <see cref="ItemEquipFilter.CanEquip"/> against the live class / level / alignment.
    /// Valid for the lifetime of the cached <c>Items</c> <see cref="JsonDocument"/>.
    /// </summary>
    public required JsonElement Row { get; init; }

    // ----- grid display (blank-on-zero so the dense grid stays readable) -----

    /// <summary>Weapon damage as <c>"min-max"</c>, blank for non-weapons.</summary>
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

    private static string Plain(int v) => v != 0 ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private static string Signed(int v) => v != 0 ? v.ToString("+0;-0", CultureInfo.InvariantCulture) : string.Empty;
    private static string Decimal(double v) => v != 0 ? v.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Project every equippable item in the active set's <c>Items</c> table into a
    /// catalog, sorted by slot then name. Items with no resolvable slot (non-equip
    /// types, <c>Worn 0</c>) are skipped. Empty when no <c>Items</c> table is loaded.
    /// </summary>
    public static IReadOnlyList<ItemFinderEntry> BuildCatalog(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        JsonDocument? doc = cache.GetRawTable("Items");
        if (doc is null) return Array.Empty<ItemFinderEntry>();

        var list = new List<ItemFinderEntry>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = GetString(row, "Name");
            if (string.IsNullOrEmpty(name)) continue;
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
                StrReq = GetInt(row, "StrReq"),
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
}
