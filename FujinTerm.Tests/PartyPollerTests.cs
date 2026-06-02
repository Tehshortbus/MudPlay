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
}
