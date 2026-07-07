using FujinTerm.Game;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MonsterLookParserTests
{
    // ---- Band math (EstimateHp) --------------------------------------------

    // The full wound ladder for a 70-HP cave worm, validated live against the
    // game: the user confirmed a heavily-wounded 70-HP worm reads 35–48 HP
    // (actual HP was 38, inside the window).
    [Theory]
    [InlineData("unwounded",               70, 70)]
    [InlineData("slightly wounded",        60, 69)]
    [InlineData("moderately wounded",      49, 59)]
    [InlineData("heavily wounded",         35, 48)]
    [InlineData("severely wounded",        21, 34)]
    [InlineData("critically wounded",      14, 20)]
    [InlineData("very critically wounded",  1, 13)]
    public void EstimateHp_CaveWorm70_MapsEachBandToItsWindow(string wound, int low, int high)
    {
        MonsterHpEstimate est = MonsterLookParser.EstimateHp(70, wound)!.Value;
        Assert.False(est.Mortal);
        Assert.Equal(low, est.Low);
        Assert.Equal(high, est.High);
    }

    [Fact]
    public void EstimateHp_Mortally_IsZeroWindowFlaggedMortal()
    {
        MonsterHpEstimate est = MonsterLookParser.EstimateHp(70, "mortally wounded")!.Value;
        Assert.True(est.Mortal);
        Assert.Equal("≤0", est.Describe());
    }

    // Second independent max HP (25-HP acid slime) so the ceil math isn't only
    // exercised at one scale.
    [Fact]
    public void EstimateHp_AcidSlime25_HeavilyWounded_Is13To17()
    {
        MonsterHpEstimate est = MonsterLookParser.EstimateHp(25, "heavily wounded")!.Value;
        Assert.Equal(13, est.Low);
        Assert.Equal(17, est.High);
    }

    // Bands can collapse to a single HP value at tiny max HP; the window must
    // stay sane (High >= Low) rather than invert.
    [Fact]
    public void EstimateHp_TinyMaxHp_CollapsesToSaneWindow()
    {
        MonsterHpEstimate est = MonsterLookParser.EstimateHp(2, "very critically wounded")!.Value;
        Assert.True(est.High >= est.Low);
        Assert.True(est.Low >= 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EstimateHp_NonPositiveMaxHp_ReturnsNull(int maxHp)
        => Assert.Null(MonsterLookParser.EstimateHp(maxHp, "heavily wounded"));

    [Fact]
    public void EstimateHp_UnknownPhrase_ReturnsNull()
        => Assert.Null(MonsterLookParser.EstimateHp(70, "annoyed"));

    [Fact]
    public void Describe_FormatsFullMidbandAndMortal()
    {
        Assert.Equal("70",    new MonsterHpEstimate(70, 70, Mortal: false).Describe());
        Assert.Equal("35-48", new MonsterHpEstimate(35, 48, Mortal: false).Describe());
        Assert.Equal("≤0",    new MonsterHpEstimate(0, 0, Mortal: true).Describe());
    }

    // ---- Wound-line parsing (TryParseWoundLine) ----------------------------

    [Theory]
    [InlineData("It appears to be unwounded.",               "unwounded")]
    [InlineData("He appears to be slightly wounded.",        "slightly wounded")]
    [InlineData("She appears to be moderately wounded.",     "moderately wounded")]
    [InlineData("It appears to be heavily wounded.",         "heavily wounded")]
    [InlineData("It appears to be severely wounded.",        "severely wounded")]
    [InlineData("It appears to be critically wounded.",      "critically wounded")]
    [InlineData("It appears to be very critically wounded.", "very critically wounded")]
    [InlineData("It appears to be mortally wounded.",        "mortally wounded")]
    public void TryParseWoundLine_AcceptsMonsterConditionLines(string line, string expected)
    {
        Assert.True(MonsterLookParser.TryParseWoundLine(line, out string wound));
        Assert.Equal(expected, wound);
    }

    // A player look ends "He is unwounded." — no "appears to be" — so the
    // monster parser must never mistake it for a monster condition line.
    [Fact]
    public void TryParseWoundLine_RejectsPlayerLookLine()
        => Assert.False(MonsterLookParser.TryParseWoundLine("He is unwounded.", out _));

    [Fact]
    public void TryParseWoundLine_RejectsProseDescription()
        => Assert.False(MonsterLookParser.TryParseWoundLine(
            "This slimy creature oozes across the floor.", out _));

    // ---- End-to-end via the Feed hook --------------------------------------

    private static MonsterLookParser Build(
        Func<string, int?> resolve,
        Func<int, int?> maxHp,
        out List<MonsterLookObserved> observed)
    {
        var captured = new List<MonsterLookObserved>();
        var p = new MonsterLookParser(
            lines: new Terminal.LineExtractor(new Terminal.TerminalEmulator(80, 25)),
            resolveMonsterNumber: resolve,
            maxHpForNumber: maxHp);
        p.TargetObserved += o => captured.Add(o);
        observed = captured;
        return p;
    }

    // The wire order for `look ca` against a cave worm: prompt, the echoed
    // command ("look ca"), the monster name, prose, then the condition line.
    // The echo doesn't resolve to a monster, so the block scan skips it and the
    // name resolves.
    [Fact]
    public void Feed_CaveWormLook_EmitsHeavilyWoundedWindow()
    {
        MonsterLookParser p = Build(
            resolve: name => name == "cave worm" ? 8 : null,
            maxHp:   n => n == 8 ? 70 : null,
            out List<MonsterLookObserved> observed);

        p.Feed("[HP=100]:", isPrompt: true);
        p.Feed("look ca");
        p.Feed("cave worm");
        p.Feed("A large pale worm burrows through the cave wall.");
        p.Feed("It appears to be heavily wounded.");

        MonsterLookObserved obs = Assert.Single(observed);
        Assert.Equal("cave worm", obs.Name);
        Assert.Equal("35-48", obs.Estimate.Describe());
    }

    [Fact]
    public void Feed_PromptClearsBufferedBlock()
    {
        MonsterLookParser p = Build(
            resolve: name => name == "cave worm" ? 8 : null,
            maxHp:   n => 70,
            out List<MonsterLookObserved> observed);

        // Name buffered, then a fresh prompt wipes the block before any wound
        // line arrives — the later wound line has no name to resolve against.
        p.Feed("cave worm");
        p.Feed("[HP=100]:", isPrompt: true);
        p.Feed("It appears to be heavily wounded.");

        Assert.Empty(observed);
    }

    [Fact]
    public void Feed_PlayerLook_DoesNotEmit()
    {
        MonsterLookParser p = Build(
            resolve: _ => null,
            maxHp:   _ => null,
            out List<MonsterLookObserved> observed);

        p.Feed("[HP=100]:", isPrompt: true);
        p.Feed("[ Fujin WuzHere ]");
        p.Feed("Fujin is a wiry Dark-Elf Mystic.  He is unwounded.");

        Assert.Empty(observed);
    }

    [Fact]
    public void Feed_UnknownMonster_DoesNotEmit()
    {
        MonsterLookParser p = Build(
            resolve: _ => null,   // nothing resolves
            maxHp:   _ => 70,
            out List<MonsterLookObserved> observed);

        p.Feed("[HP=100]:", isPrompt: true);
        p.Feed("look zz");
        p.Feed("shimmering wisp");
        p.Feed("It appears to be heavily wounded.");

        Assert.Empty(observed);
    }

    // Resolves to a monster, but game data has no usable HP row for it — no
    // window can be built, so nothing is emitted (better silent than wrong).
    [Fact]
    public void Feed_MonsterWithoutHpData_DoesNotEmit()
    {
        MonsterLookParser p = Build(
            resolve: name => name == "cave worm" ? 8 : null,
            maxHp:   _ => null,
            out List<MonsterLookObserved> observed);

        p.Feed("[HP=100]:", isPrompt: true);
        p.Feed("cave worm");
        p.Feed("It appears to be heavily wounded.");

        Assert.Empty(observed);
    }
}
