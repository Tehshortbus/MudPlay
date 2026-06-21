using System.Linq;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="EquipmentWeaponSync"/> — the read-time overlay that derives
/// CombatSettings' six weapon fields from the Equipment Manager gear sets:
/// normal + alternate from the Default set, backstab from the Backstab set when
/// enabled, otherwise the Default set.
/// </summary>
public sealed class EquipmentWeaponSyncTests
{
    private static EquipmentSet SetOf(
        EquipTriggerType trigger, bool enabled, params (EquipmentSlot Slot, string Item)[] slots)
        => new()
        {
            Trigger = trigger,
            Enabled = enabled,
            Slots = slots.Select(s => new EquipmentSlotEntry(s.Slot, s.Item)).ToList(),
        };

    [Fact]
    public void ApplyWeapons_NormalAndAlternateComeFromDefaultSet()
    {
        EquipmentSettings equip = new()
        {
            Sets =
            {
                SetOf(EquipTriggerType.Default, enabled: true,
                    (EquipmentSlot.Weapon, "long sword"),
                    (EquipmentSlot.OffHand, "buckler"),
                    (EquipmentSlot.AlternateWeapon, "war hammer"),
                    (EquipmentSlot.AlternateOffHand, "tower shield")),
            },
        };
        CombatSettings combat = new();

        EquipmentWeaponSync.ApplyWeapons(combat, equip);

        Assert.Equal("long sword", combat.NormalWeapon);
        Assert.Equal("buckler", combat.NormalOffHand);
        Assert.Equal("war hammer", combat.AlternateWeapon);
        Assert.Equal("tower shield", combat.AlternateOffHand);
    }

    [Fact]
    public void ApplyWeapons_BackstabFromBackstabSet_WhenEnabled()
    {
        EquipmentSettings equip = new()
        {
            Sets =
            {
                SetOf(EquipTriggerType.Default, enabled: true,
                    (EquipmentSlot.Weapon, "long sword"), (EquipmentSlot.OffHand, "buckler")),
                SetOf(EquipTriggerType.Backstab, enabled: true,
                    (EquipmentSlot.Weapon, "dagger"), (EquipmentSlot.OffHand, "main gauche")),
            },
        };
        CombatSettings combat = new();

        EquipmentWeaponSync.ApplyWeapons(combat, equip);

        Assert.Equal("dagger", combat.BackstabWeapon);
        Assert.Equal("main gauche", combat.BackstabOffHand);
        // Normal still comes from the Default set.
        Assert.Equal("long sword", combat.NormalWeapon);
    }

    [Fact]
    public void ApplyWeapons_BackstabFallsBackToDefault_WhenBackstabDisabled()
    {
        EquipmentSettings equip = new()
        {
            Sets =
            {
                SetOf(EquipTriggerType.Default, enabled: true,
                    (EquipmentSlot.Weapon, "long sword"), (EquipmentSlot.OffHand, "buckler")),
                SetOf(EquipTriggerType.Backstab, enabled: false,
                    (EquipmentSlot.Weapon, "dagger"), (EquipmentSlot.OffHand, "main gauche")),
            },
        };
        CombatSettings combat = new();

        EquipmentWeaponSync.ApplyWeapons(combat, equip);

        // Backstab set disabled → use the Default set's weapon for BS.
        Assert.Equal("long sword", combat.BackstabWeapon);
        Assert.Equal("buckler", combat.BackstabOffHand);
    }

    [Fact]
    public void ApplyWeapons_BackstabFallsBackToDefault_WhenBackstabSetAbsent()
    {
        EquipmentSettings equip = new()
        {
            Sets =
            {
                SetOf(EquipTriggerType.Default, enabled: true,
                    (EquipmentSlot.Weapon, "long sword")),
            },
        };
        CombatSettings combat = new();

        EquipmentWeaponSync.ApplyWeapons(combat, equip);

        Assert.Equal("long sword", combat.BackstabWeapon);
        Assert.Null(combat.BackstabOffHand); // Default has no off-hand
    }

    [Fact]
    public void ApplyWeapons_OverwritesPreviousValues_ClearingUnsetSlots()
    {
        // An empty Default set must clear stale combat weapon refs so removing a
        // gear item clears the combat reference too.
        EquipmentSettings equip = new() { Sets = { SetOf(EquipTriggerType.Default, enabled: true) } };
        CombatSettings combat = new()
        {
            NormalWeapon = "stale", NormalOffHand = "stale",
            AlternateWeapon = "stale", AlternateOffHand = "stale",
            BackstabWeapon = "stale", BackstabOffHand = "stale",
        };

        EquipmentWeaponSync.ApplyWeapons(combat, equip);

        Assert.Null(combat.NormalWeapon);
        Assert.Null(combat.NormalOffHand);
        Assert.Null(combat.AlternateWeapon);
        Assert.Null(combat.AlternateOffHand);
        Assert.Null(combat.BackstabWeapon);
        Assert.Null(combat.BackstabOffHand);
    }

    [Fact]
    public void ApplyWeapons_EmptyEquipment_AllNull()
    {
        CombatSettings combat = new();

        EquipmentWeaponSync.ApplyWeapons(combat, new EquipmentSettings());

        Assert.Null(combat.NormalWeapon);
        Assert.Null(combat.AlternateWeapon);
        Assert.Null(combat.BackstabWeapon);
    }
}
