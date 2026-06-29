using System.Collections.Generic;
using FujinTerm.ViewModels.GameData.Tables;
using FujinTerm.Views.GameData.Tables;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The Game Data grid sort comparer. Beyond pure numbers it now sorts
/// numeric-with-suffix ("2hp@90s") and numeric-list ("10/25/5") cells by
/// their leading value, so the HP Regen + Accuracy columns don't sort
/// lexically (where "10…" would come before "2…").
/// </summary>
public sealed class NumericAwareCellComparerTests
{
    private static GameDataRow Row(string? value) =>
        GameDataRow.FromDictionary(
            new Dictionary<string, string?> { ["Col"] = value },
            new[] { "Col" });

    [Theory]
    [InlineData("2hp@90s", "10hp@90s", -1)]   // 2 < 10 (not lexical)
    [InlineData("10hp@90s", "2hp@90s", 1)]
    [InlineData("10/25/5", "2/2/2", 1)]       // leading 10 > 2
    [InlineData("5hp@90s", "5hp@90s", 0)]     // identical cells compare equal
    public void SortsByLeadingNumber(string a, string b, int expectedSign)
    {
        var comparer = new NumericAwareCellComparer(0);
        int result = comparer.Compare(Row(a), Row(b));
        Assert.Equal(expectedSign, System.Math.Sign(result));
    }

    [Fact]
    public void PurelyTextualCells_StillStringCompare()
    {
        var comparer = new NumericAwareCellComparer(0);
        Assert.True(comparer.Compare(Row("Chaotic Evil"), Row("Lawful Good")) < 0);
    }

    [Fact]
    public void EmptyCells_ClusterFirst()
    {
        var comparer = new NumericAwareCellComparer(0);
        Assert.True(comparer.Compare(Row(""), Row("2hp@90s")) < 0);
    }
}
