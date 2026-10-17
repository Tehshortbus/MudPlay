using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the proactive log-off FSM: warning → wait-for-safe → send the
// exit command → confirm out of the realm (character-saved line, or the
// main-menu row, or a timeout) → drop the carrier. The engine
// is opt-in (gated on the active BBS's ReconnectAfterCleanup), latched
// to one logout per cleanup cycle, and must never log off while the
// room is unsafe or after the wire has dropped.
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
        // What the engine put on the wire, through the sender the client binds.
        public List<byte[]> Sent { get; } = new();
        // The safe-poll ticks the engine armed and the timer hasn't run yet.
        public required List<(TimeSpan Delay, Action Tick)> Polls { get; init; }
    }

    private static Harness Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        CleanupWarningWatcher cleanup = new();

        List<(TimeSpan, Action)> polls = new();
        Harness h = null!;
        CleanupLogoutOrchestrator engine = new(cleanup, router, schedulePoll: (delay, tick) => polls.Add((delay, tick)), now: () => h.Clock);
        h = new()
        {
            Engine = engine,
            Cleanup = cleanup,
            Router = router,
            Polls = polls,
        };
        engine.SetWireSender(h.Sent.Add);
        engine.SetSafePredicate(() => h.Safe);
        engine.SetConnectedCheck(() => h.Connected);
        engine.SetAutoLogoutEnabledCheck(() => h.Enabled);
        engine.SetDisconnectCallback(() => h.DisconnectCalls++);
        return h;
    }

    private static void FireWarning(Harness h, int minutes = 20)
        => h.Cleanup.Append(Encoding.Latin1.GetBytes($"shutting down in {minutes} minutes"));

    private static void DispatchMenuLine(MessageRouter router)
        => DispatchLine(router, "[E] . Enter the Realm");

    private static void DispatchSavedLine(MessageRouter router)
        => DispatchLine(router, "Your character has been saved. If you have any comments or suggestions, please");

    private static void DispatchLine(MessageRouter router, string text)
    {
        LineExtractor.EmittedLine line = new(
            text,
            new CellAttributes[20],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    private static string LastSent(Harness h)
        => Encoding.Latin1.GetString(Assert.Single(h.Sent));

    // The safe-poll timer comes round: every tick armed so far runs, as the
    // dispatcher would run them once their interval is up.
    private static void Poll(Harness h)
    {
        List<(TimeSpan Delay, Action Tick)> due = new(h.Polls);
        h.Polls.Clear();
        foreach ((TimeSpan delay, Action tick) in due)
        {
            Assert.Equal(h.Engine.SafePollInterval, delay);
            tick();
        }
    }

    // ===== Arming + gating ==============================================

    [Fact]
    public void Warning_WhenDisabled_StaysIdle()
    {
        Harness h = Setup();
        h.Enabled = false;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);
        Assert.Empty(h.Sent);
    }

    // The cleanup log-off is not a hang-up, but it is automatic: with the master
    // switch off it doesn't start (user, 2026-10-09). On, it runs as before
    // (Warning_WhenAlreadySafe_ExitsImmediately).
    [Fact]
    public void Warning_MasterSwitchOff_StaysIdle_AndALaterWarningArmsOnceItIsBackOn()
    {
        Harness h = Setup();
        bool off = true;
        h.Engine.SetMasterSwitchCheck(() => off);
        h.Safe = true;

        FireWarning(h, 20);
        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);
        Assert.Empty(h.Sent);

        off = false;
        FireWarning(h, 5);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h));
    }

    [Fact]
    public void PendingLogoff_MasterSwitchGoesOff_WaitsUntilItIsBackOn()
    {
        Harness h = Setup();
        bool off = false;
        h.Engine.SetMasterSwitchCheck(() => off);
        h.Safe = false;
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        off = true;
        h.Safe = true;
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Sent);

        off = false;
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
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
        Assert.Equal("x\r", LastSent(h));
    }

    [Fact]
    public void Warning_WhenEnabledAndUnsafe_GoesPending()
    {
        Harness h = Setup();
        h.Safe = false;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Warning_WhenAlreadySafe_ExitsImmediately()
    {
        Harness h = Setup();
        h.Safe = true;

        FireWarning(h);

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h));
    }

    // ===== Safe-poll transition =========================================

    [Fact]
    public void Pending_StaysPending_WhileUnsafe()
    {
        Harness h = Setup();
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        Poll(h);
        Poll(h);

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Pending_BecomesSafe_SendsExit()
    {
        Harness h = Setup();
        FireWarning(h);

        h.Safe = true;
        Poll(h);

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal("x\r", LastSent(h));
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

    [Fact]
    public void Exiting_CharacterSavedLine_DropsCarrier()
    {
        // The primary trigger: boards that nest the realm behind extra menus
        // exit past the "[E] Enter the Realm" row entirely (report
        // stock-20260904-230111), so the realm-exit "Your character has been
        // saved." line is what confirms we're out and drops the carrier.
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);

        DispatchSavedLine(h.Router);

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    [Fact]
    public void CharacterSavedLine_BeforeExiting_Ignored()
    {
        // A save line seen while Pending (we haven't sent our exit yet) is not
        // ours — must not trip a disconnect.
        Harness h = Setup();
        FireWarning(h);   // Pending (unsafe)

        DispatchSavedLine(h.Router);

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
        Poll(h);

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal(2, h.Sent.Count);   // original + retry
        Assert.Equal("x\r", Encoding.Latin1.GetString(h.Sent[1]));
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
        Poll(h);

        // Second timeout after the retry → drop carrier anyway.
        h.Clock = h.Clock.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        Poll(h);

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    // ===== The engine's own out-of-the-game prompt =======================

    // The saved sentence is board text a sysop can reword; the prompt after it is
    // the engine's. Seen while our exit is out, it completes the log-off.
    [Fact]
    public void Exiting_RealmLeftPrompt_DropsCarrier()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);

        h.Engine.NoteRealmLeftPrompt();

        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    // The same prompt stands on a board's menus at login and after a manual exit.
    [Fact]
    public void RealmLeftPrompt_WhenNotExiting_Ignored()
    {
        Harness h = Setup();
        h.Engine.NoteRealmLeftPrompt();                        // Idle
        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);

        FireWarning(h);                                        // unsafe: Pending
        h.Engine.NoteRealmLeftPrompt();
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Equal(0, h.DisconnectCalls);
    }

    // ===== Exit called off by an attack =================================

    // An attack during the exit wait keeps the character in the game. The engine
    // goes back to waiting for a safe room; it must not run the timeouts out and
    // drop the carrier in the middle of the fight.
    [Fact]
    public void Exiting_WaitInterrupted_GoesBackToWaitingForASafeRoom()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);   // sends "x", Exiting
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);

        h.Safe = false;   // something is swinging at us
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        // Both timeouts pass with the fight still on: no blind re-send, no drop.
        h.Clock = Now.Add(h.Engine.MenuWaitTimeout).Add(h.Engine.MenuWaitTimeout).AddSeconds(5);
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Single(h.Sent);
        Assert.Equal(0, h.DisconnectCalls);

        // The room clears, and has to stay clear for a round before the exit goes
        // out again, with its own fresh timeout.
        h.Safe = true;
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        h.Clock = h.Clock.Add(h.Engine.InterruptSettle);
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal(2, h.Sent.Count);
        Poll(h);
        Assert.Equal(2, h.Sent.Count);   // not re-sent at once

        DispatchSavedLine(h.Router);
        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
    }

    // With nothing typed during the wait the line arrives on the end of the row of
    // dots; it is read the same.
    [Fact]
    public void Exiting_WaitInterrupted_OnTheRowOfDots_IsRead()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);

        DispatchLine(h.Router, "................Your meditation has been interrupted - you may not exit now!");

        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
    }

    // After a called-off exit the room must read safe for a whole round, unbroken:
    // a reading that flickers between swings starts the count again.
    [Fact]
    public void AfterInterruption_ExitWaitsForARoundOfUnbrokenSafety()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");

        Poll(h);                           // safe: the count starts
        h.Clock = h.Clock.AddSeconds(3);
        h.Safe = false;
        Poll(h);                           // broken
        h.Safe = true;
        h.Clock = h.Clock.AddSeconds(1);
        Poll(h);                           // the count starts over
        h.Clock = h.Clock.Add(h.Engine.InterruptSettle).AddSeconds(-1);
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Single(h.Sent);

        h.Clock = h.Clock.AddSeconds(1);
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal(2, h.Sent.Count);
    }

    // An attacker the safe check can't see calls every exit off. The retries stop
    // at the cap, and from there the timeouts run as they do with no confirmation:
    // one blind re-send, then the carrier is dropped.
    [Fact]
    public void RepeatedInterruptions_AreCapped_ThenTheTimeoutsDecide()
    {
        Harness h = Setup();
        h.Safe = true;                                         // reads safe throughout
        FireWarning(h);                                        // exit 1

        for (int i = 1; i < h.Engine.MaxInterruptions; i++)
        {
            DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
            Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
            Poll(h);
            h.Clock = h.Clock.Add(h.Engine.InterruptSettle);
            Poll(h);                       // exit i + 1
            Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        }
        Assert.Equal(h.Engine.MaxInterruptions, h.Sent.Count);

        // The interruption that reaches the cap is not answered with another wait.
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Poll(h);
        Assert.Equal(h.Engine.MaxInterruptions, h.Sent.Count);

        h.Clock = h.Clock.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        Poll(h);                           // the blind re-send
        Assert.Equal(h.Engine.MaxInterruptions + 1, h.Sent.Count);
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);

        h.Clock = h.Clock.Add(h.Engine.MenuWaitTimeout).AddSeconds(1);
        Poll(h);
        Assert.Equal(CleanupLogoutPhase.Done, h.Engine.Phase);
        Assert.Equal(1, h.DisconnectCalls);
        Assert.Equal(h.Engine.MaxInterruptions + 1, h.Sent.Count);
    }

    // A new session starts the count again.
    [Fact]
    public void Reset_ClearsTheInterruptionCountAndTheSettle()
    {
        Harness h = Setup();
        h.Safe = true;
        FireWarning(h);
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        h.Engine.Reset();
        FireWarning(h);                                        // safe: exits at once, no settle owed

        Assert.Equal(CleanupLogoutPhase.Exiting, h.Engine.Phase);
        Assert.Equal(2, h.Sent.Count);
    }

    // The same line answers an exit the user typed; with no exit of ours out it
    // changes nothing.
    [Fact]
    public void WaitInterrupted_BeforeExiting_Ignored()
    {
        Harness h = Setup();
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Idle, h.Engine.Phase);

        FireWarning(h);   // unsafe: Pending
        DispatchLine(h.Router, "Your meditation has been interrupted - you may not exit now!");
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);
        Assert.Empty(h.Sent);
    }

    // ===== Abort on lost wire ===========================================

    [Fact]
    public void Pending_ConnectionDrops_Aborts()
    {
        Harness h = Setup();
        FireWarning(h);   // Pending
        Assert.Equal(CleanupLogoutPhase.Pending, h.Engine.Phase);

        h.Connected = false;
        Poll(h);

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
        Assert.Empty(h.Sent);
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
