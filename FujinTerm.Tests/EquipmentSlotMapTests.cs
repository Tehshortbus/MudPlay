using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="EquipmentSlotMap.InventorySlotForWornCode"/> turns an item's
/// <c>Items.Worn</c> code into the InventoryManager location string, so the
/// incremental "You are now wearing X." path can slot a freshly-worn piece by
/// its real position. The string vocabulary must round-trip through
/// <see cref="EquipmentSlotMap.FromWornString"/> back to the same slot.
/// </summary>
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
}
