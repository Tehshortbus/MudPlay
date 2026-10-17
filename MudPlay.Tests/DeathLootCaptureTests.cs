using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// DeathLootCapture splits an inventory snapshot into
// the deathpile's worn (re-equippable) and carried-but-unworn halves.
public sealed class DeathLootCaptureTests
{
    private static InventorySnapshot Snap(EquippedItem[] worn, string[] carried) =>
        new(CurrencyHoldings.Empty, EncumbranceReading.Empty, worn, carried, DateTimeOffset.UtcNow);

    [Fact]
    public void SplitsWornAndCarried()
    {
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("rusty dagger", "Weapon Hand"), new EquippedItem("padded vest", "Torso") },
            new[] { "torch", "ration" });

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Equal(2, equipped.Count);
        Assert.Contains(equipped, i => i is { Name: "rusty dagger", Slot: "Weapon Hand" } && i.IsHeld);
        Assert.Contains(equipped, i => i is { Name: "padded vest", Slot: "Torso" } && !i.IsHeld);

        Assert.Equal(2, lost.Count);
        Assert.Contains(lost, i => i.Name == "torch" && i.Slot is null);
        Assert.Contains(lost, i => i.Name == "ration");
    }

    [Fact]
    public void WornPiece_AndOneSpareInThePack_RecordsBoth()
    {
        // The pack entry is a second copy: a piece leaves the pack as it is worn, so
        // what is still listed there under the same name is a spare.
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("longsword", "Weapon Hand") },
            new[] { "longsword", "torch" });

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Single(equipped, i => i is { Name: "longsword", Slot: "Weapon Hand" });
        Assert.Single(lost, i => i.Name == "longsword" && i.Slot is null);
        Assert.Single(lost, i => i.Name == "torch");
    }

    [Fact]
    public void WornPiece_AndTwoSparesInThePack_KeepsThePacksCount()
    {
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("longsword", "Weapon Hand") },
            new[] { "2 longsword" });

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Single(equipped, i => i.Name == "longsword");
        Assert.Equal(new[] { "2 longsword" }, lost.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void WornPiece_NoSpare_IsRecordedOnce()
    {
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("longsword", "Weapon Hand") },
            new[] { "torch" });

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Single(equipped, i => i.Name == "longsword");
        Assert.DoesNotContain(lost, i => i.Name == "longsword");
    }

    [Fact]
    public void TheLitLight_AndTheKeys_AreLostWithThePack()
    {
        // The inventory read lists both apart from the pack; both drop at a death.
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("longsword", "Weapon Hand") },
            new[] { "ration" })
            with
            {
                ReadiedLight = new ReadiedLight("torch", 12),
                Keys = new[] { "3 black star key", "iron key" },
            };

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Single(equipped);
        Assert.Equal(new[] { "ration", "3 black star key", "iron key", "torch" }, lost.Select(i => i.Name).ToArray());
        Assert.All(lost, i => Assert.Null(i.Slot));   // nothing here is put back on
        Assert.Equal(new[] { "3 black star key", "iron key" },
            lost.Where(i => i.OnKeyRing).Select(i => i.Name).ToArray());
    }

    [Fact]
    public void NoLightLit_AndNoKeys_AddsNothing()
    {
        InventorySnapshot snap = Snap(Array.Empty<EquippedItem>(), new[] { "torch" });

        (_, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Equal(new[] { "torch" }, lost.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void EmptySnapshot_ReturnsEmptyButNonNullLists()
    {
        (List<DeathItem> equipped, List<DeathItem> lost) =
            DeathLootCapture.FromSnapshot(InventorySnapshot.Empty);

        Assert.NotNull(equipped);
        Assert.Empty(equipped);
        Assert.NotNull(lost);
        Assert.Empty(lost);
    }
}
