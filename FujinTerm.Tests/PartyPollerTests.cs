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
        var (poller, _, state, _, _, wire) = Setup();
        // IsInParty is the gate the poller now consults for defense in
        // depth (the par-block parser can spuriously re-add a name when
        // dissolution leaves the state machine in ReadingRows; the
        // poller shouldn't fire @health round-trips for those
        // ghost-adds). Flip it first to model an active party.
        state.IsInParty = true;
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
        state.IsInParty = true;
        state.Members.Add(new PartyMember { Name = "Forged", IsSelf = true });
        Assert.Empty(wire);
    }

    [Fact]
    public void MultipleMembers_AddedAtOnce_TelepathsEach()
    {
        var (poller, _, state, _, _, wire) = Setup();
        state.IsInParty = true;
        state.Members.Add(new PartyMember { Name = "Helper" });
        state.Members.Add(new PartyMember { Name = "Tank" });
        state.Members.Add(new PartyMember { Name = "Cleric" });

        Assert.Equal(3, wire.Count);
        Assert.Equal("/Helper @health\r", Encoding.Latin1.GetString(wire[0]));
        Assert.Equal("/Tank @health\r",   Encoding.Latin1.GetString(wire[1]));
        Assert.Equal("/Cleric @health\r", Encoding.Latin1.GetString(wire[2]));
    }

    [Fact]
    public void MemberAddedWhileNotInParty_DoesNotTelepath()
    {
        // The defense-in-depth gate — IsInParty stays false (a stale
        // par-block parser hangover scenario). The poller should not
        // emit any @health round-trip for these ghost-adds.
        var (poller, _, state, _, _, wire) = Setup();
        // IsInParty intentionally NOT set.
        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Empty(wire);
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
    public void HealthReply_WithMana_UpdatesBaselines()
    {
        var (_, mgr, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Helper" });
        // Simulate Helper's reply coming back as a telepath. Use the
        // real router dispatch so ChatRouter classifies + the poller
        // picks it up via its EntryClassified subscription.
        DispatchTelepath(router, "Helper", "HP 690/720, MA 200/300 (Resting)");

        PartyMember m = state.Members[0];
        Assert.Equal(720, m.BaselineHp);
        Assert.Equal(300, m.BaselineMp);
    }

    [Fact]
    public void HealthReply_WithKai_UpdatesBaselines()
    {
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Monk" });
        DispatchTelepath(router, "Monk", "HP 500/500, KAI 150/150 (Standing)");

        PartyMember m = state.Members[0];
        Assert.Equal(500, m.BaselineHp);
        Assert.Equal(150, m.BaselineMp);
    }

    [Fact]
    public void HealthReply_WarriorWithoutMana_UpdatesHpOnly()
    {
        var (_, _, state, _, router, _) = Setup();
        state.Members.Add(new PartyMember { Name = "Tank" });
        DispatchTelepath(router, "Tank", "HP 850/850 (Standing)");

        PartyMember m = state.Members[0];
        Assert.Equal(850, m.BaselineHp);
        Assert.Equal(0,   m.BaselineMp);
    }

    [Fact]
    public void HealthReply_FromUnknownPlayer_DoesNotCreateMember()
    {
        var (_, _, state, _, router, _) = Setup();
        DispatchTelepath(router, "Stranger", "HP 100/100, MA 50/50 (Standing)");

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
    }

    // ===== helper =====

    private static void DispatchTelepath(MessageRouter router, string sender, string message)
    {
        string line = $"{sender} telepaths: {message}";
        router.Dispatch(new Terminal.LineExtractor.EmittedLine(
            line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
    }
}
