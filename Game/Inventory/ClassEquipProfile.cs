namespace FujinTerm.Game.Inventory;

// The class-derived inputs ItemEquipFilter.CanEquip needs beyond the item row:
// the class id (for ClassRest / ClassOk matching), the class's weapon and armour
// capability tiers (the Classes.WeaponType / ArmourType codes that gate which
// weapon families and armour grades the class may wear), and whether the class
// is anti-magic (a Classes.Abil-* code 51, e.g. Witchunter — barred from
// magical gear). Resolved once per refresh by ItemEquipFilter.ResolveClassProfile
// and reused across every slot's candidate scan.
public readonly record struct ClassEquipProfile(
    int ClassNumber, int WeaponType, int ArmourType, bool AntiMagic)
{
    // An unresolved class — disables all class-dependent gating, so a slot lists
    // items gated only by level and alignment.
    public static ClassEquipProfile Unknown => new(0, 0, 0, false);
}
