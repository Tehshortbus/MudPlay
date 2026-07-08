using FujinTerm.Game;
using FujinTerm.Game.Stealth;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.F — <see cref="StealthManager"/> line-driven FSM, silent-
/// loss detection on room change, and hide-via-explicit-mark API.
/// </summary>
public sealed class StealthManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        public List<(StealthState From, StealthState To)> Transitions { get; } = new();
        public int SilentLossCount { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, Log);
            Stealth.StateChanged += (from, to) => Transitions.Add((from, to));
            Stealth.SilentSneakLost += () => SilentLossCount++;
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose() => Stealth.Dispose();
    }

    // ----- sneak FSM happy path ---------------------------------------

    [Fact]
    public void CleanSneakInitiate_EstablishesSneaking()
    {
        // A clean `Attempting to sneak...` (no failure suffix) is the
        // server ACK that the sneak took — we're armed to move, so the
        // FSM establishes Sneaking immediately.
        using Harness h = new();
        h.Feed("Attempting to sneak...");

        Assert.Equal(StealthState.Sneaking, h.Stealth.State);
        Assert.True(h.State.IsSneaking);
    }

    [Fact]
    public void Sneaking_ConfirmsAndSetsFlag()
    {
        using Harness h = new();
        h.Feed("Attempting to sneak...");
        h.Feed("Sneaking...");

        Assert.Equal(StealthState.Sneaking, h.Stealth.State);
        Assert.True(h.State.IsSneaking);
    }

    [Fact]
    public void Sneaking_Direct_WithoutInitiate_Works()
    {
        // Sneaking can be reported by the server on room entry even
        // without our outbound `sneak` command being observed (e.g.
        // we joined an already-sneaking session). State should still
        // converge.
        using Harness h = new();
        h.Feed("Sneaking...");

        Assert.Equal(StealthState.Sneaking, h.Stealth.State);
        Assert.True(h.State.IsSneaking);
    }

    [Fact]
    public void NotSneaking_LoudLoss_ClearsFlag()
    {
        using Harness h = new();
        h.Feed("Sneaking...");
        Assert.True(h.State.IsSneaking);

        h.Feed("You make a sound as you enter the room!");

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsSneaking);
    }

    [Fact]
    public void SneakFailed_TransitionsToFailed()
    {
        using Harness h = new();
        h.Feed("Attempting to sneak...You don't think you're sneaking.");

        Assert.Equal(StealthState.Failed, h.Stealth.State);
        Assert.False(h.State.IsSneaking);
    }

    [Fact]
    public void CantSneak_TransitionsToFailed()
    {
        using Harness h = new();
        h.Feed("You may not sneak right now!");

        Assert.Equal(StealthState.Failed, h.Stealth.State);
    }

    // ----- silent-loss detection --------------------------------------

    [Fact]
    public void NoteRoomChanged_WithoutConfirmThisRoom_IsSilentLoss()
    {
        // Two-shot model: the FIRST NoteRoomChanged ends the room that
        // produced the confirm; the SECOND ends a room that didn't
        // (so silent loss is detected by the second).
        using Harness h = new();
        h.Feed("Sneaking...");          // confirm room 1
        h.Stealth.NoteRoomChanged();    // end room 1 — confirmed, no loss
        Assert.True(h.State.IsSneaking);

        // No new Sneaking... line in room 2.
        h.Stealth.NoteRoomChanged();    // end room 2 — no confirm → silent loss

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsSneaking);
        Assert.Equal(1, h.SilentLossCount);
    }

    [Fact]
    public void NoteRoomChanged_WithConfirmEachRoom_KeepsSneaking()
    {
        using Harness h = new();
        h.Feed("Sneaking...");          // confirm room 1
        h.Stealth.NoteRoomChanged();    // end room 1
        h.Feed("Sneaking...");          // confirm room 2
        h.Stealth.NoteRoomChanged();    // end room 2

        Assert.Equal(StealthState.Sneaking, h.Stealth.State);
        Assert.True(h.State.IsSneaking);
        Assert.Equal(0, h.SilentLossCount);
    }

    [Fact]
    public void NoteRoomChanged_WhenNotSneaking_NoEvent()
    {
        using Harness h = new();
        h.Stealth.NoteRoomChanged();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.Equal(0, h.SilentLossCount);
    }

    // ----- hide -------------------------------------------------------

    [Fact]
    public void NoteHideConfirmed_SetsHiddenFlag()
    {
        using Harness h = new();
        h.Stealth.NoteHideConfirmed();

        Assert.Equal(StealthState.Hidden, h.Stealth.State);
        Assert.True(h.State.IsHidden);
    }

    [Fact]
    public void NoteHideBroken_ClearsFlag()
    {
        using Harness h = new();
        h.Stealth.NoteHideConfirmed();
        h.Stealth.NoteHideBroken();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsHidden);
    }

    [Fact]
    public void HideInitiate_OptimisticallyEstablishesHidden()
    {
        // Hide success isn't self-observable: a bare "Attempting to hide..." only
        // proves a check ran. We treat it as optimistically hidden so the backstab
        // opener arms; the surprise-round resolver confirms or denies later.
        using Harness h = new();
        h.Feed("Attempting to hide...");

        Assert.Equal(StealthState.Hidden, h.Stealth.State);
        Assert.True(h.State.IsHidden);
        Assert.True(h.Stealth.IsStealthed);
    }

    [Fact]
    public void HideFailed_DropsOptimisticHidden()
    {
        // The one ground-truth failure line clears the optimistic hidden state.
        using Harness h = new();
        h.Feed("Attempting to hide...");
        Assert.True(h.State.IsHidden);

        h.Feed("Attempting to hide...You don't think you are hidden.");

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsHidden);
    }

    [Fact]
    public void HideInitiate_DoesNotMatchFailureLine()
    {
        // The $-anchored initiate pattern must not fire on the suffixed failure
        // line — otherwise a failed hide would latch optimistic-hidden.
        using Harness h = new();
        h.Feed("Attempting to hide...You don't think you are hidden.");

        Assert.False(h.State.IsHidden);
        Assert.NotEqual(StealthState.Hidden, h.Stealth.State);
    }

    [Fact]
    public void Hidden_MoveBreaksHide()
    {
        // You can't move while hidden, so a confirmed room change drops the
        // optimistic hidden state.
        using Harness h = new();
        h.Feed("Attempting to hide...");
        Assert.True(h.State.IsHidden);

        h.Stealth.NoteRoomChanged();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsHidden);
    }

    // ----- transition event -------------------------------------------

    [Fact]
    public void StateChanged_FiresOnEveryTransition()
    {
        using Harness h = new();
        h.Feed("Attempting to sneak...");   // clean ACK → establishes Sneaking
        h.Feed("Sneaking...");              // post-move reconfirm — idempotent
        h.Feed("You make a sound as you enter the room!");

        Assert.Equal(2, h.Transitions.Count);
        Assert.Equal((StealthState.Idle, StealthState.Sneaking), h.Transitions[0]);
        Assert.Equal((StealthState.Sneaking, StealthState.Idle), h.Transitions[1]);
    }

    [Fact]
    public void StateChanged_NoSpuriousFireOnRedundantEmit()
    {
        // Two consecutive Sneaking... lines (room 1 + room 2) should
        // be one state change, not two.
        using Harness h = new();
        h.Feed("Sneaking...");
        h.Feed("Sneaking...");

        Assert.Single(h.Transitions);
    }

    // ----- Auto-sneak / auto-hide engines (Cluster 3) ----------------

    private sealed class AutoHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        public List<byte[]> Sent { get; } = new();
        public bool AutoSneakOn { get; set; }
        public bool AutoHideOn { get; set; }
        public bool InParty { get; set; }

        public AutoHarness()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, Log);
            Stealth.SetWireSender(b => Sent.Add(b));
            Stealth.SetAutoToggles(
                () => AutoSneakOn,
                () => AutoHideOn);
            Stealth.SetPartyCheck(() => InParty);
        }

        public void Feed(string line) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));

        public string LastSent() =>
            Sent.Count == 0
                ? string.Empty
                : System.Text.Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose() => Stealth.Dispose();
    }

    [Fact]
    public void AutoSneak_OnRoomChange_SendsSneak()
    {
        using AutoHarness h = new() { AutoSneakOn = true };

        h.Stealth.NoteRoomChanged();

        Assert.Single(h.Sent);
        Assert.Equal("sn", h.LastSent());
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
    }

    [Fact]
    public void AutoSneak_OnSoftRejection_ResendsSneak()
    {
        // `Attempting to sneak...You don't think you're sneaking.` is a
        // soft rejection — under auto-sneak we resend `sn` and return to
        // AttemptingSneak to await a clean ACK.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.NoteRoomChanged();        // first `sn`
        Assert.Single(h.Sent);

        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "Attempting to sneak...You don't think you're sneaking.",
            Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("sn", h.LastSent());
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
    }

    [Fact]
    public void AutoSneak_AlreadySneaking_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "Sneaking...", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        h.Stealth.NoteRoomChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoSneak_InCombat_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.State.InCombat = true;

        h.Stealth.NoteRoomChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoSneak_Off_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = false };

        h.Stealth.NoteRoomChanged();

        Assert.Empty(h.Sent);
    }

    // ----- pre-move stealth hook (PR 4.b) ----------------------------

    [Fact]
    public void RequestPreMoveStealth_FromIdle_SendsSneak()
    {
        // The proactive pre-move path: the walker/loop runner calls this
        // immediately before a move so `sn` is the last command before
        // the move bytes — the move itself is sneaked.
        using AutoHarness h = new() { AutoSneakOn = true };

        h.Stealth.RequestPreMoveStealth();

        Assert.Single(h.Sent);
        Assert.Equal("sn", h.LastSent());
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
    }

    [Fact]
    public void RequestPreMoveStealth_AlreadySneaking_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "Sneaking...", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        h.Stealth.RequestPreMoveStealth();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RequestPreMoveStealth_MidAttempt_NoDoubleSend()
    {
        // Settled-state guard: a pre-move request while already in
        // AttemptingSneak (e.g. the reactive room-change path already
        // fired) must not send a second `sn`.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.RequestPreMoveStealth();
        Assert.Single(h.Sent);

        h.Stealth.RequestPreMoveStealth();

        Assert.Single(h.Sent);
    }

    [Fact]
    public void RequestPreMoveStealth_NpcPresent_NoSend()
    {
        // Decision #1: any NPC in the room blocks sneak, so the engine
        // suppresses the doomed `sn` rather than firing it.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.SetSneakBlockCheck(() => true);

        h.Stealth.RequestPreMoveStealth();

        Assert.Empty(h.Sent);
        Assert.Equal(StealthState.Idle, h.Stealth.State);
    }

    [Fact]
    public void RequestPreMoveStealth_NpcClears_Sends()
    {
        // Predicate flips to false (NPC left / room clear) → sneak fires.
        using AutoHarness h = new() { AutoSneakOn = true };
        bool blocked = true;
        h.Stealth.SetSneakBlockCheck(() => blocked);
        h.Stealth.RequestPreMoveStealth();
        Assert.Empty(h.Sent);

        blocked = false;
        h.Stealth.RequestPreMoveStealth();

        Assert.Single(h.Sent);
        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void RequestPreMoveStealth_InCombat_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.State.InCombat = true;

        h.Stealth.RequestPreMoveStealth();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RequestPreMoveStealth_Off_NoSend()
    {
        using AutoHarness h = new() { AutoSneakOn = false };

        h.Stealth.RequestPreMoveStealth();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoHide_NoteIdle_SendsHide()
    {
        using AutoHarness h = new() { AutoHideOn = true };

        h.Stealth.NoteIdleOpportunity();

        Assert.Single(h.Sent);
        Assert.Equal("hid", h.LastSent());
    }

    [Fact]
    public void AutoHide_AlreadyHidden_NoSend()
    {
        using AutoHarness h = new() { AutoHideOn = true };
        h.Stealth.NoteHideConfirmed();

        h.Stealth.NoteIdleOpportunity();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoHide_InCombat_NoSend()
    {
        using AutoHarness h = new() { AutoHideOn = true };
        h.State.InCombat = true;

        h.Stealth.NoteIdleOpportunity();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoHide_InParty_Suppressed()
    {
        // A hidden member falls off the room's Also-here line and can't be
        // single-target-healed/buffed by the party until revealed, so auto-hide
        // must not fire while in a party.
        using AutoHarness h = new() { AutoHideOn = true, InParty = true };

        h.Stealth.NoteIdleOpportunity();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AutoHide_AttemptInFlight_NoResend()
    {
        // After the first `hid` the FSM sits in AttemptingHide waiting on the
        // ambiguous initiate line; a second idle opportunity must not stack a
        // duplicate attempt.
        using AutoHarness h = new() { AutoHideOn = true };
        h.Stealth.NoteIdleOpportunity();
        Assert.Single(h.Sent);
        Assert.Equal(StealthState.AttemptingHide, h.Stealth.State);

        h.Stealth.NoteIdleOpportunity();

        Assert.Single(h.Sent);
    }

    [Fact]
    public void AutoHide_InitiateLine_LatchesHidden()
    {
        // End-to-end: auto-hide sends `hid`, and the server's bare initiate line
        // optimistically latches Hidden (which is what re-arms the backstab
        // opener via StateChanged).
        using AutoHarness h = new() { AutoHideOn = true };
        h.Stealth.NoteIdleOpportunity();
        h.Feed("Attempting to hide...");

        Assert.Equal(StealthState.Hidden, h.Stealth.State);
        Assert.True(h.State.IsHidden);
    }

    [Fact]
    public void IsStealthed_TrueWhenSneaking()
    {
        using AutoHarness h = new();
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "Sneaking...", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        Assert.True(h.Stealth.IsStealthed);
    }

    [Fact]
    public void IsStealthed_TrueWhenHidden()
    {
        using AutoHarness h = new();
        h.Stealth.NoteHideConfirmed();

        Assert.True(h.Stealth.IsStealthed);
    }

    [Fact]
    public void IsStealthed_FalseWhenIdle()
    {
        using AutoHarness h = new();
        Assert.False(h.Stealth.IsStealthed);
    }
}
