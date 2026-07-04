namespace FujinTerm.Models.Profile;

// One slot's intent inside an EquipmentSet: which item the set wants in Slot.
//
// A null / empty ItemName is the slot's {no change} state — the set leaves the
// slot untouched on apply, so the character keeps whatever is already there.
// Only slots that name an item take part in the apply diff; a set never forces a
// slot bare. Sets persist only their item-bearing slots, so the list is sparse.
//
// For the virtual slots (EquipmentSlot.AlternateWeapon /
// EquipmentSlot.AlternateOffHand) a named item writes the backing
// CombatSettings field instead of sending a wear; an empty name leaves it
// unchanged.
public sealed class EquipmentSlotEntry
{
    // Which of the 21 slots this entry configures.
    public EquipmentSlot Slot { get; set; }

    // Game-data item name the set wants worn here. Null / empty = no change.
    public string? ItemName { get; set; }

    public EquipmentSlotEntry() { }

    public EquipmentSlotEntry(EquipmentSlot slot, string? itemName)
    {
        Slot = slot;
        ItemName = itemName;
    }
}
