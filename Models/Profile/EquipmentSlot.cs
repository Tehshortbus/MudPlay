namespace FujinTerm.Models.Profile;

/// <summary>
/// The 21 equipment slots an <see cref="EquipmentSet"/> can control, in the
/// Workshop's display order. <see cref="AlternateWeapon"/> /
/// <see cref="AlternateOffHand"/> are <i>virtual</i> — applying a set never
/// sends a wire <c>wear</c> for them; instead it writes the matching
/// <see cref="CombatSettings.AlternateWeapon"/> /
/// <see cref="CombatSettings.AlternateOffHand"/> so the combat weapon-swap
/// matrix picks them up. The remaining slots map to real worn-item placements
/// (see <see cref="Game.Inventory.EquipmentSlotMap"/> for the MajorMUD worn-id
/// behind each).
/// </summary>
public enum EquipmentSlot
{
    Weapon,
    OffHand,
    AlternateWeapon,
    AlternateOffHand,
    Head,
    Ears,
    Eyes,
    Face,
    Neck,
    Back,
    Torso,
    Arms,
    Wrist1,
    Wrist2,
    Hands,
    Finger1,
    Finger2,
    Waist,
    Legs,
    Feet,
    Worn,
}
