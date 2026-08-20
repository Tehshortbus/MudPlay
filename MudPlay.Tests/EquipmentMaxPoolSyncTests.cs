using System.Collections.Generic;
using MudPlay.Game.Health;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="EquipmentMaxPoolSync"/> — diffs the worn set's aggregate
/// flat HP/mana bonus across observations and applies only the delta, so an
/// equip/remove mid-session composes with whatever base the prompt ratchet or
/// stat screen already established instead of overriding it.
/// </summary>
public sealed class EquipmentMaxPoolSyncTests
{
    private static IReadOnlyList<EquippedItem> Worn(params EquippedItem[] items) => items;

    [Fact]
    public void FirstObservation_SeedsBaseline_AppliesNoDelta()
    {
        var applied = new List<(int Hp, int Ma)>();
        EquipmentMaxPoolSync sync = new(
            _ => (0, 50),   // e.g. the severed head already worn at login.
            (hp, ma) => applied.Add((hp, ma)));

        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));

        Assert.Empty(applied);
    }

    [Fact]
    public void EquipBonusItem_AfterBaseline_AppliesPositiveDelta()
    {
        var applied = new List<(int Hp, int Ma)>();
        int ma = 0;
        EquipmentMaxPoolSync sync = new(_ => (0, ma), (hp, d) => applied.Add((hp, d)));

        sync.OnEquippedItemsChanged(Worn());           // baseline: nothing worn.
        ma = 50;
        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));

        Assert.Equal(new[] { (0, 50) }, applied);
    }

    [Fact]
    public void RemoveBonusItem_AfterBaseline_AppliesNegativeDelta()
    {
        var applied = new List<(int Hp, int Ma)>();
        int ma = 50;
        EquipmentMaxPoolSync sync = new(_ => (0, ma), (hp, d) => applied.Add((hp, d)));

        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));
        ma = 0;
        sync.OnEquippedItemsChanged(Worn());

        Assert.Equal(new[] { (0, -50) }, applied);
    }

    [Fact]
    public void UnchangedTotal_AppliesNoDelta()
    {
        var applied = new List<(int Hp, int Ma)>();
        EquipmentMaxPoolSync sync = new(_ => (0, 50), (hp, d) => applied.Add((hp, d)));

        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));
        // A room-item pickup or unrelated equip fires Changed again with the
        // same worn-set total — must not re-fire the delta.
        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));

        Assert.Empty(applied);
    }

    [Fact]
    public void Reset_ReseedsWithoutApplyingTheFullTotalAsADelta()
    {
        var applied = new List<(int Hp, int Ma)>();
        EquipmentMaxPoolSync sync = new(
            equipped => (0, equipped.Count > 0 ? 50 : 0),
            (hp, d) => applied.Add((hp, d)));

        sync.OnEquippedItemsChanged(Worn());            // baseline: 0.
        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));
        Assert.Single(applied);                          // the +50 equip delta.

        // Character swap / active-set change — the new character's stat
        // screen or ratchet already accounts for whatever it's wearing.
        sync.Reset();
        sync.OnEquippedItemsChanged(Worn(new EquippedItem("severed head of Goru-Nezar", "Worn")));

        Assert.Single(applied);                          // no second delta fired.
    }
}
