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
    public void NoteSelfDropped_WipesEverything()
    {
        // Dropping to 0 HP removes us from the party game-side — being dragged
        // afterwards is relocation, not membership. NoteSelfDropped clears the
        // stale roster so a follower/leader movement gate doesn't hold forever.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("You are now following Raijin."));
        Assert.True(p.State.IsInParty);
        Assert.Equal("Raijin", p.State.LeaderName);

        p.NoteSelfDropped();

        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Null(p.State.LeaderName);
    }

    [Fact]
    public void NoteSelfDropped_OnAlreadyEmpty_NoOp()
    {
        // Idempotent — a drop while already party-less mustn't churn state.
        var (router, p) = Setup();
        p.NoteSelfDropped();
        Assert.Empty(p.State.Members);
        Assert.False(p.State.IsInParty);
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

    // ===== WasRecentlyPartied — leader-side @comeback eligibility =====
    // A left-behind follower is dropped from the party server-side, so the
    // engine's IsActivePartyMember gate can't authorise their @comeback.
    // WasRecentlyPartied bridges the grace window: true only for senders who
    // departed (self-left / disconnected) inside DisconnectGraceWindow and
    // were NOT deliberately uninvited.

    [Fact]
    public void WasRecentlyPartied_StoppedFollowing_WithinWindow_IsEligible()
    {
        DateTimeOffset t0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.NowProvider = () => t0;
        router.Dispatch(Line("Raijin started to follow you."));

        // Raijin gets left behind / self-departs — stamps the grace window.
        router.Dispatch(Line("Raijin stops following you."));
        Assert.DoesNotContain(p.State.Members,
            m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase));

        Assert.True(p.WasRecentlyPartied("Raijin"));
    }

    [Fact]
    public void WasRecentlyPartied_FamilyNameSenderMatchesGivenNameStamp()
    {
        DateTimeOffset t0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.NowProvider = () => t0;

        // Follow/stop lines capture the given name only (the \w+ patterns
        // never span the space), so the grace-window key is "Raijin".
        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Raijin stops following you."));

        // The @comeback telepath, however, can arrive with the full
        // "given family" name — WasRecentlyPartied pairs it via GivenNameOf.
        Assert.True(p.WasRecentlyPartied("Raijin WuzHere"));
    }

    [Fact]
    public void WasRecentlyPartied_PastWindow_IsNotEligible()
    {
        DateTimeOffset t0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.DisconnectGraceWindow = TimeSpan.FromSeconds(30);
        p.NowProvider = () => t0;
        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Raijin stops following you."));

        // Past the grace window → stale → not eligible.
        p.NowProvider = () => t0.AddSeconds(31);
        Assert.False(p.WasRecentlyPartied("Raijin"));
    }

    [Fact]
    public void WasRecentlyPartied_DeliberatelyUninvited_IsNotEligible()
    {
        DateTimeOffset t0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.NowProvider = () => t0;
        router.Dispatch(Line("Raijin started to follow you."));

        // WE uninvited Raijin — "removed from your followers" does NOT
        // stamp the grace window, so they're correctly rejected.
        router.Dispatch(Line("Raijin has been removed from your followers."));

        Assert.False(p.WasRecentlyPartied("Raijin"));
    }

    [Fact]
    public void WasRecentlyPartied_NeverPartied_IsNotEligible()
    {
        var (_, p) = Setup(localCharacterName: "Fujin");
        Assert.False(p.WasRecentlyPartied("Stranger"));
    }

    [Fact]
    public void WasRecentlyPartied_EmptySender_IsNotEligible()
    {
        var (_, p) = Setup(localCharacterName: "Fujin");
        Assert.False(p.WasRecentlyPartied(""));
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
    public void ParBlock_Rank_ParsedPerMember()
    {
        // The trailing column of each par row carries the combat rank
        // (Frontrank / Midrank / Backrank). PartyMember.Rank stores it
        // so the PartyWindow can render the per-row rank chip in the
        // skeleton-design colour-coded style. Default Mid when missing.
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            "  Backline WuzHere                (Mage)          [M:100%] [H:100%]   - Backrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember fujin  = p.State.Members.First(x => x.Name == "Fujin WuzHere");
        PartyMember back   = p.State.Members.First(x => x.Name == "Backline WuzHere");
        Assert.Equal(Models.Profile.PartyRank.Mid,   raijin.Rank);
        Assert.Equal(Models.Profile.PartyRank.Front, fujin.Rank);
        Assert.Equal(Models.Profile.PartyRank.Back,  back.Rank);
    }

    [Fact]
    public void ParBlock_NonFullHpPercent_ParsesLeadingSpacePadding()
    {
        // MajorMUD right-pads the percentage to a 3-char column so the
        // table aligns: at 100% no padding ("[H:100%]"), at <100% a
        // leading space ("[H: 85%]"), at <10% two leading spaces
        // ("[H:  5%]"). Earlier regex required no whitespace between
        // the colon and the digits, so every non-100% par row silently
        // failed to match and PartyMember.HpPercent stayed frozen at
        // whatever the last 100% poll captured.
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M: 72%] [H: 85%]   - Backrank",
            "  Hurter WuzHere                  (Mystic)        [M:  3%] [H:  5%]   - Frontrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Midrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember hurter = p.State.Members.First(x => x.Name == "Hurter WuzHere");
        PartyMember fujin  = p.State.Members.First(x => x.Name == "Fujin WuzHere");
        Assert.Equal(85, raijin.HpPercent);
        Assert.Equal(72, raijin.MpPercent);
        Assert.Equal(5,  hurter.HpPercent);
        Assert.Equal(3,  hurter.MpPercent);
        Assert.Equal(100, fujin.HpPercent);
    }

    // ===== Initial-join rank-preference command (follower path only) =====
    // MajorMUD forces party leaders to frontrank — the server rejects a
    // rerank attempt from a leader — so the rank-preference command
    // only fires on the follower-join path ("You are now following X.").
    // The leader-join path ("X started to follow you.") never sends.

    [Fact]
    public void FollowerJoin_RankFront_SendsFrontrankCommand()
    {
        var (router, p) = Setup(localCharacterName: "Fujin");
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);
        p.LocalRankPreference = Models.Profile.PartyRank.Front;

        router.Dispatch(Line("You are now following Raijin."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("frontrank\r", System.Text.Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void FollowerJoin_RankBack_SendsBackrankCommand()
    {
        var (router, p) = Setup(localCharacterName: "Fujin");
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);
        p.LocalRankPreference = Models.Profile.PartyRank.Back;

        router.Dispatch(Line("You are now following Raijin."));

        byte[] sent = Assert.Single(wire);
        Assert.Equal("backrank\r", System.Text.Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void FollowerJoin_RankMid_SendsNothing()
    {
        // Mid is the server-side default rank — no command needed.
        var (router, p) = Setup(localCharacterName: "Fujin");
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);
        p.LocalRankPreference = Models.Profile.PartyRank.Mid;

        router.Dispatch(Line("You are now following Raijin."));

        Assert.Empty(wire);
    }

    [Fact]
    public void LeaderJoin_AnyRankPreference_SendsNothing()
    {
        // We're the leader — MajorMUD forces leaders to frontrank and
        // rejects a rerank attempt, so the preference must NOT be sent
        // even when set to Front or Back.
        var (router, p) = Setup(localCharacterName: "Fujin");
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);
        p.LocalRankPreference = Models.Profile.PartyRank.Back;

        router.Dispatch(Line("Raijin started to follow you."));

        Assert.Empty(wire);
    }

    [Fact]
    public void FollowerJoin_NoWireSender_DoesNotThrow()
    {
        // Defensive: PartyManager works without a wire-sender (the
        // construction order doesn't guarantee one's bound by the
        // time the first join lands).
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.LocalRankPreference = Models.Profile.PartyRank.Front;

        router.Dispatch(Line("You are now following Raijin."));
        // No assertion — passes if no exception.
    }

    [Fact]
    public void LiveRankChange_OtherMember_UpdatesRankImmediately()
    {
        // Observer side: when another party member reranks, the game
        // emits one of three phrasings (front/back keep "rank in your
        // group", middle drops "rank" → "of your group"). All three
        // must update PartyMember.Rank instantly — without waiting for
        // the next par poll — so the PartyWindow rank chip swaps colour
        // on the same tick the rerank message lands.
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });
        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        Assert.Equal(Models.Profile.PartyRank.Mid, raijin.Rank);

        router.Dispatch(Line("Raijin just moved to the front rank in your group."));
        Assert.Equal(Models.Profile.PartyRank.Front, raijin.Rank);

        router.Dispatch(Line("Raijin just moved to the back rank in your group."));
        Assert.Equal(Models.Profile.PartyRank.Back, raijin.Rank);

        router.Dispatch(Line("Raijin just moved to the middle of your group."));
        Assert.Equal(Models.Profile.PartyRank.Mid, raijin.Rank);
    }

    [Fact]
    public void LiveRankChange_Self_UpdatesIsSelfRow()
    {
        // Self side: the player making the change sees "You have moved
        // to the {front|middle|back} ranks of your group." (note the
        // consistent "ranks of" form — different from the observer-side
        // grammar). Routes to the IsSelf row by flag, since the
        // message carries no name.
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Midrank",
            string.Empty,
        });
        PartyMember self = p.State.Members.First(x => x.IsSelf);
        Assert.Equal(Models.Profile.PartyRank.Mid, self.Rank);

        router.Dispatch(Line("You have moved to the front ranks of your group."));
        Assert.Equal(Models.Profile.PartyRank.Front, self.Rank);

        router.Dispatch(Line("You have moved to the back ranks of your group."));
        Assert.Equal(Models.Profile.PartyRank.Back, self.Rank);

        router.Dispatch(Line("You have moved to the middle ranks of your group."));
        Assert.Equal(Models.Profile.PartyRank.Mid, self.Rank);
    }

    [Fact]
    public void LiveRankChange_UnknownPlayer_IsNoOp()
    {
        // Defensive: a rerank line for a name not in the roster (race
        // with disconnect / late-arriving observation) must not throw
        // and must not magic up a phantom member.
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Midrank",
            string.Empty,
        });
        int before = p.State.Members.Count;

        router.Dispatch(Line("Stranger just moved to the front rank in your group."));

        Assert.Equal(before, p.State.Members.Count);
    }

    [Fact]
    public void ParBlock_StateFlag_R_SetsRestingPosition()
    {
        // The single-letter column between [H:nn%] and the dash carries
        // the rest/meditate state — R = Resting, M = Meditating, blank
        // = Standing. Real Playpen format observed live.
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%] R - Midrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember fujin  = p.State.Members.First(x => x.Name == "Fujin WuzHere");
        Assert.Equal(PlayerPosition.Resting, raijin.Position);
        Assert.True(raijin.Resting);
        Assert.False(raijin.Meditating);
        // No state flag on Fujin's row → Standing.
        Assert.Equal(PlayerPosition.Standing, fujin.Position);
        Assert.False(fujin.Resting);
    }

    [Fact]
    public void ParBlock_StateFlag_M_SetsMeditatingPosition()
    {
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%] M - Midrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        Assert.Equal(PlayerPosition.Meditating, raijin.Position);
        Assert.True(raijin.Meditating);
        Assert.False(raijin.Resting);
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

    // ===== End-of-par-block reconciliation =====
    // A member the game dropped (walked away, logged out, got left behind)
    // stops appearing in the `par` table. Since the table terminates on the
    // statline prompt — never a blank line — the roster only self-corrects
    // when the prompt-line terminator drives ReconcileMissingFromPar. This is
    // the wording-independent membership fix: it never reads a departure
    // message, so it holds even on re-created realms with non-canonical text.

    [Fact]
    public void ParPoll_MemberAbsentFromPar_LeaderDropsThemOnPromptTerminator()
    {
        // Reproduces the reported desync: Fujin leads a 4-person party;
        // MindGoblin leaves on their own and the next par shows only 3, but
        // the roster stayed at 4 because reconciliation was gated on a blank
        // line the BBS never sends. The prompt terminator now closes the block.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("MindGoblin started to follow you."));
        router.Dispatch(Line("Suijin started to follow you."));
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.True(p.State.SelfIsLeader);
        Assert.Equal(4, p.State.Members.Count);   // self + 3 followers

        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin                          (Missionary) [M:100%] [H:100%]   - Frontrank",
            "  MindGoblin                     (Druid)      [M:100%] [H:100%]   - Midrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Midrank",
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        p.FeedTestPromptLine();
        Assert.Equal(4, p.State.Members.Count);   // everyone still present → no drop

        // MindGoblin walked upstairs; the next par omits them.
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin                          (Missionary) [M:100%] [H: 88%]   - Frontrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Midrank",
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        p.FeedTestPromptLine();

        Assert.Equal(3, p.State.Members.Count);
        Assert.DoesNotContain(p.State.Members, m => m.Name.StartsWith("MindGoblin", StringComparison.Ordinal));
        Assert.True(p.State.IsInParty);
        // Leader stamps the departed member so the auto-reinvite flow can
        // pull them back if they return within the grace window.
        Assert.True(p.WasRecentlyPartied("MindGoblin"));
    }

    [Fact]
    public void ParPoll_MemberAbsentFromPar_FollowerDropsButDoesNotStamp()
    {
        // A non-leader's roster must self-correct too — par is authoritative
        // for the whole travel party regardless of who leads. But a follower
        // can't invite anyone, so no grace-window stamp is recorded.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("You are now following Raijin."));
        Assert.False(p.State.SelfIsLeader);

        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Frontrank",
            "  Fujin                          (Missionary) [M:100%] [H:100%]   - Midrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Backrank",
        });
        p.FeedTestPromptLine();
        Assert.Equal(3, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Frontrank",
            "  Fujin                          (Missionary) [M:100%] [H:100%]   - Midrank",
        });
        p.FeedTestPromptLine();

        Assert.Equal(2, p.State.Members.Count);
        Assert.DoesNotContain(p.State.Members, m => m.Name.StartsWith("Suijin", StringComparison.Ordinal));
        Assert.False(p.WasRecentlyPartied("Suijin"));
    }

    [Fact]
    public void ParPoll_TruncatedParWithNoRows_DoesNotWipeRoster()
    {
        // Defensive guard: a par header immediately followed by the prompt
        // (garbled / truncated output, zero rows parsed) must not be read as
        // "everyone left the party".
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("MindGoblin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestPromptLine();

        Assert.Equal(2, p.State.Members.Count);
    }

    [Fact]
    public void ParPoll_SelfNeverReconciledAway_WhenAbsentFromOwnPar()
    {
        // The self row is skipped by reconciliation — a par table that (for
        // whatever display reason) omitted our own row must never drop us.
        var (router, p) = Setup(localCharacterName: "Fujin");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        p.FeedTestPromptLine();

        Assert.Contains(p.State.Members, m => m.IsSelf);
    }

    // ===== Mystic / monk kai bracket ([K:N%]) parses like mana =====
    // A Mystic party member's secondary resource is kai, rendered [K:N%] — not
    // [M:N%]. The par-row regex must accept both letters. Before it did, a
    // Mystic's [K:] row failed to match, so end-of-block reconciliation read the
    // Mystic as having left and dropped their roster row every poll — then
    // re-added them (re-firing PartyPoller's on-join @health round-trip) on the
    // one poll where their kai read 0% and the bracket vanished. This is the
    // mid-combat "/Fujin @health" spam the stock realm capture showed.

    [Fact]
    public void ParBlock_MysticKaiBracket_ParsesResourceIntoMpPercent()
    {
        var (_, p) = Setup(localCharacterName: "Raijin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                  (Mystic)     [K: 60%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
            string.Empty,
        });

        PartyMember fujin = p.State.Members.First(x => x.Name == "Fujin WuzHere");
        Assert.Equal("Mystic", fujin.Class);
        Assert.Equal(82, fujin.HpPercent);
        // Kai flows into the shared mp field (same as the @health reply's KAI=).
        Assert.Equal(60, fujin.MpPercent);
    }

    [Fact]
    public void ParPoll_MysticWithKai_StaysInRoster_AcrossKaiDrainingToZero()
    {
        // End-to-end reproduction: a Mystic leader whose [K:N%] rows failed to
        // parse got reconciled out of the roster every poll, then re-added on
        // the poll where kai read 0% (bracket omitted) — the re-add fired the
        // on-join @health round-trip mid-combat. The Mystic row must parse
        // whether kai is present or drained, so the SAME roster instance
        // persists across the cycle: no drop, no re-add, no @health re-fire.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        PartyMember fujin = p.State.Members.First(m => m.Name.StartsWith("Fujin", StringComparison.Ordinal));

        // Poll 1: Fujin has kai → [K:100%] bracket present.
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                  (Mystic)     [K:100%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
        });
        p.FeedTestPromptLine();
        Assert.Same(fujin, p.State.Members.First(m => m.Name.StartsWith("Fujin", StringComparison.Ordinal)));

        // Poll 2: Fujin's kai drained → the [K:] bracket vanishes entirely.
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                  (Mystic)              [H: 76%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 90%]   - Backrank",
        });
        p.FeedTestPromptLine();

        // Same instance throughout — never reconciled away, never re-added.
        Assert.Same(fujin, p.State.Members.First(m => m.Name.StartsWith("Fujin", StringComparison.Ordinal)));
        Assert.Equal(76, fujin.HpPercent);
    }

    [Fact]
    public void ParPoll_KnownCasterDrainedToZero_ShowsZeroNotStalePercent()
    {
        // par omits the [M:]/[K:] bracket at exactly 0 points. For a member we
        // KNOW has a secondary pool (a prior @health set BaselineMp), an absent
        // bracket means drained-to-0 — the mana bar must read 0, not freeze at
        // the last non-zero percent.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        // @health round-trip gives Fujin a known mana pool (BaselineMp > 0).
        p.SetMemberHealthSnapshot("Fujin WuzHere", hpCur: 200, hpMax: 200, mpCur: 300, mpMax: 300, isKai: false);
        PartyMember fujin = p.State.Members.First(m => m.Name.StartsWith("Fujin", StringComparison.Ordinal));

        // Poll 1: mana present at 40%.
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                  (Priest)     [M: 40%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
        });
        p.FeedTestPromptLine();
        Assert.Equal(40, fujin.MpPercent);

        // Poll 2: mana drained to 0 → [M:] bracket vanishes entirely.
        router.Dispatch(Line("The following people are in your travel party:"));
        p.FeedTestLines(new[]
        {
            "  Fujin WuzHere                  (Priest)              [H: 76%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 90%]   - Backrank",
        });
        p.FeedTestPromptLine();

        // The bar reads 0, not the stale 40 from poll 1.
        Assert.Equal(0, fujin.MpPercent);
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

    // ===== PartyMember.HpDisplay / MaDisplay =====
    //
    // The PartyWindow row template binds directly to these so the
    // visual updates without an MVVM-side converter. Before the on-join
    // @health round-trip completes (BaselineHp == 0), fall back to the
    // raw "percent%" form so the row still says something useful. After
    // the baseline arrives, compute current = baseline * percent / 100
    // and render "current/max".

    [Fact]
    public void HpDisplay_FallsBackToPercent_WhenBaselineUnknown()
    {
        PartyMember m = new() { HpPercent = 75 };
        Assert.Equal("75%", m.HpDisplay);
    }

    [Fact]
    public void HpDisplay_RendersCurOverMax_WhenBaselineKnown()
    {
        // 720 max at 90% = 648 current. Integer division matches the
        // percent display the par poll uses so the numbers don't drift
        // by ±1 across renders.
        PartyMember m = new() { BaselineHp = 720, HpPercent = 90 };
        Assert.Equal("648/720", m.HpDisplay);
    }

    [Fact]
    public void HpDisplay_UpdatesWhenPercentChanges()
    {
        // Verify the [NotifyPropertyChangedFor(nameof(HpDisplay))]
        // wiring — changing percent fires PropertyChanged for
        // HpDisplay so the bound TextBlock refreshes on every par
        // poll.
        PartyMember m = new() { BaselineHp = 200, HpPercent = 100 };
        Assert.Equal("200/200", m.HpDisplay);
        m.HpPercent = 50;
        Assert.Equal("100/200", m.HpDisplay);
    }

    [Fact]
    public void MaDisplay_FallsBackToPercent_WhenBaselineUnknown()
    {
        PartyMember m = new() { MpPercent = 33 };
        Assert.Equal("33%", m.MaDisplay);
    }

    [Fact]
    public void MaDisplay_RendersCurOverMax_WhenBaselineKnown()
    {
        PartyMember m = new() { BaselineMp = 300, MpPercent = 66 };
        Assert.Equal("198/300", m.MaDisplay);
    }

    // ===== PartyMember.HpRichDisplay / MaRichDisplay =====
    //
    // Skeleton-design verbose form for the stacked HP+MA column:
    // "H:cur/max pct%" / "M:cur/max pct%". Used by the PartyWindow's
    // new single stacked column (HEALTH · MANA). Falls back to
    // "H:pct%" when baseline is unknown to avoid an awkward double-
    // percent like "H:95% 95%".

    [Fact]
    public void HpRichDisplay_RendersFullForm_WhenBaselineKnown()
    {
        PartyMember m = new() { BaselineHp = 817, HpPercent = 95 };
        Assert.Equal("H:776/817 95%", m.HpRichDisplay);
    }

    [Fact]
    public void HpRichDisplay_FallsBackToPercentOnly_WhenBaselineUnknown()
    {
        PartyMember m = new() { HpPercent = 75 };
        Assert.Equal("H:75%", m.HpRichDisplay);
    }

    [Fact]
    public void MaRichDisplay_RendersFullForm_WhenBaselineKnown()
    {
        PartyMember m = new() { BaselineMp = 580, MpPercent = 98 };
        Assert.Equal("M:568/580 98%", m.MaRichDisplay);
    }

    [Fact]
    public void MaRichDisplay_LabelsKaiWithK_NotM()
    {
        // Mystic / monk secondary pool is kai — the row must read K:, not M:.
        PartyMember m = new() { BaselineMp = 200, MpPercent = 60, IsKai = true };
        Assert.Equal("K:120/200 60%", m.MaRichDisplay);
    }

    [Fact]
    public void ParPoll_MysticHealthReply_LabelsSecondaryAsKai()
    {
        // A Mystic member's @health reply carries KAI= (not MA=); the party row
        // must pick that up and label the bar K:.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        p.SetMemberHealthSnapshot("Fujin WuzHere", hpCur: 200, hpMax: 200, mpCur: 120, mpMax: 200, isKai: true);
        PartyMember fujin = p.State.Members.First(m => m.Name.StartsWith("Fujin", StringComparison.Ordinal));
        Assert.True(fujin.IsKai);
        Assert.Equal("K:120/200 60%", fujin.MaRichDisplay);
    }

    [Fact]
    public void HpRichDisplay_UpdatesWhenPercentChanges()
    {
        // Validates the [NotifyPropertyChangedFor(HpRichDisplay)]
        // wiring on the percent field — par-poll percent ticks must
        // re-fire the bound TextBlock in the new stacked layout.
        PartyMember m = new() { BaselineHp = 200, HpPercent = 100 };
        Assert.Equal("H:200/200 100%", m.HpRichDisplay);
        m.HpPercent = 50;
        Assert.Equal("H:100/200 50%", m.HpRichDisplay);
    }

    // ===== Self row mirrors PlayerState =====
    //
    // The on-join @health round-trip is intentionally skipped for the
    // local character (PromptParser is the fresher source). Mirroring
    // PlayerState into the self PartyMember row keeps the PartyWindow
    // current with per-prompt HP/MA changes between par polls.

    [Fact]
    public void AttachPlayerState_MirrorsHpMaInto_SelfRow_OnPropertyChange()
    {
        var (router, p) = Setup(localCharacterName: "Fujin");
        PlayerState ps = new();
        p.AttachPlayerState(ps);
        // Form a party so the self row gets added via AddSelfIfKnown.
        router.Dispatch(Line("Helper started to follow you."));
        PartyMember self = p.State.Members.Single(m => m.IsSelf);
        Assert.Equal(0, self.BaselineHp);   // no PlayerState data yet

        // Simulate a prompt update — PromptParser writes these fields
        // on every status-line observation.
        ps.MaxHp = 150;
        ps.Hp    = 90;
        ps.MaxMa = 80;
        ps.Ma    = 40;

        Assert.Equal(150, self.BaselineHp);
        Assert.Equal(60,  self.HpPercent);  // 90/150 = 60%
        Assert.Equal(80,  self.BaselineMp);
        Assert.Equal(50,  self.MpPercent);  // 40/80  = 50%
        Assert.Equal("90/150", self.HpDisplay);
        Assert.Equal("40/80",  self.MaDisplay);
    }

    [Fact]
    public void AttachPlayerState_SelfRow_SyncsOnAddEvenWhenStateWasPrePopulated()
    {
        // Reverse order: PlayerState already has values, then we form
        // a party — the new self row should get the values via the
        // CollectionChanged sync, not just on the next PropertyChanged.
        var (router, p) = Setup(localCharacterName: "Fujin");
        PlayerState ps = new() { MaxHp = 200, Hp = 100, MaxMa = 50, Ma = 25 };
        p.AttachPlayerState(ps);

        router.Dispatch(Line("Helper started to follow you."));
        PartyMember self = p.State.Members.Single(m => m.IsSelf);

        Assert.Equal(200, self.BaselineHp);
        Assert.Equal(50,  self.HpPercent);
        Assert.Equal(50,  self.BaselineMp);
        Assert.Equal(50,  self.MpPercent);
    }

    [Fact]
    public void AttachPlayerState_NoMana_LeavesBaselineMpZero()
    {
        // Warrior-style ManaType.None: MaxMa stays 0 → BaselineMp stays
        // 0 → PartyWindow hides the MA column (or shows the empty
        // placeholder for alignment) per the existing converter.
        var (router, p) = Setup(localCharacterName: "Tank");
        PlayerState ps = new() { MaxHp = 850, Hp = 850, MaxMa = 0, Ma = 0 };
        p.AttachPlayerState(ps);

        router.Dispatch(Line("Helper started to follow you."));
        PartyMember self = p.State.Members.Single(m => m.IsSelf);

        Assert.Equal(850, self.BaselineHp);
        Assert.Equal(0,   self.BaselineMp);
    }

    // ===== Pending-invite tracking ("You have invited X to follow you.") =====

    [Fact]
    public void YouInvited_AddsRowWithIsInvitedTrue()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("You have invited Helper to follow you."));

        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);
        // Two members: self (leader) + the invitee row.
        Assert.Equal(2, p.State.Members.Count);
        PartyMember invitee = p.State.Members.Single(m => m.Name == "Helper");
        Assert.True(invitee.IsInvited);
        Assert.False(invitee.IsLeader);
        Assert.False(invitee.IsSelf);
    }

    [Fact]
    public void YouInvited_SetsSelfIsLeaderSoUninviteButtonEnables()
    {
        // The PartyWindow's X-button IsEnabled binds to
        // State.SelfIsLeader. Sending an invite while solo must flip
        // the flag so the user can withdraw the offer immediately,
        // without waiting for the invitee to accept.
        var (router, p) = Setup(localCharacterName: "Forged");
        Assert.False(p.State.SelfIsLeader);

        router.Dispatch(Line("You have invited Helper to follow you."));

        Assert.True(p.State.SelfIsLeader);
    }

    [Fact]
    public void Acceptance_ClearsIsInvitedOnSameRow()
    {
        // Invite then acceptance — the SAME row's IsInvited flips
        // to false; no second row added (matched by given name).
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("You have invited Helper to follow you."));
        Assert.True(p.State.Members.Single(m => m.Name == "Helper").IsInvited);

        router.Dispatch(Line("Helper started to follow you."));

        Assert.Equal(2, p.State.Members.Count);
        Assert.False(p.State.Members.Single(m => m.Name == "Helper").IsInvited);
    }

    [Fact]
    public void Uninvite_SendsWireCommandWithGivenName()
    {
        // Manager-level Uninvite is the wire-send escape hatch the
        // PartyWindow row's X button reuses (via PartyViewModel.Uninvite,
        // which routes through SendUserInput in production). Verify the
        // shape directly here so the wire matches "uninvite X\r" with
        // family stripped.
        var (_, p) = Setup();
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);

        p.Uninvite("Helper Lastname");

        byte[] sent = Assert.Single(wire);
        Assert.Equal("uninvite Helper\r", System.Text.Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Uninvite_BlankName_NoOps()
    {
        var (_, p) = Setup();
        List<byte[]> wire = new();
        p.SetWireSender(wire.Add);

        p.Uninvite(string.Empty);
        p.Uninvite("   ");

        Assert.Empty(wire);
    }

    // ===== par's invited-row form ========================================
    // par re-seeds pending invitees alongside real members so the user
    // can return mid-session (or after a relaunch) and recover the
    // INVITED rows without needing to have witnessed the original
    // "You have invited X to follow you." echo.

    [Fact]
    public void ParInvitedRow_AddsRowWithIsInvitedTrue()
    {
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });

        Assert.Equal(2, p.State.Members.Count);
        PartyMember invitee = p.State.Members.Single(m => m.Name == "Raijin WuzHere");
        Assert.True(invitee.IsInvited);
        Assert.Equal("Priest", invitee.Class);
        // The HP / position / rank fields stay at their defaults — par's
        // invited form carries no live data for them.
        Assert.Equal(0, invitee.HpPercent);
        Assert.Equal(PlayerPosition.Standing, invitee.Position);
    }

    [Fact]
    public void ParInvitedRow_FlipsSelfIsLeader()
    {
        // par's invited form is sufficient by itself to flip
        // SelfIsLeader=true so the PartyWindow X button enables on
        // the invitee's row immediately, even if we relaunched and
        // never saw the original outbound echo.
        var (_, p) = Setup(localCharacterName: "Fujin");
        Assert.False(p.State.SelfIsLeader);

        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            string.Empty,
        });

        Assert.True(p.State.SelfIsLeader);
        Assert.True(p.State.IsInParty);
    }

    [Fact]
    public void ParInvitedRow_AcceptanceClearsIsInvited()
    {
        // Cross-session continuity check: par re-seeds the row as
        // invited; then the player accepts via "X started to follow
        // you." → existing OnFollowsYou flips IsInvited=false on the
        // same row (matched by given name), no double-add.
        var (router, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            string.Empty,
        });
        Assert.True(p.State.Members.Single(m => m.Name == "Raijin WuzHere").IsInvited);

        router.Dispatch(Line("Raijin started to follow you."));

        Assert.Single(p.State.Members, m => m.Name.StartsWith("Raijin"));
        Assert.False(p.State.Members.Single(m => m.Name.StartsWith("Raijin")).IsInvited);
    }

    [Fact]
    public void ParInvitedRow_MixedWithNormalRows_BothParse()
    {
        // The two row regexes must not collide — the invited regex is
        // strictly narrower and is tried first. A par block with both
        // invited and real members should round-trip cleanly.
        var (_, p) = Setup(localCharacterName: "Fujin");
        p.TestEnterParBlock();
        p.FeedTestLines(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            "  Helper Lastname                 (Cleric)        [M:80%] [H:94%]   - Backrank",
            "  Fujin WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });

        PartyMember invitee = p.State.Members.Single(m => m.Name == "Raijin WuzHere");
        Assert.True(invitee.IsInvited);
        PartyMember realMember = p.State.Members.Single(m => m.Name == "Helper Lastname");
        Assert.False(realMember.IsInvited);
        Assert.Equal(94, realMember.HpPercent);
        Assert.Equal(80, realMember.MpPercent);
        Assert.Equal(Models.Profile.PartyRank.Back, realMember.Rank);
    }

    // ===== SetMemberAilment (inbound chip set, find-only) =====

    [Fact]
    public void SetMemberAilment_PresentMember_SetsChip()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));

        p.SetMemberAilment("Helper", Models.GameData.MessageFlags.Poisoned, true);

        PartyMember helper = p.State.Members.Single(m => m.Name == "Helper");
        Assert.True(helper.Poisoned);
    }

    [Fact]
    public void SetMemberAilment_MatchesByGivenName()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));
        PartyMember helper = p.State.Members.Single(m => m.Name == "Helper");
        helper.Name = "Helper Lastname";

        // Inbound say carries only the given name "Helper".
        p.SetMemberAilment("Helper", Models.GameData.MessageFlags.Blinded, true);

        Assert.True(helper.Blinded);
    }

    [Fact]
    public void SetMemberAilment_AbsentMember_IsNoOp()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));

        // Stranger isn't in the roster → nothing happens, no phantom added.
        p.SetMemberAilment("Stranger", Models.GameData.MessageFlags.Diseased, true);

        Assert.DoesNotContain(p.State.Members, m => m.Name == "Stranger");
        Assert.All(p.State.Members, m => Assert.False(m.Diseased));
    }

    // ===== "X stops to rest." observed-rest line =====

    [Fact]
    public void MemberRestObserved_FlipsRestingFlag()
    {
        // We follow Fujin (leader). Seeing "Fujin stops to rest." flips the
        // leader's Resting flag immediately, ahead of the next par poll.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        PartyMember leader = p.State.Members.Single(m => m.Name == "Fujin");
        Assert.False(leader.Resting);

        router.Dispatch(Line("Fujin stops to rest."));

        Assert.True(leader.Resting);
    }

    [Fact]
    public void MemberRestObserved_GivenNameMatchesFamilyRoster()
    {
        // Roster row carries a family name; the observed line has only the given
        // name — GivenNameOf matching still resolves it.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));
        PartyMember leader = p.State.Members.Single(m => m.Name == "Fujin");
        leader.Name = "Fujin WuzHere";

        router.Dispatch(Line("Fujin stops to rest."));

        Assert.True(leader.Resting);
    }

    [Fact]
    public void MemberRestObserved_NonMember_IsNoOp()
    {
        // A bystander resting in the room isn't on the roster → no phantom row,
        // no flag change on existing members.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following Fujin."));

        router.Dispatch(Line("Stranger stops to rest."));

        Assert.DoesNotContain(p.State.Members, m => m.Name == "Stranger");
        Assert.All(p.State.Members, m => Assert.False(m.Resting));
    }
}
