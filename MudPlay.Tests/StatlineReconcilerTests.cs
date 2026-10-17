using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class StatlineReconcilerTests
{
    private const string CustomCommand = "full custom [HP=%h/MA=%m]: %r";

    // The prompt from report stock-20260929-111956 — not a shape the Default
    // pattern reads.
    private const string OtherShapePrompt = "[HP=145/145][MA=46/46]:";

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private sealed class Harness
    {
        public WirePromptScanner Scanner { get; } = new();
        public StatlineReconciler Reconciler { get; }
        // What Reconciler put on the wire, through the sender the client binds.
        public List<byte[]> ReconcilerWire { get; } = new();
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public int FlagChanges;

        public Harness(string? desired = CustomCommand, bool bind = true)
        {
            // Install the same pattern the editor command would compile to, so
            // the scanner's match / mismatch decision mirrors production.
            Scanner.InstallRegex(StatlinePromptRegexBuilder.Build(desired));
            Reconciler = new StatlineReconciler(Scanner, now: () => Now);
            Reconciler.SetDesiredCommandProvider(() => desired);
            Reconciler.FlagChanged += () => FlagChanges++;
            if (bind) Reconciler.SetWireSender(ReconcilerWire.Add);
        }

        // Connect, then the first room display puts us in the game.
        public void EnterGame()
        {
            Reconciler.Arm();
            Reconciler.NoteRoomDisplayed();
        }

        // One prompt as the server parks it: row rewrite, the prompt, the echo.
        public void Prompt(string prompt, string echo = "")
            => Scanner.Append(B($"\x1b[79D\x1b[K\x1b[0;37m{prompt} {echo}\r\n"));

        public void Prompts(string prompt, int count)
        {
            for (int i = 0; i < count; i++) Prompt(prompt);
        }

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        public IReadOnlyList<string> Sent =>
            ReconcilerWire.Select(b => Encoding.Latin1.GetString(b)).ToList();
    }

    [Fact]
    public void CustomEditor_GameOnDefault_ResendsStatlineAfterThreeInARow()
    {
        var h = new Harness();
        h.EnterGame();

        // Class-default HP-only prompt: the custom (MA-requiring) pattern can't
        // match it → mismatch; the third in a row resends.
        h.Prompts("[HP=120]:", 2);
        Assert.Empty(h.Sent);
        h.Prompt("[HP=120]:");

        Assert.Single(h.Sent);
        Assert.Equal("set statline full custom [HP=%h/MA=%m]: %r\r", h.Sent[0]);
    }

    [Fact]
    public void DefaultEditor_GamePrintsOtherShape_SendsSetStatlineFull()
    {
        var h = new Harness(desired: "full");
        h.EnterGame();

        h.Prompt(OtherShapePrompt, "go manhole");
        h.Prompt(OtherShapePrompt, "w");
        h.Prompt(OtherShapePrompt, "w");

        Assert.Equal(new[] { "set statline full\r" }, h.Sent);
        Assert.Equal(OtherShapePrompt, h.Reconciler.LastUnmatchedPrompt);
        Assert.False(h.Reconciler.LastPromptMatched);
    }

    [Fact]
    public void DefaultEditor_ResetTakes_ThenALaterMismatchRunResetsAgain()
    {
        var h = new Harness(desired: "full");
        h.EnterGame();
        h.Prompts(OtherShapePrompt, 3);
        Assert.Single(h.Sent);

        // The server applies `set statline full` → the stock shape the Default
        // pattern reads.
        h.Prompt("[HP=145/MA=46]:");
        Assert.True(h.Reconciler.IsSynced);
        h.Prompts("[HP=145/MA=46]:", 5);
        Assert.Single(h.Sent);              // matching prompts send nothing

        // The statline is changed in-game mid-session: a new run, its own reset.
        h.Advance(10);
        h.Prompts(OtherShapePrompt, 3);
        Assert.Equal(2, h.Sent.Count);
        Assert.False(h.Reconciler.IsSynced);
        Assert.False(h.Reconciler.IsFlagged);
    }

    [Fact]
    public void DefaultEditor_StockPrompts_NeverResend()
    {
        var h = new Harness(desired: "full");
        h.EnterGame();

        h.Prompt("[HP=120]:");
        h.Prompt("[HP=27/MA=31]:");
        h.Prompt("[HP=44/KAI=2]:");

        Assert.True(h.Reconciler.IsSynced);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void FirstMatchingPrompt_LatchesSynced_NoResend()
    {
        var h = new Harness();
        h.EnterGame();

        // A prompt in the editor's custom shape → already in sync.
        h.Prompt("[HP=874/MA=441]:");

        Assert.True(h.Reconciler.IsSynced);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void BeforeARoomDisplay_MismatchesDoNotCount()
    {
        // Connected but still in the BBS menus — nothing counts until a room
        // display says we're in the game.
        var h = new Harness(desired: "full");
        h.Reconciler.Arm();

        h.Prompts(OtherShapePrompt, 5);
        Assert.Empty(h.Sent);

        h.Reconciler.NoteRoomDisplayed();
        h.Prompts(OtherShapePrompt, 3);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void MatchingPromptBreaksTheRun()
    {
        var h = new Harness(desired: "full");
        h.EnterGame();

        // Two stray unmatched lines, a matching prompt, two more: never three
        // in a row, so nothing is sent (the match also syncs).
        h.Prompts(OtherShapePrompt, 2);
        h.Prompt("[HP=145/MA=46]:");
        h.Prompts(OtherShapePrompt, 2);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RapidMismatches_CollapseToOneResend_WithinCooldown()
    {
        var h = new Harness();
        h.EnterGame();

        // Nine unmatched prompts with the clock frozen → one resend; the
        // in-flight burst between our send and the server applying it must not
        // blast duplicate commands.
        h.Prompts("[HP=120]:", 9);

        Assert.Single(h.Sent);
    }

    [Fact]
    public void Resends_AreBounded_ThenFlaggedOnce()
    {
        var h = new Harness(desired: "full");
        h.Reconciler.MaxRetries = 2;
        h.EnterGame();

        h.Prompts(OtherShapePrompt, 3);          // attempt 1
        h.Advance(3);
        h.Prompts(OtherShapePrompt, 3);          // attempt 2
        Assert.Equal(2, h.Sent.Count);
        Assert.False(h.Reconciler.IsFlagged);

        h.Advance(3);
        h.Prompts(OtherShapePrompt, 3);          // over cap — give up, flag
        Assert.Equal(2, h.Sent.Count);
        Assert.True(h.Reconciler.HasGivenUp);
        Assert.True(h.Reconciler.IsFlagged);
        Assert.Equal(1, h.FlagChanges);
        Assert.Contains($"\"{OtherShapePrompt}\"", h.Reconciler.FlagNotice);
        Assert.Contains("set statline full", h.Reconciler.FlagNotice);

        h.Advance(3);
        h.Prompts(OtherShapePrompt, 6);          // stays given up, no repeat flag
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(1, h.FlagChanges);
    }

    [Fact]
    public void Flag_ClearsOnMatchingPrompt_AndSurvivesReconnectUntilThen()
    {
        var h = new Harness(desired: "full");
        h.Reconciler.MaxRetries = 0;
        h.EnterGame();
        h.Prompts(OtherShapePrompt, 3);
        Assert.True(h.Reconciler.IsFlagged);

        // Reconnect: still flagged, and giving up again doesn't re-announce.
        h.Reconciler.Disarm();
        h.EnterGame();
        Assert.True(h.Reconciler.IsFlagged);
        h.Prompts(OtherShapePrompt, 3);
        Assert.Equal(1, h.FlagChanges);

        // The user fixed it (typed `set statline full`) → the next prompt reads.
        h.Prompt("[HP=145/MA=46]:");
        Assert.False(h.Reconciler.IsFlagged);
        Assert.Null(h.Reconciler.FlagNotice);
        Assert.Equal(2, h.FlagChanges);
    }

    [Fact]
    public void Notice_IsPlainAsciiClientNotice_AndNamesTheRemedy()
    {
        string forDefault = StatlineReconciler.BuildNotice(OtherShapePrompt, "full");
        string forCustom = StatlineReconciler.BuildNotice(OtherShapePrompt, CustomCommand);

        foreach (string notice in new[] { forDefault, forCustom })
        {
            Assert.True(ClientNotice.IsNotice(notice));
            Assert.All(notice, c => Assert.InRange(c, ' ', '~'));
            Assert.Contains("doesn't match Settings -> Statline", notice);
        }
        Assert.Contains("type: set statline full", forDefault);
        Assert.Contains("set it back to Default", forCustom);
    }

    [Fact]
    public void NotArmed_DoesNotResend()
    {
        var h = new Harness();
        // No Arm().
        h.Reconciler.NoteRoomDisplayed();
        h.Prompts("[HP=120]:", 3);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Disarm_StopsResending()
    {
        var h = new Harness();
        h.EnterGame();
        h.Prompts("[HP=120]:", 3);
        Assert.Single(h.Sent);

        h.Reconciler.Disarm();
        h.Advance(5);
        h.Prompts("[HP=120]:", 3);

        Assert.Single(h.Sent);
    }

    [Fact]
    public void WireNotBound_RecordsAndSendsNothing()
    {
        var h = new Harness(bind: false);
        h.EnterGame();

        h.Prompts("[HP=120]:", 3);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ReArm_ResetsRetryBudgetAndSyncLatch()
    {
        var h = new Harness();
        h.Reconciler.MaxRetries = 1;
        h.EnterGame();
        h.Prompts("[HP=120]:", 3);               // attempt 1 — cap reached
        h.Advance(3);
        h.Prompts("[HP=120]:", 3);               // over cap — no send
        Assert.Single(h.Sent);

        h.EnterGame();                           // fresh connect re-arms
        h.Advance(3);
        h.Prompts("[HP=120]:", 3);               // budget restored — sends again

        Assert.Equal(2, h.Sent.Count);
    }
}
