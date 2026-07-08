using System.Text.Json;
using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AbilityNamesTests
{
    [Theory]
    [InlineData(1,    "Damage")]
    [InlineData(2,    "AC")]
    [InlineData(17,   "Damage(-MR)")]
    [InlineData(18,   "Heal")]
    [InlineData(22,   "Accuracy")]
    [InlineData(34,   "Dodge")]
    [InlineData(42,   "LearnSp")]
    [InlineData(43,   "CastsSp")]
    [InlineData(46,   "Strength")]
    [InlineData(78,   "Animal")]
    [InlineData(88,   "MaxHP")]
    [InlineData(97,   "GoodOnly")]
    [InlineData(105,  "Accuracy2")]
    [InlineData(106,  "Accuracy3")]
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

    // ----- HasTrapAbility -------------------------------------------------

    private static JsonElement Row(params int[] abilCodes)
    {
        // Mirror the imported Classes / Races row shape: Abil-0..N int
        // slots (with paired AbilVal-N the helper ignores).
        var sb = new System.Text.StringBuilder("{");
        for (int i = 0; i < abilCodes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"Abil-{i}\":{abilCodes[i]},\"AbilVal-{i}\":0");
        }
        sb.Append('}');
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    [Theory]
    [InlineData(40)]    // FindTraps — stock Traps-skill grant
    [InlineData(41)]    // DisarmTraps — custom-set grant
    [InlineData(1002)]  // GrantTraps — custom-set grant
    public void HasTrapAbility_RowWithTrapGrant_ReturnsTrue(int code)
    {
        // Embed the grant among unrelated abilities to prove the scan
        // hits any slot, not just the first.
        Assert.True(AbilityNames.HasTrapAbility(Row(103, 37, code, 31)));
    }

    [Fact]
    public void HasTrapAbility_RowWithoutTrapGrant_ReturnsFalse()
    {
        // Warrior-shaped row (Bash only) — no trap grant.
        Assert.False(AbilityNames.HasTrapAbility(Row(31)));
        // Picklocks (37) / Thievery (39) are thief-ish but not the Traps
        // grant — must not count.
        Assert.False(AbilityNames.HasTrapAbility(Row(103, 37, 39, 31)));
    }

    [Fact]
    public void HasTrapAbility_EmptyRow_ReturnsFalse()
    {
        Assert.False(AbilityNames.HasTrapAbility(JsonDocument.Parse("{}").RootElement));
    }

    // ----- HasShadowRest --------------------------------------------------

    [Fact]
    public void HasShadowRest_RowWithAbility1103_ReturnsTrue()
    {
        // Ability code 1103 (ShadowRest, Paradigm) embedded among unrelated
        // slots — the scan must hit any slot, not just the first.
        Assert.True(AbilityNames.HasShadowRest(Row(103, 37, 1103, 31)));
    }

    [Fact]
    public void HasShadowRest_RowWithoutAbility1103_ReturnsFalse()
    {
        Assert.False(AbilityNames.HasShadowRest(Row(103, 37, 39, 31)));
    }

    [Fact]
    public void HasShadowRest_EmptyRow_ReturnsFalse()
    {
        Assert.False(AbilityNames.HasShadowRest(JsonDocument.Parse("{}").RootElement));
    }
}
