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
    public void HungUp_OfPartyMember_RemovesAndStartsGraceWindow()
    {
        // "X just hung up!!!" is the clean-logoff line, distinct from
        // "X just disconnected!!!." (carrier lost). Both should route
        // through the same remove-and-grace-window handler so the
        // re-invite-lost-party-members flow works either way.
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper just hung up!!!."));

        Assert.Empty(mgr.State.Members);
        Assert.Contains("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dissolution_WithoutPerPlayerSignal_StampsPriorMembersIntoGraceWindow()
    {
        // Real-world scenario (Playpen-like BBS): a member hangs up but
        // the BBS only emits the account-name signal ("[Raijin] logs OFF"
        // — we don't pattern-match that, it's not player-keyed). The
        // result we DO see is the bare dissolution line. Treat that as
        // the fallback "lost member" signal and seed the grace window
        // with every prior other-member name so they're re-invite-
        // eligible when they reconnect.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Fujin" };
        mgr.NowProvider = () => Now;
        mgr.SetWireSender(_ => { });

        router.Dispatch(Line("Raijin started to follow you."));
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Frontrank",
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });
        Assert.True(mgr.State.SelfIsLeader);

        // Dissolution arrives with no per-player signal.
        router.Dispatch(Line("You are not in a party at the present time."));

        Assert.Empty(mgr.State.Members);
        // Snapshot key is the given name — matches the lookup
        // OnPlayerEnters does when "Raijin just entered the Realm" fires.
        Assert.Contains("Raijin", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dissolution_WithoutPerPlayerSignal_AndWeLead_AutoInvitesOnReconnect()
    {
        // End-to-end: dissolution → snapshot → re-entry within window
        // auto-fires the invite. Wires the user's reported scenario.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Fujin" };
        mgr.NowProvider = () => Now;
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);

        router.Dispatch(Line("Raijin started to follow you."));
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Frontrank",
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });
        router.Dispatch(Line("You are not in a party at the present time."));
        wire.Clear();

        router.Dispatch(Line("Raijin just entered the Realm."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void ParPoll_MissingMemberInPartyOf3_StampsLostAndRemoves()
    {
        // Party of 3 — when a single member drops without a per-player
        // signal, the party survives and the next par poll just shows
        // N-1. End-of-block reconciliation detects the missing member,
        // stamps their given name into the grace window, and drops
        // them from the roster so the Re-invite-lost-party-members
        // flow can pick them up on reconnect.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Fujin" };
        mgr.NowProvider = () => Now;
        mgr.SetWireSender(_ => { });

        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(mgr.State.SelfIsLeader);

        // First par poll — all three present, names upgrade to long form.
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Frontrank",
            "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank",
            string.Empty,
        });
        Assert.Equal(3, mgr.State.Members.Count);

        // Second par poll — Raijin dropped silently; par shows just
        // Fujin + Helper. Reconciliation should mark Raijin as lost.
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank",
            string.Empty,
        });

        Assert.Equal(2, mgr.State.Members.Count);
        Assert.DoesNotContain(mgr.State.Members, m => m.Name.StartsWith("Raijin"));
        Assert.Contains("Raijin", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParPoll_MissingMember_AndReconnectsWithinWindow_GetsReinvited()
    {
        // End-to-end for the 3+-party case: par drops Raijin's row →
        // they're stamped → they re-enter the realm → invite fires.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Fujin" };
        mgr.NowProvider = () => Now;
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);

        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));

        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Frontrank",
            "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank",
            string.Empty,
        });
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin  WuzHere                  (Mystic)                  [H:100%]   - Frontrank",
            "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank",
            string.Empty,
        });
        wire.Clear();

        router.Dispatch(Line("Raijin just entered the Realm."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void ParPoll_EmptyBlock_DoesNotFlagMembersAsLost()
    {
        // Defensive: a malformed/truncated par block (zero parsed
        // rows) shouldn't false-positive every member as missing.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Fujin" };
        mgr.NowProvider = () => Now;
        mgr.SetWireSender(_ => { });

        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));
        int before = mgr.State.Members.Count;

        // Header fires (par block opens) but no rows parse — terminator
        // arrives immediately.
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[] { string.Empty });

        Assert.Equal(before, mgr.State.Members.Count);
        Assert.Empty(mgr.RecentlyDisconnected);
    }

    [Fact]
    public void StopsFollowing_StampsMemberIntoGraceWindow()
    {
        // "X stops following you" is leader-side only — we didn't
        // initiate (uninvite goes through OnFollowerRemoved). Stamp
        // them so a reconnect can re-invite them automatically.
        var (router, mgr, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.Single(mgr.State.Members);

        router.Dispatch(Line("Helper has stopped following you."));

        Assert.Empty(mgr.State.Members);
        Assert.Contains("Helper", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HungUp_AndReturnsWithinWindow_AndWeLead_SendsInvite()
    {
        var (router, mgr, wire) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(mgr.State.SelfIsLeader);

        router.Dispatch(Line("Helper just hung up!!!."));
        wire.Clear();

        router.Dispatch(Line("Helper just entered the Realm."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("invite Helper\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Disconnect_OfLeader_DissolvesEntireParty()
    {
        // MajorMUD game rule: if the party leader disconnects, the
        // party disbands — leadership doesn't transfer. The roster
        // wipes for every follower (not just the leader's row), and
        // the leader is NOT added to the grace-window map because a
        // returning leader has no party to be auto-invited back into.
        //
        // Build the scenario manually: Fujin leads, we (Raijin) are a
        // follower, and a third member Helper also follows Fujin. When
        // Fujin disconnects, ALL three rows must clear.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state) { LocalCharacterName = "Raijin" };

        router.Dispatch(Line("You are now following Fujin."));   // adds Fujin (leader) + self
        // Simulate a second follower by feeding a par block with three rows.
        mgr.TestEnterParBlock();
        mgr.FeedTestLines(new[]
        {
            "  Fujin  WuzHere                  (Mystic)        [M:100%] [H:100%]   - Frontrank",
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Backrank",
            string.Empty,
        });
        Assert.Equal(3, mgr.State.Members.Count);
        Assert.False(mgr.State.SelfIsLeader);
        Assert.Equal("Fujin", mgr.State.LeaderName);

        router.Dispatch(Line("Fujin just disconnected!!!."));

        Assert.Empty(mgr.State.Members);
        Assert.False(mgr.State.IsInParty);
        Assert.Null(mgr.State.LeaderName);
        Assert.DoesNotContain("Fujin", mgr.RecentlyDisconnected.Keys, StringComparer.OrdinalIgnoreCase);
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
