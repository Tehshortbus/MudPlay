using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the Phase 6 PR 6.5 additions to PartyManager —
/// disconnect / death / grace-window auto-invite. PR 6.1 tests
/// cover follows-you / stops-following / par-block; this class
/// focuses on the new event paths only.
/// </summary>
public sealed class PartyDisconnectDeathTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static (MessageRouter router, PartyManager mgr, List<byte[]> wire) Setup(DateTimeOffset? clock = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        DateTimeOffset fakeNow = clock ?? Now;
        mgr.NowProvider = () => fakeNow;
        return (router, mgr, wire);
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // ===== Disconnect =====

    [Fact]
    public void Disconnect_OfPartyMember_RemovesAndStartsGraceWindow()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper now follows you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper just disconnected!!!."));

        Assert.Empty(mgr.State.Members);
        Assert.Contains("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disconnect_OfNonMember_IsIgnored()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper now follows you."));

        router.Dispatch(Line("Stranger just disconnected!!!."));

        Assert.Single(mgr.State.Members);
        Assert.DoesNotContain("Stranger", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ===== Reconnect within grace window — auto-invite =====

    [Fact]
    public void Reconnect_WithinWindow_AndWeLead_SendsInvite()
    {
        // Have to enter the par block to flip SelfIsLeader — only the
        // par parser marks IsLeader on members.
        var (router, mgr, wire) = Setup();
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[] { " * ME          : Mage             100%    100%   Standing" });
        Assert.True(mgr.State.SelfIsLeader);

        // Add Helper, then disconnect them.
        router.Dispatch(Line("Helper now follows you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        wire.Clear();

        // Helper reconnects.
        router.Dispatch(Line("Helper just entered the Realm."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("invite Helper\r", Encoding.Latin1.GetString(sent));
        Assert.DoesNotContain("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconnect_WithinWindow_AndWeFollow_DoesNotInvite()
    {
        // Not leader — no invite even on quick reconnect.
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("Helper now follows you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        wire.Clear();
        Assert.False(mgr.State.SelfIsLeader);

        router.Dispatch(Line("Helper just entered the Realm."));

        Assert.Empty(wire);
    }

    [Fact]
    public void Reconnect_OutsideWindow_DoesNotInvite()
    {
        // Use a mutable clock — advance past the grace window between disconnect and re-entry.
        DateTimeOffset clock = Now;
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        mgr.NowProvider = () => clock;
        mgr.DisconnectGraceWindow = TimeSpan.FromSeconds(10);

        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[] { " * ME          : Mage             100%    100%   Standing" });

        router.Dispatch(Line("Helper now follows you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        wire.Clear();

        // Jump 11 s into the future — past the 10 s window.
        clock = clock.AddSeconds(11);
        router.Dispatch(Line("Helper just entered the Realm."));

        Assert.Empty(wire);
        Assert.DoesNotContain("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconnect_OfNonDisconnectedPlayer_IsIgnored()
    {
        // Someone we never had in the party connecting shouldn't trip us.
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("Stranger just entered the Realm."));

        Assert.Empty(wire);
    }

    [Fact]
    public void Reconnect_NoWireSender_NoThrow()
    {
        var (router, mgr, _) = Setup();
        // Test ctor wires a sender; replace with one that won't help.
        // Actually we can't unset the sender, but we can prove the
        // wire was empty after a fresh router without leader status.
        router.Dispatch(Line("Helper now follows you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        // Not leader → no invite even if wire-sender bound.
        router.Dispatch(Line("Helper just entered the Realm."));
        // No assertion — passes if no exception.
    }

    // ===== Death =====

    [Fact]
    public void Death_OfPartyMember_RemovesImmediately()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper now follows you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper has been slain by a giant rat."));

        Assert.Empty(mgr.State.Members);
        // Death is NOT a disconnect — grace window doesn't track them.
        Assert.DoesNotContain("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Death_OfNonMember_IsIgnored()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper now follows you."));

        router.Dispatch(Line("Stranger has been slain by a dragon."));

        Assert.Single(mgr.State.Members);
    }
}
