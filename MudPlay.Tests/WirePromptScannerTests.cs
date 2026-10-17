using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class WirePromptScannerTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static List<PromptObservation> Collect(WirePromptScanner scanner)
    {
        List<PromptObservation> seen = new();
        scanner.PromptObserved += seen.Add;
        return seen;
    }

    // ----- the engine's out-of-the-game prompt -----

    private static int CountRealmLeft(WirePromptScanner scanner, Action feed)
    {
        int seen = 0;
        scanner.RealmLeftPromptObserved += () => seen++;
        feed();
        return seen;
    }

    [Fact]
    public void RealmLeftPrompt_AtARowStart_FiresOnce()
    {
        WirePromptScanner s = new();
        int seen = CountRealmLeft(s, () =>
        {
            s.Append(B("Your character has been saved. Thanks.\r\n[MAJORMUD]: "));
            s.Append(B(""));
            s.Append(B("x"));                    // more bytes with the prompt still in the carryover
        });
        Assert.Equal(1, seen);
    }

    [Fact]
    public void RealmLeftPrompt_SplitAcrossReads_StillFires()
    {
        WirePromptScanner s = new();
        int seen = CountRealmLeft(s, () =>
        {
            s.Append(B("...............\r\n[MAJOR"));
            s.Append(B("MUD]: "));
        });
        Assert.Equal(1, seen);
    }

    [Fact]
    public void RealmLeftPrompt_SeenTwice_FiresTwice()
    {
        WirePromptScanner s = new();
        int seen = CountRealmLeft(s, () =>
        {
            s.Append(B("\r\n[MAJORMUD]: "));
            s.Append(B("x\r\n[MAJORMUD]: "));
        });
        Assert.Equal(2, seen);
    }

    // Quoted in chat, or anywhere but the start of a row, it is ordinary text.
    [Theory]
    [InlineData("Bob gossips: [MAJORMUD]: is where you land\r\n")]
    [InlineData("You say \"[MAJORMUD]:\"\r\n")]
    [InlineData("[HP=27/MA=31]:look\r\nThe sign reads [MAJORMUD]: welcome\r\n")]
    public void RealmLeftPrompt_InsideOtherText_DoesNotFire(string wire)
    {
        WirePromptScanner s = new();
        Assert.Equal(0, CountRealmLeft(s, () => s.Append(B(wire))));
    }

    // A custom statline may carry the same words; that is the game's statline, with
    // the character still in the realm.
    [Fact]
    public void RealmLeftPrompt_AsPartOfTheActiveStatline_DoesNotFire()
    {
        WirePromptScanner s = new();
        s.InstallRegex(new System.Text.RegularExpressions.Regex(
            @"\[MAJORMUD\]: HP=(?<hp>-?\d+) MA=(?<mana>\d+)>"));
        var prompts = Collect(s);

        int seen = CountRealmLeft(s, () => s.Append(B("\r\n[MAJORMUD]: HP=27 MA=31>")));

        Assert.Single(prompts);
        Assert.Equal(0, seen);
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
    public void NegativeHp_WhileMortallyWounded_IsObserved()
    {
        // A dropped character bleeds into negative HP and the game prints it
        // ([HP=-4/MA=31]:). The prompt must still match — otherwise the HP
        // reading freezes at its last positive value, the drop gate never
        // fires, and the mortally-wounded body keeps taking engine commands.
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("[HP=-4/MA=31]:"));

        Assert.Single(seen);
        Assert.Equal(-4, seen[0].Hp);
        Assert.Equal(31, seen[0].Mana);
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

    // The prompt as the wire carries it: colour codes inside, the flag after the
    // colon, and the echo of the next command glued on.
    [Theory]
    [InlineData("]: (Meditating) wear fiery crown\r", PlayerPosition.Meditating)]
    [InlineData("]: (Resting) wear fiery crown\r", PlayerPosition.Resting)]
    [InlineData("]: (Meditating) ", PlayerPosition.Meditating)]
    [InlineData("]: (Resting) ", PlayerPosition.Resting)]
    [InlineData("]:isto\r", PlayerPosition.Standing)]
    public void RestingFlag_TrailingForm_WithTheEchoGluedOn(string tail, PlayerPosition expected)
    {
        WirePromptScanner s = new();
        var seen = Collect(s);
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("\u001b[79D\u001b[K\u001b[0;37m[HP=\u001b[0;37m749\u001b[0;37m/MA=\u001b[0;37m608\u001b[0;37m" + tail));

        PromptObservation prompt = Assert.Single(seen);
        Assert.Equal(expected, prompt.Position);
        Assert.Equal(749, prompt.Hp);
        Assert.Equal(608, prompt.Mana);
        Assert.Equal(0, unmatched);
    }

    // A handler that answers the prompt with a burst of commands (the prompt that
    // ends a rest starts a gear swap) reaches NoteCommandSent while the matched
    // text is still in the buffer (report paradigm-20261010-145330).
    [Theory]
    [InlineData("Meditating")]
    [InlineData("Resting")]
    public void CommandsSentFromAPromptHandler_AreNotReportedAsAnUnreadPrompt(string flag)
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;
        s.PromptObserved += p =>
        {
            if (p.Mana != 608) return;
            for (int i = 0; i < 12; i++) s.NoteCommandSent();
        };

        s.Append(B($"\u001b[79D\u001b[K[HP=749/MA=254]: ({flag}) \u001b[79D\u001b[K[HP=749/MA=608]: ({flag}) "));
        Assert.Empty(unmatched);

        s.NoteCommandSent();                // and one more once the read is done
        s.NoteCommandSent();
        Assert.Empty(unmatched);
    }

    // The guard covers the burst only: a prompt the statline truly can't read is
    // still reported at the commands that follow.
    [Fact]
    public void GenuineUnreadPromptAfterAHandlerBurst_IsStillReported()
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;
        bool burst = true;
        s.PromptObserved += _ =>
        {
            if (!burst) return;
            for (int i = 0; i < 5; i++) s.NoteCommandSent();
        };

        s.Append(B("\u001b[79D\u001b[K[HP=749/MA=608]: (Meditating) "));
        Assert.Empty(unmatched);
        burst = false;

        s.NoteCommandSent();
        s.Append(B("\r\nwear fiery crown\r\nHits 749 Mana 608 > "));   // a prompt with no brackets
        s.NoteCommandSent();

        Assert.Equal(new[] { "Hits 749 Mana 608 >" }, unmatched);
    }

    // A handler that throws must not leave the scanner deaf to the cursor's row.
    [Fact]
    public void PromptHandlerThrows_TheGuardIsReleased()
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;
        bool boom = true;
        s.PromptObserved += _ => { if (boom) throw new InvalidOperationException("x"); };

        Assert.Throws<InvalidOperationException>(() => s.Append(B("\u001b[79D\u001b[K[HP=749/MA=608]:")));
        boom = false;
        s.NoteCommandSent();   // the answer to the prompt that threw
        s.Append(B("\r\nHits 749 Mana 608 > "));
        s.NoteCommandSent();

        Assert.Single(unmatched);
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

    // Report paradigm-20260824-010304: another player pasted their prompt into
    // gossip. The unanchored scanner read HP=671 as ours, PromptParser ratcheted
    // MaxHp 176→671, and the major-heal threshold spammed grhe until mana was gone.
    // Preserve the genuine prompts surrounding that chat row, but never emit the
    // prompt-shaped substring after the printable "Mindcrime gossips: " prefix.
    [Fact]
    public void PromptQuotedInsideGossip_IsRejected_RealPromptsAroundItStillFire()
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B(
            "\x1b[79D\x1b[K[HP=157/MA=100]:"
            + "\x1b[79D\x1b[KMindcrime gossips: \x1b[0;35m[HP=671/KAI=40]:w\r\n"
            + "\x1b[79D\x1b[K[HP=158/MA=90]:"));

        Assert.Equal(2, seen.Count);
        Assert.Equal(new[] { 157, 158 }, seen.Select(o => o.Hp));
        Assert.All(seen, o => Assert.Equal(ManaType.Mana, o.ManaType));
    }

    [Fact]
    public void PromptQuotedInsideGossip_DoesNotTriggerCustomStatlineReconcile()
    {
        WirePromptScanner s = new();
        s.InstallRegex(StatlinePromptRegexBuilder.Build("full custom <HP %h>"));
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("Mindcrime gossips: [HP=671/KAI=40]:w\r\n"));

        Assert.Equal(0, unmatched);
    }

    // The link drops with a prompt half-sent. Left in the buffer, the next
    // connection's first bytes would finish it as a prompt nobody sent; the
    // disconnect's Reset drops it, and the new session's own prompt reads alone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADisconnect_DropsThePromptTheLinkCutOff(bool disconnected)
    {
        WirePromptScanner s = new();
        var seen = Collect(s);

        s.Append(B("\u001b[79D\u001b[K[HP=2"));   // the link drops here
        if (disconnected) s.Reset();
        s.Append(B("8/MA=34]:"));                  // first bytes of the next connection

        if (disconnected) Assert.Empty(seen);
        else Assert.Equal(28, Assert.Single(seen).Hp);   // what the reset is there to stop

        s.Append(B("\r\n\u001b[79D\u001b[K[HP=120/MA=40]:"));
        Assert.Equal(120, seen[^1].Hp);
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
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("[HP=120]:"));

        Assert.Equal(1, unmatched);
        Assert.Empty(observed);
    }

    [Fact]
    public void PromptShapeUnmatched_DoesNotFire_WhenDefaultPromptsMatchDefaultPattern()
    {
        // Default-statline user on a stock-shaped prompt → the active (default)
        // pattern reads every one, so nothing is unmatched.
        WirePromptScanner s = new();
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("[HP=120]:[HP=27/MA=31]:"));
        s.Append(B("\x1b[79D\x1b[K[HP=145/MA=46]: look west\r\n"));

        Assert.Equal(0, unmatched);
    }

    [Fact]
    public void PromptShapeUnmatched_FiresOnDefaultPattern_WhenGamePrintsAnotherShape()
    {
        // Report stock-20260929-111956: Settings -> Statline on Default, but the
        // game prints "[HP=145/145][MA=46/46]:" — the prompt sits at the row
        // start before the echoed command, and the Default pattern can't read it.
        WirePromptScanner s = new();
        var observed = Collect(s);
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;

        s.Append(B("\x1b[79D\x1b[K\x1b[0;37m[HP=145/145][MA=46/46]: go manhole\r\n"));

        Assert.Equal(new[] { "[HP=145/145][MA=46/46]:" }, unmatched);
        Assert.Empty(observed);
    }

    [Fact]
    public void CustomEditorMatchingTheReportPrompt_ReadsHpAndMana()
    {
        // The remedy the mismatch warning points at: author the game's shape in
        // Settings -> Statline and the parser reads it.
        WirePromptScanner s = new();
        s.InstallRegex(StatlinePromptRegexBuilder.Build("full custom [HP=%h/%H][MA=%m/%M]:"));
        var observed = Collect(s);
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("\x1b[79D\x1b[K\x1b[0;37m[HP=145/145][MA=46/46]: go manhole\r\n"));

        Assert.Equal(0, unmatched);
        Assert.Single(observed);
        Assert.Equal(145, observed[0].Hp);
        Assert.Equal(46, observed[0].Mana);
        Assert.Equal(ManaType.Mana, observed[0].ManaType);
    }

    [Fact]
    public void PromptShapeUnmatched_FiresOncePerPrompt_NotAgainForTheSameText()
    {
        WirePromptScanner s = new();
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        // Cursor-parked prompt, then unrelated output, then the next prompt.
        s.Append(B("\x1b[79D\x1b[K[HP=145/145][MA=46/46]: "));
        s.Append(B("\r\n>> Cricket has just logged on, 103 users on-line.\r\n"));
        Assert.Equal(1, unmatched);

        s.Append(B("\x1b[79D\x1b[K[HP=145/145][MA=46/46]: "));
        Assert.Equal(2, unmatched);
    }

    [Fact]
    public void PromptShapeUnmatched_IgnoresMenusAndNonStatlineText()
    {
        // BBS / game menus and ordinary output are not statlines: no digit in the
        // brackets, no bracketed-group-then-colon shape, or not at a row start.
        WirePromptScanner s = new();
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("\r\n[MAJORMUD]: "));
        s.Append(B("\r\nPress any key to continue: "));
        s.Append(B("\r\nMain System Menu (TOP) Make your selection (A,B,C or X to exit): "));
        s.Append(B("\r\nMindcrime gossips: [HP=671/KAI=40]:w\r\n"));

        Assert.Equal(0, unmatched);
    }

    [Fact]
    public void PromptShapeUnmatched_DoesNotFire_WhenTheSameReadAlsoMatches()
    {
        // A matching prompt in the read means the parser is reading the game.
        WirePromptScanner s = new();
        int unmatched = 0;
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("\r\n[Sysop 1]: server message\r\n\x1b[79D\x1b[K[HP=145/MA=46]: "));

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
        s.PromptShapeUnmatched += _ => unmatched++;

        s.Append(B("[HP=120]:"));

        Assert.Equal(0, unmatched);
        Assert.Single(observed);
    }

    // ----- the cursor's row when a command goes out -----

    // A statline set to plain text has no brackets or digits for the shape check,
    // but it's still what sits on the cursor's row when a command goes out.
    [Fact]
    public void CommandSent_ReportsAPlainTextPrompt()
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;

        s.Append(B("Slimy Sewer Tunnel\r\nObvious exits: south, east, west\r\npenis"));
        Assert.Empty(unmatched);            // nothing statline-shaped arrived

        s.NoteCommandSent();
        Assert.Equal(new[] { "penis" }, unmatched);

        s.NoteCommandSent();                // a second command before the next prompt
        Assert.Single(unmatched);           // the same prompt isn't counted twice

        s.Append(B("\r\nSlimy Sewer Tunnel\r\npenis"));
        s.NoteCommandSent();
        Assert.Equal(2, unmatched.Count);   // the next prompt is
    }

    [Fact]
    public void CommandSent_AfterAMatchingPrompt_ReportsNothing()
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;

        s.Append(B("Obvious exits: south\r\n[HP=91/MA=7]:"));
        s.NoteCommandSent();
        s.NoteCommandSent();                // an engine burst before the next prompt

        Assert.Empty(unmatched);
    }

    [Fact]
    public void CommandSent_WithNothingOnTheCursorRow_ReportsNothing()
    {
        WirePromptScanner s = new();
        List<string> unmatched = new();
        s.PromptShapeUnmatched += unmatched.Add;

        s.Append(B("Obvious exits: south\r\n"));
        s.NoteCommandSent();

        Assert.Empty(unmatched);
    }
}
