using System.Text.RegularExpressions;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class TriggerEngineTests
{
    // ----- LiteralToRegex -------------------------------------------------

    [Fact]
    public void LiteralToRegex_EscapesRegexMetacharacters()
    {
        string regex = TriggerEngine.LiteralToRegex("HP=10/20 (Resting).");
        Assert.Matches(regex, "HP=10/20 (Resting).");
        // The literal '.' must NOT match an arbitrary character.
        Assert.DoesNotMatch(regex, "HP=10/20 (Resting)X");
    }

    [Fact]
    public void LiteralToRegex_TranslatesStarToGreedyNonCapture()
    {
        string regex = TriggerEngine.LiteralToRegex("* enters the room.");
        Match m = Regex.Match("Joe enters the room.", regex);
        Assert.True(m.Success);
        // Star is non-capturing — only Group[0] (the full-match) should exist.
        Assert.Single(m.Groups);
    }

    [Fact]
    public void LiteralToRegex_TranslatesBracedNameToNamedCapture()
    {
        string regex = TriggerEngine.LiteralToRegex("{usr} enters the room.");
        Match m = Regex.Match("Joe enters the room.", regex);
        Assert.True(m.Success);
        Assert.Equal("Joe", m.Groups["usr"].Value);
    }

    [Fact]
    public void LiteralToRegex_EndOfPatternCaptureConsumesRestOfLine()
    {
        // Regression for the "Also here: {test}" case where non-greedy `.+?`
        // matched only the first character ("h") instead of "healer.".
        string regex = TriggerEngine.LiteralToRegex("Also here: {test}");
        Match m = Regex.Match("Also here: healer.", regex);
        Assert.True(m.Success);
        Assert.Equal("healer.", m.Groups["test"].Value);
    }

    [Fact]
    public void LiteralToRegex_LeavesInvalidBracePairsAsLiterals()
    {
        // {123} starts with a digit — not a valid identifier; treat as literal.
        string regex = TriggerEngine.LiteralToRegex("hp {123}");
        Match m = Regex.Match("hp {123}", regex);
        Assert.True(m.Success);
        Assert.DoesNotContain("123", m.Groups.Keys);
    }

    [Fact]
    public void LiteralToRegex_HandlesMultipleCaptures()
    {
        string regex = TriggerEngine.LiteralToRegex("{who} hit {tgt} for {dmg} damage.");
        Match m = Regex.Match("Joe hit an orc for 12 damage.", regex);
        Assert.True(m.Success);
        Assert.Equal("Joe",    m.Groups["who"].Value);
        Assert.Equal("an orc", m.Groups["tgt"].Value);
        Assert.Equal("12",     m.Groups["dmg"].Value);
    }

    // ----- TryInterpolate -------------------------------------------------

    [Fact]
    public void TryInterpolate_SubstitutesKnownVariables()
    {
        TriggerEngine engine = new();
        engine.Variables["usr"] = "Joe";
        bool ok = engine.TryInterpolate("/{usr} hi there!", "test", out string result);
        Assert.True(ok);
        Assert.Equal("/Joe hi there!", result);
    }

    [Fact]
    public void TryInterpolate_AbortsOnUndefinedVariable()
    {
        TriggerEngine engine = new();
        bool ok = engine.TryInterpolate("hello {foo}", "test", out string result);
        Assert.False(ok);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TryInterpolate_LeavesUnknownBracesAsLiteralText()
    {
        // {123} isn't a valid identifier — left as literal text, not treated as undefined.
        TriggerEngine engine = new();
        bool ok = engine.TryInterpolate("loose {123} braces", "test", out string result);
        Assert.True(ok);
        Assert.Equal("loose {123} braces", result);
    }

    [Fact]
    public void TryInterpolate_EmptyTemplateReturnsEmpty()
    {
        TriggerEngine engine = new();
        bool ok = engine.TryInterpolate(string.Empty, "test", out string result);
        Assert.True(ok);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TryInterpolate_ChainsMultipleVariables()
    {
        TriggerEngine engine = new();
        engine.Variables["who"] = "Joe";
        engine.Variables["dmg"] = "12";
        bool ok = engine.TryInterpolate("{who} hit for {dmg}", "test", out string result);
        Assert.True(ok);
        Assert.Equal("Joe hit for 12", result);
    }

    // ----- Variable capture flow -----------------------------------------

    [Fact]
    public void NamedCaptures_PopulateSharedVariableCache()
    {
        // Compile a literal pattern through the engine's translator,
        // then run the match by hand — the cache is populated only
        // when the engine's TryFire path runs, but we can verify the
        // regex produces the right named groups here.
        string regex = TriggerEngine.LiteralToRegex("{usr} enters the room.");
        Match m = Regex.Match("Joe enters the room.", regex);
        Assert.True(m.Success);
        Assert.Equal("Joe", m.Groups["usr"].Value);
    }
}
