namespace FujinTerm.Models.Profile;

/// <summary>
/// One slot's intent inside an <see cref="EquipmentSet"/>: which item the set
/// wants in <see cref="Slot"/>.
/// </summary>
/// <remarks>
/// <para>
/// A null / empty <see cref="ItemName"/> is the slot's <c>{no change}</c> state —
/// the set leaves the slot untouched on apply, so the character keeps whatever is
/// already there. Only slots that name an item take part in the apply diff; a set
/// never forces a slot bare. Sets persist only their item-bearing slots, so the
/// list is sparse.
/// </para>
/// <para>
/// For the virtual slots (<see cref="EquipmentSlot.AlternateWeapon"/> /
/// <see cref="EquipmentSlot.AlternateOffHand"/>) a named item writes the backing
/// <see cref="CombatSettings"/> field instead of sending a <c>wear</c>; an empty
/// name leaves it unchanged.
/// </para>
/// </remarks>
public sealed class EquipmentSlotEntry
{
    /// <summary>Which of the 21 slots this entry configures.</summary>
    public EquipmentSlot Slot { get; set; }

    /// <summary>Game-data item name the set wants worn here. Null / empty = no change.</summary>
    public string? ItemName { get; set; }

    public EquipmentSlotEntry() { }

    public EquipmentSlotEntry(EquipmentSlot slot, string? itemName)
    {
        Slot = slot;
        ItemName = itemName;
    }
}
