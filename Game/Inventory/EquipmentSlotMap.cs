using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Translation layer between the Workshop's EquipmentSlot enum and MajorMUD's
// worn-id model. Owns the display labels, the virtual-slot test, the Items.Worn
// codes each slot accepts (so the slot editor can list the items that fit it),
// and the reverse map from EquippedItem.Slot strings (as InventoryManager emits
// them) back to an EquipmentSlot for "Snapshot Current".
//
// The Items.Worn code ladder is the one LookupEnums publishes (Head=2 …
// Worn=16, Off-Hand=12). Weapons aren't worn — they're resolved by ItemType == 1
// instead. Finger and Wrist each have two physical slots that share the same
// pair of worn codes, so both map members list both codes.
public static class EquipmentSlotMap
{
    // Items.ItemType code for a weapon (the Weapon / Alt-Weapon slots filter on
    // this rather than a Worn code — weapons are held, not worn).
    private const int WeaponItemType = 1;

    private static readonly IReadOnlyDictionary<EquipmentSlot, string> Labels =
        new Dictionary<EquipmentSlot, string>
        {
            [EquipmentSlot.Weapon] = "Weapon",
            [EquipmentSlot.OffHand] = "Off-Hand",
            [EquipmentSlot.AlternateWeapon] = "Alt Weapon",
            [EquipmentSlot.AlternateOffHand] = "Alt Off-Hand",
            [EquipmentSlot.Head] = "Head",
            [EquipmentSlot.Ears] = "Ears",
            [EquipmentSlot.Eyes] = "Eyes",
            [EquipmentSlot.Face] = "Face",
            [EquipmentSlot.Neck] = "Neck",
            [EquipmentSlot.Back] = "Back",
            [EquipmentSlot.Torso] = "Torso",
            [EquipmentSlot.Arms] = "Arms",
            [EquipmentSlot.Wrist1] = "Wrist (1)",
            [EquipmentSlot.Wrist2] = "Wrist (2)",
            [EquipmentSlot.Hands] = "Hands",
            [EquipmentSlot.Finger1] = "Finger (1)",
            [EquipmentSlot.Finger2] = "Finger (2)",
            [EquipmentSlot.Waist] = "Waist",
            [EquipmentSlot.Legs] = "Legs",
            [EquipmentSlot.Feet] = "Feet",
            [EquipmentSlot.Worn] = "Worn",
        };

    // The Items.Worn code(s) that fill each physical slot. Weapon / Alt-Weapon are
    // absent — they filter on ItemType, not Worn.
    private static readonly IReadOnlyDictionary<EquipmentSlot, int[]> WornCodes =
        new Dictionary<EquipmentSlot, int[]>
        {
            [EquipmentSlot.OffHand] = new[] { 12 },
            [EquipmentSlot.AlternateOffHand] = new[] { 12 },
            [EquipmentSlot.Head] = new[] { 2 },
            [EquipmentSlot.Ears] = new[] { 15 },
            [EquipmentSlot.Eyes] = new[] { 18 },
            [EquipmentSlot.Face] = new[] { 19 },
            [EquipmentSlot.Neck] = new[] { 8 },
            [EquipmentSlot.Back] = new[] { 7 },
            [EquipmentSlot.Torso] = new[] { 11 },
            [EquipmentSlot.Arms] = new[] { 6 },
            [EquipmentSlot.Wrist1] = new[] { 14, 17 },
            [EquipmentSlot.Wrist2] = new[] { 14, 17 },
            [EquipmentSlot.Hands] = new[] { 3 },
            [EquipmentSlot.Finger1] = new[] { 4, 13 },
            [EquipmentSlot.Finger2] = new[] { 4, 13 },
            [EquipmentSlot.Waist] = new[] { 10 },
            [EquipmentSlot.Legs] = new[] { 9 },
            [EquipmentSlot.Feet] = new[] { 5 },
            [EquipmentSlot.Worn] = new[] { 16 },
        };

