using MudPlay.Game.GameData;
using Xunit;

namespace MudPlay.Tests;

public sealed class SpellAilmentIndexTests
{
    [Theory]
    [InlineData(19, "poison")]
    [InlineData(71, "confuse")]
    [InlineData(107, "blind")]
    [InlineData(74, "hold")]
    public void AilmentTokens_MapsEachAilmentCode(int code, string expected)
    {
        var tokens = SpellAilmentIndex.AilmentTokens(new[] { code });
        Assert.Contains(expected, tokens);
        Assert.Single(tokens);
    }

    [Fact]
    public void AilmentTokens_MultipleCodes_YieldsAll()
    {
        // A spell that blinds AND poisons (e.g. codes 107 + 19) reports both.
        var tokens = SpellAilmentIndex.AilmentTokens(new[] { 107, 19, 2 /* AC, ignored */ });
        Assert.Contains("blind", tokens);
        Assert.Contains("poison", tokens);
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void AilmentTokens_NoAilmentCode_IsEmpty()
    {
        // Damage (17) + AC (2) — no ailment.
        Assert.Empty(SpellAilmentIndex.AilmentTokens(new[] { 17, 2 }));
    }
}
