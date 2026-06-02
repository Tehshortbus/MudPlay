using System.Globalization;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the two PartyWindow-row converters: <see cref="RankChipConverter"/>
/// (PartyRank → F/M/B chip label) and <see cref="GreaterThanZeroConverter"/>
/// (numeric baseline → visibility of the MA bar row).
/// </summary>
public sealed class PartyWindowConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(PartyRank.Front, "F")]
    [InlineData(PartyRank.Mid,   "M")]
    [InlineData(PartyRank.Back,  "B")]
    public void RankChipConverter_MapsEachRank(PartyRank rank, string expected)
    {
        object result = RankChipConverter.Instance.Convert(rank, typeof(string), null, Invariant);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RankChipConverter_FallsBackToMid_ForNonRankInput()
    {
        // Defensive — XAML binding might briefly hand us a placeholder
        // before the VM populates.
        object result = RankChipConverter.Instance.Convert("garbage", typeof(string), null, Invariant);
        Assert.Equal("M", result);
    }

    [Theory]
    [InlineData(0,   false)]
    [InlineData(1,   true)]
    [InlineData(100, true)]
    [InlineData(-1,  false)]
    public void GreaterThanZero_Int(int v, bool expected)
    {
        object result = GreaterThanZeroConverter.Instance.Convert(v, typeof(bool), null, Invariant);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GreaterThanZero_NonNumeric_IsFalse()
    {
        object result = GreaterThanZeroConverter.Instance.Convert("string", typeof(bool), null, Invariant);
        Assert.False((bool)result);
    }
}
