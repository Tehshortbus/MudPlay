namespace FujinTerm.Models.Profile;

// The 21 equipment slots an EquipmentSet can control, in the Workshop's display
// order. AlternateWeapon / AlternateOffHand are virtual — applying a set never
// sends a wire wear for them; instead it writes the matching
// CombatSettings.AlternateWeapon / CombatSettings.AlternateOffHand so the combat
// weapon-swap matrix picks them up. The remaining slots map to real worn-item
// placements (see Game.Inventory.EquipmentSlotMap for the MajorMUD worn-id
// behind each).
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
