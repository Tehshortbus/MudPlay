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
    /// Build a router with the default party patterns seeded and wire a
    /// PartyManager + PartyState. The manager doesn't need a real
    /// LineExtractor for these unit tests — par rows are fed via
    /// <see cref="PartyManager.FeedTestLines"/>.
    /// </summary>
    private static (MessageRouter router, PartyManager party) Setup(string? localCharacterName = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager party = new(router, state);
        if (localCharacterName is not null) party.LocalCharacterName = localCharacterName;
        return (router, party);
    }

    // ===== Single-line membership signals =====
    // Real-BBS-verified phrasings (Playpen BBS):
    //   "X started to follow you."     — X joined our party (we lead)
    //   "You are now following X."     — we joined X's party (X leads)
    //   "X has stopped following you." / "X stops following you." — X left

    [Fact]
    public void FollowsYou_AddsMemberAndMarksUsLeader()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));

        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);
        Assert.Equal("Forged", p.State.LeaderName);
        // Two members: Helper (just joined) + Forged (self, marked leader)
        Assert.Equal(2, p.State.Members.Count);
        Assert.Contains(p.State.Members, m => m.Name == "Helper" && !m.IsSelf && !m.IsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Forged" &&  m.IsSelf &&  m.IsLeader);
    }

    [Fact]
    public void FollowsYou_NoLocalName_StillAddsMember()
    {
        // No profile loaded → LocalCharacterName=null → can't self-stamp.
        // We still record the follower and flip SelfIsLeader.
        var (router, p) = Setup();
        router.Dispatch(Line("Helper started to follow you."));

        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);
        Assert.Single(p.State.Members);
        Assert.Equal("Helper", p.State.Members[0].Name);
    }

    [Fact]
    public void FollowsYou_IsIdempotentOnRepeat()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));

        // Helper added once, self added once. No duplicates on repeat.
        Assert.Equal(2, p.State.Members.Count);
    }

    [Fact]
    public void YouFollowing_AddsLeaderAndMarksUsFollower()
    {
        // "You are now following Fujin." — Fujin leads us.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));

        Assert.True(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Equal("Fujin", p.State.LeaderName);
        Assert.Contains(p.State.Members, m => m.Name == "Fujin"  && !m.IsSelf &&  m.IsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Raijin" &&  m.IsSelf && !m.IsLeader);
    }

    [Fact]
    public void StopsFollowing_RemovesMemberAndClearsPartyWhenEmpty()
    {
        // Solo test path — no self in roster, so removing the only
        // follower leaves zero members.
        var (router, p) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(p.State.IsInParty);

        router.Dispatch(Line("Helper has stopped following you."));

        Assert.False(p.State.IsInParty);
        Assert.Empty(p.State.Members);
    }

    [Fact]
    public void StopsFollowing_AltWording_StopsFollowingYou_AlsoMatches()
    {
        // Alternation handles both observed BBS phrasings.
        var (router, p) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper stops following you."));

        Assert.Empty(p.State.Members);
    }

    [Fact]
    public void StopsFollowing_OnNonMember_IsHarmless()
    {
        var (router, p) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Stranger has stopped following you."));

        Assert.Single(p.State.Members);
        Assert.True(p.State.IsInParty);
    }

    // ===== Dissolution signals (Playpen-verified 2026-06-01 uninvite scenario) =====
    //   Leader's view (Fujin uninvites Raijin):
    //     "Raijin has been removed from your followers."
    //     "You are not in a party at the present time."
    //   Follower's view (Raijin sees Fujin's uninvite):
    //     "You are no longer following Fujin."
    //     "You are not in a party at the present time."

    [Fact]
    public void FollowerRemoved_RemovesMember_FromLeaderView()
    {
        // Leader scenario — we lead, then we uninvite Raijin. After the
        // "has been removed from your followers" line the roster drops
        // them. The follow-up "not in a party" line would land too in
        // a real session — covered separately.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);   // Fujin self + Raijin

        router.Dispatch(Line("Raijin has been removed from your followers."));

        Assert.DoesNotContain(p.State.Members,
            m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void YouNoLongerFollowing_RemovesLeader_FromFollowerView()
    {
        // Follower scenario — we joined Fujin's party, then Fujin
        // uninvites us. The follow-up line on our terminal is "You are
        // no longer following Fujin." which evicts Fujin from the
        // roster.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        Assert.Equal(2, p.State.Members.Count);   // Fujin leader + Raijin self

        router.Dispatch(Line("You are no longer following Fujin."));

        Assert.DoesNotContain(p.State.Members,
            m => m.Name.Equals("Fujin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dissolved_WipesEverything()
    {
        // Authoritative dissolution — regardless of how the per-row
        // evictions landed, this line guarantees an empty party.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);

        router.Dispatch(Line("You are not in a party at the present time."));

        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Null(p.State.LeaderName);
    }

    [Fact]
    public void Dissolved_OnAlreadyEmpty_NoOp()
    {
        // Idempotent — receiving the dissolution line when we're
        // already party-less shouldn't churn observable properties.
        var (router, p) = Setup();
        router.Dispatch(Line("You are not in a party at the present time."));
        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
    }

    [Fact]
    public void FullUninviteSequence_LeaderSide_LeavesEmptyParty()
    {
        // End-to-end replay of the screenshot scenario: Fujin
        // uninvites Raijin. Both signal lines arrive in sequence; the
        // dissolved line is what guarantees a clean wipe even if the
        // per-row eviction has self-membership quirks.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("Raijin has been removed from your followers."));
        router.Dispatch(Line("You are not in a party at the present time."));

        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Null(p.State.LeaderName);
    }

    [Fact]
    public void FullUninviteSequence_FollowerSide_LeavesEmptyParty()
    {
        // Mirror scenario from Raijin's side after Fujin uninvites him.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("You are no longer following Fujin."));
        router.Dispatch(Line("You are not in a party at the present time."));

        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
    }

    // ===== IsSelf detection against full-display LocalCharacterName =====

    [Fact]
    public void ParRow_LocalCharacterNameWithFamily_StillMatchesSelfRow()
    {
        // The screenshot bug: profile.Name was "Fujin WuzHere" and the
        // par row was "Fujin WuzHere ...". Pre-fix the IsSelf compare
        // extracted "Fujin" from the par row but compared it to the
        // full "Fujin WuzHere" LocalCharacterName → mismatch → our
        // own row had IsSelf=false → PartyPoller telepathed
        // /Fujin @health to ourselves. Both sides now reduce to given.
        var (router, p) = Setup(localCharacterName: "Fujin WuzHere");
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  Fujin WuzHere                                   (Mystic)            [H:100%]   - Frontrank",
            string.Empty,
        });

        PartyMember? self = p.State.Members.FirstOrDefault(m => m.IsSelf);
        Assert.NotNull(self);
        Assert.Equal("Fujin WuzHere", self!.Name);
    }

    // ===== Dissolution flushes par-block state machine =====

    [Fact]
    public void Dissolved_FlushesParBlockState_PreventingGhostAdd()
    {
        // Real Playpen output: after the first par returns, the
        // par-block parser stays in ReadingRows because the BBS doesn't
        // emit a blank-line terminator between the par table and the
        // next prompt. When dissolution fires on the next poll, the
        // "Fujin WuzHere" solo row that follows the dissolution line
        // would otherwise match ParRow and re-add Fujin — keeping
        // IsInParty true and the poller alive.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)                [H:100%]   - Midrank",
            "  Fujin WuzHere                   (Mystic)                [H:100%]   - Frontrank",
            // NO blank line — the par-block parser carries ReadingRows
            // forward to the next poll cycle just like real Playpen.
        });
        Assert.True(p.State.IsInParty);

        // Next poll: dissolution line + lone Fujin row in "solo" par.
        router.Dispatch(Line("You are not in a party at the present time."));
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                   (Mystic)                [H:100%]   - Midrank",
        });

        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
    }

    // ===== par-block parsing — real Playpen BBS format =====
    //   "The following people are in your travel party:"
    //     Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank
    //     Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank

    [Fact]
    public void ParBlock_RealBbsFormat_PopulatesMembers()
    {
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            "",
        });

        Assert.Equal(2, p.State.Members.Count);
        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember fujin  = p.State.Members.First(x => x.Name == "Fujin WuzHere");

        Assert.Equal("Priest", raijin.Class);
        Assert.Equal(100,      raijin.HpPercent);
        Assert.Equal(100,      raijin.MpPercent);
        Assert.False(raijin.IsSelf);

        Assert.Equal("Mystic", fujin.Class);
        Assert.Equal(100,      fujin.HpPercent);
        // Mystic at low level has no Kai — [M:] omitted; MpPercent stays at default (0)
        Assert.Equal(0,        fujin.MpPercent);
        Assert.True(fujin.IsSelf);  // matched LocalCharacterName="Fujin"
    }

    [Fact]
    public void ParBlock_BlankLineEndsBlock()
    {
        // A blank line is the par-block terminator — rows after it must
        // not get parsed.
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Forged WuzHere                  (Mage)          [M:100%] [H:100%]   - Frontrank",
            "",
            "  Ghost WuzHere                   (Bystander)     [M:50%] [H:50%]    - Backrank",
        });

        Assert.Single(p.State.Members);
        Assert.Equal("Forged WuzHere", p.State.Members[0].Name);
    }

    [Fact]
    public void ParBlock_HeaderLineIsTolerated()
    {
        // The "Name Class Hits Mana" header doesn't match the row regex.
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "   Name         Class            Hits     Mana",
            "   ----         -----            ----     ----",
            "  Forged WuzHere                  (Mage)          [M:100%] [H:100%]   - Frontrank",
        });

        Assert.Single(p.State.Members);
        Assert.Equal("Forged WuzHere", p.State.Members[0].Name);
    }

    [Fact]
    public void ParBlock_PercentsUpdateOnRepeatObservation()
    {
        var (_, p) = Setup();
        p.TestEnterParBlock();
        p.FeedTestLines(new[] { "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank" });
        p.TestEnterParBlock();
        p.FeedTestLines(new[] { "  Helper WuzHere                  (Cleric)        [M:30%] [H:42%]    - Midrank" });

        PartyMember m = Assert.Single(p.State.Members);
        Assert.Equal(42, m.HpPercent);
        Assert.Equal(30, m.MpPercent);
    }

    [Fact]
    public void ParBlock_HeaderPatternDispatchEntersReadingRows()
    {
        // End-to-end: the real BBS header line dispatched via the router
        // catalogue should flip the state machine into ReadingRows.
        var (router, p) = Setup();
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[] { "  Forged WuzHere                  (Mage)          [M:100%] [H:100%]   - Frontrank" });

        Assert.Single(p.State.Members);
        Assert.Equal("Forged WuzHere", p.State.Members[0].Name);
    }

    [Fact]
    public void DefaultPatterns_HaveAllPartyEntries()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        Assert.True(router.TryGetPattern(KnownPatterns.PartyFollowsYou,     out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyYouFollowing,   out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyStopsFollowing, out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyHeader,         out _));
        Assert.True(router.TryGetPattern(KnownPatterns.PartyMemberDeath,    out _));
    }
}
