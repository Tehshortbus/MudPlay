using FujinTerm.Game.Cash;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="CurrencyNaming"/> is the runtime source-of-truth for the per-BBS
/// runic word. It normalises the configured name (trim + lower, blank → stock
/// "runic"), recognises both the renamed word AND the stock word as runic
/// (<see cref="CurrencyNaming.IsRunic"/>), and canonicalises any word back to
/// "runic" for the value ladder (<see cref="CurrencyNaming.Canonicalize"/>).
/// Refresh re-reads the configured name so a BBS pin takes effect live.
/// </summary>
public sealed class CurrencyNamingTests
{
    [Fact]
    public void Unconfigured_FallsBackToStockRunic()
    {
        CurrencyNaming naming = new();
        Assert.Equal("runic", naming.RunicName);
    }

    [Theory]
    [InlineData(null, "runic")]
    [InlineData("", "runic")]
    [InlineData("   ", "runic")]
    [InlineData("Quatloos", "quatloos")]   // trimmed + lowered
    [InlineData("  Zorkmid ", "zorkmid")]
    public void Normalizes_ConfiguredName(string? configured, string expected)
    {
        CurrencyNaming naming = new(() => configured);
        Assert.Equal(expected, naming.RunicName);
    }

    [Fact]
    public void IsRunic_MatchesRenamedWordAndStockWord()
    {
        CurrencyNaming naming = new(() => "quatloos");
        Assert.True(naming.IsRunic("quatloos"));
        Assert.True(naming.IsRunic("QUATLOOS"));   // case-insensitive
        Assert.True(naming.IsRunic("runic"));      // stock word always recognised
        Assert.False(naming.IsRunic("gold"));
    }

    [Fact]
    public void Canonicalize_MapsRenamedAndStockToRunic_LeavesOthers()
    {
        CurrencyNaming naming = new(() => "quatloos");
        Assert.Equal("runic", naming.Canonicalize("quatloos"));
        Assert.Equal("runic", naming.Canonicalize("runic"));
        Assert.Equal("gold", naming.Canonicalize("gold"));
        Assert.Equal("platinum", naming.Canonicalize("PLATINUM")); // trimmed + lowered
    }

    [Fact]
    public void Refresh_RereadsConfiguredName()
    {
        string? configured = null;
        CurrencyNaming naming = new(() => configured);
        Assert.Equal("runic", naming.RunicName);

        configured = "quatloos";
        naming.Refresh();
        Assert.Equal("quatloos", naming.RunicName);
        Assert.True(naming.IsRunic("quatloos"));
    }
}
