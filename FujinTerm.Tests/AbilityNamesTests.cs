using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AbilityNamesTests
{
    [Theory]
    [InlineData(1,    "Damage")]            // MME canonical (was "DamageNoMR")
    [InlineData(2,    "AC")]
    [InlineData(17,   "Damage(-MR)")]       // MME spells this differently than our prior table
    [InlineData(18,   "Heal")]
    [InlineData(34,   "Dodge")]
    [InlineData(42,   "LearnSp")]           // MME abbreviates ("LearnSp" not "LearnSpell")
    [InlineData(43,   "CastsSp")]
    [InlineData(46,   "Strength")]
    [InlineData(78,   "Animal")]
    [InlineData(88,   "MaxHP")]
    [InlineData(97,   "GoodOnly")]
    [InlineData(116,  "BSAccu")]
    [InlineData(170,  "Sleep")]
    [InlineData(1001, "GrantThievery")]
    public void GetName_KnownCodes_ReturnExpected(int code, string expected)
    {
        Assert.Equal(expected, AbilityNames.GetName(code));
    }

    [Theory]
    [InlineData(191)]
    [InlineData(195)]
    [InlineData(219)]
    [InlineData(250)]
    [InlineData(400)]
    public void GetName_UnnamedQuestFlagRange_ReturnsQuestFlagLabel(int code)
    {
        Assert.Equal($"QuestFlag{code}", AbilityNames.GetName(code));
    }

    [Fact]
    public void GetName_UnmappedPositiveCode_ReturnsAbilityFallback()
    {
        // Any unmapped positive code falls back to "Ability {N}" — the
        // call sites should never need to render a raw "Abil{N}" suffix.
        Assert.Equal("Ability 99999", AbilityNames.GetName(99_999));
    }

    [Fact]
    public void GetName_NonPositiveCode_ReturnsNull()
    {
        Assert.Null(AbilityNames.GetName(0));
        Assert.Null(AbilityNames.GetName(-1));
    }

    [Fact]
    public void FormatId_KnownCode_ReturnsName()
    {
        Assert.Equal("AC", AbilityNames.FormatId(2));
    }

    [Fact]
    public void FormatId_NonPositiveCode_ReturnsUnknownLabel()
    {
        Assert.Equal("Unknown(0)", AbilityNames.FormatId(0));
    }
}
