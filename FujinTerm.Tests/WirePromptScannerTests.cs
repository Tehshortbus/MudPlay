using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class WirePromptScannerTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static List<PromptObservation> Collect(WirePromptScanner scanner)
    {
        List<PromptObservation> seen = new();
        scanner.PromptObserved += seen.Add;
        return seen;
    }

    [Fact]
    public void SimpleStatline_EmitsOneObservation()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=27/MA=31]:"));

        Assert.Single(seen);
        Assert.Equal(27, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
        Assert.Equal(ManaType.Mana, seen[0].ManaType);
        Assert.Equal(PlayerPosition.Standing, seen[0].Position);
    }

    [Fact]
    public void OverwrittenStatlines_OnSameRow_EmitOnePerStatline()
    {
        // The bug this whole path exists to fix: server chains four ticks
        // worth of statlines into one row using CR + erase-line. Without
        // the wire scanner, LineExtractor would emit one line at most and
        // we'd lose three ticks.
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=28/MA=34]:[HP=29/MA=34]:[HP=30/MA=34]:[HP=31/MA=34]:"));

        Assert.Equal(4, seen.Count);
        Assert.Equal(new[] { 28, 29, 30, 31 }, seen.Select(o => o.Hp));
    }

    [Fact]
    public void StatlineWithInterveningCsiSgr_StripsAndMatches()
    {
        // Real wire shape: SGR colour codes split the HP / MA fields.
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=27\x1b[0;37m/MA=31\x1b[0;37m]:"));

        Assert.Single(seen);
        Assert.Equal(27, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
    }

    [Fact]
    public void StatlineWithCursorReturnAndEraseLine_StripsAndMatches()
    {
        // Real wire shape: server re-anchors at column 0 + erases line
        // before each rewrite.
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("\x1b[79D\x1b[K\x1b[0;37m[HP=40/MA=26\x1b[0;37m]:"));

        Assert.Single(seen);
        Assert.Equal(40, seen[0].Hp);
        Assert.Equal(26, seen[0].Mana);
    }

    [Fact]
    public void PartialMatchSplitAcrossChunks_StillFires()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=2"));
        Assert.Empty(seen);                          // partial — no match yet.

        s.Append(B("8/MA=34]:"));
        Assert.Single(seen);
        Assert.Equal(28, seen[0].Hp);
    }

    [Fact]
    public void RestingFlag_LeadingForm_Detected()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=44/KAI=2 (Meditating) ]:"));

        Assert.Equal(PlayerPosition.Meditating, seen[0].Position);
        Assert.Equal(ManaType.Kai, seen[0].ManaType);
    }

    [Fact]
    public void RestingFlag_TrailingForm_Detected()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=779/MA=571]: (Resting)"));

        Assert.Equal(PlayerPosition.Resting, seen[0].Position);
    }

    [Fact]
    public void HpOnlyPrompt_HasNoManaType()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=120]:"));

        Assert.Equal(ManaType.None, seen[0].ManaType);
        Assert.Equal(0, seen[0].Mana);
    }

    [Fact]
    public void NonPromptBytes_DoNotFire()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("Newhaven, Narrow Road\r\nObvious exits: n^Horth, e^Hast\r\n"));

        Assert.Empty(seen);
    }

    [Fact]
    public void PromptInTheMiddleOfRoomOutput_StillCaught()
    {
        // E.g., combat line followed by a fresh prompt on the same chunk.
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("The kobold thief lunges at you with their shortsword!\r\n[HP=38/MA=26]:"));

        Assert.Single(seen);
        Assert.Equal(38, seen[0].Hp);
    }

    [Fact]
    public void Reset_DropsCarryoverState()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=2"));   // partial.
        s.Reset();
        s.Append(B("8/MA=34]:"));   // would have completed the partial; should not match.

        Assert.Empty(seen);
    }

    [Fact]
    public void PromptShapeUnmatched_FiresWhenDefaultShapeButActiveIsCustom()
    {
        // Editor authored a custom statline, but the game is still on the
        // class default → the active (custom) pattern can't match, the
        // permissive default does → mismatch fires once, no observation.
        WirePromptScanner s = new();
        s.InstallRegex(StatlinePromptRegexBuilder.Build("full custom <HP %h>"));
        var observed = Collect(s);
        int unmatched = 0;
        s.PromptShapeUnmatched += () => unmatched++;

        s.Append(B("[HP=120]:"));

        Assert.Equal(1, unmatched);
        Assert.Empty(observed);
    }

    [Fact]
    public void PromptShapeUnmatched_NeverFiresOnDefaultPattern()
    {
        // Default-statline users run the permissive pattern AS the active
        // one, so there's nothing to drift from → mismatch is structurally
        // unreachable even when prompts arrive.
        WirePromptScanner s = new();
        int unmatched = 0;
        s.PromptShapeUnmatched += () => unmatched++;

        s.Append(B("[HP=120]:[HP=27/MA=31]:"));

        Assert.Equal(0, unmatched);
    }

    [Fact]
    public void PromptShapeUnmatched_DoesNotFire_WhenActiveMatches()
    {
        // The live prompt already has the editor's custom shape → active
        // pattern matches → in sync, no mismatch signal.
        WirePromptScanner s = new();
        s.InstallRegex(StatlinePromptRegexBuilder.Build("full custom [HP=%h]:"));
        var observed = Collect(s);
        int unmatched = 0;
        s.PromptShapeUnmatched += () => unmatched++;

        s.Append(B("[HP=120]:"));

        Assert.Equal(0, unmatched);
        Assert.Single(observed);
    }
}