    // InventoryManager labels worn pieces with these strings; map each back to the
    // first matching slot. Ambiguous "Finger" / "Wrist" resolve to slot 1; the
    // snapshot caller falls through to slot 2 when slot 1 is already filled.
    private static readonly IReadOnlyDictionary<string, EquipmentSlot> FromWorn =
        new Dictionary<string, EquipmentSlot>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weapon Hand"] = EquipmentSlot.Weapon,
            ["Off-Hand"] = EquipmentSlot.OffHand,
            ["Head"] = EquipmentSlot.Head,
            ["Ears"] = EquipmentSlot.Ears,
            ["Eyes"] = EquipmentSlot.Eyes,
            ["Face"] = EquipmentSlot.Face,
            ["Neck"] = EquipmentSlot.Neck,
            ["Back"] = EquipmentSlot.Back,
            ["Torso"] = EquipmentSlot.Torso,
            ["Arms"] = EquipmentSlot.Arms,
            ["Wrist"] = EquipmentSlot.Wrist1,
            ["Hands"] = EquipmentSlot.Hands,
            ["Finger"] = EquipmentSlot.Finger1,
            ["Waist"] = EquipmentSlot.Waist,
            ["Legs"] = EquipmentSlot.Legs,
            ["Feet"] = EquipmentSlot.Feet,
            ["Worn"] = EquipmentSlot.Worn,
        };

    // Inverse of FromWorn: the InventoryManager location string for a slot. Values
    // in FromWorn are unique, so the inversion is clean; the paired Finger / Wrist
    // slots share the game's single "(Finger)" / "(Wrist)" wording via their slot-1
    // entry. Weapons take the held path, so they aren't resolved here.
    private static readonly IReadOnlyDictionary<EquipmentSlot, string> ToWornString =
        FromWorn.ToDictionary(kv => kv.Value, kv => kv.Key);

    // Every slot in the Workshop's display order.
    public static IReadOnlyList<EquipmentSlot> DisplayOrder { get; } =
        Enum.GetValues<EquipmentSlot>();

    // The short label shown in the slot grid (e.g. "Alt Off-Hand").
    public static string Label(EquipmentSlot slot) =>
        Labels.TryGetValue(slot, out string? l) ? l : slot.ToString();

    // The slot's family label without the physical-position disambiguator the
    // equipment-manager grid needs. The Item Finder collapses the paired Finger /
    // Wrist slots into one family (SlotForItem always resolves to slot 1), so the
    // "(1)" that tells the two physical slots apart carries no meaning there —
    // "Wrist", not "Wrist (1)".
    public static string FamilyLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Wrist1 or EquipmentSlot.Wrist2 => "Wrist",
        EquipmentSlot.Finger1 or EquipmentSlot.Finger2 => "Finger",
        _ => Label(slot),
    };

    // True for the two virtual slots (AlternateWeapon / AlternateOffHand) — they
    // never send a wire wear; applying a set writes CombatSettings instead.
    public static bool IsVirtual(EquipmentSlot slot) =>
        slot is EquipmentSlot.AlternateWeapon or EquipmentSlot.AlternateOffHand;

    // Resolve an InventoryManager worn-slot string to its EquipmentSlot, or null
    // when unrecognised. "Finger" / "Wrist" map to slot 1 — callers wanting the
    // paired slot fall through themselves.
    public static EquipmentSlot? FromWornString(string? slot) =>
        !string.IsNullOrEmpty(slot) && FromWorn.TryGetValue(slot, out EquipmentSlot s) ? s : null;

    // The InventoryManager location string an item's Items.Worn code fills
    // (e.g. code 11 → "Torso"), or null when the code isn't a wearable slot.
    // Lets the incremental "You are now wearing X." path label a freshly-worn
    // piece with its true slot instead of a generic placeholder, so "Snapshot
    // Current" files it correctly. Finger / Wrist codes resolve to the shared
    // "Finger" / "Wrist" string; the off-hand code resolves to "Off-Hand"
    // (though off-hand items take the held path, not this one).
    public static string? InventorySlotForWornCode(int worn)
    {
        foreach (EquipmentSlot slot in DisplayOrder)
            if (WornCodes.TryGetValue(slot, out int[]? codes) && codes.Contains(worn)
                && ToWornString.TryGetValue(slot, out string? s))
                return s;
        return null;
    }

    // The game-data item names that can occupy slot and that a character of the
    // given level / classProfile / alignment can equip — sorted, de-duplicated;
    // the suggestion list for the slot's item field. Weapon slots list every
    // ItemType == 1 item; physical slots list items whose Worn code matches.
    // Each candidate then passes through ItemEquipFilter, so a Mystic-barred
    // longsword or an evil-only blade never reaches the wrong character. A
    // non-positive level, an unknown class profile, or a null alignment bucket
    // disables that dimension's filter. Returns empty when no Items table is
    // loaded.
    public static IReadOnlyList<string> GetItemsForSlot(
        GameDataCache cache, EquipmentSlot slot,
        int level, ClassEquipProfile classProfile, AlignmentBucket? alignment)
    {
        ArgumentNullException.ThrowIfNull(cache);
        JsonDocument? doc = cache.GetRawTable("Items");
        if (doc is null || SlotMatcher(slot) is not { } matches) return Array.Empty<string>();

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = GetString(row, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (matches(row) && ItemEquipFilter.CanEquip(row, level, classProfile, alignment))
                names.Add(name);
        }
        return names.ToList();
    }

    // The primary EquipmentSlot an Items row occupies — a weapon (ItemType == 1)
    // resolves to Weapon, every other equippable row maps from its Worn code.
    // Returns null for an item the realm can't wear (Worn 0 / a non-equip
    // ItemType). The paired Finger / Wrist codes resolve to slot 1 — the Item
    // Finder groups by slot family, not physical position — and the off-hand
    // code resolves to OffHand over its Alt twin (both share code 12).
    public static EquipmentSlot? SlotForItem(JsonElement row)
    {
        if (GetInt(row, "ItemType") == WeaponItemType) return EquipmentSlot.Weapon;
        int worn = GetInt(row, "Worn");
        // DisplayOrder is the enum order, so the slot-1 / primary variant always wins
        // the tie for the doubled Finger / Wrist / Off-Hand codes.
        foreach (EquipmentSlot slot in DisplayOrder)
            if (WornCodes.TryGetValue(slot, out int[]? codes) && codes.Contains(worn))
                return slot;
        return null;
    }

    // True when the loaded Items table holds at least one named item that can
    // occupy slot — a weapon (ItemType == 1) for the weapon slots, or a matching
    // Worn code for a physical slot. Independent of any character filter: it
    // answers "does this game-data set have gear for the slot at all", so the
    // Equipment Manager can drop a slot the realm never fills (e.g. an Eyes /
    // Face slot with no items). False when no Items table is loaded.
    public static bool SlotHasItems(GameDataCache cache, EquipmentSlot slot)
    {
        ArgumentNullException.ThrowIfNull(cache);
        JsonDocument? doc = cache.GetRawTable("Items");
        if (doc is null || SlotMatcher(slot) is not { } matches) return false;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
            if (!string.IsNullOrEmpty(GetString(row, "Name")) && matches(row))
                return true;
        return false;
    }

    // The item-membership test for a slot: weapon slots match ItemType == 1, physical
    // slots match one of their Worn codes. Null when the slot has no membership rule.
    private static Func<JsonElement, bool>? SlotMatcher(EquipmentSlot slot)
    {
        if (slot is EquipmentSlot.Weapon or EquipmentSlot.AlternateWeapon)
            return row => GetInt(row, "ItemType") == WeaponItemType;
        return WornCodes.TryGetValue(slot, out int[]? codes)
            ? row => codes.Contains(GetInt(row, "Worn"))
            : null;
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
