namespace FujinTerm.Game.Inventory;

/// <summary>
/// The class-derived inputs <see cref="ItemEquipFilter.CanEquip"/> needs beyond the
/// item row: the class id (for <c>ClassRest</c> / <c>ClassOk</c> matching), the
/// class's weapon and armour capability tiers (the <c>Classes.WeaponType</c> /
/// <c>ArmourType</c> codes that gate which weapon families and armour grades the
/// class may wear), and whether the class is anti-magic (a <c>Classes.Abil-*</c>
/// code 51, e.g. Witchunter — barred from magical gear). Resolved once per refresh
/// by <see cref="ItemEquipFilter.ResolveClassProfile"/> and reused across every
/// slot's candidate scan.
/// </summary>
public readonly record struct ClassEquipProfile(
    int ClassNumber, int WeaponType, int ArmourType, bool AntiMagic)
{
    /// <summary>An unresolved class — disables all class-dependent gating, so a slot
    /// lists items gated only by level and alignment.</summary>
    public static ClassEquipProfile Unknown => new(0, 0, 0, false);
}
