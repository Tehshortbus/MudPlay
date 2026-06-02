using System.Linq;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyManagerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    /// <summary>
    /// Construct a fresh router with the default party patterns seeded and
    /// wire a PartyManager + PartyState. The manager doesn't need a real
    /// LineExtractor for the unit tests — par rows are fed via
    /// <see cref="PartyManager.FeedTestLines"/>.
    /// </summary>
    private static (MessageRouter router, PartyManager party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager party = new(router, state);
        return (router, party);
    }

    // ===== Single-line membership signals =====

    [Fact]
    public void FollowsYou_AddsMemberAndMarksPartyActive()
    {
        var (router, p) = Setup();
        router.Dispatch(Line("Forged now follows you."));

        Assert.True(p.State.IsInParty);
        PartyMember m = Assert.Single(p.State.Members);
        Assert.Equal("Forged", m.Name);
    }

    [Fact]
    public void FollowsYou_IsIdempotentOnRepeat()
    {
        // Re-observation of an already-known follower must not duplicate the
        // row — the same player can fire follows-you multiple times in a
        // session (after re-invite, after a disconnect-and-rejoin, etc.).
        var (router, p) = Setup();
        router.Dispatch(Line("Forged now follows you."));
        router.Dispatch(Line("Forged now follows you."));

        Assert.Single(p.State.Members);
    }

    [Fact]
    public void StopsFollowing_RemovesMemberAndClearsPartyWhenEmpty()
    {
        var (router, p) = Setup();
        router.Dispatch(Line("Forged now follows you."));
        Assert.True(p.State.IsInParty);

        router.Dispatch(Line("Forged stops following you."));

        Assert.False(p.State.IsInParty);
        Assert.Empty(p.State.Members);
    }

    [Fact]
    public void StopsFollowing_OnNonMember_IsHarmless()
    {
        var (router, p) = Setup();
        router.Dispatch(Line("Forged now follows you."));
        router.Dispatch(Line("Stranger stops following you."));

        Assert.Single(p.State.Members);
        Assert.True(p.State.IsInParty);
    }

    [Fact]
    public void StopsFollowing_LeaderLeaves_ClearsLeaderName()
    {
        var (router, p) = Setup();
        p.TestEnterParBlock();
        // Single par row with a leader marker.
        p.FeedTestLines(new[] { " * Forged    : Mage              100%    100%  Standing" });
        Assert.Equal("Forged", p.State.LeaderName);

        router.Dispatch(Line("Forged stops following you."));

        Assert.Null(p.State.LeaderName);
        Assert.Empty(p.State.Members);
        Assert.False(p.State.SelfIsLeader);
    }

    // ===== par-block parsing =====

    [Fact]
    public void ParBlock_SimpleTwoRow_PopulatesMembersWithPercents()
    {
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "   Name         Class            Hits     Mana",
            " * Forged     : Mage             100%     100%   Standing",
            "   Helper     : Cleric            94%      80%   Resting",
            "",
        });

        Assert.Equal(2, p.State.Members.Count);
        PartyMember leader = p.State.Members.First(x => x.Name == "Forged");
        PartyMember follower = p.State.Members.First(x => x.Name == "Helper");

        Assert.True(leader.IsLeader);
        Assert.Equal("Mage",   leader.Class);
        Assert.Equal(100,      leader.HpPercent);
        Assert.Equal(100,      leader.MpPercent);
        Assert.Equal(PlayerPosition.Standing, leader.Position);

        Assert.False(follower.IsLeader);
        Assert.Equal("Cleric", follower.Class);
        Assert.Equal(94,       follower.HpPercent);
        Assert.Equal(80,       follower.MpPercent);
        Assert.Equal(PlayerPosition.Resting, follower.Position);

        Assert.Equal("Forged", p.State.LeaderName);
        Assert.True(p.State.IsInParty);
    }

    [Fact]
    public void ParBlock_BlankLineEndsBlock()
    {
        // A blank line is the par-block terminator. Rows after it must not
        // get parsed (they could be normal game output that incidentally
        // matches the per-row regex shape).
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            " * Forged     : Mage             100%     100%   Standing",
            "",
            "   Ghost      : Bystander         50%      50%   Standing",
        });

        Assert.Single(p.State.Members);
        Assert.Equal("Forged", p.State.Members[0].Name);
    }

    [Fact]
    public void ParBlock_HeaderLineIsTolerated()
    {
        // The "Name Class Hits Mana" header doesn't match the row regex —
        // the parser stays in ReadingRows but doesn't add a row.
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "   Name         Class            Hits     Mana",
            "   ----         -----            ----     ----",
            " * Forged     : Mage             100%     100%   Standing",
        });

        Assert.Single(p.State.Members);
        Assert.Equal("Forged", p.State.Members[0].Name);
    }

    [Fact]
    public void ParBlock_PercentsUpdateOnRepeatObservation()
    {
        // First par observation establishes the member; second updates the
        // percentages on the same row (we don't add duplicates).
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[] { "   Helper     : Cleric           100%     100%   Standing" });
        p.TestEnterParBlock();
        p.FeedTestLines(new[] { "   Helper     : Cleric            42%      30%   Resting" });

        PartyMember m = Assert.Single(p.State.Members);
        Assert.Equal(42, m.HpPercent);
        Assert.Equal(30, m.MpPercent);
        Assert.Equal(PlayerPosition.Resting, m.Position);
    }

    [Fact]
    public void ParBlock_HeaderPatternDispatchEntersReadingRows()
    {
        // End-to-end: the "Party Status:" header line dispatched via the
        // real router catalogue should flip the state machine. This is the
        // entry point in production code — TestEnterParBlock is just a
        // shortcut for the cases above.
        var (router, p) = Setup();
        router.Dispatch(Line("Party Status:                          Hits     Mana"));
        p.FeedTestLines(new[] { " * Forged     : Mage              100%     100%   Standing" });

        Assert.Single(p.State.Members);
    }

    [Fact]
    public void DefaultPatterns_HaveTheThreePartyEntries()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        Assert.True(router.TryGetPattern(KnownPatterns.PartyFollowsYou, out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyStopsFollowing, out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyHeader, out _));
    }
}
