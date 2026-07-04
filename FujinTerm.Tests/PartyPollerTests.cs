using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyPollerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Setup uses the test-seam ctor (useTimer=false) so par cadence is
    /// driven manually via DoParPollForTests — keeps unit tests
    /// deterministic without spinning up Avalonia's DispatcherTimer.
    /// </summary>
    private static (PartyPoller poller, PartyManager mgr, PartyState state, ChatRouter chat, MessageRouter router, List<byte[]> wire) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);
        PartyPoller poller = new(chat, state, mgr, useTimer: false);
        List<byte[]> wire = new();
        poller.SetWireSender(wire.Add);
        return (poller, mgr, state, chat, router, wire);
    }

    private static string LastWire(List<byte[]> w) =>
        Encoding.Latin1.GetString(w[^1]);

    // ===== par-poll cadence =====

    [Fact]
    public void ParPoll_WhenSolo_SendsNothing()
    {
        var (poller, _, _, _, _, wire) = Setup();
        poller.DoParPollForTests();
        Assert.Empty(wire);
    }

    [Fact]
    public void ParPoll_WhenInParty_SendsPar()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged" });
        state.IsInParty = true;

        poller.DoParPollForTests();

        Assert.Equal("par\r", LastWire(wire));
    }

    [Fact]
    public void ParPoll_NoWireSender_NoThrow()
    {
        var (poller, _, state, _, _, _) = Setup();
        state.IsInParty = true;
        poller.SetWireSender(_ => { }); // bind something to satisfy null-guard then no-op
        poller.DoParPollForTests();
        // No assertion — test passes by not throwing.
    }

    [Fact]
    public void ParPoll_GateReturnsFalse_SendsNothing()
    {
        // par rides the auto-heal/rest toggle; a false gate (toggle off,
        // or auto-all killed the flag) must silence the poll.
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged" });
        state.IsInParty = true;
        poller.IsParPollEnabled = () => false;
        wire.Clear();   // drop the on-join @health send; isolate the par poll

        poller.DoParPollForTests();

        Assert.Empty(wire);
    }

    [Fact]
    public void ParPoll_GateReturnsTrue_SendsPar()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged" });
        state.IsInParty = true;
        poller.IsParPollEnabled = () => true;

        poller.DoParPollForTests();

        Assert.Equal("par\r", LastWire(wire));
    }

    // ===== trainer-menu suppression =====
    //
    // The trainer stats screen is a full-screen ANSI menu that suppresses the
    // in-game statline, so the poller's wall-clock cadences don't naturally
    // pause the way prompt-driven automation does. A live gate silences par +
    // @health while in the menu; the auto-trainer's own CP replay is untouched
    // because it sends through a separate wire path, not this poller.

    [Fact]
    public void ParPoll_WhileInTrainerMenu_SendsNothing()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged" });
        state.IsInParty = true;
        poller.IsParPollEnabled = () => true;
        poller.IsInTrainerMenu = () => true;
        wire.Clear();   // drop the on-join @health send; isolate the par poll

        poller.DoParPollForTests();

        Assert.Empty(wire);
    }

    [Fact]
    public void ParPoll_AfterTrainerMenuExit_ResumesPar()
    {
        bool inMenu = true;
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged" });
        state.IsInParty = true;
        poller.IsParPollEnabled = () => true;
        poller.IsInTrainerMenu = () => inMenu;

        poller.DoParPollForTests();
        Assert.DoesNotContain(wire, b => Encoding.Latin1.GetString(b) == "par\r");

        inMenu = false;   // menu exited — the next poll must send par again
        poller.DoParPollForTests();
        Assert.Equal("par\r", LastWire(wire));
    }

    [Fact]
    public void OnJoinHealth_WhileInTrainerMenu_DoesNotTelepath()
    {
        var (poller, _, state, _, _, wire) = Setup();
        poller.IsInTrainerMenu = () => true;

        state.Members.Add(new PartyMember { Name = "Helper" });

        Assert.Empty(wire);
    }

    [Fact]
    public void HealthNag_WhileInTrainerMenu_PausesThenResumesAfterExit()
    {
        bool inMenu = false;
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        poller.IsInTrainerMenu = () => inMenu;

        // Arm the nag while out of the menu (initial @health fires here).
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        // Enter the menu — a due retry must not go on the wire.
        inMenu = true;
        advance(TimeSpan.FromSeconds(6));
        poller.TickHealthNagsForTests();
        Assert.Empty(wire);

        // Exit — the pending nag resumes on the next tick.
        inMenu = false;
        poller.TickHealthNagsForTests();
        Assert.Single(wire);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(wire[0]));
    }

    // ===== Settings.Party par-cadence setter =====

    [Fact]
    public void SetParCadence_UpdatesProperty()
    {
        var (poller, _, _, _, _, _) = Setup();
        poller.SetParCadence(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), poller.ParCadence);
    }

    [Fact]
    public void SetParCadence_NonPositive_Throws()
    {
        var (poller, _, _, _, _, _) = Setup();
        Assert.Throws<ArgumentOutOfRangeException>(() => poller.SetParCadence(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => poller.SetParCadence(TimeSpan.FromSeconds(-1)));
    }

    // ===== on-join @health request =====

    [Fact]
    public void NewMember_AddedToParty_TriggersHealthTelepath()
    {
        // No IsInParty gate on the poller's OnMembersChanged path —
        // PartyManager fires the CollectionChanged.Add BEFORE
        // flipping IsInParty (the field is set early in OnFollowsYou
        // but still: the CollectionChanged shouldn't have any
        // dependency on derived state at the moment of fire). The
        // poller's only gates are IsSelf + non-empty name.
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });

        // CollectionChanged Add fires synchronously from the
        // ObservableCollection — the poller's handler runs inline.
        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SelfMember_AddedToParty_DoesNotTelepathSelf()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Forged", IsSelf = true });
        Assert.Empty(wire);
    }

    [Fact]
    public void MultipleMembers_AddedAtOnce_TelepathsEach()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });
        state.Members.Add(new PartyMember { Name = "Tank" });
        state.Members.Add(new PartyMember { Name = "Cleric" });

        Assert.Equal(3, wire.Count);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(wire[0]));
        Assert.Equal("/Tank @health\r",   Encoding.Latin1.GetString(wire[1]));
        Assert.Equal("/Cleric @health\r", Encoding.Latin1.GetString(wire[2]));
    }

    [Fact]
    public void HealthNagDisabled_NewMember_SendsNoHealthRequest()
    {
        // "send @health nags to party members" off — a freshly joined
        // member is never telepathed @health and no nag is armed.
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        poller.HealthNagEnabled = false;

        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Empty(wire);

        // Past the initial delay + a frequency window — still nothing,
        // because no nag was armed in the first place.
        advance(TimeSpan.FromSeconds(6));
        poller.TickHealthNagsForTests();
        advance(TimeSpan.FromSeconds(11));
        poller.TickHealthNagsForTests();
        Assert.Empty(wire);
    }

    [Fact]
    public void FollowsYouLine_TriggersHealthRoundTrip_EndToEnd()
    {
        // Regression for the live bug: after Fujin manually invited
        // Raijin and Raijin started following, no /Raijin @health was
        // sent. Root cause was a defensive IsInParty gate on the
        // poller that fired before PartyManager flipped the field.
        // This pins the full flow — dispatching the real BBS line
        // through the router should add Raijin to Members AND
        // produce the @health wire-send.
        var (_, mgr, state, _, router, wire) = Setup();
        mgr.LocalCharacterName = "Fujin";

        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            "Raijin started to follow you.",
            new Terminal.CellAttributes[32],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false));

        Assert.True(state.IsInParty);
        Assert.Contains(state.Members,
            m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase));
        // Only the new member triggers @health — self is skipped.
        Assert.Contains(wire,
            b => Encoding.Latin1.GetString(b) == "/Raijin @health\r");
    }

    [Fact]
    public void RemovedMember_DoesNotTelepath()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        state.Members.RemoveAt(0);

        Assert.Empty(wire);
    }

    // ===== @health reply parsing =====

    [Fact]
    public void HealthReply_WithMana_UpdatesBaselinesAndPercents()
    {
        var (_, mgr, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });
        // Simulate Helper's reply coming back as a telepath. Use the
        // real router dispatch so ChatRouter classifies + the poller
        // picks it up via its EntryClassified subscription. Reply body
        // is the brace-wrapped key=value shape the engine produces:
        // {HP=cur/max,MA=cur/max, Resting}.
        DispatchTelepath(router, "Helper", "{HP=690/720,MA=200/300, Resting}");

        PartyMember m = state.Members[0];
        Assert.Equal(720, m.BaselineHp);
        Assert.Equal(300, m.BaselineMp);
        // 690/720 = 95.83%; integer truncation → 95.
        Assert.Equal(95, m.HpPercent);
        // 200/300 = 66.67% → 66.
        Assert.Equal(66, m.MpPercent);
    }

    [Fact]
    public void HealthReply_FullHealth_PercentsAreHundred()
    {
        // Regression: a member who joins at full health should land on
        // the roster reading "H:36/36 100%", not "H:0/36 0%" until the
        // next par poll catches up.
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin" });
        DispatchTelepath(router, "Raijin", "{HP=36/36,MA=34/34}");

        PartyMember m = state.Members[0];
        Assert.Equal(36,  m.BaselineHp);
        Assert.Equal(34,  m.BaselineMp);
        Assert.Equal(100, m.HpPercent);
        Assert.Equal(100, m.MpPercent);
    }

    [Fact]
    public void HealthReply_NegativeCurrentHp_ParsesAndCancelsNag()
    {
        // MajorMUD's "dropped" state: HP can go from 0 to the BBS's
        // death threshold (negative) while the player is still alive
        // but immobile. The @health reply legitimately carries a
        // negative cur value in that range. Pre-fix the `\d+` cur
        // regex silently failed to match, BaselineHp stayed 0, and
        // the nag retried until max-total instead of cancelling.
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin" });

        DispatchTelepath(router, "Raijin", "{HP=-5/40,MA=10/34}");

        PartyMember m = state.Members[0];
        // Baseline = max half (always positive).
        Assert.Equal(40, m.BaselineHp);
        // Percent reflects the actual negative value (-5/40 = -12).
        // The ProgressBar control will clamp at 0; HpDisplay surfaces
        // the real "-2/40" so the user can see they're dropped.
        Assert.Equal(-12, m.HpPercent);
        Assert.Equal(34, m.BaselineMp);
    }

    [Fact]
    public void HealthReply_ZeroCurrentHp_StillParses()
    {
        // Edge of the dropped range — 0 HP is the immobile threshold.
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin" });

        DispatchTelepath(router, "Raijin", "{HP=0/40}");

        PartyMember m = state.Members[0];
        Assert.Equal(40, m.BaselineHp);
        Assert.Equal(0,  m.HpPercent);
    }

    [Fact]
    public void HealthReply_WithKai_UpdatesBaselinesAndPercents()
    {
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Monk" });
        // Standing is the idle default, no position suffix.
        DispatchTelepath(router, "Monk", "{HP=500/500,KAI=150/150}");

        PartyMember m = state.Members[0];
        Assert.Equal(500, m.BaselineHp);
        Assert.Equal(150, m.BaselineMp);
        Assert.Equal(100, m.HpPercent);
        Assert.Equal(100, m.MpPercent);
    }

    [Fact]
    public void HealthReply_WarriorWithoutMana_UpdatesHpOnlyAndZeroesMana()
    {
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Tank" });
        DispatchTelepath(router, "Tank", "{HP=850/850}");

        PartyMember m = state.Members[0];
        Assert.Equal(850, m.BaselineHp);
        Assert.Equal(0,   m.BaselineMp);
        Assert.Equal(100, m.HpPercent);
        Assert.Equal(0,   m.MpPercent);
    }

    [Fact]
    public void HealthReply_FromUnknownPlayer_DoesNotCreateMember()
    {
        var (_, _, state, _, router, _) = Setup();
        DispatchTelepath(router, "Stranger", "{HP=100/100,MA=50/50}");

        Assert.Empty(state.Members);
    }

    [Fact]
    public void NonHealthTelepath_DoesNotModifyBaselines()
    {
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });
        DispatchTelepath(router, "Helper", "hey what's up");

        PartyMember m = state.Members[0];
        Assert.Equal(0, m.BaselineHp);
        Assert.Equal(0, m.BaselineMp);
        Assert.Equal(0, m.HpPercent);
        Assert.Equal(0, m.MpPercent);
    }

    // ===== helper =====

    private static void DispatchTelepath(MessageRouter router, string sender, string message)
    {
        string line = $"{sender} telepaths: {message}";
        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
    }

    // ===== IsSelf race regression =====
    //
    // If par parses our own row first (no AddSelfIfKnown path having run
    // yet — e.g. mid-session connect into an existing party state), the
    // member used to land in the collection with IsSelf=false and only
    // get corrected AFTER CollectionChanged.Add fired. PartyPoller's
    // skip-self gate would miss us, fire `/Fujin @health`, and the
    // server would respond "Why are you telepathing to yourself?".
    // Fix: AddOrTouchMember now sets IsSelf at construction.

    [Fact]
    public void OnMembersChanged_ParSelfRow_DoesNotFireHealthRoundTrip()
    {
        var (poller, mgr, _, _, _, wire) = Setup();
        mgr.LocalCharacterName = "Fujin";

        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });

        // Wire should be empty — no /Fujin @health.
        Assert.Empty(wire);
    }

    [Fact]
    public void OnMembersChanged_ParOtherMember_DoesFireHealthRoundTrip()
    {
        // Sanity: the fix above must not break the legitimate case
        // where a non-self member is added via par and triggers the
        // @health round-trip.
        var (poller, mgr, _, _, _, wire) = Setup();
        mgr.LocalCharacterName = "Fujin";

        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Frontrank",
            string.Empty,
        });

        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Raijin @health\r", Encoding.Latin1.GetString(sent));
    }

    // ===== @health nag retry (shares JoinNag* settings) ==================

    /// <summary>
    /// Build a poller wired with a frozen-clock NowProvider and short
    /// nag cadences so the tick-based tests don't need real time
    /// to elapse.
    /// </summary>
    private static (PartyPoller poller, PartyManager mgr, PartyState state, ChatRouter chat, MessageRouter router, List<byte[]> wire, Func<DateTime> clock, Action<TimeSpan> advance)
        SetupWithClock()
    {
        var setup = Setup();
        DateTime t = Now;
        setup.poller.NowProvider = () => t;
        setup.poller.HealthNagInitialDelay = TimeSpan.FromSeconds(5);
        setup.poller.HealthNagFrequency    = TimeSpan.FromSeconds(10);
        setup.poller.HealthNagMaxTotal     = TimeSpan.FromSeconds(55);
        Action<TimeSpan> advance = span => t = t.Add(span);
        return (setup.poller, setup.mgr, setup.state, setup.chat, setup.router, setup.wire, () => t, advance);
    }

    [Fact]
    public void HealthNag_BeforeInitialDelay_DoesNotResend()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        // Initial @health already fired in OnMembersChanged.
        Assert.Single(wire);

        // Tick 1s in — still inside the initial-delay window (5s).
        advance(TimeSpan.FromSeconds(1));
        poller.TickHealthNagsForTests();

        Assert.Single(wire);
    }

    [Fact]
    public void HealthNag_PastInitialDelay_SendsFirstRetry()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        advance(TimeSpan.FromSeconds(6));
        poller.TickHealthNagsForTests();

        Assert.Single(wire);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(wire[0]));
    }

    [Fact]
    public void HealthNag_PastFrequency_SendsSubsequentRetries()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        // First retry at 6s.
        advance(TimeSpan.FromSeconds(6));
        poller.TickHealthNagsForTests();
        // Second retry — needs Frequency (10s) past the first retry.
        advance(TimeSpan.FromSeconds(11));
        poller.TickHealthNagsForTests();

        Assert.Equal(2, wire.Count);
    }

    [Fact]
    public void HealthNag_BaselineArrives_CancelsNag()
    {
        var (poller, _, state, _, router, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        // Reply lands before the first retry — should cancel.
        DispatchTelepath(router, "Helper", "{HP=100/100,MA=50/50}");

        // Plenty of time elapsed — should NOT retry now.
        advance(TimeSpan.FromSeconds(20));
        poller.TickHealthNagsForTests();

        Assert.Empty(wire);
    }

    [Fact]
    public void HealthNag_PastMaxTotal_GivesUp()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        // Jump past the max-total cap (55s) — no retry, no further work.
        advance(TimeSpan.FromSeconds(60));
        poller.TickHealthNagsForTests();
        // Tick again to confirm the timer is now idle.
        advance(TimeSpan.FromSeconds(10));
        poller.TickHealthNagsForTests();

        Assert.Empty(wire);
    }

    [Fact]
    public void HealthNag_MemberLeaves_CancelsNag()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Helper" });
        wire.Clear();

        state.Members.RemoveAt(0);
        // Plenty of time elapsed — should NOT retry; member is gone.
        advance(TimeSpan.FromSeconds(20));
        poller.TickHealthNagsForTests();

        Assert.Empty(wire);
    }

    [Fact]
    public void HealthNag_SelfMember_NoNagScheduled()
    {
        var (poller, _, state, _, _, wire, _, advance) = SetupWithClock();
        state.Members.Add(new PartyMember { Name = "Forged", IsSelf = true });

        advance(TimeSpan.FromSeconds(60));
        poller.TickHealthNagsForTests();

        Assert.Empty(wire);
    }

    // ===== Invited row gating ============================================

    [Fact]
    public void InvitedMember_AddedToParty_SkipsOnJoinHealthRoundTrip()
    {
        // PartyManager adds an IsInvited=true row when "You have
        // invited X to follow you." fires. The PartyWindow needs the
        // row for the INVITED chip + X button, but we must NOT
        // telepath /X @health while X hasn't joined — they'd see a
        // confused @health request from a stranger.
        var (poller, _, state, _, _, wire) = Setup();
        _ = poller;
        state.Members.Add(new PartyMember { Name = "Helper", IsInvited = true });

        Assert.Empty(wire);
    }

    [Fact]
    public void InvitedThenAccepted_FiresHealthRoundTrip()
    {
        // Acceptance path: PartyManager.OnFollowsYou flips IsInvited
        // false on the SAME row. PartyPoller's per-row PropertyChanged
        // hook catches the transition and fires the @health round-
        // trip that the CollectionChanged.Add path would normally
        // trigger for a fresh row.
        var (poller, _, state, _, _, wire) = Setup();
        _ = poller;
        PartyMember row = new() { Name = "Helper", IsInvited = true };
        state.Members.Add(row);
        Assert.Empty(wire);

        row.IsInvited = false;

        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void InvitedMember_Removed_DetachesPropertyChangedHandler()
    {
        // If the user uninvites before acceptance and the row is
        // removed, mutating the orphaned PartyMember instance must
        // not produce wire traffic — the poller's PropertyChanged
        // subscription on the row must have been detached.
        var (poller, _, state, _, _, wire) = Setup();
        _ = poller;
        PartyMember row = new() { Name = "Helper", IsInvited = true };
        state.Members.Add(row);
        state.Members.Remove(row);
        Assert.Empty(wire);

        // Orphaned mutation: poller should NOT react.
        row.IsInvited = false;

        Assert.Empty(wire);
    }

    [Fact]
    public void YouInvitedLine_ThroughPartyManager_SkipsHealthRoundTrip()
    {
        // Regression for the live bug: "You have invited Raijin to follow
        // you." fired /Raijin @health immediately, before Raijin joined.
        // Root cause was PartyManager adding the roster row with IsInvited
        // still default-false — Members.Add fired the poller's on-join
        // round-trip synchronously, before OnYouInvited set the flag true.
        // The flag is now set at construction, so the add-time gate blocks.
        var (_, mgr, state, _, router, wire) = Setup();
        mgr.LocalCharacterName = "Fujin";

        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            "You have invited Raijin to follow you.",
            new Terminal.CellAttributes[64],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false));

        // The row exists (for the PartyWindow INVITED chip) and is invited,
        // but no @health went out — the invitee hasn't joined.
        Assert.Contains(state.Members,
            m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase) && m.IsInvited);
        Assert.Empty(wire);
    }

    [Fact]
    public void InviteThenFollows_ThroughPartyManager_FiresHealthOnlyOnJoin()
    {
        // Pins the full sequence: the invite echo must stay silent, and the
        // @health round-trip fires exactly once — when the invitee actually
        // starts following (IsInvited flips true→false on the same row).
        var (_, mgr, state, _, router, wire) = Setup();
        mgr.LocalCharacterName = "Fujin";

        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            "You have invited Raijin to follow you.",
            new Terminal.CellAttributes[64],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false));
        Assert.Empty(wire);   // nothing yet — still pending

        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            "Raijin started to follow you.",
            new Terminal.CellAttributes[32],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false));

        // Now — and only now — the on-join @health goes out, once.
        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Raijin @health\r", Encoding.Latin1.GetString(sent));
    }
}
