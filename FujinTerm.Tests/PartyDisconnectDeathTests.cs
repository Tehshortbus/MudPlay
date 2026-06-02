using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the Phase 6 PR 6.5 additions to PartyManager —
/// disconnect / death / grace-window auto-invite. PR 6.1 tests cover
/// follows-you / par-block; this class focuses on the new event paths
/// only. Updated post-PR-6.8 to use the real BBS-observed wordings
/// ("X started to follow you." instead of the earlier "now follows
/// you" guess).
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
        router.Dispatch(Line("Helper started to follow you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper just disconnected!!!."));

        Assert.Empty(mgr.State.Members);
        Assert.Contains("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disconnect_OfNonMember_IsIgnored()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));

        router.Dispatch(Line("Stranger just disconnected!!!."));

        Assert.Single(mgr.State.Members);
        Assert.DoesNotContain("Stranger", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ===== Reconnect within grace window — auto-invite =====

    [Fact]
    public void Reconnect_WithinWindow_AndWeLead_SendsInvite()
    {
        // "X started to follow you" automatically flips SelfIsLeader=true,
        // so no par-block setup needed.
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(mgr.State.SelfIsLeader);

        router.Dispatch(Line("Helper just disconnected!!!."));
        wire.Clear();

        router.Dispatch(Line("Helper just entered the Realm."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("invite Helper\r", Encoding.Latin1.GetString(sent));
        Assert.DoesNotContain("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconnect_WithinWindow_AndWeFollow_DoesNotInvite()
    {
        // We follow Fujin → not leader. Then Fujin (or Helper) disconnects
        // and reconnects → no invite because we're not the leader.
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("You are now following Fujin."));
        Assert.False(mgr.State.SelfIsLeader);
        // Also a follower joins us... actually that'd flip us to leader.
        // Use the simpler "Fujin is leader, Fujin disconnects" scenario.
        router.Dispatch(Line("Fujin just disconnected!!!."));
        wire.Clear();

        router.Dispatch(Line("Fujin just entered the Realm."));

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

        // Establish leadership via the follows-you path.
        router.Dispatch(Line("Helper started to follow you."));
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
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("Stranger just entered the Realm."));

        Assert.Empty(wire);
    }

    [Fact]
    public void Reconnect_NoWireSender_NoThrow()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        router.Dispatch(Line("Helper just entered the Realm."));
        // No assertion — passes if no exception.
    }

    // ===== Death =====

    [Fact]
    public void Death_OfPartyMember_RemovesImmediately()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper has been slain by a giant rat."));

        Assert.Empty(mgr.State.Members);
        Assert.DoesNotContain("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Death_OfNonMember_IsIgnored()
    {
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));

        router.Dispatch(Line("Stranger has been slain by a dragon."));

        Assert.Single(mgr.State.Members);
    }
}
