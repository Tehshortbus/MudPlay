using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

// One item dropped into a deathpile, captured on a DeathRecord at the moment of
// death. Split across the record's two lists: items worn at death
// (DeathRecord.EquippedAtDeath) carry their Slot; carried-but-unworn items
// (DeathRecord.LostItems) leave Slot null.
public sealed class DeathItem
{
    // Bare item name as the game prints it (slot suffix stripped).
    public string Name { get; set; } = string.Empty;

    // Worn-slot label at death (the Game.Inventory.EquippedItem.Slot
    // vocabulary), or null for a carried-but-unworn item. Drives the re-equip
    // verb on auto-recovery (IsHeld).
    public string? Slot { get; set; }

    // True when the item was readied in a hand at death — re-equip uses hold for
    // these and wear for everything else. Display-only, derived from Slot.
    [JsonIgnore]
    public bool IsHeld => Slot is "Weapon Hand" or "Off-Hand";

    public DeathItem() { }

    public DeathItem(string name, string? slot = null)
    {
        Name = name;
        Slot = slot;
    }
}
