using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the trainer-menu detection state machine. The whole point is to
/// be safe against chat-noise false positives — the outbound-command
/// gate is the load-bearing guard.
/// </summary>
public sealed class TrainerMenuTrackerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (TrainerMenuTracker tracker, MessageRouter router, PartyState state) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state)
        {
            NowProvider = () => Now,
        };
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
        TrainerMenuTracker tracker = new(router, state) { NowProvider = () => t };
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
    public void EntryToMenu_SnapshotsNonSelfRoster()
    {
        var (tracker, router, state) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        state.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        state.Members.Add(new PartyMember { Name = "Helper" });

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");

        Assert.Equal(2, tracker.RosterAtMenuEntry.Count);
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);
        Assert.Contains("Helper", tracker.RosterAtMenuEntry);
        Assert.DoesNotContain("Fujin", tracker.RosterAtMenuEntry);
    }
}
