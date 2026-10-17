using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the latch model. The entry command must NEVER fire on a
// menu-shaped line unless the engine was just armed by the BBS-
// login-automation completion event — otherwise a malicious player
// could gossip "[E] . Enter the Realm" mid-game and trick the
// client into re-entering when the player wanted to stay menu-side.
public sealed class MainMenuEntryAutomationTests
{
    // The clock the MainMenuEntryAutomation reads: the real one unless a test sets its own.
    private Func<DateTime> _menuNow = () => DateTime.UtcNow;

    private static readonly DateTime Now = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

    // What the engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

    // The startup-sequence step timer.
    private readonly ManualTimers _menuTimers = new();

    private (MainMenuEntryAutomation engine, MessageRouter router, GameCommands commands, HangupSignal signal) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GameCommands commands = new();   // defaults: "E" / "=x"
        HangupSignal signal = new();
        _menuNow = () => Now;
        MainMenuEntryAutomation engine = new(router, commands, signal, schedule: _menuTimers.Arm, now: () => _menuNow());
        engine.SetWireSender(_sent.Add);
        return (engine, router, commands, signal);
    }

    private static void DispatchMenuLine(MessageRouter router)
    {
        LineExtractor.EmittedLine line = new(
            "[E] . Enter the Realm",
            new CellAttributes[20],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    // Dispatch an in-game room display's "Obvious exits:" line — the
    // signal that releases the post-entry stat/i refresh gate.
    private static void DispatchRoomLine(MessageRouter router)
    {
        LineExtractor.EmittedLine line = new(
            "Obvious exits: north, south, east",
            new CellAttributes[40],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    [Fact]
    public void MenuLine_WhenNotArmed_DoesNothing()
    {
        // The default state — engine sitting idle, an in-game chat or
        // room description matching the pattern. Must not fire.
        var (engine, router, _, _) = Setup();
        DispatchMenuLine(router);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Arm_ThenMenuLine_SendsEntryCommand()
    {
        var (engine, router, _, _) = Setup();
        engine.Arm();

        DispatchMenuLine(router);

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("E\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Arm_ConsumesOnFirstFire_NoReFire()
    {
        // Latch must be one-shot — once the entry command goes out,
        // a subsequent menu line (in-game chat etc.) is ignored.
        var (engine, router, _, _) = Setup();
        engine.Arm();

        DispatchMenuLine(router);
        DispatchMenuLine(router);
        DispatchMenuLine(router);

        Assert.Single(_sent);
    }

    [Fact]
    public void Arm_ExpiresAfterWindow_NoFire()
    {
        // Login completed but main menu never rendered (BBS hung,
        // dropped, whatever). After the arm window, an in-game
        // matching line must NOT fire.
        var (engine, router, _, _) = Setup();
        DateTime t0 = Now;
        _menuNow = () => t0;
        engine.ArmWindow = TimeSpan.FromSeconds(15);
        engine.Arm();

        // Advance past the window.
        _menuNow = () => t0.AddSeconds(16);
        DispatchMenuLine(router);

        Assert.Empty(_sent);
    }

    [Fact]
    public void Arm_BeforeWindowExpires_StillFires()
    {
        var (engine, router, _, _) = Setup();
        DateTime t0 = Now;
        _menuNow = () => t0;
        engine.ArmWindow = TimeSpan.FromSeconds(15);
        engine.Arm();

        // Inside the window.
        _menuNow = () => t0.AddSeconds(10);
        DispatchMenuLine(router);

        Assert.Single(_sent);
    }

    [Fact]
    public void RespectsCustomEntryCommand()
    {
        var (engine, router, commands, _) = Setup();
        commands.EntryCommand = "enter";
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Equal("enter\r", Encoding.Latin1.GetString(Assert.Single(_sent)));
    }

    [Fact]
    public void BlankEntryCommand_SendsNothing()
    {
        // Defensive — a user-misconfigured blank entry command shouldn't
        // dump a lone CR on the wire when armed.
        var (engine, router, commands, _) = Setup();
        commands.EntryCommand = string.Empty;
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Empty(_sent);
    }

    [Fact]
    public void IsArmed_ReflectsLatchState()
    {
        var (engine, _, _, _) = Setup();
        DateTime t0 = Now;
        _menuNow = () => t0;

        Assert.False(engine.IsArmed);
        engine.Arm();
        Assert.True(engine.IsArmed);

        _menuNow = () => t0.Add(engine.ArmWindow + TimeSpan.FromSeconds(1));
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void MenuLineWithVariableWhitespace_StillMatches()
    {
        // Some BBSes pad the brackets differently — regex should be
        // tolerant of the spacing variants.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        LineExtractor.EmittedLine line = new(
            "[E]  .  Enter the Realm   (something extra)",
            new CellAttributes[40],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);

        Assert.Single(_sent);
    }

    [Fact]
    public void Dispose_StopsHearingPattern()
    {
        var (engine, router, _, _) = Setup();
        engine.Arm();
        engine.Dispose();

        DispatchMenuLine(router);

        Assert.Empty(_sent);
    }

    // ===== Hangup-signal suppression =====================================

    [Fact]
    public void Arm_AfterHangupSignal_StaysClosed()
    {
        // HangupSignal.SignalHangup() was called earlier in the
        // session (remote @hangup, future hang-up-if-naked /
        // hang-up-if-low-HP). The user manually reconnects, login
        // automation completes, MainWindowVM calls Arm — but the
        // entry latch must stay shut so the user reads what's on
        // screen and types `E` themselves.
        var (engine, router, _, signal) = Setup();
        signal.SignalHangup();

        engine.Arm();
        DispatchMenuLine(router);

        Assert.Empty(_sent);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Arm_AfterHangupSignal_ConsumesFlag()
    {
        // The suppression is one-shot — a SECOND connect later in the
        // session arms normally. Without this, every subsequent
        // reconnect would skip auto-entry forever after a single
        // hangup, which would surprise the user.
        var (engine, router, _, signal) = Setup();
        signal.SignalHangup();

        // First Arm — suppressed.
        engine.Arm();
        DispatchMenuLine(router);
        Assert.Empty(_sent);

        // Second Arm — normal behaviour.
        engine.Arm();
        DispatchMenuLine(router);
        Assert.Single(_sent);
    }

    // ===== KeepArmed — arm-at-login-start + refresh-on-progress ==========

    [Fact]
    public void KeepArmed_RefreshesWindow_MenuAfterOriginalExpiryStillFires()
    {
        // The reported "did not re-enter" case: auto-entry is armed at login
        // START, then a trailing holdover step stalls so LoggedIntoGame never
        // comes. Each matched step calls KeepArmed, refreshing the window, so the
        // realm-entry menu that renders AFTER the original arm window would have
        // lapsed still fires the entry command.
        var (engine, router, _, _) = Setup();
        DateTime t0 = Now;
        _menuNow = () => t0;
        engine.ArmWindow = TimeSpan.FromSeconds(15);
        engine.Arm();                                 // login start → window t0+15

        _menuNow = () => t0.AddSeconds(14); // a step lands late in the window
        engine.KeepArmed();                           // window now (t0+14)+15 = t0+29

        _menuNow = () => t0.AddSeconds(20);  // past the ORIGINAL t0+15
        DispatchMenuLine(router);

        Assert.Single(_sent);        // still fires thanks to the refresh
    }

    [Fact]
    public void KeepArmed_AfterHangupSuppressedArm_StaysClosed()
    {
        // KeepArmed must not defeat the hangup exclusion: an Arm that was
        // suppressed (health/manual hangup preceded the connect) never authorizes
        // entry, so no amount of step progress can re-open the latch.
        var (engine, router, _, signal) = Setup();
        signal.SignalHangup();

        engine.Arm();          // suppressed → not authorized
        engine.KeepArmed();    // must stay closed
        engine.KeepArmed();
        DispatchMenuLine(router);

        Assert.Empty(_sent);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void KeepArmed_WithoutPriorArm_DoesNotOpenLatch()
    {
        // Authorization comes only from Arm(). KeepArmed alone (e.g. a stray step
        // event with no login start) must never open the entry latch.
        var (engine, router, _, _) = Setup();
        engine.KeepArmed();
        DispatchMenuLine(router);
        Assert.Empty(_sent);
    }

    [Fact]
    public void KeepArmed_AfterEntryFired_DoesNotReArm()
    {
        // One auto-entry per login: once the entry command has gone out, a later
        // KeepArmed (a trailing step advancing, or LoggedIntoGame) must not
        // re-open the latch so a second menu-shaped line can't re-fire.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);
        Assert.Single(_sent);

        engine.KeepArmed();          // post-entry step progress
        DispatchMenuLine(router);
        Assert.Single(_sent);   // still just one
    }

    // ===== Realm-entry move suppression =================================

    [Fact]
    public void EntryCommand_InvokesMoveSuppressor_BeforeSend()
    {
        // The realm-entry keystroke ("E") collides with cardinal East and rides
        // the same wire-observe path as manual movement. It must be flagged to
        // the outbound-move observer as a non-move BEFORE it hits the wire — the
        // send is synchronous, so the observer sees the keystroke on its very
        // next call. Capturing the sent-count at suppressor-invoke time proves
        // the ordering (0 = fired pre-send).
        var (engine, router, _, _) = Setup();
        int suppressCalls = 0;
        int sentAtSuppress = -1;
        engine.SetMoveSuppressor(() =>
        {
            suppressCalls++;
            sentAtSuppress = _sent.Count;
        });
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Equal(1, suppressCalls);
        Assert.Equal(0, sentAtSuppress);
        Assert.Single(_sent);
    }

    [Fact]
    public void MoveSuppressor_NotInvoked_WhenEntryDoesNotFire()
    {
        // No entry send (hangup intent) → no move suppression either. A spurious
        // suppress here would silently drop the user's first genuine manual move
        // after they type the entry command themselves.
        var (engine, router, _, signal) = Setup();
        int suppressCalls = 0;
        engine.SetMoveSuppressor(() => suppressCalls++);
        signal.SignalHangup();
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Equal(0, suppressCalls);
    }

    // ===== Post-entry startup sequence (room-gated) =====================

    [Fact]
    public void EntryAlone_DoesNotFireStartup_UntilRoomSeen()
    {
        // The entry command goes out, but stat/i must NOT fire
        // until the first in-game room display confirms we landed in
        // the realm. Before the room line the startup timer is idle —
        // ticking it is a no-op.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);
        // Only the entry command on the wire so far.
        Assert.Equal("E\r", Encoding.Latin1.GetString(Assert.Single(_sent)));

        _menuTimers.RunAll();   // no room yet → nothing
        Assert.Single(_sent);
    }

    [Fact]
    public void RoomDisplayAfterEntry_ReleasesStartup_OrderedStatI()
    {
        // Entry → first "Obvious exits:" line → the two-step refresh
        // fires in order, ending with `i`.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);
        DispatchRoomLine(router);
        for (int i = 0; i < MainMenuEntryAutomation.StartupSequence.Count; i++)
            _menuTimers.RunAll();

        // First slot is the entry command, then the 2 startup steps.
        Assert.Equal(3, _sent.Count);
        Assert.Equal("E\r",    Encoding.Latin1.GetString(_sent[0]));
        Assert.Equal("stat\r", Encoding.Latin1.GetString(_sent[1]));
        Assert.Equal("i\r",    Encoding.Latin1.GetString(_sent[2]));
    }

    [Fact]
    public void RoomDisplay_BeforeEntry_DoesNotFireStartup()
    {
        // A room line with no prior entry-command send must not trip
        // the refresh — the gate requires an entry first.
        var (engine, router, _, _) = Setup();
        DispatchRoomLine(router);
        _menuTimers.RunAll();

        Assert.Empty(_sent);
    }

    [Fact]
    public void RoomGate_IsOneShot_OnlyFirstRoomReleasesStartup()
    {
        // Once the first room releases the refresh, subsequent room
        // displays (normal walking) must NOT re-fire stat/i.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);
        DispatchRoomLine(router);
        for (int i = 0; i < MainMenuEntryAutomation.StartupSequence.Count; i++)
            _menuTimers.RunAll();
        Assert.Equal(3, _sent.Count);   // E + stat/i

        // A second room display must not queue another refresh.
        DispatchRoomLine(router);
        for (int i = 0; i < MainMenuEntryAutomation.StartupSequence.Count; i++)
            _menuTimers.RunAll();
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public void StartupSequence_DoesNotOverrun()
    {
        // Once the two steps fire, ticking further must NOT send any
        // additional commands — the sequence must terminate cleanly.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);
        DispatchRoomLine(router);
        for (int i = 0; i < MainMenuEntryAutomation.StartupSequence.Count + 5; i++)
            _menuTimers.RunAll();

        // Entry + 2 startup steps, no more.
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public void HangupSuppressedEntry_DoesNotQueueStartupSequence()
    {
        // If the latch refused to arm because of a hangup intent, the
        // entry never fires, so even a room display must NOT release
        // the refresh — the whole point of the suppression is to stop
        // wire-spam while the user is in a dangerous spot.
        var (engine, router, _, signal) = Setup();
        signal.SignalHangup();
        engine.Arm();
        DispatchMenuLine(router);
        DispatchRoomLine(router);
        for (int i = 0; i < MainMenuEntryAutomation.StartupSequence.Count + 1; i++)
            _menuTimers.RunAll();

        Assert.Empty(_sent);
    }

    [Fact]
    public void Arm_ClearsStaleRoomGate_FromPriorUnroomedEntry()
    {
        // Entry fired on a prior connect but no room ever appeared
        // (MOTD hang). A fresh Arm (new login) must clear the stale
        // gate so a late room line can't fire the refresh out of band;
        // only the new login's own entry re-arms it.
        var (engine, router, _, _) = Setup();
        engine.Arm();
        DispatchMenuLine(router);   // entry fired, awaiting room
        Assert.Single(_sent);

        engine.Arm();               // fresh login resets the gate

        DispatchRoomLine(router);   // stale room line — must be ignored
        _menuTimers.RunAll();
        Assert.Single(_sent);   // still just the entry
    }
}
