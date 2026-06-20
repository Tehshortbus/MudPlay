namespace FujinTerm.Models.Profile;

/// <summary>
/// One slot's intent inside an <see cref="EquipmentSet"/>: which item the set
/// wants in <see cref="Slot"/>, and whether the set governs that slot at all.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Controlled"/> is the per-slot checkbox on the Workshop's set
/// editor. When false the set leaves the slot untouched on apply — the
/// character keeps whatever is already there. Only controlled slots take part
/// in the apply diff.
/// </para>
/// <para>
/// A controlled slot with a null / empty <see cref="ItemName"/> means "this set
/// owns this slot but specifies no item" — applying it does not equip anything
/// for that slot (it does not force the slot bare; full-loadout sets transition
/// via the game's auto-swap when a different item is worn). For virtual slots
/// (<see cref="EquipmentSlot.AlternateWeapon"/> /
/// <see cref="EquipmentSlot.AlternateOffHand"/>) an empty name leaves the
/// backing <see cref="CombatSettings"/> field unchanged.
/// </para>
/// </remarks>
public sealed class EquipmentSlotEntry
{
    /// <summary>Which of the 21 slots this entry configures.</summary>
    public EquipmentSlot Slot { get; set; }

    /// <summary>Does this set govern <see cref="Slot"/>? Unchecked = untouched on apply.</summary>
    public bool Controlled { get; set; } = true;

    /// <summary>Game-data item name the set wants worn here. Null / empty = no item.</summary>
    public string? ItemName { get; set; }

    public EquipmentSlotEntry() { }

    public EquipmentSlotEntry(EquipmentSlot slot, bool controlled, string? itemName)
    {
        Slot = slot;
        Controlled = controlled;
        ItemName = itemName;
    }
}
