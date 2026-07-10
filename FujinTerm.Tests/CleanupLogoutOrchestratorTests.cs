using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the proactive log-off FSM: warning → wait-for-safe → send the
/// exit command → wait for the main menu → drop the carrier. The engine
/// is opt-in (gated on the active BBS's ReconnectAfterCleanup), latched
/// to one logout per cleanup cycle, and must never log off while the
/// room is unsafe or after the wire has dropped.
/// </summary>
public sealed class CleanupLogoutOrchestratorTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required CleanupLogoutOrchestrator Engine { get; init; }
        public required CleanupWarningWatcher Cleanup { get; init; }
        public required MessageRouter Router { get; init; }
        public bool Safe { get; set; }
        public bool Connected { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public int DisconnectCalls { get; set; }
        public DateTime Clock { get; set; } = Now;
    }

    private static Harness Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        CleanupWarningWatcher cleanup = new();

        CleanupLogoutOrchestrator engine = new(cleanup, router);
        Harness h = new()
        {
            Engine = engine,
            Cleanup = cleanup,
            Router = router,
        };
        engine.NowProvider = () => h.Clock;
        engine.SetWireSender(_ => { });
        engine.SetSafePredicate(() => h.Safe);
        engine.SetConnectedCheck(() => h.Connected);
        engine.SetAutoLogoutEnabledCheck(() => h.Enabled);
        engine.SetDisconnectCallback(() => h.DisconnectCalls++);
        return h;
    }

    private static void FireWarning(Harness h, int minutes = 20)
        => h.Cleanup.Append(Encoding.Latin1.GetBytes($"shutting down in {minutes} minutes"));

    private static void DispatchMenuLine(MessageRouter router)
    {
        LineExtractor.EmittedLine line = new(
            "[E] . Enter the Realm",
            new CellAttributes[20],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    private static string LastSent(CleanupLogoutOrchestrator engine)
        => Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests));

    // ===== Arming + gating ==============================================

    [Fact]
    public void Warning_WhenDisabled_StaysIdle()
    {
        Harness h = Setup();
        h.Enabled = false;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);
        Assert.Empty(h.Engine.LastSentForTests);
    }

    [Fact]
    public void Warning_WhenDisconnected_StaysIdle()
    {
        Harness h = Setup();
        h.Connected = false;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);
    }

    [Fact]
    public void Warning_ReconnectAfterCleanupOnly_ArmsIndependentOfHangups()
    {
        // The cleanup log-off is gated on ReconnectAfterCleanup alone — it is
        // deliberately exempt from the master "Disable hangups" kill-switch, so
        // a safe room exits at shutdown even when the user has hangups disabled.
        // The orchestrator no longer knows about DisableHangups at all; this
        // pins that the opt-in is the sole arming gate.
        Harness h = Setup();
        h.Safe = true;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h.Engine));
    }

    [Fact]
    public void Warning_WhenEnabledAndUnsafe_GoesPending()
    {
        Harness h = Setup();
        h.Safe = false;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Engine.LastSentForTests);
    }

    [Fact]
    public void Warning_WhenAlreadySafe_ExitsImmediately()
    {
        Harness h = Setup();
        h.Safe = true;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h.Engine));
    }

    // ===== Safe-poll transition =========================================

    [Fact]
    public void Pending_StaysPending_WhileUnsafe()
    {
        Harness h = Setup();
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        h.Engine.EvaluateForTests();
        h.Engine.EvaluateForTests();

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Engine.LastSentForTests);
    }

    [Fact]
    public void Pending_BecomesSafe_SendsExit()
    {
        Harness h = Setup();
        FireWarning(h);

        h.Safe = true;
        h.Engine.EvaluateForTests();

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h.Engine));
    }

    [Fact]
    public void RespectsCustomExitCommand()
    {
        Harness h = Setup();
        h.Engine.ExitCommand = "quit";
        h.Safe = true;

        FireWarning(h);

        Assert.Equal("quit\r", LastSent(h.Engine));
    }

    // ===== Menu detection completes logout ==============================

    [Fact]
    public void Exiting_MainMenuLine_DropsCarrier()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);

        DispatchMenuLine(h.Router);

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    [Fact]
    public void MainMenuLine_BeforeExiting_Ignored()
    {
        // A menu-shaped line seen while Idle/Pending is not ours — must
        // not trip a disconnect.
        Harness h = Setup();
        FireWarning(h);   // Pending (unsafe)

        DispatchMenuLine(h.Router);

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Equal(0, h.DisconnectCalls);
    }

    // ===== Menu-wait timeout: one retry, then force-disconnect ==========

    [Fact]
    public void Exiting_MenuTimeout_RetriesExitOnce()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);   // sends "x", Exiting

        h.Clock = Now.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        h.Engine.EvaluateForTests();

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal(2, h.Engine.LastSentForTests.Count);   // original + retry
        Assert.Equal("x\r", Encoding.Latin1.GetString(h.Engine.LastSentForTests[1]));
        Assert.Equal(0, h.DisconnectCalls);
    }

    [Fact]
    public void Exiting_SecondMenuTimeout_ForceDisconnects()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);

        // First timeout → retry.
        h.Clock = Now.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        h.Engine.EvaluateForTests();

        // Second timeout after the retry → drop carrier anyway.
        h.Clock = h.Clock.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        h.Engine.EvaluateForTests();

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    // ===== Abort on lost wire ===========================================

    [Fact]
    public void Pending_ConnectionDrops_Aborts()
    {
        Harness h = Setup();
        FireWarning(h);   // Pending
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        h.Connected = false;
        h.Engine.EvaluateForTests();

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(0, h.DisconnectCalls);   // wire already gone; nothing to drop
    }

    // ===== Latch: one logout per cycle ==================================

    [Fact]
    public void FollowUpWarnings_DoNotReArm()
    {
        Harness h = Setup();
        FireWarning(h, 20);   // Pending
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        // The 5m / 2m follow-ups must be ignored — already armed.
        FireWarning(h, 5);
        FireWarning(h, 2);

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Engine.LastSentForTests);
    }

    [Fact]
    public void Reset_ReopensLatch()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        DispatchMenuLine(h.Router);
        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);

        // New session — Reset re-arms cleanly.
        h.Engine.Reset();
        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);

        h.Safe = false;
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
    }

    [Fact]
    public void Dispose_StopsHearingMenu()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);   // Exiting
        h.Engine.Dispose();

        DispatchMenuLine(h.Router);

        Assert.Equal(0, h.DisconnectCalls);
    }
}
