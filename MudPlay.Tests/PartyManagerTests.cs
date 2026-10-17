using System.Linq;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyManagerTests
{
    // The clock the PartyManager reads: the real one unless a test sets its own.
    private Func<DateTimeOffset> _partyNow = () => DateTimeOffset.UtcNow;

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // The router the manager reads, and the game's output fed through it and the
    // extractor the manager is attached to, the way the client wires them.
    private readonly MessageRouter _router = new();
    private readonly ServerFeed _feed;

    public PartyManagerTests()
    {
        DefaultPatterns.Seed(_router);
        _feed = new ServerFeed(_router);
    }

    // A PartyManager + PartyState on the class router, attached to the class feed so
    // par rows arrive through the extractor as they do in the client.
    private (MessageRouter router, PartyManager party) Setup(string? localCharacterName = null)
    {
        MessageRouter router = _router;
        PartyState state = new();
        PartyManager party = new(router, state, now: () => _partyNow());
        party.AttachLineExtractor(_feed.Lines);
        if (localCharacterName is not null) party.LocalCharacterName = localCharacterName;
        return (router, party);
    }

    // ===== Leader-side reconnect reform =====

    [Fact]
    public void BeginLeaderReconnectReform_FiresReInvite_WhenAutoInviteEnabled()
    {
        // A leader-drop dissolved the party; on reconnect we re-invite the remembered
        // followers (who stayed put) — the co-located re-invite the passive grace window
        // never does.
        var (_, p) = Setup();
        p.AutoInviteEnabled = true;
        IReadOnlyList<string>? invited = null;
        p.LeaderReconnectReformInvites += list => invited = list;

        p.BeginLeaderReconnectReform(new[] { "Raijin", "Suijin" });

        Assert.Equal(new[] { "Raijin", "Suijin" }, invited);
    }

    [Fact]
    public void BeginLeaderReconnectReform_NoReInvite_WhenAutoInviteDisabled()
    {
        var (_, p) = Setup();
        p.AutoInviteEnabled = false;
        bool fired = false;
        p.LeaderReconnectReformInvites += _ => fired = true;

        p.BeginLeaderReconnectReform(new[] { "Raijin" });

        Assert.False(fired);
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
        // "You are now following MudPlay." — MudPlay leads us.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));

        Assert.True(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Equal("MudPlay", p.State.LeaderName);
        Assert.Contains(p.State.Members, m => m.Name == "MudPlay"  && !m.IsSelf &&  m.IsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Raijin" &&  m.IsSelf && !m.IsLeader);
    }

    [Fact]
    public void YouFollowing_RaisesFollowingLeader_WithTheStateSet()
    {
        // The ailment re-announce reads the party state when this fires, so it must
        // come after the leader is set, never in between.
        var (router, p) = Setup(localCharacterName: "Raijin");
        string? leader = null;
        bool consistent = false;
        p.FollowingLeader += name =>
        {
            leader = name;
            consistent = p.State.IsInParty && !p.State.SelfIsLeader && p.State.LeaderName == name;
        };

        router.Dispatch(Line("You are now following MudPlay."));

        Assert.Equal("MudPlay", leader);
        Assert.True(consistent);
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
    //   Leader's view (MudPlay uninvites Raijin):
    //     "Raijin has been removed from your followers."
    //     "You are not in a party at the present time."
    //   Follower's view (Raijin sees MudPlay's uninvite):
    //     "You are no longer following MudPlay."
    //     "You are not in a party at the present time."

    [Fact]
    public void FollowerRemoved_RemovesMember_FromLeaderView()
    {
        // Leader scenario — we lead, then we uninvite Raijin. After the
        // "has been removed from your followers" line the roster drops
        // them. The follow-up "not in a party" line would land too in
        // a real session — covered separately.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);   // MudPlay self + Raijin

        router.Dispatch(Line("Raijin has been removed from your followers."));

        Assert.DoesNotContain(p.State.Members,
            m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void YouNoLongerFollowing_DissolvesFollowerPartyToSolo()
    {
        // Follower scenario — we joined MudPlay's party, then we leave it: the
        // leader uninvited us, we unfollowed, or we died. "You are no longer
        // following MudPlay." means we're solo now, so the WHOLE roster clears — not
        // just the leader. Dropping only MudPlay leaves self in Members, IsInParty
        // stays true, and @join/@invite keep replying "I'm following someone;
        // denied." (reported: died via suicide, client still thought it followed).
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        Assert.Equal(2, p.State.Members.Count);   // MudPlay leader + Raijin self

        router.Dispatch(Line("You are no longer following MudPlay."));

        // Solo now — the roster keeps the lone self row (PartyWindow self-display),
        // but it's no longer a party.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
        Assert.False(p.State.SelfIsLeader);
    }

    [Fact]
    public void SelfDropped_WhileFollowing_ClearsToSolo()
    {
        // Instant death (`suicide`) skips the HP<=0 drop, so PlayerDeathObserved →
        // NoteSelfDropped is the path that clears our party — including for a leader,
        // who never sees a "no longer following" line.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        Assert.True(p.State.IsInParty);

        p.NoteSelfDropped();

        // Lone self row remains (solo self-display); not a party.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
    }

    [Fact]
    public void Dissolved_WipesEverything()
    {
        // Authoritative dissolution — regardless of how the per-row
        // evictions landed, this line guarantees an empty party.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);

        router.Dispatch(Line("You are not in a party at the present time."));

        // Lone self row remains (solo self-display); not a party.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("You are now following Raijin."));
        Assert.True(p.State.IsInParty);
        Assert.Equal("Raijin", p.State.LeaderName);

        p.NoteSelfDropped();

        // Lone self row remains (solo self-display); not a party.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
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
    public void NoteTrainStatsExcursion_Follower_ClearsFollowingState()
    {
        // Entering the train-stats screen breaks up our party server-side; a
        // follower's own stale "following <leader>" state must clear so the
        // leader's fresh re-invite on return is auto-accepted, not rejected as
        // "already following" (report stock-20260801-002423).
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("You are now following Raijin."));
        Assert.True(p.State.IsInParty);
        Assert.Equal("Raijin", p.State.LeaderName);

        p.NoteTrainStatsExcursion();

        // Lone self row remains (solo self-display); following state cleared.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
    }

    [Fact]
    public void NoteTrainStatsExcursion_Leader_LeavesRosterIntact()
    {
        // A LEADER reforms its group on trainer exit from a roster snapshot that
        // needs SelfIsLeader intact — so its own state must NOT reset here.
        var (router, p) = Setup(localCharacterName: "Forged");
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(p.State.SelfIsLeader);

        p.NoteTrainStatsExcursion();

        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Helper");
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
        // End-to-end replay of the screenshot scenario: MudPlay
        // uninvites Raijin. Both signal lines arrive in sequence; the
        // dissolved line is what guarantees a clean wipe even if the
        // per-row eviction has self-membership quirks.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("Raijin has been removed from your followers."));
        router.Dispatch(Line("You are not in a party at the present time."));

        // Lone self row remains (solo self-display); party fully wound down.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _partyNow = () => t0;
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _partyNow = () => t0;

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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        p.DisconnectGraceWindow = TimeSpan.FromSeconds(30);
        p.ComebackWindow = TimeSpan.FromMinutes(2);
        _partyNow = () => t0;
        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("Raijin stops following you."));

        // Eligibility runs on the @comeback window, not the re-invite one.
        _partyNow = () => t0.AddSeconds(31);
        Assert.True(p.WasRecentlyPartied("Raijin"));
        _partyNow = () => t0.AddSeconds(121);
        Assert.False(p.WasRecentlyPartied("Raijin"));
    }

    [Fact]
    public void WasRecentlyPartied_DeliberatelyUninvited_IsNotEligible()
    {
        DateTimeOffset t0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _partyNow = () => t0;
        router.Dispatch(Line("Raijin started to follow you."));

        // WE uninvited Raijin — "removed from your followers" does NOT
        // stamp the grace window, so they're correctly rejected.
        router.Dispatch(Line("Raijin has been removed from your followers."));

        Assert.False(p.WasRecentlyPartied("Raijin"));
    }

    [Fact]
    public void WasRecentlyPartied_NeverPartied_IsNotEligible()
    {
        var (_, p) = Setup(localCharacterName: "MudPlay");
        Assert.False(p.WasRecentlyPartied("Stranger"));
    }

    [Fact]
    public void WasRecentlyPartied_EmptySender_IsNotEligible()
    {
        var (_, p) = Setup(localCharacterName: "MudPlay");
        Assert.False(p.WasRecentlyPartied(""));
    }

    [Fact]
    public void FullUninviteSequence_FollowerSide_LeavesEmptyParty()
    {
        // Mirror scenario from Raijin's side after MudPlay uninvites him.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("You are no longer following MudPlay."));
        router.Dispatch(Line("You are not in a party at the present time."));

        // Lone self row remains (solo self-display); following state wound down.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
    }

    // ===== IsSelf detection against full-display LocalCharacterName =====

    [Fact]
    public void ParRow_LocalCharacterNameWithFamily_StillMatchesSelfRow()
    {
        // The screenshot bug: profile.Name was "MudPlay WuzHere" and the
        // par row was "MudPlay WuzHere ...". Pre-fix the IsSelf compare
        // extracted "MudPlay" from the par row but compared it to the
        // full "MudPlay WuzHere" LocalCharacterName → mismatch → our
        // own row had IsSelf=false → PartyPoller telepathed
        // /MudPlay @health to ourselves. Both sides now reduce to given.
        var (router, p) = Setup(localCharacterName: "MudPlay WuzHere");
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  MudPlay WuzHere                                   (Mystic)            [H:100%]   - Frontrank",
            string.Empty,
        });

        PartyMember? self = p.State.Members.FirstOrDefault(m => m.IsSelf);
        Assert.NotNull(self);
        Assert.Equal("MudPlay WuzHere", self!.Name);
    }

    // ===== Dissolution flushes par-block state machine =====

    [Fact]
    public void Dissolved_FlushesParBlockState_PreventingGhostAdd()
    {
        // Real Playpen output: after the first par returns, the
        // par-block parser stays in ReadingRows because the BBS doesn't
        // emit a blank-line terminator between the par table and the
        // next prompt. When dissolution fires on the next poll, the
        // "MudPlay WuzHere" solo row that follows the dissolution line
        // would otherwise match ParRow and re-add MudPlay — keeping
        // IsInParty true and the poller alive.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Raijin started to follow you."));
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)                [H:100%]   - Midrank",
            "  MudPlay WuzHere                   (Mystic)                [H:100%]   - Frontrank",
            // NO blank line — the par-block parser carries ReadingRows
            // forward to the next poll cycle just like real Playpen.
        });
        Assert.True(p.State.IsInParty);

        // Next poll: dissolution line + lone MudPlay row in "solo" par.
        router.Dispatch(Line("You are not in a party at the present time."));
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                   (Mystic)                [H:100%]   - Midrank",
        });

        // The flushed par-block parser ignores the trailing solo row, so no GHOST
        // member is added and IsInParty stays false. The roster holds only the lone
        // self row (solo self-display), re-seeded by the dissolution reset.
        Assert.Single(p.State.Members);
        Assert.True(p.State.Members[0].IsSelf);
        Assert.False(p.State.IsInParty);
    }

    // ===== par-block parsing — real Playpen BBS format =====
    //   "The following people are in your travel party:"
    //     Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank
    //     MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank

    [Fact]
    public void ParBlock_RealBbsFormat_PopulatesMembers()
    {
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            "",
        });

        Assert.Equal(2, p.State.Members.Count);
        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember mudplay  = p.State.Members.First(x => x.Name == "MudPlay WuzHere");

        Assert.Equal("Priest", raijin.Class);
        Assert.Equal(100,      raijin.HpPercent);
        Assert.Equal(100,      raijin.MpPercent);
        Assert.False(raijin.IsSelf);

        Assert.Equal("Mystic", mudplay.Class);
        Assert.Equal(100,      mudplay.HpPercent);
        // Mystic at low level has no Kai — [M:] omitted; MpPercent stays at default (0)
        Assert.Equal(0,        mudplay.MpPercent);
        Assert.True(mudplay.IsSelf);  // matched LocalCharacterName="MudPlay"
    }

    [Fact]
    public void ParBlock_Rank_ParsedPerMember()
    {
        // The trailing column of each par row carries the combat rank
        // (Frontrank / Midrank / Backrank). PartyMember.Rank stores it
        // so the PartyWindow can render the per-row rank chip in the
        // skeleton-design colour-coded style. Default Mid when missing.
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            "  Backline WuzHere                (Mage)          [M:100%] [H:100%]   - Backrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember mudplay  = p.State.Members.First(x => x.Name == "MudPlay WuzHere");
        PartyMember back   = p.State.Members.First(x => x.Name == "Backline WuzHere");
        Assert.Equal(Models.Profile.PartyRank.Mid,   raijin.Rank);
        Assert.Equal(Models.Profile.PartyRank.Front, mudplay.Rank);
        Assert.Equal(Models.Profile.PartyRank.Back,  back.Rank);
    }

    [Fact]
    public void ParBlock_PoisonedRow_StillParsesRank()
    {
        // A poisoned member's par row butts a trailing `P` flag right up
        // against the HP bracket with no space ("[H: 89%]P  - Frontrank").
        // Before the regex consumed that flag the `-` rank suffix never
        // matched on a poisoned row, so rank fell through to its Mid default
        // and the PartyWindow demoted a force-frontranked (poisoned) leader
        // to midrank the moment they were poisoned. Rank must survive the flag.
        var (_, p) = Setup(localCharacterName: "MindGoblin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H: 89%]P  - Frontrank",
            "  MindGoblin                     (Druid)      [M: 98%] [H:100%]   - Midrank",
            "  Suijin                         (Mage)       [M: 98%] [H: 81%]P  - Backrank",
            string.Empty,
        });

        PartyMember mudplay  = p.State.Members.First(x => x.Name == "MudPlay");
        PartyMember suijin = p.State.Members.First(x => x.Name == "Suijin");
        Assert.Equal(Models.Profile.PartyRank.Front, mudplay.Rank);
        Assert.Equal(Models.Profile.PartyRank.Back,  suijin.Rank);
        Assert.Equal(89, mudplay.HpPercent);
        Assert.Equal(81, suijin.HpPercent);
    }

    [Fact]
    public void ParBlock_PoisonFlag_DrivesOtherMemberPoisonChip_SetAndClear()
    {
        // The par `P` flag is the authoritative cross-client source for an OTHER
        // member's poison chip — poison is never announced verbosely, so par is
        // the only way to see it. A poll with the flag sets the chip; a later poll
        // without it clears the chip straight off the flag dropping.
        var (_, p) = Setup(localCharacterName: "MindGoblin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Suijin                         (Mage)       [M: 98%] [H: 81%]P  - Backrank",
            "  MindGoblin                     (Druid)      [M: 98%] [H:100%]   - Midrank",
            string.Empty,
        });
        PartyMember suijin = p.State.Members.First(x => x.Name == "Suijin");
        Assert.True(suijin.Poisoned);   // P flag present → chip set

        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Suijin                         (Mage)       [M: 98%] [H: 95%]   - Backrank",
            "  MindGoblin                     (Druid)      [M: 98%] [H:100%]   - Midrank",
            string.Empty,
        });
        Assert.False(suijin.Poisoned);  // flag gone → chip cleared
    }

    [Fact]
    public void ParBlock_PoisonFlag_OnSelfRow_DoesNotDriveSelfChip()
    {
        // Self's poison chip is owned by ConditionTracker (timelier than the 5s par
        // poll and apply/wear-off-message-driven), so par must NOT set it even when
        // our own row shows `P`.
        var (_, p) = Setup(localCharacterName: "MindGoblin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MindGoblin                     (Druid)      [M: 98%] [H: 70%]P  - Midrank",
            string.Empty,
        });
        PartyMember self = p.State.Members.First(x => x.IsSelf);
        Assert.Equal("MindGoblin", self.Name);
        Assert.False(self.Poisoned);    // par does not drive the self chip
    }

    [Fact]
    public void ParBlock_PoisonFlag_DoesNotDemoteLeaderAcrossPolls()
    {
        // Regression for the reported bug: a clean par poll sets the leader
        // Front, then a later poll where the leader is poisoned must NOT
        // silently revert them to Mid. The rank has to hold across the
        // poison-flagged poll.
        var (_, p) = Setup(localCharacterName: "MindGoblin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Frontrank",
            string.Empty,
        });
        Assert.Equal(Models.Profile.PartyRank.Front,
            p.State.Members.First(x => x.Name == "MudPlay").Rank);

        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H: 89%]P  - Frontrank",
            string.Empty,
        });
        Assert.Equal(Models.Profile.PartyRank.Front,
            p.State.Members.First(x => x.Name == "MudPlay").Rank);
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
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M: 72%] [H: 85%]   - Backrank",
            "  Hurter WuzHere                  (Mystic)        [M:  3%] [H:  5%]   - Frontrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Midrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember hurter = p.State.Members.First(x => x.Name == "Hurter WuzHere");
        PartyMember mudplay  = p.State.Members.First(x => x.Name == "MudPlay WuzHere");
        Assert.Equal(85, raijin.HpPercent);
        Assert.Equal(72, raijin.MpPercent);
        Assert.Equal(5,  hurter.HpPercent);
        Assert.Equal(3,  hurter.MpPercent);
        Assert.Equal(100, mudplay.HpPercent);
    }

    // ===== Initial-join rank-preference command (follower path only) =====
    // MajorMUD forces party leaders to frontrank — the server rejects a
    // rerank attempt from a leader — so the rank-preference command
    // only fires on the follower-join path ("You are now following X.").
    // The leader-join path ("X started to follow you.") never sends.

    [Fact]
    public void FollowerJoin_RankFront_SendsFrontrankCommand()
    {
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Midrank",
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Midrank",
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
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [M:100%] [H:100%] R - Midrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
            string.Empty,
        });

        PartyMember raijin = p.State.Members.First(x => x.Name == "Raijin WuzHere");
        PartyMember mudplay  = p.State.Members.First(x => x.Name == "MudPlay WuzHere");
        Assert.Equal(PlayerPosition.Resting, raijin.Position);
        Assert.True(raijin.Resting);
        Assert.False(raijin.Meditating);
        // No state flag on MudPlay's row → Standing.
        Assert.Equal(PlayerPosition.Standing, mudplay.Position);
        Assert.False(mudplay.Resting);
    }

    [Fact]
    public void ParBlock_StateFlag_M_SetsMeditatingPosition()
    {
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
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
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
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
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
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
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[] { "  Helper WuzHere                  (Cleric)        [M:100%] [H:100%]   - Midrank" });
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[] { "  Helper WuzHere                  (Cleric)        [M:30%] [H:42%]    - Midrank" });

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
        _feed.Rows(new[] { "  Forged WuzHere                  (Mage)          [M:100%] [H:100%]   - Frontrank" });

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
        // Reproduces the reported desync: MudPlay leads a 4-person party;
        // MindGoblin leaves on their own and the next par shows only 3, but
        // the roster stayed at 4 because reconciliation was gated on a blank
        // line the BBS never sends. The prompt terminator now closes the block.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("MindGoblin started to follow you."));
        router.Dispatch(Line("Suijin started to follow you."));
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.True(p.State.SelfIsLeader);
        Assert.Equal(4, p.State.Members.Count);   // self + 3 followers

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Frontrank",
            "  MindGoblin                     (Druid)      [M:100%] [H:100%]   - Midrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Midrank",
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
        Assert.Equal(4, p.State.Members.Count);   // everyone still present → no drop

        // MindGoblin walked upstairs; the next par omits them.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H: 88%]   - Frontrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Midrank",
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("You are now following Raijin."));
        Assert.False(p.State.SelfIsLeader);

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Frontrank",
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Midrank",
            "  Suijin                         (Mage)       [M:100%] [H:100%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
        Assert.Equal(3, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Frontrank",
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Midrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("MindGoblin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.Equal(2, p.State.Members.Count);
    }

    // A consumer that asked `par` a question acts on ParReplyRead, so it is raised
    // once the roster agrees with the reply: after a block with rows, after the
    // game's "no party" line, and not for a block that was cut short.
    [Fact]
    public void ParReplyRead_IsRaisedOnceTheRosterAgreesWithTheReply()
    {
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Member started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));
        List<int> rosterAtRead = new();
        p.ParReplyRead += () => rosterAtRead.Add(p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Prompt("[HP=0/MA=0]:");                           // cut short: answers nothing
        Assert.Empty(rosterAtRead);

        // The first reply that leaves a member out takes them off the roster.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Frontrank",
            "  Helper                         (Mage)       [M:100%] [H:100%]   - Midrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
        Assert.Equal(new[] { 2 }, rosterAtRead);
        Assert.DoesNotContain(p.State.Members, m => m.Name == "Member");

        router.Dispatch(Line("You are not in a party at the present time."));
        Assert.Equal(new[] { 2, 1 }, rosterAtRead);        // the lone self row
    }

    // A member the reply lists as `[Invited]` is on the list and not following.
    // The row stays, marked invited, which is how a reader tells them from one
    // who came along.
    [Fact]
    public void ParReply_ListingAFollowerAsInvited_KeepsTheRowMarkedInvited()
    {
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Member started to follow you."));
        router.Dispatch(Line("Helper started to follow you."));

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Member                          (Priest)        [Invited]",
            "  Helper                         (Mage)       [M:100%] [H:100%]   - Midrank",
            "  MudPlay                          (Missionary) [M:100%] [H:100%]   - Frontrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.True(p.State.Members.Single(m => m.Name == "Member").IsInvited);
        Assert.False(p.State.Members.Single(m => m.Name == "Helper").IsInvited);
    }

    [Fact]
    public void ParPoll_SelfNeverReconciledAway_WhenAbsentFromOwnPar()
    {
        // The self row is skipped by reconciliation — a par table that (for
        // whatever display reason) omitted our own row must never drop us.
        var (router, p) = Setup(localCharacterName: "MudPlay");
        router.Dispatch(Line("Raijin started to follow you."));
        Assert.Equal(2, p.State.Members.Count);

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Raijin                         (Priest)     [M:100%] [H:100%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.Contains(p.State.Members, m => m.IsSelf);
    }

    // ===== Mystic / monk kai bracket ([K:N%]) parses like mana =====
    // A Mystic party member's secondary resource is kai, rendered [K:N%] — not
    // [M:N%]. The par-row regex must accept both letters. Before it did, a
    // Mystic's [K:] row failed to match, so end-of-block reconciliation read the
    // Mystic as having left and dropped their roster row every poll — then
    // re-added them (re-firing PartyPoller's on-join @health round-trip) on the
    // one poll where their kai read 0% and the bracket vanished. This is the
    // mid-combat "/MudPlay @health" spam the stock realm capture showed.

    [Fact]
    public void ParBlock_MysticKaiBracket_ParsesResourceIntoMpPercent()
    {
        var (_, p) = Setup(localCharacterName: "Raijin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                  (Mystic)     [K: 60%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
            string.Empty,
        });

        PartyMember mudplay = p.State.Members.First(x => x.Name == "MudPlay WuzHere");
        Assert.Equal("Mystic", mudplay.Class);
        Assert.Equal(82, mudplay.HpPercent);
        // Kai flows into the shared mp field (same as the @health reply's KAI=).
        Assert.Equal(60, mudplay.MpPercent);
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
        router.Dispatch(Line("You are now following MudPlay."));
        PartyMember mudplay = p.State.Members.First(m => m.Name.StartsWith("MudPlay", StringComparison.Ordinal));

        // Poll 1: MudPlay has kai → [K:100%] bracket present.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                  (Mystic)     [K:100%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
        Assert.Same(mudplay, p.State.Members.First(m => m.Name.StartsWith("MudPlay", StringComparison.Ordinal)));

        // Poll 2: MudPlay's kai drained → the [K:] bracket vanishes entirely.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                  (Mystic)              [H: 76%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 90%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        // Same instance throughout — never reconciled away, never re-added.
        Assert.Same(mudplay, p.State.Members.First(m => m.Name.StartsWith("MudPlay", StringComparison.Ordinal)));
        Assert.Equal(76, mudplay.HpPercent);
    }

    [Fact]
    public void ParPoll_KnownCasterDrainedToZero_ShowsZeroNotStalePercent()
    {
        // par omits the [M:]/[K:] bracket at exactly 0 points. For a member we
        // KNOW has a secondary pool (a prior @health set BaselineMp), an absent
        // bracket means drained-to-0 — the mana bar must read 0, not freeze at
        // the last non-zero percent.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        // @health round-trip gives MudPlay a known mana pool (BaselineMp > 0).
        p.SetMemberHealthSnapshot("MudPlay WuzHere", hpCur: 200, hpMax: 200, mpCur: 300, mpMax: 300, isKai: false);
        PartyMember mudplay = p.State.Members.First(m => m.Name.StartsWith("MudPlay", StringComparison.Ordinal));

        // Poll 1: mana present at 40%.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                  (Priest)     [M: 40%] [H: 82%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 86%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
        Assert.Equal(40, mudplay.MpPercent);

        // Poll 2: mana drained to 0 → [M:] bracket vanishes entirely.
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  MudPlay WuzHere                  (Priest)              [H: 76%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M:100%] [H: 90%]   - Backrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        // The bar reads 0, not the stale 40 from poll 1.
        Assert.Equal(0, mudplay.MpPercent);
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
        router.Dispatch(Line("You are now following MudPlay."));
        p.SetMemberHealthSnapshot("MudPlay WuzHere", hpCur: 200, hpMax: 200, mpCur: 120, mpMax: 200, isKai: true);
        PartyMember mudplay = p.State.Members.First(m => m.Name.StartsWith("MudPlay", StringComparison.Ordinal));
        Assert.True(mudplay.IsKai);
        Assert.Equal("K:120/200 60%", mudplay.MaRichDisplay);
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        Assert.Equal("H:90/150 60%", self.HpRichDisplay);
        Assert.Equal("M:40/80 50%",  self.MaRichDisplay);
    }

    [Fact]
    public void AttachPlayerState_SelfRow_SyncsOnAddEvenWhenStateWasPrePopulated()
    {
        // Reverse order: PlayerState already has values, then we form
        // a party — the new self row should get the values via the
        // CollectionChanged sync, not just on the next PropertyChanged.
        var (router, p) = Setup(localCharacterName: "MudPlay");
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
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
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
        var (_, p) = Setup(localCharacterName: "MudPlay");
        Assert.False(p.State.SelfIsLeader);

        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
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
        var (router, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
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
    public void ParJoinedRow_ClearsStaleInvite_WithoutFollowsYouLine()
    {
        // Follower's-view bug: "X started to follow you." prints only for the
        // LEADER, so a non-leader client never sees OnFollowsYou clear the invite
        // chip. The invitee was seeded IsInvited via par's "[Invited]" row; once
        // they accept, the follower sees them only as a normal joined par row (with
        // an [H:] bracket). That joined row must clear the stale invite — otherwise
        // the member is stuck "Invited" forever, their health stays hidden, and the
        // healer skips them.
        var (_, p) = Setup(localCharacterName: "Raijin");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Suijin WuzHere                   (Mage)          [Invited]",
            string.Empty,
        });
        Assert.True(p.State.Members.Single(m => m.Name == "Suijin WuzHere").IsInvited);

        // Next poll — no follows-you line ever arrived; Suijin now shows as a
        // joined member with live vitals.
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Suijin WuzHere                   (Mage)          [M:98%] [H: 83%]   - Midrank",
            string.Empty,
        });

        PartyMember suijin = p.State.Members.Single(m => m.Name == "Suijin WuzHere");
        Assert.False(suijin.IsInvited);
        Assert.Equal(83, suijin.HpPercent);
        Assert.Equal(98, suijin.MpPercent);
    }

    [Fact]
    public void ParInvitedRow_MixedWithNormalRows_BothParse()
    {
        // The two row regexes must not collide — the invited regex is
        // strictly narrower and is tried first. A par block with both
        // invited and real members should round-trip cleanly.
        var (_, p) = Setup(localCharacterName: "MudPlay");
        _feed.Line("The following people are in your travel party:");
        _feed.Rows(new[]
        {
            "  Raijin WuzHere                  (Priest)        [Invited]",
            "  Helper Lastname                 (Cleric)        [M:80%] [H:94%]   - Backrank",
            "  MudPlay WuzHere                   (Mystic)                  [H:100%]   - Frontrank",
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
        // We follow MudPlay (leader). Seeing "MudPlay stops to rest." flips the
        // leader's Resting flag immediately, ahead of the next par poll.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        PartyMember leader = p.State.Members.Single(m => m.Name == "MudPlay");
        Assert.False(leader.Resting);

        router.Dispatch(Line("MudPlay stops to rest."));

        Assert.True(leader.Resting);
    }

    [Fact]
    public void MemberRestObserved_GivenNameMatchesFamilyRoster()
    {
        // Roster row carries a family name; the observed line has only the given
        // name — GivenNameOf matching still resolves it.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));
        PartyMember leader = p.State.Members.Single(m => m.Name == "MudPlay");
        leader.Name = "MudPlay WuzHere";

        router.Dispatch(Line("MudPlay stops to rest."));

        Assert.True(leader.Resting);
    }

    [Fact]
    public void MemberRestObserved_NonMember_IsNoOp()
    {
        // A bystander resting in the room isn't on the roster → no phantom row,
        // no flag change on existing members.
        var (router, p) = Setup(localCharacterName: "Raijin");
        router.Dispatch(Line("You are now following MudPlay."));

        router.Dispatch(Line("Stranger stops to rest."));

        Assert.DoesNotContain(p.State.Members, m => m.Name == "Stranger");
        Assert.All(p.State.Members, m => Assert.False(m.Resting));
    }

    // ===== Solo self row (PartyWindow self-display) =====

    [Fact]
    public void SoloSelfRow_AppearsWhenCharacterNameSet_NotAParty()
    {
        var (_, p) = Setup(localCharacterName: "Forged");

        // A lone self row is kept while solo so the PartyWindow shows the local
        // character — but it is NOT a party.
        PartyMember self = Assert.Single(p.State.Members);
        Assert.True(self.IsSelf);
        Assert.Equal("Forged", self.Name);
        Assert.False(self.IsLeader);
        Assert.False(p.State.IsInParty);
    }

    [Fact]
    public void SoloSelfRow_RemovedWhenNameCleared()
    {
        var (_, p) = Setup(localCharacterName: "Forged");
        Assert.Single(p.State.Members);

        p.LocalCharacterName = null;   // profile close

        Assert.Empty(p.State.Members);
    }

    [Fact]
    public void SoloSelfRow_SwappedWhenNameChanges()
    {
        var (_, p) = Setup(localCharacterName: "Forged");

        p.LocalCharacterName = "Raijin";   // profile swap to another character

        PartyMember self = Assert.Single(p.State.Members);
        Assert.True(self.IsSelf);
        Assert.Equal("Raijin", self.Name);
    }

    [Fact]
    public void SoloSelfRow_ReusedWhenPartyForms()
    {
        var (router, p) = Setup(localCharacterName: "Forged");
        Assert.Single(p.State.Members);   // solo self row

        router.Dispatch(Line("Helper started to follow you."));

        // The solo self row is reused (marked leader), not duplicated; Helper joins.
        Assert.True(p.State.IsInParty);
        Assert.Equal(2, p.State.Members.Count);
        Assert.Single(p.State.Members, m => m.IsSelf);
        Assert.Contains(p.State.Members, m => m.IsSelf && m.IsLeader && m.Name == "Forged");
        Assert.Contains(p.State.Members, m => m.Name == "Helper" && !m.IsSelf);
    }

    [Fact]
    public void SoloSelfRow_HpSyncsFromPlayerState()
    {
        var (_, p) = Setup(localCharacterName: "Forged");
        PlayerState player = new();
        p.AttachPlayerState(player);   // mirrors PlayerState onto the self row

        player.MaxHp = 200;
        player.Hp = 50;

        PartyMember self = Assert.Single(p.State.Members);
        Assert.True(self.IsSelf);
        Assert.Equal(200, self.BaselineHp);
        Assert.Equal(25, self.HpPercent);   // 50 / 200
    }

    // ===== a follow the game ended without a line (Stock) =====

    // The leader refused our @comeback, or never came. The follow we still count
    // is cleared as the game's own "no longer following" line clears it, and the
    // follower's hold on our own movement goes with it.
    [Fact]
    public void NoteFollowGivenUp_ClearsTheFollow_AndFreesOurMovement()
    {
        var (router, p) = Setup("MudPlay WuzHere");
        var coordinator = new MudPlay.Game.Map.MovementCoordinator();
        using var gate = new PartyFollowerMovementGate(p.State, coordinator);
        router.Dispatch(Line("You are now following Boss."));
        Assert.True(p.State.IsInParty);
        Assert.Contains(MudPlay.Game.Map.MovementCoordinator.FollowerGate, coordinator.AssertedGates);

        p.NoteFollowGivenUp("Boss");

        Assert.False(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Null(p.State.LeaderName);
        Assert.DoesNotContain(MudPlay.Game.Map.MovementCoordinator.FollowerGate, coordinator.AssertedGates);
    }

    // Another leader's name, a party we lead and no party at all are left alone:
    // where the game's own line has ended the follow (Paradigm) there is nothing
    // to clear, and a leader who refused us long ago is not the one we follow now.
    [Fact]
    public void NoteFollowGivenUp_ForAnotherLeader_OrWhenNotFollowing_DoesNothing()
    {
        var (router, p) = Setup("MudPlay WuzHere");
        p.NoteFollowGivenUp("Boss");
        Assert.False(p.State.IsInParty);

        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Other");
        Assert.True(p.State.IsInParty);
        Assert.Equal("Boss", p.State.LeaderName);

        router.Dispatch(Line("You are no longer following Boss."));
        Assert.False(p.State.IsInParty);
        p.NoteFollowGivenUp("Boss");          // the game's line came first
        Assert.False(p.State.IsInParty);
    }

    // ----- a follower an exit refused: Stock keeps them on the leader's list -----
    //
    // GAME_MECHANICS "A follower who can't move is left behind". With the realm
    // probe unset or false (Paradigm) nothing differs from what it always was.

    // What a follower still on the old leader's list, and following nobody, is
    // printed for `par` (GAME_MECHANICS "`par` output block"): no "You are
    // following" line, the listed members as [Invited], its own row last.
    private void ParOfAListedFollower(MessageRouter router)
    {
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Boss Hogg                      (Warrior)    [Invited]",
            "  Other Guy                      (Mage)       [Invited]",
            "  Me Myself                      (Priest)     [M:100%] [H: 86%]   - Midrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");
    }

    // On Stock giving the follow up frees our own engines (the roster is solo),
    // and the leader is kept: the game has not taken us off their list.
    [Fact]
    public void Stock_TheFollowIsGivenUp_RosterGoesSolo_AndTheLeaderIsKeptAsListedWith()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("You are now following Boss."));

        p.NoteFollowGivenUp("Boss");

        Assert.False(p.State.IsInParty);
        Assert.Null(p.State.LeaderName);
        Assert.Equal("Boss", p.ListedWithLeader);
    }

    [Fact]
    public void Paradigm_TheFollowIsGivenUp_NothingIsKept()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        router.Dispatch(Line("You are now following Boss."));

        p.NoteFollowGivenUp("Boss");

        Assert.False(p.State.IsInParty);
        Assert.Null(p.ListedWithLeader);
    }

    // Every line that says where we stand ends the belief.
    [Theory]
    [InlineData("You are now following Boss.")]
    [InlineData("You are now following Other.")]
    [InlineData("You are no longer following Boss.")]
    [InlineData("You are not in a party at the present time.")]
    [InlineData("You have invited Other to follow you.")]
    [InlineData("Other started to follow you.")]
    [InlineData("Boss just hung up!!!")]
    public void Stock_ListedWith_EndsOnTheGamesOwnWord(string line)
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        Assert.Equal("Boss", p.ListedWithLeader);

        router.Dispatch(Line(line));

        Assert.Null(p.ListedWithLeader);
    }

    [Fact]
    public void Stock_ListedWith_EndsWhenAJoinIsRefused_OrTheLinkDrops_OrWeDrop()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;

        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        p.NoteNotListedWith("Other");
        Assert.Equal("Boss", p.ListedWithLeader);        // another leader's refusal says nothing
        p.NoteNotListedWith("Boss");
        Assert.Null(p.ListedWithLeader);

        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        p.NoteLinkDropped();
        Assert.Null(p.ListedWithLeader);

        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        p.NoteSelfDropped();
        Assert.Null(p.ListedWithLeader);
    }

    [Fact]
    public void Paradigm_AfterTheFollowIsGivenUp_AParOfInvitedRows_MakesUsTheLeader()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        Assert.False(p.State.IsInParty);

        ParOfAListedFollower(router);

        Assert.True(p.State.IsInParty);
        Assert.True(p.State.SelfIsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Boss Hogg" && m.IsInvited);
    }

    // On Stock the same reply is the old leader's party, listed [Invited] because
    // we follow none of them: no invite of ours, so nothing makes us a leader.
    [Fact]
    public void Stock_AfterTheFollowIsGivenUp_AParOfInvitedRows_LeavesUsSolo_AndStillListed()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");
        int replies = 0;
        p.ParReplyRead += () => replies++;

        ParOfAListedFollower(router);

        Assert.False(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Null(p.State.LeaderName);
        Assert.DoesNotContain(p.State.Members, m => !m.IsSelf);
        Assert.Equal("Boss", p.ListedWithLeader);
        Assert.Equal(1, replies);
    }

    // The leader is gone from the list (they left the game): we are listed with
    // nobody, and the rows of that reply are still not applied.
    [Fact]
    public void Stock_AParThatNoLongerListsTheLeader_EndsListedWith()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Other Guy                      (Mage)       [Invited]",
            "  Me Myself                      (Priest)     [M:100%] [H: 86%]   - Midrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.Null(p.ListedWithLeader);
        Assert.False(p.State.SelfIsLeader);
        Assert.False(p.State.IsInParty);
    }

    // Leader side. A member who followed us and now reads [Invited] on Stock was
    // left behind at an exit and is still on our list; one we only invited is not.
    [Fact]
    public void Stock_AMemberWhoFollowedUs_ThenReadsInvited_IsLeftBehindOnOurList()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("You have invited Newcomer to follow you."));
        Assert.False(p.IsLeftBehindOnOurList("Tank"));            // following

        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Tank Smith                     (Warrior)    [Invited]",
            "  Newcomer Jones                 (Mage)       [Invited]",
            "  Me Myself                      (Priest)     [M:100%] [H: 86%]   - Frontrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.True(p.IsLeftBehindOnOurList("Tank"));
        Assert.True(p.IsLeftBehindOnOurList("Tank Smith"));
        Assert.False(p.IsLeftBehindOnOurList("Newcomer"));        // invited, never joined

        router.Dispatch(Line("Tank started to follow you."));
        Assert.False(p.IsLeftBehindOnOurList("Tank"));            // back with us
    }

    [Fact]
    public void Stock_LeftBehindOnOurList_EndsWhenTheRowGoes()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("You have invited Tank to follow you."));   // row reads invited
        Assert.True(p.IsLeftBehindOnOurList("Tank"));

        router.Dispatch(Line("Tank has been removed from your followers."));
        router.Dispatch(Line("You have invited Tank to follow you."));   // a fresh invite

        Assert.False(p.IsLeftBehindOnOurList("Tank"));
    }

    [Fact]
    public void Paradigm_NobodyIsEverLeftBehindOnOurList()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("You have invited Tank to follow you."));

        Assert.False(p.IsLeftBehindOnOurList("Tank"));
    }

    // The game's own "You are following X." outranks the belief: an ordinary
    // follower's reply, read as one.
    [Fact]
    public void Stock_AParThatSaysWeFollow_IsReadAsAFollowers()
    {
        var (router, p) = Setup(localCharacterName: "Me");
        p.OnStock = () => true;
        router.Dispatch(Line("You are now following Boss."));
        p.NoteFollowGivenUp("Boss");

        router.Dispatch(Line("You are following Boss."));
        router.Dispatch(Line("The following people are in your travel party:"));
        _feed.Rows(new[]
        {
            "  Boss Hogg                      (Warrior)             [H:100%]   - Frontrank",
            "  Me Myself                      (Priest)     [M:100%] [H: 86%]   - Midrank",
        });
        _feed.Prompt("[HP=0/MA=0]:");

        Assert.Null(p.ListedWithLeader);
        Assert.True(p.State.IsInParty);
        Assert.False(p.State.SelfIsLeader);
        Assert.Contains(p.State.Members, m => m.Name == "Boss Hogg" && !m.IsInvited);
    }
}
