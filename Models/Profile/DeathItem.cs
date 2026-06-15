using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

/// <summary>
/// One item dropped into a deathpile, captured on a <see cref="DeathRecord"/>
/// at the moment of death. Split across the record's two lists: items worn at
/// death (<see cref="DeathRecord.EquippedAtDeath"/>) carry their
/// <see cref="Slot"/>; carried-but-unworn items
/// (<see cref="DeathRecord.LostItems"/>) leave <see cref="Slot"/> <c>null</c>.
/// </summary>
public sealed class DeathItem
{
    /// <summary>Bare item name as the game prints it (slot suffix stripped).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Worn-slot label at death (the <see cref="Game.Inventory.EquippedItem.Slot"/>
    /// vocabulary), or <c>null</c> for a carried-but-unworn item. Drives the
    /// re-equip verb on auto-recovery (<see cref="IsHeld"/>).
    /// </summary>
    public string? Slot { get; set; }

    /// <summary>
    /// True when the item was readied in a hand at death — re-equip uses
    /// <c>hold</c> for these and <c>wear</c> for everything else. Display-only,
    /// derived from <see cref="Slot"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsHeld => Slot is "Weapon Hand" or "Off-Hand";

    public DeathItem() { }

    public DeathItem(string name, string? slot = null)
    {
        Name = name;
        Slot = slot;
    }
}
