using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="ItemCastSequencer"/> — the equip → use → re-equip wire
/// sequence that casts a spell from an item. Only unlimited-use items are
/// eligible; the displaced weapon is read from the inventory snapshot and
/// re-wielded afterward.
/// </summary>
public sealed class ItemCastSequencerTests
{
    private static readonly IReadOnlyList<ClassCastItem> Items = new[]
    {
        new ClassCastItem(1, "Emerald Tipped Crozier", 50, "bless", 0, 0), // unlimited, free
        new ClassCastItem(2, "Scroll of Light", 60, "light", 0, 3),        // limited
    };

    private static InventorySnapshot InvWith(params EquippedItem[] equipped)
        => InventorySnapshot.Empty with { EquippedItems = equipped };

    private static (ItemCastSequencer Seq, List<byte[]> Sent) NewSeq(InventorySnapshot inv)
    {
        ItemCastSequencer seq = new(() => Items, () => inv);
        seq.SetWireSender(_ => { });
        return (seq, seq.LastSentForTests);
    }

    private static List<string> Decode(IEnumerable<byte[]> sent)
        => sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    [Fact]
    public void Execute_UnlimitedItem_WieldsUsesAndRewieldsWeapon()
    {
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("long sword", "Weapon Hand")));

        Assert.True(seq.Execute("#emerald tipped crozier"));
        // Commands use the canonical game-data item name, not the lowercase token.
        Assert.Equal(new[]
        {
            "wield Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
            "wield long sword",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_NoWeaponEquipped_SkipsRewield()
    {
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InventorySnapshot.Empty);

        Assert.True(seq.Execute("#emerald tipped crozier"));
        Assert.Equal(new[]
        {
            "wield Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_AlreadyWieldingCastItem_SkipsRewield()
    {
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("Emerald Tipped Crozier", "Weapon Hand")));

        Assert.True(seq.Execute("#emerald tipped crozier"));
        Assert.Equal(new[]
        {
            "wield Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_LimitedChargeItem_DoesNothing()
    {
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("long sword", "Weapon Hand")));

        Assert.False(seq.Execute("#scroll of light"));
        Assert.Empty(sent);
    }

    [Fact]
    public void Execute_UnknownToken_DoesNothing()
    {
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InventorySnapshot.Empty);

        Assert.False(seq.Execute("#nonexistent"));
        Assert.Empty(sent);
    }

    [Fact]
    public void Execute_WireNotBound_ReturnsFalse()
    {
        ItemCastSequencer seq = new(() => Items, () => InventorySnapshot.Empty);
        // No SetWireSender — must not act.
        Assert.False(seq.Execute("#emerald tipped crozier"));
        Assert.Empty(seq.LastSentForTests);
    }
}
