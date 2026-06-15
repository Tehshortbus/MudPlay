using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.5 — <see cref="DeathLootCapture"/> splits an inventory snapshot into
/// the deathpile's worn (re-equippable) and carried-but-unworn halves.
/// </summary>
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
    public void DedupesWornItemLingeringInCarriedList()
    {
        // Between full 'i' dumps the worn set is patched live while the carried
        // set isn't, so a just-equipped item can appear in both. It must show
        // only under equipped — never double-counted as lost.
        InventorySnapshot snap = Snap(
            new[] { new EquippedItem("longsword", "Weapon Hand") },
            new[] { "longsword", "torch" });

        (List<DeathItem> equipped, List<DeathItem> lost) = DeathLootCapture.FromSnapshot(snap);

        Assert.Single(equipped, i => i.Name == "longsword");
        Assert.DoesNotContain(lost, i => i.Name == "longsword");
        Assert.Single(lost, i => i.Name == "torch");
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
