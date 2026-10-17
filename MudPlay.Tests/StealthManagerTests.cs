using MudPlay.Game;
using MudPlay.Game.Stealth;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// StealthManager line-driven FSM, silent-
// loss detection on room change, and hide-via-explicit-mark API.
public sealed class StealthManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        // The clock the Stealth reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> StealthNow { get; set; } = () => DateTimeOffset.UtcNow;
        // The manager's re-sneak, cooldown-retry, answer and held-cast timers.
        public ManualTimers Timers { get; } = new();
        public List<(StealthState From, StealthState To)> Transitions { get; } = new();
        public int SilentLossCount { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, Log, now: () => StealthNow(), schedule: Timers.Arm);
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
    public void SneakBrokeOnEntry_ReportsOnce_WithinTheWindow()
    {
        using Harness h = new();
        DateTimeOffset now = new(2026, 9, 26, 22, 22, 6, TimeSpan.Zero);
        h.StealthNow = () => now;
        h.Feed("Sneaking...");
        h.Feed("You make a sound as you enter the room!");

        Assert.True(h.Stealth.TakeSneakBrokeOnEntry());
        Assert.False(h.Stealth.TakeSneakBrokeOnEntry());   // taken

        h.Feed("Sneaking...");
        h.Feed("You make a sound as you enter the room!");
        now = now.AddSeconds(10);                           // stale by the next room
        Assert.False(h.Stealth.TakeSneakBrokeOnEntry());
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

    // ----- sneak entries (Session Stats sneak %) ----------------------

    [Fact]
    public void SneakEntry_HeldOnConfirm_LostOnLoudOrSilentEntry()
    {
        using Harness h = new();
        List<bool> entries = new();
        h.Stealth.SneakEntry += entries.Add;

        h.Feed("Sneaking...");                               // room 1 held
        h.Stealth.NoteRoomChanged();
        h.Stealth.NoteRoomChanged();                         // room 2: no confirm → silent loss
        h.Feed("Attempting to sneak...");
        h.Feed("You make a sound as you enter the room!");   // room 3: loud loss
        h.Stealth.NoteRoomChanged();                         // already Idle: not a second loss

        Assert.Equal(new[] { true, false, false }, entries);
    }

    [Fact]
    public void SneakEntry_NotRaised_WhenNotSneaking()
    {
        using Harness h = new();
        List<bool> entries = new();
        h.Stealth.SneakEntry += entries.Add;

        h.Stealth.NoteRoomChanged();
        h.Feed("You make a sound as you enter the room!");   // FSM wasn't sneaking

        Assert.Empty(entries);
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
    public void HideFailed_StockSpacedSuffix_DropsOptimisticHidden()
    {
        // Stock prints a space before the failure suffix.
        using Harness h = new();
        h.Feed("Attempting to hide...");
        h.Feed("Attempting to hide... You don't think you are hidden.");

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

    // ----- Auto-sneak / auto-hide engines ----------------------------

    private sealed class AutoHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        // The clock the Stealth reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> StealthNow { get; set; } = () => DateTimeOffset.UtcNow;
        // The manager's re-sneak, cooldown-retry, answer and held-cast timers.
        public ManualTimers Timers { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public bool AutoSneakOn { get; set; }
        public bool AutoHideOn { get; set; }
        public bool InParty { get; set; }

        public AutoHarness()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, Log, now: () => StealthNow(), schedule: Timers.Arm);
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

    // ----- Re-sneak in place when nothing is driving the moves ---------------
    // Report paradigm-20261002-004148: walking by hand through a tunnel with a monster
    // in every room, a sneak broken by a typed `sea` or spent on a fight never came
    // back, because the only re-sneak left was arriving in a room with no NPC.

    private static AutoHarness ManualPlay(bool engineDriving = false)
    {
        AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.SetEngineDrivingCheck(() => engineDriving);
        return h;
    }

    [Fact]
    public void ManualPlay_SneakBrokenByACommand_IsReTakenInPlace_ABeatLater()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.NoteRoomChanged();              // arrives, sneaks
        h.Sent.Clear();

        h.Stealth.NoteSneakBroken("'sea'");
        Assert.Empty(h.Sent);                     // not at once: a burst may still be going out

        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);       // the pause has passed
        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void ManualPlay_FightEndsWithNoSneakHeld_SneaksInTheClearedRoom()
    {
        // The report's case: entered seen (an NPC was there, so no sneak), fought, won.
        using AutoHarness h = ManualPlay();

        h.Stealth.NoteCombatEndedStealthReset();
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);

        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void ManualPlay_NpcStillInTheRoom_DoesNotBurnASneak()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.SetSneakBlockCheck(() => true);

        h.Stealth.NoteCombatEndedStealthReset();
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void EngineDriving_LeavesTheReSneakToItsPreMoveHook()
    {
        using AutoHarness h = ManualPlay(engineDriving: true);
        h.Stealth.NoteRoomChanged();
        h.Sent.Clear();

        h.Stealth.NoteSneakBroken("'sea'");
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);

        Assert.Empty(h.Sent);
    }

    // The scrollback shows `sea` typed about a second before the next `e`: too soon
    // for the in-place re-sneak to be sure of landing first. A typed move sneaks ahead
    // of itself, the way an engine's step does.
    [Fact]
    public void ManualPlay_TypedMove_SneaksAheadOfTheStep()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.NoteTypedMove();
        Assert.Equal("sn", h.LastSent());

        h.Sent.Clear();
        h.Stealth.NoteTypedMove();                // already attempting: no second sn
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void EngineDriving_TypedMoveHook_DoesNothing()
    {
        using AutoHarness h = ManualPlay(engineDriving: true);
        h.Stealth.NoteTypedMove();
        Assert.Empty(h.Sent);
    }

    // `sn` then `sea`: the game takes the sneak and then ends it, but the answer to
    // the `sn` arrives after we noted the `sea`. It must not read as sneaking.
    [Fact]
    public void SneakAnswer_AfterALaterSneakEndingCommand_IsNotReadAsSneaking()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.NoteRoomChanged();              // sn out, unanswered
        h.Stealth.NoteSneakBroken("'sea'");       // sea sent after it

        h.Feed("Attempting to sneak...");         // the answer to that first sn
        Assert.False(h.State.IsSneaking);
        Assert.False(h.Stealth.IsSneaking);

        // The next sn's answer counts again.
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);
        h.Feed("Attempting to sneak...");
        Assert.True(h.Stealth.IsSneaking);
    }

    // Stock (user, 2026-10-02): a rest typed by hand isn't broken to sneak. The sneak
    // waits for the rest to reach rest-max and goes out then.
    [Fact]
    public void ManualPlay_RestShortOfRestMax_IsNotBrokenToSneak()
    {
        using AutoHarness h = ManualPlay();
        bool resting = true;
        h.Stealth.SetIdleRestChecks(restUnderWay: () => resting, restKeepsSneak: () => false);

        h.Stealth.NoteCombatEndedStealthReset();
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);
        Assert.Empty(h.Sent);

        resting = false;                          // topped off
        h.Timers.RunDue(StealthManager.InPlaceRestRecheck);
        Assert.Equal("sn", h.LastSent());
    }

    // Paradigm ShadowRest (user, 2026-10-02): an `sn` doesn't break the rest and the
    // rest keeps the sneak, so the sneak goes out over a rest, hand-typed or not.
    [Fact]
    public void ManualPlay_ShadowRest_SneaksOverARest()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.SetIdleRestChecks(restUnderWay: () => true, restKeepsSneak: () => true);

        h.Stealth.NoteCombatEndedStealthReset();
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);
        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void ManualPlay_SwitchingAutoSneakOn_SneaksWhereWeStand()
    {
        using AutoHarness h = ManualPlay();
        h.Stealth.NoteAutoSneakSwitchedOn();
        h.Timers.RunDue(StealthManager.InPlaceReSneakDelay);
        Assert.Equal("sn", h.LastSent());

        using AutoHarness off = ManualPlay();
        off.AutoSneakOn = false;
        off.Stealth.NoteCombatEndedStealthReset();
        off.Timers.RunDue(StealthManager.InPlaceReSneakDelay);
        Assert.Empty(off.Sent);
    }

    // ----- Followed: monsters coming in right behind us ------------------------
    // Backscroll 2026-10-04 14:50: a sprint whose sneak broke picked up two followers.
    // They arrived a few ms after each room display, so every step sent sn (before the
    // step and again on arrival), drew "You may not sneak right now!", and the cooldown
    // hold stood us still under attack.

    private static (AutoHarness H, Game.Map.MovementCoordinator Coord, Func<DateTimeOffset> Clock, Action<double> Advance) Chase()
    {
        AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        DateTimeOffset now = new(2026, 10, 4, 14, 50, 0, TimeSpan.Zero);
        h.StealthNow = () => now;
        return (h, coord, () => now, ms => now = now.AddMilliseconds(ms));
    }

    [Fact]
    public void Followed_NoSnAndNoHold_UntilWeLeaveARoomNobodyFollowedUsInto()
    {
        var (h, coord, _, advance) = Chase();
        using AutoHarness _h = h;

        h.Stealth.NoteRoomChanged();                     // sneak lost on the way in: arrival sn
        advance(5);
        h.Stealth.NoteMonsterArrival();                  // the zombie walks in behind us
        h.Feed("You may not sneak right now!");
        Assert.True(h.Stealth.IsFollowed);
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));
        h.Sent.Clear();

        advance(1200);
        Assert.True(h.Stealth.ReadyToMoveSneaking());    // step out unsneaked, no sn
        h.Stealth.NoteRoomChanged();                     // next room
        advance(10);
        h.Stealth.NoteMonsterArrival();                  // still behind us
        advance(1200);
        Assert.True(h.Stealth.ReadyToMoveSneaking());
        h.Stealth.NoteRoomChanged();                     // nothing follows into this one
        Assert.Empty(h.Sent);

        advance(1200);
        h.Stealth.NoteRoomChanged();                     // left a room nobody followed us into
        Assert.False(h.Stealth.IsFollowed);
        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void AMonsterWanderingInLater_IsNotAFollower()
    {
        var (h, _, _, advance) = Chase();
        using AutoHarness _h = h;

        h.Stealth.NoteRoomChanged();
        advance(3000);
        h.Stealth.NoteMonsterArrival();

        Assert.False(h.Stealth.IsFollowed);
    }

    [Fact]
    public void Followed_AFightOverInTheRoom_LiftsIt_ButAFreshRoomDisplayDoesNot()
    {
        var (h, _, _, advance) = Chase();
        using AutoHarness _h = h;
        h.Stealth.NoteRoomChanged();
        advance(5);
        h.Stealth.NoteMonsterArrival();

        h.Stealth.NoteCombatEndedStealthReset();         // just the new room reading clear
        Assert.True(h.Stealth.IsFollowed);

        advance(8000);
        h.Stealth.NoteCombatEndedStealthReset();         // fought it out here
        Assert.False(h.Stealth.IsFollowed);
    }

    // Report paradigm-20261004-180252: a backstab killed the follower half a second
    // after we walked in, the flag stayed, and the step out went unsneaked.
    [Fact]
    public void Followed_AKillRightAfterArriving_LiftsIt()
    {
        var (h, _, _, advance) = Chase();
        using AutoHarness _h = h;
        h.Stealth.NoteRoomChanged();
        advance(5);
        h.Stealth.NoteMonsterArrival();

        advance(500);
        h.Stealth.NoteCombatEndedStealthReset(roomClearedByKill: true);

        Assert.False(h.Stealth.IsFollowed);
    }

    // Report paradigm-20260926-233357: "You may not sneak right now!" is a post-combat
    // cooldown. Hold the route and retry sn instead of walking on unsneaked.
    [Fact]
    public void SneakCooldown_HoldsMovement_RetriesUntilItTakes()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        h.Stealth.NoteRoomChanged();                     // sn on arrival
        h.Feed("You may not sneak right now!");
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));

        h.Sent.Clear();
        h.Timers.RunDue(StealthManager.SneakCooldownRetry);     // 2s later
        Assert.Equal("sn", h.LastSent());

        h.Feed("Attempting to sneak...");                 // it took
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));
    }

    // Report paradigm-20260927-003304: an arrival sn must hold movement until the game
    // answers it, so a resuming engine can't walk on ahead of the answer.
    [Fact]
    public void ArrivalSneak_HoldsMovementUntilTheAnswer()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        h.Stealth.NoteRoomChanged();                     // sn on arrival
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));

        h.Feed("Attempting to sneak...");                 // answered — it took
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
    }

    [Fact]
    public void ArrivalSneak_Refused_HandsOverToTheCooldownHold()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        h.Stealth.NoteRoomChanged();
        h.Feed("You may not sneak right now!");

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));
    }

    [Fact]
    public void PreMoveSneak_DoesNotHold()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        h.Stealth.RequestPreMoveStealth();

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
    }

    [Fact]
    public void SneakCooldown_GivesUpAfterTheCap()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        DateTimeOffset now = new(2026, 9, 26, 23, 33, 53, TimeSpan.Zero);
        h.StealthNow = () => now;

        h.Stealth.NoteRoomChanged();
        h.Feed("You may not sneak right now!");
        now = now.AddSeconds(16);
        h.Timers.RunDue(StealthManager.SneakCooldownRetry);

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));
    }

    [Fact]
    public void SneakCooldown_AutoSneakOff_NoHold()
    {
        using AutoHarness h = new() { AutoSneakOn = false };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        h.Feed("You may not sneak right now!");

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCooldownGate));
    }

    // Report paradigm-20260927-011624: a loud entry into an empty room was still "the
    // sneak broke" two seconds later, after we'd snuck cleanly into the next one.
    [Fact]
    public void SneakBrokeOnEntry_ClearedByALaterSneak()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");
        h.Feed("You make a sound as you enter the room!");
        h.Feed("Attempting to sneak...");                  // re-snuck
        h.Feed("Sneaking...");                             // entered the next room unseen

        Assert.False(h.Stealth.TakeSneakBrokeOnEntry());
    }

    // Report paradigm-20260927-014325: `sn` acknowledged, walked in, no "Sneaking..." —
    // a silent break; the backstab must not count on it.
    [Fact]
    public void SneakedMove_WithoutArrivalConfirm_IsASilentBreak()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Attempting to sneak...");                  // acknowledged in the old room
        h.Stealth.RequestPreMoveStealth();                 // the move goes out

        Assert.False(h.Stealth.IsStealthedHere);           // the new room hasn't confirmed
        Assert.True(h.Stealth.TakeSneakBrokeOnEntry());
    }

    [Fact]
    public void SneakedMove_ArrivalConfirmed_IsStealthedHere()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Attempting to sneak...");
        h.Stealth.RequestPreMoveStealth();
        h.Feed("Sneaking...");                             // the new room confirms

        Assert.True(h.Stealth.IsStealthedHere);
        Assert.False(h.Stealth.TakeSneakBrokeOnEntry());
    }

    // Report paradigm-20260927-013820: the loop's first step went out with the `sn`
    // and walked in seen when it was refused. The step now waits for the answer.
    [Fact]
    public void ReadyToMoveSneaking_NotSneaking_SneaksAndHolds()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);

        Assert.False(h.Stealth.ReadyToMoveSneaking());
        Assert.Equal("sn", h.LastSent());
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));

        h.Feed("Attempting to sneak...");
        Assert.True(h.Stealth.ReadyToMoveSneaking());
    }

    // Report paradigm-20260928-165844: a buff sneak keeping held on the way goes out in
    // the first NPC-free room — the step and the arrival `sn` wait, and the re-sneak
    // after the cast sends us on.
    [Fact]
    public void HeldCast_ClearRoom_HoldsStepUntilTheCastGoesOut()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        bool castDue = true;
        h.Stealth.SetHeldCastCheck(() => castDue);

        h.Stealth.NoteRoomChanged();
        Assert.Empty(h.Sent);                               // arrival sn waits for the cast
        Assert.False(h.Stealth.ReadyToMoveSneaking());
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCastGate));

        castDue = false;
        h.Stealth.ReSneakAfterCast();                       // CastFired
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCastGate));
        Assert.Equal("sn", h.LastSent());
        h.Feed("Attempting to sneak...");
        Assert.True(h.Stealth.ReadyToMoveSneaking());
    }

    [Fact]
    public void HeldCast_NpcRoom_DoesNotHold()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        h.Stealth.SetHeldCastCheck(() => true);
        h.Stealth.SetSneakBlockCheck(() => true);

        Assert.True(h.Stealth.ReadyToMoveSneaking());
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCastGate));
    }

    // Report paradigm-20260927-014032: the answer timer ran out on retry 2 and the loop
    // stepped in seen. Retries keep the route held until the sneak takes (or 15s).
    [Fact]
    public void ReadyToMoveSneaking_SoftFailures_KeepHoldingAndRetrying()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        DateTimeOffset now = new(2026, 9, 27, 1, 40, 26, TimeSpan.Zero);
        h.StealthNow = () => now;

        h.Stealth.ReadyToMoveSneaking();
        for (int i = 0; i < 12; i++)
        {
            now = now.AddMilliseconds(600);
            h.Feed("Attempting to sneak...You don't think you're sneaking.");
        }
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
        Assert.False(h.Stealth.ReadyToMoveSneaking());

        now = now.AddSeconds(10);                           // past the 15s total
        h.Feed("Attempting to sneak...You don't think you're sneaking.");
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
        Assert.True(h.Stealth.ReadyToMoveSneaking());       // moves on unsneaked, once
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

    // Report paradigm-20261007-213809: with a log raft in the pack every `sn` was
    // refused, and Auto-Sneak resent it for the whole settle window in each room.
    [Fact]
    public void AutoSneak_CarryingAnItemThatKillsStealth_StandsDownWithOneNotice()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        string? carrying = "log raft (Stealth -125)";
        List<string> notices = new();
        h.Stealth.SetCarriedPenaltyCheck(() => carrying, notices.Add);

        h.Stealth.NoteRoomChanged();
        h.Stealth.RequestPreMoveStealth();
        h.Stealth.NoteRoomChanged();

        Assert.Empty(h.Sent);
        Assert.Equal("log raft (Stealth -125)", h.Stealth.StoodDownFor);
        string notice = Assert.Single(notices);
        Assert.Contains("log raft", notice);
        Assert.True(h.Stealth.ReadyToMoveSneaking());      // the step isn't held for it

        carrying = null;
        h.Stealth.NoteCarriedChanged();

        Assert.Null(h.Stealth.StoodDownFor);
        Assert.Equal(2, notices.Count);
        Assert.Contains("sneaking again", notices[1]);

        h.Stealth.NoteRoomChanged();
        Assert.Equal("sn", h.LastSent());
    }

    [Fact]
    public void AutoSneakOff_CarryingTheItem_SaysNothing()
    {
        using AutoHarness h = new() { AutoSneakOn = false };
        List<string> notices = new();
        h.Stealth.SetCarriedPenaltyCheck(() => "log raft (Stealth -125)", notices.Add);

        h.Stealth.NoteRoomChanged();

        Assert.Empty(notices);
        Assert.Null(h.Stealth.StoodDownFor);
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
    public void AutoSneak_StaleAttempt_RoomChangeResets_ReFires()
    {
        // 235341: a sneak attempt whose ACK ("Attempting to sneak...") and the
        // room's "Sneaking..." confirm both go unobserved strands the FSM in
        // AttemptingSneak. On the next room change the stale attempt must reset to
        // Idle so auto-sneak re-fires — without the reset, TryBeginAutoSneak's
        // in-flight guard (!= Idle && != Failed) blocks every re-attempt and
        // auto-sneak silently stops for the rest of the run.
        using AutoHarness h = new() { AutoSneakOn = true };

        h.Stealth.NoteRoomChanged();                 // first `sn` → AttemptingSneak
        Assert.Single(h.Sent);
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);

        // No ACK, no `Sneaking...` — the room changes again with the attempt
        // still in flight. The stale attempt resets and a fresh `sn` goes out.
        h.Stealth.NoteRoomChanged();

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

    // ----- re-sneak after an out-of-combat cast ----------------------

    [Fact]
    public void ReSneakAfterCast_WhileSneaking_ResetsAndResends()
    {
        // A cast breaks sneak with no line to latch → FSM reads stale-Sneaking.
        // ReSneakAfterCast drops the stale state and re-attempts sneak in place.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");
        Assert.True(h.State.IsSneaking);

        h.Stealth.ReSneakAfterCast();

        Assert.Equal("sn", h.LastSent());
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
    }

    // A buff cast mid-rest ends the sneak; without ShadowRest the next `rest` would end
    // a fresh one too, so it isn't re-taken until the next move (report
    // paradigm-20260930-184343).
    [Fact]
    public void ReSneakAfterCast_DuringARestThatEndsSneak_DoesNotResend()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.SetReSneakSkipForRest(() => true);
        h.Feed("Sneaking...");
        int sent = h.Sent.Count;

        h.Stealth.ReSneakAfterCast();

        Assert.Equal(sent, h.Sent.Count);
        Assert.False(h.State.IsSneaking);
        Assert.Equal(StealthState.Idle, h.Stealth.State);
    }

    // Reports paradigm-20260929-185544 / -191106: releasing the cast hold resumed the
    // walker, and the next move went out before the post-cast re-sneak held it. The
    // re-sneak's hold is up before the cast hold comes off, so movement never frees up
    // in between.
    [Fact]
    public void ReSneakAfterCast_KeepsMovementHeldThroughTheHandOff()
    {
        using AutoHarness h = new() { AutoSneakOn = true };
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Stealth.SetMovementCoordinator(coord);
        h.Feed("Sneaking...");
        bool castDue = true;
        h.Stealth.SetHeldCastCheck(() => castDue);
        Assert.False(h.Stealth.ReadyToMoveSneaking());      // the walker asks; the buff goes first
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCastGate));

        bool freedMidHandOff = false;
        coord.PauseStateChanged += paused => { if (!paused) freedMidHandOff = true; };
        castDue = false;
        h.Stealth.ReSneakAfterCast();

        Assert.False(freedMidHandOff);
        Assert.Equal("sn", h.LastSent());
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakSettleGate));
        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SneakCastGate));
    }

    [Fact]
    public void ReSneakAfterCast_AutoSneakOff_NoSend()
    {
        // Everything sneak-aware is gated on auto-sneak: off ⇒ no re-sneak.
        using AutoHarness h = new() { AutoSneakOn = false };
        h.Feed("Sneaking...");

        h.Stealth.ReSneakAfterCast();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ReSneakAfterCast_InCombat_NoSend()
    {
        // An in-combat cast never re-sneaks here — the combat-end reset owns that
        // path, and you can't sneak mid-fight anyway.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");
        h.State.InCombat = true;

        h.Stealth.ReSneakAfterCast();

        Assert.Empty(h.Sent);
    }

    // ----- pre-move stealth hook -------------------------------------

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

    // ----- combat-spent stealth reset (report stock-20260730-163044) --

    [Fact]
    public void CombatEndedReset_FromSneaking_DropsToIdle()
    {
        // Attacking spends the sneak but emits no line the FSM keys on, so
        // IsSneaking is stale-true after a kill. The reset drops it to Idle
        // without sending anything (the re-sneak is the pre-move hook's job).
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");
        Assert.Equal(StealthState.Sneaking, h.Stealth.State);

        h.Stealth.NoteCombatEndedStealthReset();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsSneaking);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CombatEndedReset_ThenPreMove_ReSneaksBeforeLeaving()
    {
        // The reported bug end-to-end: sneak-approach a room, fight it clear,
        // then step out. Without the reset the pre-move hook sees a stale
        // Sneaking and no-ops (never re-sneaking); with it, the walker's
        // pre-move `sn` re-establishes stealth for the move out.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");                     // approached under sneak
        h.State.InCombat = true;                   // engaged the room's hostile

        // Room clears: combat tracker flips InCombat false, then resets stealth,
        // then the walker's pre-move hook fires as it steps out.
        h.State.InCombat = false;
        h.Stealth.NoteCombatEndedStealthReset();
        h.Stealth.RequestPreMoveStealth();

        Assert.Single(h.Sent);
        Assert.Equal("sn", h.LastSent());
        Assert.Equal(StealthState.AttemptingSneak, h.Stealth.State);
    }

    [Fact]
    public void CombatEndedReset_WhenNotSneaking_NoOp()
    {
        // Never engaged under stealth (Idle) → nothing to reset, no transition.
        using AutoHarness h = new() { AutoSneakOn = true };
        Assert.Equal(StealthState.Idle, h.Stealth.State);

        h.Stealth.NoteCombatEndedStealthReset();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CombatEndedReset_ThenPreMove_NpcPresent_NoReSneak()
    {
        // A friendly NPC left in the cleared room still blocks sneak — the
        // post-combat re-sneak must stay suppressed rather than burn a doomed sn.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.SetSneakBlockCheck(() => true);
        h.Feed("Sneaking...");

        h.Stealth.NoteCombatEndedStealthReset();
        h.Stealth.RequestPreMoveStealth();

        Assert.Empty(h.Sent);
        Assert.Equal(StealthState.Idle, h.Stealth.State);
    }

    [Fact]
    public void CombatEndedReset_ThenPreMove_NoDoubleSneak()
    {
        // The reactive room-change path and the pre-move path can both fire for
        // the same step-out; the settled-state guard keeps it to one `sn`.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Feed("Sneaking...");

        h.Stealth.NoteCombatEndedStealthReset();
        h.Stealth.RequestPreMoveStealth();
        Assert.Single(h.Sent);

        h.Stealth.RequestPreMoveStealth();
        Assert.Single(h.Sent);
    }

    [Fact]
    public void CombatEndedReset_FromHidden_DropsHide()
    {
        // Combat reveals a hidden opener too — drop the optimistic hidden state.
        using AutoHarness h = new() { AutoSneakOn = true };
        h.Stealth.NoteHideConfirmed();
        Assert.Equal(StealthState.Hidden, h.Stealth.State);

        h.Stealth.NoteCombatEndedStealthReset();

        Assert.Equal(StealthState.Idle, h.Stealth.State);
        Assert.False(h.State.IsHidden);
    }

    // A solo character with Auto-Hide on sent `hid` in every room a walk or a loop
    // took it through, ahead of the next step: each arrival was taken for being
    // idle. An arrival, or the walk stopping, only starts a dwell now, and the hide
    // goes out if the character is still there and at rest when it runs out.
    private sealed class Dwell
    {
        public bool AtRest = true;
        public List<Action> Timers { get; } = new();
        public List<TimeSpan> Delays { get; } = new();

        public void WireInto(StealthManager stealth) =>
            stealth.SetHideDwell(() => AtRest, (delay, fire) => { Delays.Add(delay); Timers.Add(fire); });

        public void RunOut()
        {
            Action[] due = Timers.ToArray();
            Timers.Clear();
            foreach (Action fire in due) fire();
        }
    }

    [Fact]
    public void AutoHide_WithADwell_AnArrivalHidesNothingUntilTheCharacterHasStoodStill()
    {
        using AutoHarness h = new() { AutoHideOn = true };
        Dwell dwell = new();
        dwell.WireInto(h.Stealth);

        h.Stealth.NoteIdleOpportunity();

        Assert.Empty(h.Sent);                               // not on arrival
        Assert.True(h.Stealth.AutoHideWaitingOutTheDwell);
        Assert.Equal(StealthManager.HideDwell, Assert.Single(dwell.Delays));

        dwell.RunOut();                                     // still here, nothing walking it

        Assert.Equal("hid", h.LastSent());
        Assert.False(h.Stealth.AutoHideWaitingOutTheDwell);
    }

    [Fact]
    public void AutoHide_WithADwell_ARoomPassedThroughIsNotHiddenIn()
    {
        using AutoHarness h = new() { AutoHideOn = true };
        Dwell dwell = new();
        dwell.WireInto(h.Stealth);

        // A walk through three rooms: each arrival replaces the one before, and the
        // walk is still running when the first two dwells run out.
        h.Stealth.NoteIdleOpportunity();
        h.Stealth.NoteIdleOpportunity();
        dwell.AtRest = false;
        h.Stealth.NoteIdleOpportunity();
        dwell.RunOut();

        Assert.Empty(h.Sent);

        // The walk stops in the third room: that is an opportunity of its own.
        dwell.AtRest = true;
        h.Stealth.NoteIdleOpportunity();
        dwell.RunOut();

        Assert.Single(h.Sent);
        Assert.Equal("hid", h.LastSent());
    }

    [Fact]
    public void AutoHide_WithADwell_SwitchedOffMeanwhile_SendsNothing()
    {
        using AutoHarness h = new() { AutoHideOn = true };
        Dwell dwell = new();
        dwell.WireInto(h.Stealth);
        h.Stealth.NoteIdleOpportunity();

        h.AutoHideOn = false;
        dwell.RunOut();

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
