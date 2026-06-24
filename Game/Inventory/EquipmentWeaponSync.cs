using System;
using System.Linq;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Derives the combat weapon-swap matrix — the six weapon fields on
/// <see cref="CombatSettings"/> — from the Equipment Manager's gear sets, which
/// are now the single editing surface for combat weapons (the Combat tab no
/// longer picks them). Applied as a read-time overlay onto the
/// <see cref="CombatSettings"/> that <see cref="Game.Combat.CombatManager"/>
/// reads each round, so the values always reflect the current gear sets and the
/// backstab fallback is re-evaluated live against the set's
/// <see cref="EquipmentSet.Enabled"/> state.
/// </summary>
/// <remarks>
/// Normal + alternate weapons come from the <see cref="EquipTriggerType.Default"/>
/// set (its <see cref="EquipmentSlot.Weapon"/> / <see cref="EquipmentSlot.OffHand"/>
/// and the virtual <see cref="EquipmentSlot.AlternateWeapon"/> /
/// <see cref="EquipmentSlot.AlternateOffHand"/> slots). Backstab gear comes from
/// the <see cref="EquipTriggerType.Backstab"/> set when the user has enabled it,
/// otherwise it falls back to the Default set's weapon so "Do BS attacks" works
/// without a dedicated backstab loadout.
/// </remarks>
public static class EquipmentWeaponSync
{
    /// <summary>
    /// Overlay the gear-set-derived weapons onto <paramref name="combat"/>.
    /// Overwrites all six weapon fields unconditionally (clearing one to
    /// <c>null</c> when its gear slot is unset) so removing a gear item also
    /// clears the combat reference.
    /// </summary>
    public static void ApplyWeapons(CombatSettings combat, EquipmentSettings equipment)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(equipment);

        EquipmentSet? def = Find(equipment, EquipTriggerType.Default);
        EquipmentSet? backstab = Find(equipment, EquipTriggerType.Backstab);

        combat.NormalWeapon     = Slot(def, EquipmentSlot.Weapon);
        combat.NormalOffHand    = Slot(def, EquipmentSlot.OffHand);
        combat.AlternateWeapon  = Slot(def, EquipmentSlot.AlternateWeapon);
        combat.AlternateOffHand = Slot(def, EquipmentSlot.AlternateOffHand);

        // Backstab gear: the Backstab set when the user enabled it, otherwise the
        // Default set (so "Do BS attacks" works with just the baseline weapon).
        EquipmentSet? bsSource = backstab is { Enabled: true } ? backstab : def;
        combat.BackstabWeapon  = Slot(bsSource, EquipmentSlot.Weapon);
        combat.BackstabOffHand = Slot(bsSource, EquipmentSlot.OffHand);
    }

    private static EquipmentSet? Find(EquipmentSettings equipment, EquipTriggerType trigger)
        => equipment.Sets.FirstOrDefault(s => s.Trigger == trigger);

    /// <summary>The trimmed item name a set wants in <paramref name="slot"/>, or
    /// <c>null</c> when the set is missing or the slot is unset / blank.</summary>
    private static string? Slot(EquipmentSet? set, EquipmentSlot slot)
    {
        if (set is null) return null;
        foreach (EquipmentSlotEntry entry in set.Slots)
            if (entry.Slot == slot)
            {
                string? name = entry.ItemName?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
        return null;
    }
}
