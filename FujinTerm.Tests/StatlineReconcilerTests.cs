using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class StatlineReconcilerTests
{
    private const string CustomCommand = "full custom [HP=%h/MA=%m]: %r";

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private sealed class Harness
    {
        public WirePromptScanner Scanner { get; } = new();
        public StatlineReconciler Reconciler { get; }
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Harness(string? desired = CustomCommand, bool bind = true)
        {
            // Install the same pattern the editor command would compile to, so
            // the scanner's match / mismatch decision mirrors production.
            Scanner.InstallRegex(StatlinePromptRegexBuilder.Build(desired));
            Reconciler = new StatlineReconciler(Scanner) { NowProvider = () => Now };
            Reconciler.SetDesiredCommandProvider(() => desired);
            if (bind) Reconciler.SetWireSender(static _ => { });
        }

        public void Feed(string wire) => Scanner.Append(B(wire));

        public IReadOnlyList<string> Sent =>
            Reconciler.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();
    }

    [Fact]
    public void CustomEditor_GameOnDefault_ResendsStatline()
    {
        var h = new Harness();
        h.Reconciler.Arm();

        // Class-default HP-only prompt: the custom (MA-requiring) pattern can't
        // match it, but the permissive default does → mismatch → resend.
        h.Feed("[HP=120]:");

        Assert.Single(h.Sent);
        Assert.Equal("set statline full custom [HP=%h/MA=%m]: %r\r", h.Sent[0]);
    }

    [Fact]
    public void DefaultEditor_NeverResends()
    {
        var h = new Harness(desired: "full");
        h.Reconciler.Arm();

        h.Feed("[HP=120]:");
        h.Feed("[HP=27/MA=31]:");
        h.Feed("[HP=44/KAI=2]:");

        Assert.True(h.Reconciler.IsSynced);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void FirstMatchingPrompt_LatchesSynced_NoResend()
    {
        var h = new Harness();
        h.Reconciler.Arm();

        // A prompt in the editor's custom shape → already in sync.
        h.Feed("[HP=874/MA=441]: ");

        Assert.True(h.Reconciler.IsSynced);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RapidDefaultPrompts_CollapseToOneResend_WithinCooldown()
    {
        var h = new Harness();
        h.Reconciler.Arm();

        // Three default prompts within RetryDelay (clock frozen) → one resend;
        // the in-flight burst between our send and the server applying it must
        // not blast duplicate commands.
        h.Feed("[HP=120]:");
        h.Feed("[HP=121]:");
        h.Feed("[HP=122]:");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void Resends_AreBounded_ByMaxRetries()
    {
        var h = new Harness();
        h.Reconciler.MaxRetries = 2;
        h.Reconciler.Arm();

        h.Feed("[HP=120]:");                 // attempt 1
        h.Now = h.Now.AddSeconds(3);
        h.Feed("[HP=120]:");                 // attempt 2
        h.Now = h.Now.AddSeconds(3);
        h.Feed("[HP=120]:");                 // over cap — give up

        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void NotArmed_DoesNotResend()
    {
        var h = new Harness();
        // No Arm().
        h.Feed("[HP=120]:");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Disarm_StopsResending()
    {
        var h = new Harness();
        h.Reconciler.Arm();
        h.Feed("[HP=120]:");
        Assert.Single(h.Sent);

        h.Reconciler.Disarm();
        h.Now = h.Now.AddSeconds(5);
        h.Feed("[HP=120]:");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void WireNotBound_RecordsAndSendsNothing()
    {
        var h = new Harness(bind: false);
        h.Reconciler.Arm();

        h.Feed("[HP=120]:");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ReArm_ResetsRetryBudgetAndSyncLatch()
    {
        var h = new Harness();
        h.Reconciler.MaxRetries = 1;
        h.Reconciler.Arm();
        h.Feed("[HP=120]:");                 // attempt 1 — cap reached
        h.Now = h.Now.AddSeconds(3);
        h.Feed("[HP=120]:");                 // over cap — no send
        Assert.Single(h.Sent);

        h.Reconciler.Arm();                  // fresh connect re-arms
        h.Now = h.Now.AddSeconds(3);
        h.Feed("[HP=120]:");                 // budget restored — sends again

        Assert.Equal(2, h.Sent.Count);
    }
}
