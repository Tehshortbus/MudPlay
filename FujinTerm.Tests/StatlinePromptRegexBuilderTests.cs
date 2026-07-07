using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class StatlinePromptRegexBuilderTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static List<PromptObservation> RunThroughScanner(Regex regex, string wire)
    {
        WirePromptScanner s = new();
        s.InstallRegex(regex);
        List<PromptObservation> seen = new();
        s.PromptObserved += seen.Add;
        s.Append(B(wire));
        return seen;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData(" full ")]
    public void DefaultCommands_ReturnTheSharedDefaultRegex(string? command)
    {
        // The whole point of keeping default on a fixed regex: byte-identical
        // behaviour to before the builder existed.
        Assert.Same(StatlinePromptRegexBuilder.Default, StatlinePromptRegexBuilder.Build(command));
    }

    [Theory]
    [InlineData("[HP=120]:", 120, ManaType.None, 0)]
    [InlineData("[HP=27/MA=31]:", 27, ManaType.Mana, 31)]
    [InlineData("[HP=44/KAI=2]:", 44, ManaType.Kai, 2)]
    public void Default_MatchesAllThreeClassShapes(string wire, int hp, ManaType type, int mana)
    {
        var seen = RunThroughScanner(StatlinePromptRegexBuilder.Default, wire);

        Assert.Single(seen);
        Assert.Equal(hp, seen[0].Hp);
        Assert.Equal(type, seen[0].ManaType);
        Assert.Equal(mana, seen[0].Mana);
    }

    [Fact]
    public void Default_MatchesNegativeHp_WhileMortallyWounded()
    {
        // Mortally wounded → HP goes negative and the game prints it. The
        // default pattern must still match so the drop is recognised.
        var seen = RunThroughScanner(StatlinePromptRegexBuilder.Default, "[HP=-4/MA=31]:");

        Assert.Single(seen);
        Assert.Equal(-4, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
    }

    [Fact]
    public void CustomStatline_MatchesNegativeHp_WhileMortallyWounded()
    {
        // The %h fragment is signed too — a custom statline recognises the drop.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h/MA=%m]:");

        var seen = RunThroughScanner(regex, "[HP=-4/MA=31]:");
        Assert.Single(seen);
        Assert.Equal(-4, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
    }

    [Fact]
    public void CustomStatline_CapturesHpManaAndPosition()
    {
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h/MA=%m]: %r");

        var resting = RunThroughScanner(regex, "[HP=874/MA=441]: (Resting)");
        Assert.Single(resting);
        Assert.Equal(874, resting[0].Hp);
        Assert.Equal(ManaType.Mana, resting[0].ManaType);
        Assert.Equal(441, resting[0].Mana);
        Assert.Equal(PlayerPosition.Resting, resting[0].Position);

        var standing = RunThroughScanner(regex, "[HP=874/MA=441]: ");
        Assert.Single(standing);
        Assert.Equal(PlayerPosition.Standing, standing[0].Position);
    }

    [Fact]
    public void ManaLabel_IsCapturedAsTypeGroup()
    {
        // R1/D1: the MA / KAI label is literal text in a custom statline, but the
        // decode loop reads it as the type group. A naive Regex.Escape of the
        // whole template would lose the group and silently drop ManaType to None.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom <%h KAI=%m>");

        var seen = RunThroughScanner(regex, "<44 KAI=2>");
        Assert.Single(seen);
        Assert.Equal(ManaType.Kai, seen[0].ManaType);
        Assert.Equal(2, seen[0].Mana);
    }

    [Fact]
    public void MaxWildcards_RenderButAreNotCaptured()
    {
        // %H / %M emit non-capturing digit runs so no second writer appears for
        // the max fields; the prompt still matches with the denominators present.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h/%H MA=%m/%M]:");

        var seen = RunThroughScanner(regex, "[HP=874/985 MA=441/600]:");
        Assert.Single(seen);
        Assert.Equal(874, seen[0].Hp);
        Assert.Equal(ManaType.Mana, seen[0].ManaType);
        Assert.Equal(441, seen[0].Mana);
    }

    [Fact]
    public void FormattingCodes_AreStrippedFromPattern()
    {
        // Colour codes render as ANSI escapes the scanner strips before matching,
        // so the built pattern must ignore them and match the plain text.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom %f7[HP=%h]%d: %r");

        var seen = RunThroughScanner(regex, "[HP=300]: ");
        Assert.Single(seen);
        Assert.Equal(300, seen[0].Hp);
    }

    [Fact]
    public void NewlineWildcard_ContributesNoScannedCharacter()
    {
        // %n -> CR / LF, which the scanner drops; the pattern emits nothing for
        // it, so the literals on either side sit adjacent in the scanned text.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h]%n:");

        var seen = RunThroughScanner(regex, "[HP=55]:");
        Assert.Single(seen);
        Assert.Equal(55, seen[0].Hp);
    }

    [Fact]
    public void ExtraNumericWildcards_AreConsumedWithoutBreakingTheMatch()
    {
        // %c / %x / %X consume their rendered digits so the surrounding pattern
        // stays aligned, even though we don't capture them.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h $%c xp:%x]:");

        var seen = RunThroughScanner(regex, "[HP=410 $1500 xp:88231]:");
        Assert.Single(seen);
        Assert.Equal(410, seen[0].Hp);
    }

    [Fact]
    public void ChainedCustomStatlines_OnOneRow_EmitOnePerStatline()
    {
        // Custom shapes chain on a single row just like the default ones.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom {HP=%h}");

        var seen = RunThroughScanner(regex, "{HP=28}{HP=29}{HP=30}");
        Assert.Equal(3, seen.Count);
        Assert.Equal(new[] { 28, 29, 30 }, seen.Select(o => o.Hp));
    }

    [Fact]
    public void CustomStatline_SurvivesInterveningCsiColourCodes()
    {
        // The scanner strips inline CSI before matching, so a custom pattern
        // matches even when the server splits fields with SGR colour codes.
        Regex regex = StatlinePromptRegexBuilder.Build("full custom [HP=%h/MA=%m]:");

        var seen = RunThroughScanner(regex, "[HP=27\x1b[0;37m/MA=31\x1b[0;37m]:");
        Assert.Single(seen);
        Assert.Equal(27, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
    }
}
