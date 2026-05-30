using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AbilityNamesTests
{
    [Theory]
    [InlineData(1,    "DamageNoMR")]
    [InlineData(2,    "AC")]
    [InlineData(18,   "Heal")]
    [InlineData(34,   "Dodge")]
    [InlineData(46,   "Strength")]
    [InlineData(170,  "Sleep")]
    [InlineData(1001, "GrantThievery")]
    public void GetName_KnownCodes_ReturnExpected(int code, string expected)
    {
        Assert.Equal(expected, AbilityNames.GetName(code));
    }

    [Fact]
    public void GetName_UnknownCode_ReturnsNull()
    {
        Assert.Null(AbilityNames.GetName(99_999));
    }

    [Fact]
    public void FormatId_KnownCode_ReturnsName()
    {
        Assert.Equal("AC", AbilityNames.FormatId(2));
    }

    [Fact]
    public void FormatId_UnknownCode_ReturnsUnknownLabel()
    {
        Assert.Equal("Unknown(99999)", AbilityNames.FormatId(99_999));
    }
}
