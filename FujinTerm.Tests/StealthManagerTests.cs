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
    public void SneakInitiate_TransitionsToAttempting()
    {
        using Harness h = new();
        h.Feed("Attempting to sneak...");

        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
        Assert.False(h.State.IsSneaking);     // not confirmed yet
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

    // ----- transition event -------------------------------------------

    [Fact]
    public void StateChanged_FiresOnEveryTransition()
    {
        using Harness h = new();
        h.Feed("Attempting to sneak...");
        h.Feed("Sneaking...");
        h.Feed("You make a sound as you enter the room!");

        Assert.Equal(3, h.Transitions.Count);
        Assert.Equal((StealthState.Idle, StealthState.AttemptingSneak), h.Transitions[0]);
        Assert.Equal((StealthState.AttemptingSneak, StealthState.Sneaking), h.Transitions[1]);
        Assert.Equal((StealthState.Sneaking, StealthState.Idle), h.Transitions[2]);
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

        public AutoHarness()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, Log);
            Stealth.SetWireSender(b => Sent.Add(b));
            Stealth.SetAutoToggles(
                () => AutoSneakOn,
                () => AutoHideOn);
        }

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
        Assert.Equal("sneak", h.LastSent());
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

    [Fact]
    public void AutoHide_NoteIdle_SendsHide()
    {
        using AutoHarness h = new() { AutoHideOn = true };

        h.Stealth.NoteIdleOpportunity();

        Assert.Single(h.Sent);
        Assert.Equal("hide", h.LastSent());
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
