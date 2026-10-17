using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// EquipmentSlotMap.InventorySlotForWornCode turns an item's
// Items.Worn code into the InventoryManager location string, so the
// incremental "You are now wearing X." path can slot a freshly-worn piece by
// its real position. The string vocabulary must round-trip through
// EquipmentSlotMap.FromWornString back to the same slot.
public sealed class EquipmentSlotMapTests
{
    [Theory]
    [InlineData(11, "Torso", EquipmentSlot.Torso)]
    [InlineData(9, "Legs", EquipmentSlot.Legs)]
    [InlineData(2, "Head", EquipmentSlot.Head)]
    [InlineData(5, "Feet", EquipmentSlot.Feet)]
    [InlineData(16, "Worn", EquipmentSlot.Worn)]
    public void InventorySlotForWornCode_ResolvesAndRoundTrips(
        int worn, string expectedSlot, EquipmentSlot expectedEnum)
    {
        string? slot = EquipmentSlotMap.InventorySlotForWornCode(worn);

        Assert.Equal(expectedSlot, slot);
        // The produced string feeds "Snapshot Current" — it must map back to the
        // same slot it came from.
        Assert.Equal(expectedEnum, EquipmentSlotMap.FromWornString(slot));
    }

    [Theory]
    [InlineData(4, "Finger", EquipmentSlot.Finger1)]
    [InlineData(13, "Finger", EquipmentSlot.Finger1)]
    [InlineData(14, "Wrist", EquipmentSlot.Wrist1)]
    [InlineData(17, "Wrist", EquipmentSlot.Wrist1)]
    public void InventorySlotForWornCode_PairedCodes_ResolveToSharedString(
        int worn, string expectedSlot, EquipmentSlot expectedEnum)
    {
        string? slot = EquipmentSlotMap.InventorySlotForWornCode(worn);

        Assert.Equal(expectedSlot, slot);
        Assert.Equal(expectedEnum, EquipmentSlotMap.FromWornString(slot));
    }

    [Theory]
    [InlineData(0)]    // not wearable
    [InlineData(99)]   // no such code
    public void InventorySlotForWornCode_UnknownCode_ReturnsNull(int worn)
    {
        Assert.Null(EquipmentSlotMap.InventorySlotForWornCode(worn));
    }

    [Fact]
    public void DisplayOrder_MatchesInGameLook_WeaponBlockLast_AlternatesAfterPrimaries()
    {
        // The Equipment Manager rows and the Item Finder trial slots both render in
        // DisplayOrder, so this pins the in-game "look" ordering the user asked for:
        // worn slots top-to-bottom, then Off-Hand / Weapon at the bottom, with the
        // alternates mirroring that pairing right after. SlotForItem /
        // InventorySlotForWornCode resolve a paired code to the first matching slot
        // here, so each primary also comes before its pair-mate.
        EquipmentSlot[] expected =
        {
            EquipmentSlot.Head, EquipmentSlot.Ears, EquipmentSlot.Eyes, EquipmentSlot.Face,
            EquipmentSlot.Neck, EquipmentSlot.Back, EquipmentSlot.Torso, EquipmentSlot.Arms,
            EquipmentSlot.Wrist1, EquipmentSlot.Wrist2, EquipmentSlot.Hands,
            EquipmentSlot.Finger1, EquipmentSlot.Finger2, EquipmentSlot.Waist,
            EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Worn,
            EquipmentSlot.OffHand, EquipmentSlot.Weapon,
            EquipmentSlot.AlternateOffHand, EquipmentSlot.AlternateWeapon,
        };

        Assert.Equal(expected, EquipmentSlotMap.DisplayOrder);
    }

}
