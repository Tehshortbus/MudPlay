using System.Collections.Generic;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the <see cref="ItemCastToken"/> buff-slot scheme: a '#'-prefixed slot
/// value names an item to cast from (equip → use → re-equip) rather than a
/// direct spell cast-code.
/// </summary>
public sealed class ItemCastTokenTests
{
    [Fact]
    public void Format_PrefixesItemName()
        => Assert.Equal("#emerald tipped crozier", ItemCastToken.Format("emerald tipped crozier"));

    [Fact]
    public void Format_TrimsName()
        => Assert.Equal("#crozier", ItemCastToken.Format("  crozier  "));

    [Theory]
    [InlineData("#crozier", true)]
    [InlineData("  #crozier  ", true)]
    [InlineData("mihe", false)]
    [InlineData("#", false)]   // prefix alone names nothing
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsToken_DetectsPrefix(string? value, bool expected)
        => Assert.Equal(expected, ItemCastToken.IsToken(value));

    [Fact]
    public void ItemName_StripsPrefixAndTrims()
    {
        Assert.Equal("emerald tipped crozier", ItemCastToken.ItemName("#emerald tipped crozier"));
        Assert.Equal("crozier", ItemCastToken.ItemName("  #  crozier  "));
        Assert.Null(ItemCastToken.ItemName("mihe"));
        Assert.Null(ItemCastToken.ItemName(null));
    }

    [Fact]
    public void TryResolve_MatchesByNameCaseInsensitive()
    {
        IReadOnlyList<ClassCastItem> items = new[]
        {
            new ClassCastItem(1, "Emerald Tipped Crozier", 50, "bless", 0, 0),
            new ClassCastItem(2, "Wand of Stars", 100, "starlight", 0, 0),
        };

        Assert.True(ItemCastToken.TryResolve("#emerald tipped crozier", items, out ClassCastItem match));
        Assert.Equal(1, match.ItemNumber);
        Assert.Equal("bless", match.SpellName);
    }

    [Fact]
    public void TryResolve_FailsForUnknownOrNonToken()
    {
        IReadOnlyList<ClassCastItem> items = new[]
        {
            new ClassCastItem(1, "Emerald Tipped Crozier", 50, "bless", 0, 0),
        };

        Assert.False(ItemCastToken.TryResolve("#nonexistent", items, out _));
        Assert.False(ItemCastToken.TryResolve("mihe", items, out _));
        Assert.False(ItemCastToken.TryResolve(null, items, out _));
    }
}
