using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Translation layer between the Workshop's <see cref="EquipmentSlot"/> enum and
/// MajorMUD's worn-id model. Owns the display labels, the virtual-slot test, the
/// <c>Items.Worn</c> codes each slot accepts (so the slot editor can list the
/// items that fit it), and the reverse map from <see cref="EquippedItem.Slot"/>
/// strings (as <see cref="InventoryManager"/> emits them) back to an
/// <see cref="EquipmentSlot"/> for "Snapshot Current".
/// </summary>
/// <remarks>
/// The <c>Items.Worn</c> code ladder is the one <see cref="GameData.LookupEnums"/>
/// publishes (Head=2 … Worn=16, Off-Hand=12). Weapons aren't worn — they're
/// resolved by <c>ItemType == 1</c> instead. Finger and Wrist each have two
/// physical slots that share the same pair of worn codes, so both map members
/// list both codes.
/// </remarks>
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

    /// <summary>Every slot in the Workshop's display order.</summary>
    public static IReadOnlyList<EquipmentSlot> DisplayOrder { get; } =
        Enum.GetValues<EquipmentSlot>();

    /// <summary>The short label shown in the slot grid (e.g. <c>"Alt Off-Hand"</c>).</summary>
    public static string Label(EquipmentSlot slot) =>
        Labels.TryGetValue(slot, out string? l) ? l : slot.ToString();

    /// <summary>
    /// True for the two virtual slots (<see cref="EquipmentSlot.AlternateWeapon"/> /
    /// <see cref="EquipmentSlot.AlternateOffHand"/>) — they never send a wire
    /// <c>wear</c>; applying a set writes <see cref="CombatSettings"/> instead.
    /// </summary>
    public static bool IsVirtual(EquipmentSlot slot) =>
        slot is EquipmentSlot.AlternateWeapon or EquipmentSlot.AlternateOffHand;

    /// <summary>
    /// Resolve an <see cref="InventoryManager"/> worn-slot string to its
    /// <see cref="EquipmentSlot"/>, or null when unrecognised. "Finger" / "Wrist"
    /// map to slot 1 — callers wanting the paired slot fall through themselves.
    /// </summary>
    public static EquipmentSlot? FromWornString(string? slot) =>
        !string.IsNullOrEmpty(slot) && FromWorn.TryGetValue(slot, out EquipmentSlot s) ? s : null;

    /// <summary>
    /// The game-data item names that can occupy <paramref name="slot"/> <i>and</i>
    /// that a character of <paramref name="level"/> / <paramref name="classProfile"/>
    /// / <paramref name="alignment"/> can equip — sorted, de-duplicated; the
    /// suggestion list for the slot's item field. Weapon slots list every
    /// <c>ItemType == 1</c> item; physical slots list items whose <c>Worn</c> code
    /// matches. Each candidate then passes through <see cref="ItemEquipFilter"/>, so
    /// a Mystic-barred longsword or an evil-only blade never reaches the wrong
    /// character. A non-positive level, an <see cref="ClassEquipProfile.Unknown"/>
    /// class, or a null alignment bucket disables that dimension's filter. Returns
    /// empty when no <c>Items</c> table is loaded.
    /// </summary>
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

    /// <summary>
    /// True when the loaded <c>Items</c> table holds at least one named item that can
    /// occupy <paramref name="slot"/> — a weapon (<c>ItemType == 1</c>) for the weapon
    /// slots, or a matching <c>Worn</c> code for a physical slot. Independent of any
    /// character filter: it answers "does this game-data set have gear for the slot at
    /// all", so the Equipment Manager can drop a slot the realm never fills (e.g. an
    /// Eyes / Face slot with no items). False when no <c>Items</c> table is loaded.
    /// </summary>
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
