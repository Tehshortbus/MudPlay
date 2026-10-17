using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the trainer-menu detection state machine. The whole point is to
// be safe against chat-noise false positives — the outbound-command
// gate is the load-bearing guard.
public sealed class TrainerMenuTrackerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (TrainerMenuTracker tracker, MessageRouter router, PartyState state) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state, now: () => Now);
        return (tracker, router, state);
    }

    private static void Dispatch(MessageRouter router, string text)
    {
        router.Dispatch(new LineExtractor.EmittedLine(
            Text: text,
            Attributes: Array.Empty<CellAttributes>(),
            Timestamp: Now,
            IsPromptLine: false));
    }

    [Fact]
    public void MarkerWithoutOutboundTrainStats_DoesNotEnterMenu()
    {
        // Chat-noise / unrelated source — "Point Cost Chart" line
        // appears without a preceding outbound train command. Must
        // NOT confirm menu entry.
        var (tracker, router, _) = Setup();
        Dispatch(router, "    Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void OutboundTrainStats_ThenMarker_EntersMenu()
    {
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    // ForceExit is the recovery path when the exit prompt is missed (AutoTrainManager's
    // CP-replay ExitGrace fallback). It must clear the keyboard hold AND fire the exit
    // event so the event-driven TrainerScreenGate re-evaluates and releases the send-gate
    // before the loop-resume's first move (report paradigm-20260815-072308).
    [Fact]
    public void ForceExit_ReleasesKeyboardHold_AndFiresInputMenuExited()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(tracker.MenuOwnsKeyboard);   // the input form owns the keyboard

        bool inputExited = false;
        tracker.InputMenuExited += () => inputExited = true;

        tracker.ForceExit("test");

        Assert.False(tracker.MenuOwnsKeyboard);   // gate can now release
        Assert.True(inputExited);                 // event fired → TrainerScreenGate re-evaluates
    }

    [Fact]
    public void ForceExit_NothingHeld_IsNoOp()
    {
        var (tracker, _, _) = Setup();
        bool fired = false;
        tracker.InputMenuExited += () => fired = true;
        tracker.MenuExited += () => fired = true;

        tracker.ForceExit("test");   // nothing armed

        Assert.False(tracker.MenuOwnsKeyboard);
        Assert.False(fired);         // no spurious events
    }

    [Fact]
    public void OutboundBareTrain_ThenMarker_EntersMenu()
    {
        // `train` alone is also a legitimate entry path on most BBSes.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void OutboundUnrelated_DoesNotArmGate()
    {
        // Arbitrary commands sent before chat shouldn't flip the gate.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("look\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ChatLineContainingMenuText_WithoutOutbound_DoesNotMatch()
    {
        // Chat line embedding the phrase — without an outbound
        // `train stats` to arm the gate, the unanchored marker is
        // ignored entirely.
        var (tracker, router, _) = Setup();
        Dispatch(router, "Foo gossips: did you see the Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ChatLineContainingMenuText_WithRecentOutbound_AcceptedTradeOff()
    {
        // Documented false-positive: within the 5 s post-`train stats`
        // window, ANY line containing "Point Cost Chart" enters menu
        // state. The marker is intentionally unanchored because the
        // menu's first terminal row contains a side-by-side layout
        // ("MAJOR MUD Character Creation" box on the left, "Point Cost
        // Chart" panel on the right) — an anchored marker missed the
        // emitted line entirely. The outbound-`train stats` gate is
        // the load-bearing defence; the residual risk of a chat
        // mention landing within 5 s of training is operationally
        // zero, and the worst-case outcome is a wasted re-invite
        // cycle (not damage).
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "Foo gossips: hey check the Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void CharCreationTrainingRow_WithoutOutbound_EntersMenu()
    {
        // Initial character creation reaches the training screen from the
        // class/race/alignment flow with no outbound `train stats`, so the
        // gate never arms. The training row carries the "MAJOR MUD
        // Character Creation" box beside the "Point Cost Chart" panel;
        // both phrases on one line confirm entry without the gate.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation      Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void CharCreationPhraseAlone_WithoutMarker_DoesNotEnterMenu()
    {
        // "Character Creation" without the "Point Cost Chart" marker never
        // reaches OnMenuMarker (the marker pattern requires the panel
        // header), so no earlier char-creation screen flips the state.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation — choose your class");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void MarkerAfterExpiringWindow_DoesNotEnterMenu()
    {
        // Mutable clock: arm, then jump past the expecting-menu window.
        DateTime t = Now;
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state, now: () => t);
        tracker.ExpectingMenuWindow = TimeSpan.FromSeconds(5);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        t = t.AddSeconds(6);
        router.Dispatch(new LineExtractor.EmittedLine(
            "    Point Cost Chart", Array.Empty<CellAttributes>(), t, false));

        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void Menu_PromptArrives_FiresMenuExitedAndClears()
    {
        var (tracker, router, _) = Setup();
        bool fired = false;
        tracker.MenuExited += () => fired = true;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);

        // The trainer screen doesn't print game prompts; the next one
        // appears only after we exit.
        Dispatch(router, "[HP=33]:");

        Assert.True(fired);
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void PromptWithoutMenu_DoesNotFireExit()
    {
        var (tracker, router, _) = Setup();
        bool fired = false;
        tracker.MenuExited += () => fired = true;

        // No menu was entered — a regular in-game prompt mustn't fire
        // the exit event.
        Dispatch(router, "[HP=33]:");
        Assert.False(fired);
    }

    [Fact]
    public void OutboundTrainStats_ArmsInputMenuEntered_BeforeAnyMarker()
    {
        // The character-mode switch is driven by the command, not the marker:
        // on a cursor-positioned full-screen menu the "Point Cost Chart" row
        // completes too late (or never inline), so arming must happen the
        // instant `train stats` goes out — before any line comes back.
        var (tracker, router, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.Equal(1, entered);
    }

    [Fact]
    public void InputMenu_SkipsCommandEchoPrompt_ThenExitsOnNextPrompt()
    {
        // Arming a menu leaves two prompts in its wake: the command's own
        // `[HP=...]:train stats` echo (which must NOT be read as an exit) and
        // the bare prompt that returns when the user leaves the box.
        var (tracker, router, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        // First prompt = the command echo — swallowed, no exit.
        Dispatch(router, "[HP=33]:");
        Assert.Equal(0, exited);

        // Second prompt = the user left the box — line-mode resumes.
        Dispatch(router, "[HP=33]:");
        Assert.Equal(1, exited);
    }

    [Fact]
    public void InputMenu_RoomDisplayReleasesKeyboard_WithoutWaitingForCommittedPrompt()
    {
        // Report paradigm-20260906-090057: after `train stats` closes, a room
        // display is emitted immediately, but the returning `[HP=…]:` prompt only
        // regen-redraws in place — it's never committed as a line until the user's
        // NEXT command's Enter. Waiting for that prompt means the command is typed
        // while the form's character-mode still holds the keyboard (sent byte-by-
        // byte, so outbound observers miss it). The room display must resume
        // line-mode on its own.
        var (tracker, router, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "[HP=213/MA=194]:Train stats");   // the command's own echo — swallowed
        Assert.Equal(0, exited);
        Assert.True(tracker.IsInputMenuActive);            // still owns the keyboard

        Dispatch(router, "Obvious exits: north");          // form closed — back in the world

        Assert.Equal(1, exited);
        Assert.False(tracker.IsInputMenuActive);           // line-mode resumes
    }

    [Fact]
    public void RoomDisplayWithoutMenu_DoesNotFireExit()
    {
        // A normal room display in play, with no trainer/creation form open, must
        // not fire the exit teardown.
        var (tracker, router, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        Dispatch(router, "Obvious exits: north");
        Assert.Equal(0, exited);
    }

    [Fact]
    public void LivePrompt_ReleasesKeyboard_AfterSkippingEcho()
    {
        // The live-wire prompt path (WirePromptScanner) sees the in-place statline
        // redraws the committed StatusLine pattern never emits. It swallows the
        // command's own echo statline, then releases on the exit prompt the form's
        // close produces — even in a dark room with no "Obvious exits" line.
        var (tracker, _, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(tracker.IsInputMenuActive);

        tracker.NotifyLivePromptObserved();   // command echo statline — swallowed
        Assert.Equal(0, exited);
        Assert.True(tracker.IsInputMenuActive);

        tracker.NotifyLivePromptObserved();   // exit prompt — line-mode resumes
        Assert.Equal(1, exited);
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void LivePromptWithoutInputMenu_DoesNotFireExit()
    {
        // Every live prompt in normal play flows through here; with no `train
        // stats` char-mode state armed it must be a no-op.
        var (tracker, _, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        tracker.NotifyLivePromptObserved();
        tracker.NotifyLivePromptObserved();
        Assert.Equal(0, exited);
    }

    [Fact]
    public void IsInputMenuActive_TracksArmThenExitLifecycle()
    {
        // The realm-independent "the stat box owns the keyboard" flag that the
        // auto-train replay trigger and the auto-cast suppression gate both read.
        // False before, true the instant `train stats` arms, false once the bare
        // exit prompt returns (the echo prompt in between doesn't clear it).
        var (tracker, router, _) = Setup();
        Assert.False(tracker.IsInputMenuActive);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(tracker.IsInputMenuActive);

        Dispatch(router, "[HP=33]:"); // command echo — swallowed, still active
        Assert.True(tracker.IsInputMenuActive);

        Dispatch(router, "[HP=33]:"); // exit prompt — box left, input released
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void IsInputMenuActive_StaysFalseForBareTrain()
    {
        // Bare `train` is line-driven, not the full-screen box, so it never
        // captures the keyboard — the gate/replay signals must stay clear.
        var (tracker, _, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void OutboundBareTrain_DoesNotArmInputMenu()
    {
        // Bare `train` opens a line-driven prompt, not the full-screen stat
        // box, so it must not flip character-mode input.
        var (tracker, router, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));

        Assert.Equal(0, entered);
    }

    [Fact]
    public void MenuOwnsKeyboard_TrueOnTrainStats_EvenWithoutMarker()
    {
        // Regression: the timed `par` poll gated on IsInTrainerMenu (marker-
        // confirmed), which never sets on Paradigm's cursor-positioned stat box
        // (the "Point Cost Chart" marker row never scrolls inline). So a `par\r`
        // leaked into the form and overwrote the Family Name (last name). The
        // realm-independent read automated sends must gate on is MenuOwnsKeyboard,
        // which the command-armed input session carries even with no marker line.
        var (tracker, _, _) = Setup();
        Assert.False(tracker.MenuOwnsKeyboard);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.True(tracker.MenuOwnsKeyboard);   // owns the keyboard...
        Assert.False(tracker.IsInTrainerMenu);   // ...though the marker never confirmed
    }

    [Fact]
    public void MenuOwnsKeyboard_TrueOnMarkerConfirmedMenu()
    {
        // The character-creation / stock-realm path confirms via the marker with
        // no command to arm input on; MenuOwnsKeyboard must fold that in too.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void MenuOwnsKeyboard_ClearsOnMenuExit()
    {
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(tracker.MenuOwnsKeyboard);

        Dispatch(router, "[HP=33]:"); // command echo — swallowed
        Dispatch(router, "[HP=33]:"); // exit prompt — screen left
        Assert.False(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void InputMenu_ReOpen_ArmsAgainAfterExit()
    {
        // A fresh `train stats` after the box was left re-arms input mode.
        var (tracker, router, _) = Setup();
        int entered = 0, exited = 0;
        tracker.InputMenuEntered += () => entered++;
        tracker.InputMenuExited += () => exited++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "[HP=33]:"); // echo skip
        Dispatch(router, "[HP=33]:"); // exit
        Assert.Equal(1, entered);
        Assert.Equal(1, exited);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.Equal(2, entered);
    }

    [Fact]
    public void InputMenu_DoubleTrainStatsBeforePrompt_EntersOnce()
    {
        // The observed Paradigm sequence sends `train stats` twice before the
        // box settles; InputMenuEntered fires once, and both echoes are
        // swallowed so the exit still lands on the real bare prompt.
        var (tracker, router, _) = Setup();
        int entered = 0, exited = 0;
        tracker.InputMenuEntered += () => entered++;
        tracker.InputMenuExited += () => exited++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.Equal(1, entered);

        // Each `train stats` re-armed the echo skip, so one prompt is
        // swallowed and the next is the exit.
        Dispatch(router, "[HP=33]:");
        Assert.Equal(0, exited);
        Dispatch(router, "[HP=33]:");
        Assert.Equal(1, exited);
    }

    [Fact]
    public void CharCreationAbbreviatedRow_WithoutOutbound_EntersMenu()
    {
        // ParaMUD abbreviates the box title to "Char. Creation" (not the stock
        // "Character Creation"); the emitted-line marker path must accept it too,
        // or the party-roster excursion detection never confirms on that realm.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  / P A R A D I G M   Char. Creation /   Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ObserveScreen_ParadigmCreationBox_ArmsInputMode()
    {
        // Live-screen path: ParaMUD paints the creation stat box with cursor
        // positioning, so the "Point Cost Chart" marker row never emits inline
        // and the arrows stay bound to history recall. Scanning the live screen
        // (with the abbreviated "Char. Creation" title) must arm character mode.
        var (tracker, _, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveScreen(
            "   / P A R A D I G M      Char. Creation /    \\  +-    Point Cost Chart    -+\n"
            + "  | Given Name   Ermias   |   1st 10 points: 1 CP each |\n");

        Assert.Equal(1, entered);
        Assert.True(tracker.IsInputMenuActive);
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void ObserveScreen_StockCreationBox_ArmsInputMode()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("  MAJOR MUD Character Creation      Point Cost Chart\n");
        Assert.True(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_MarkerWithoutCreationTitle_DoesNotArm()
    {
        // The in-game `train stats` box shows "Point Cost Chart" with no creation
        // title; that path arms off the outbound command, not the screen scan.
        // The live-screen detector must ignore it — and any chat mention.
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("  Ye Olde Trainer      Point Cost Chart\n");
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_NoMarker_DoesNotArm()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("You are standing in a field.\nExits: north, south.\n");
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_AlreadyArmed_DoesNotReFire()
    {
        // Fired once per feed while the box is up; the early-out keeps it from
        // re-invoking InputMenuEntered on every subsequent feed.
        var (tracker, _, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;
        const string box = "  MAJOR MUD Character Creation      Point Cost Chart\n";
        tracker.ObserveScreen(box);
        tracker.ObserveScreen(box);
        Assert.Equal(1, entered);
    }

    [Fact]
    public void EntryToMenu_SnapshotsNonSelfRoster()
    {
        var (tracker, router, state) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        state.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        state.Members.Add(new PartyMember { Name = "Helper" });

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");

        Assert.Equal(2, tracker.RosterAtMenuEntry.Count);
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);
        Assert.Contains("Helper", tracker.RosterAtMenuEntry);
        Assert.DoesNotContain("MudPlay", tracker.RosterAtMenuEntry);
    }
}
