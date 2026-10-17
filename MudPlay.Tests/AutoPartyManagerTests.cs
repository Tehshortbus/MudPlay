using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

#pragma warning disable CA1859 // Intentional interface-typed deps for readability in tests.

namespace MudPlay.Tests;

public sealed class AutoPartyManagerTests
{
    // The clock the AutoPartyManager reads: the real one unless a test sets its own.
    private Func<DateTime> _partyNow = () => DateTime.UtcNow;
    // The manager's nag loop and reform-redisplay ticks.
    private readonly ManualTimers _partyTimers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // What the engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

    // Self-contained engine + every dependency. Default patterns are
    // seeded so the RoomAlsoHere / PartyInviteReceived regexes are
    // resolvable. Tests dispatch via the router with synthesised
    // EmittedLine values and read what the engine sent from _sent.
    private (AutoPartyManager engine, MessageRouter router, PlayerDatabase players, PartyState party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        PartyState party = new();
        AutoPartyManager engine = new(router, players, party, schedule: _partyTimers.Arm, now: () => _partyNow());
        _partyNow = () => Now;
        // The wire-sender-null guard exists to prevent TTL burn during the
        // startup window where AutoPartyManager subscribes before
        // MainWindowViewModel binds the sender — tests model the post-bind state.
        engine.SetWireSender(_sent.Add);
        return (engine, router, players, party);
    }

    private static void Dispatch(MessageRouter router, string text)
    {
        LineExtractor.EmittedLine line = new(
            Text:         text,
            Attributes:   Array.Empty<CellAttributes>(),
            Timestamp:    Now,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    private static void SeedPlayer(PlayerDatabase db, string name, bool inviteOnSeen = false, bool joinOnInvited = false)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(
            InviteToPartyIfSeen: inviteOnSeen,
            JoinPartyIfInvited:  joinOnInvited));
    }

    // ===== Invite-on-seen via "Also here:" =====

    [Fact]
    public void AlsoHere_FlaggedPlayer_SendsInvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // Settings → Other "Only auto-invite while navigation is running": seen while idle,
    // no invite; seen while a walk / loop / lair / deposit / train runs, invited.
    [Fact]
    public void AlsoHere_OnlyWhileNavigating_InvitesOnlyWhileNavigationRuns()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        bool navigating = false;
        engine.OnlyWhileNavigating = true;
        engine.SetNavigationProbe(() => navigating);

        Dispatch(router, "Also here: Raijin.");
        Assert.Empty(_sent);

        navigating = true;
        Dispatch(router, "Also here: Raijin.");
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // Report paradigm-20261004-061346: a flagged player already standing beside us
    // when a run starts is invited as it starts, not walked away from.
    [Fact]
    public void OnlyWhileNavigating_SeenWhileIdle_InvitedWhenNavigationStartsInThatRoom()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        bool navigating = false;
        engine.OnlyWhileNavigating = true;
        engine.SetNavigationProbe(() => navigating);
        engine.SetRoomProbe(() => new RoomKey(16, 10110));

        Dispatch(router, "Also here: Raijin.");
        engine.OnNavigationStateChanged();
        Assert.Empty(_sent);

        navigating = true;
        engine.OnNavigationStateChanged();
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));

        engine.OnNavigationStateChanged();
        Assert.Single(_sent);
    }

    [Fact]
    public void OnlyWhileNavigating_SeenWhileIdle_NotInvitedFromAnotherRoomOrAfterLeaving()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        SeedPlayer(players, "Fujin", inviteOnSeen: true);
        bool navigating = false;
        RoomKey room = new(16, 10110);
        engine.OnlyWhileNavigating = true;
        engine.SetNavigationProbe(() => navigating);
        engine.SetRoomProbe(() => room);

        Dispatch(router, "Also here: Raijin.");
        room = new RoomKey(16, 10111);
        Dispatch(router, "Also here: Fujin.");
        Dispatch(router, "Fujin just left to the north.");

        navigating = true;
        engine.OnNavigationStateChanged();
        Assert.Empty(_sent);
    }

    [Fact]
    public void AlsoHere_UnflaggedPlayer_NoInvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: false);

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void AlsoHere_UnknownPlayer_NoInvite()
    {
        // No customization record at all — the dialog never even saw
        // them, so default-deny.
        var (engine, router, _, _) = Setup();

        Dispatch(router, "Also here: Stranger.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void AlsoHere_PlayerAlreadyInParty_NoReinvite()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        // We lead and Raijin is already a joined member: no invite.
        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.IsInParty = true;

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void AlsoHere_MultiplePlayers_InvitesEachFlaggedOnce()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin",  inviteOnSeen: true);
        SeedPlayer(players, "Forged",  inviteOnSeen: true);
        SeedPlayer(players, "Stranger", inviteOnSeen: false);

        // Oxford-and form covering the 3-name shape MajorMUD uses.
        Dispatch(router, "Also here: Raijin, Forged and Stranger.");

        Assert.Equal(2, _sent.Count);
        string a = Encoding.Latin1.GetString(_sent[0]);
        string b = Encoding.Latin1.GetString(_sent[1]);
        Assert.Contains("invite Raijin\r", new[] { a, b });
        Assert.Contains("invite Forged\r", new[] { a, b });
    }

    [Fact]
    public void AlsoHere_TtlSuppressesReinviteWithinCooldown()
    {
        // The screenshot scenario: room re-renders every move tick, and
        // the same "Also here:" line keeps coming. We invite once,
        // then the cooldown should keep the wire quiet until it expires
        // or the player joins the party (whichever first).
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        Dispatch(router, "Also here: Raijin.");
        Dispatch(router, "Also here: Raijin.");
        Dispatch(router, "Also here: Raijin.");

        Assert.Single(_sent);
    }

    [Fact]
    public void AlsoHere_TtlExpires_AllowsReinvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        DateTime t0 = Now;
        _partyNow = () => t0;
        Dispatch(router, "Also here: Raijin.");

        // Advance past the default 60s cooldown.
        _partyNow = () => t0.AddSeconds(61);
        Dispatch(router, "Also here: Raijin.");

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void AlsoHere_GivenNameStrippedFromFullDisplayName()
    {
        // Real-world rendering can include the family in the room
        // listing ("Raijin WuzHere"). The invite command takes the
        // given name only, so the engine strips down to the first
        // whitespace token.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        Dispatch(router, "Also here: Raijin WuzHere.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== Accept-invite via "X has invited you to follow him/her" =====
    //
    // Real Playpen BBS wording (verified live, 2026-06-01 screenshot):
    //   "MudPlay has invited you to follow him."
    // MajorMUD player characters are male or female only, so the
    // pronoun alternation is him / her — no "them" arm.

    [Fact]
    public void InviteReceived_FlaggedSender_SendsFollow()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "MudPlay", joinOnInvited: true);

        Dispatch(router, "MudPlay has invited you to follow him.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("follow MudPlay\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void InviteReceived_UnflaggedSender_NoAccept()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "MudPlay", joinOnInvited: false);

        Dispatch(router, "MudPlay has invited you to follow him.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void InviteReceived_UnknownSender_NoAccept()
    {
        var (engine, router, _, _) = Setup();

        Dispatch(router, "Stranger has invited you to follow her.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void InviteReceived_FromBelievedLeader_AcceptsDespiteStaleRoster()
    {
        // Our train-stats trip broke the party server-side with no leave-line
        // reaching us, so our roster still lists the leader (Raijin). A leader
        // never re-invites a current follower, so this re-invite proves the roster
        // is stale — accept it (send follow) instead of skipping on the phantom
        // membership (report stock-20260801-002423).
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", joinOnInvited: true);
        party.LeaderName = "Raijin";
        party.IsInParty = true;
        party.SelfIsLeader = false;
        party.Members.Add(new PartyMember { Name = "Raijin", IsLeader = true });

        Dispatch(router, "Raijin has invited you to follow him.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("follow Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Theory]
    [InlineData("him")]
    [InlineData("her")]
    public void InviteReceived_PronounVariants_AllMatch(string pronoun)
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "MudPlay", joinOnInvited: true);

        Dispatch(router, $"MudPlay has invited you to follow {pronoun}.");

        Assert.Single(_sent);
    }

    [Fact]
    public void InviteReceived_NeuterPronoun_NoMatch()
    {
        // Monsters can be neuter ("it") but monsters don't issue
        // party invites — the pattern is player→player only. A line
        // ending in "follow it." is therefore noise, not an invite.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "MudPlay", joinOnInvited: true);

        Dispatch(router, "MudPlay has invited you to follow it.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void InviteReceived_AlreadyInParty_NoDuplicateFollow()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "MudPlay", joinOnInvited: true);
        party.Members.Add(new PartyMember { Name = "MudPlay" });
        party.IsInParty = true;

        Dispatch(router, "MudPlay has invited you to follow him.");

        Assert.Empty(_sent);
    }

    // ===== Wire-sender-null guard =====

    [Fact]
    public void AlsoHere_WithoutWireSender_DoesNotBurnTtl()
    {
        // Construct WITHOUT the no-op sender Setup binds. Mirrors the
        // startup window where AutoPartyManager subscribes before
        // MainWindowViewModel binds SendUserInput as the sender.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        PartyState party = new();
        _partyNow = () => Now;
        AutoPartyManager engine = new(router, players, party, schedule: _partyTimers.Arm, now: () => _partyNow());
        // NOTE: no SetWireSender. The pre-fix path set
        // _recentlyInvited[given]=now before the send went nowhere — so
        // the next dispatch within 60 s would TTL-suppress even after
        // the wire-sender was bound. Post-fix the engine bails first.
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        Dispatch(router, "Also here: Raijin.");

        // Now bind the sender and re-fire — the TTL must NOT have been
        // burned, so this dispatch should produce the invite.
        engine.SetWireSender(_sent.Add);
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", System.Text.Encoding.Latin1.GetString(sent));
    }

    // ===== TTL housekeeping on party-roster changes =====

    [Fact]
    public void MemberLeavingParty_ClearsTheirInviteCooldown()
    {
        // Scenario from live test: MudPlay auto-invites Raijin. Raijin
        // accepts; they're in the party. Raijin walks east; party
        // dissolves. MudPlay walks back into Raijin's room — the next
        // "Also here:" line should re-invite Raijin without waiting
        // out the 60 s TTL. Before the fix, the stale cooldown entry
        // suppressed the re-invite for a full minute.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        // Initial auto-invite — TTL stamped.
        Dispatch(router, "Also here: Raijin.");
        Assert.Single(_sent);
        _sent.Clear();

        // Raijin joins the party (engine ignores subsequent "Also here:"
        // lines while they're in our roster). Simulate by adding the row.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        Dispatch(router, "Also here: Raijin.");
        Assert.Empty(_sent);

        // Raijin leaves the party — the membership-change subscriber
        // should drop "Raijin" from the cooldown map so a subsequent
        // sighting can re-invite immediately.
        party.Members.RemoveAt(0);

        Dispatch(router, "Also here: Raijin.");
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== Follower gate =====

    [Fact]
    public void AlsoHere_WhenWeAreAFollower_DoesNotInvite()
    {
        // Inviting only makes sense when solo or leading. As a follower
        // we have no authority over someone else's roster.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        party.IsInParty    = true;
        party.SelfIsLeader = false;

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void AlsoHere_WhenWeAreLeader_DoesInvite()
    {
        // Leader of our own party — auto-invite still applies.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        party.IsInParty    = true;
        party.SelfIsLeader = true;

        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== @join nag escalation =====

    [Fact]
    public void JoinNag_FiresAfterInitialDelay()
    {
        // Within the initial delay window, no @join. Past it, one @join
        // fires on the nag loop's next tick.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear(); // ignore the initial invite

        // 3s in — no nag yet.
        _partyNow = () => t0.AddSeconds(3);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);

        // 5s in — first @join.
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        byte[] first = Assert.Single(_sent);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(first));
    }

    [Fact]
    public void JoinNag_ResendsAtFrequencyAfterFirst()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        // First @join at t+5s, then re-fire at t+15s, t+25s.
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _partyNow = () => t0.AddSeconds(15);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _partyNow = () => t0.AddSeconds(25);
        _partyTimers.RunDue(AutoPartyManager.NagTick);

        Assert.Equal(3, _sent.Count);
        foreach (byte[] sent in _sent)
            Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void JoinNag_OkTelepath_HaltsSendsButKeepsWaiting()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent); // first @join

        // Raijin replies {Ok} — further sends should halt even though
        // the cadence + cap windows would otherwise allow them.
        Dispatch(router, "Raijin telepaths: {Ok}");
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(20);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _partyNow = () => t0.AddSeconds(40);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void JoinNag_NonBracedTextReply_AbortsEntireNag()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent);

        // Non-braced free text is a human replying (a decline) — kill the nag.
        Dispatch(router, "Raijin telepaths: nah I'm good");
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(20);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void JoinNag_BracedMachineTelepath_DoesNotAbort()
    {
        // Reproduces the live bug: right after inviting Raijin the leader pinged
        // his @health, and his client's automated {HP=…} reply landed before the
        // initial delay elapsed — the old code treated any non-{Ok} reply as a
        // decline and cancelled the nag, so no @join ever fired. A fully-braced
        // machine payload must be ignored: the nag stays live and still fires.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        // @health reply arrives within the initial-delay window, before any @join.
        Dispatch(router, "Raijin telepaths: {HP=43/43,MA=15/34, Resting}");

        // Past the delay — the nag survived, so the first @join still fires.
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void JoinNag_IncomingRemoteCommand_DoesNotAbort()
    {
        // Report stock-20260924-145545: a follower whose party broke probes its old
        // leader with `@where`; that's its client asking, not a decline.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();
        Dispatch(router, "Raijin telepaths: @where");

        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void InviteAlreadyOnTheWire_IsNotSentTwice()
    {
        // Report stock-20260924-175224: a member back from training re-entered the
        // realm (PartyManager re-invites that) and then showed in "Also here" before
        // the echo landed, so invite-if-seen sent a second `invite`.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        engine.ObserveOutbound(Encoding.Latin1.GetBytes("invite Raijin\r"));
        Dispatch(router, "Also here: Raijin.");

        Assert.DoesNotContain(_sent, b => Encoding.Latin1.GetString(b).StartsWith("invite", StringComparison.Ordinal));
    }

    [Fact]
    public void SeeingAQuietInvitedMember_ReinvitesAndNagsAgain()
    {
        // The same report: the member sat in an [Invited] slot with no nag running
        // (the follow broke into it), and walking past him did nothing.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        party.Members.Add(new PartyMember { Name = "Raijin", IsInvited = true });

        Dispatch(router, "Also here: Raijin.");

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(engine.ActiveNagSnapshot(), n => n.Given == "Raijin");
    }

    [Fact]
    public void ActiveNagSnapshot_ReflectsInFlightNagProgression()
    {
        // Backs the bug-report engine-state dump — the snapshot must mirror the
        // live nag as it progresses (armed → first send → {Ok} ack).
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Assert.Empty(engine.ActiveNagSnapshot());

        Dispatch(router, "Also here: Raijin.");
        AutoPartyManager.NagSnapshot armed = Assert.Single(engine.ActiveNagSnapshot());
        Assert.Equal("Raijin", armed.Given);
        Assert.Equal(0, armed.JoinSends);
        Assert.Null(armed.LastJoinAt);
        Assert.False(armed.Acknowledged);

        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);          // first @join
        Dispatch(router, "Raijin telepaths: {Ok}");

        AutoPartyManager.NagSnapshot acked = Assert.Single(engine.ActiveNagSnapshot());
        Assert.Equal(1, acked.JoinSends);
        Assert.NotNull(acked.LastJoinAt);
        Assert.True(acked.Acknowledged);
    }

    [Fact]
    public void JoinNag_TargetJoinsParty_CancelsNag()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent);

        // Target joins — CollectionChanged.Add fires the cancel.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        _sent.Clear();

        _partyNow = () => t0.AddSeconds(20);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void JoinNag_PlaceholderRowFlipsInvitedFalse_CancelsNag()
    {
        // Production leader path: `invite Raijin` adds a placeholder
        // row with IsInvited=true (a CollectionChanged.Add), then the
        // real accept flips that SAME row's IsInvited true→false (a
        // PropertyChanged, NOT a new Add). The add-based CancelNag never
        // sees the real join, so the IsInvited-flip hook must cancel it.
        var (engine, router, _, party) = Setup();
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        // PartyManager.OnYouInvited adds the placeholder row, then the
        // server echo arms the nag (mirrors YouInvited_PendingInvitedRow).
        PartyMember row = new() { Name = "Raijin", IsInvited = true };
        party.Members.Add(row);
        Dispatch(router, "You have invited Raijin to follow you.");

        // First @join fires after the delay — nag is live.
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent,
            b => Encoding.Latin1.GetString(b) == "/Raijin @join\r");
        _sent.Clear();

        // Raijin accepts — PartyManager.OnFollowsYou flips the existing
        // row in place. This is a PropertyChanged, not a CollectionChanged.Add.
        row.IsInvited = false;

        // Subsequent ticks (past cadence + cap windows) produce no @join.
        _partyNow = () => t0.AddSeconds(15);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _partyNow = () => t0.AddSeconds(25);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void JoinNag_TotalWindowExpired_StopsNagging()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();

        // Tick across the entire 55s window — sends fire until then.
        for (int t = 5; t <= 55; t += 10)
        {
            _partyNow = () => t0.AddSeconds(t);
            _partyTimers.RunDue(AutoPartyManager.NagTick);
        }
        int sendsWithinWindow = _sent.Count;

        _sent.Clear();

        // Anything past 55s — nothing further.
        _partyNow = () => t0.AddSeconds(70);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
        Assert.True(sendsWithinWindow >= 1);
    }

    [Fact]
    public void JoinNag_BecomingAFollowerMidFlow_AbortsAllNags()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _sent.Clear();

        // We accept someone else's invite mid-nag — became a follower.
        party.IsInParty    = true;
        party.SelfIsLeader = false;

        _partyNow = () => t0.AddSeconds(20);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void PartyFullyDissolving_ClearsEntireInviteCooldownMap()
    {
        // Whole-party wipe (IsInParty flips false) — the entire cooldown
        // map flushes so any previous roster member that re-appears in
        // our room becomes immediately eligible for a fresh auto-invite.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        SeedPlayer(players, "Helper", inviteOnSeen: true);

        // Stamp both cooldowns from a first sighting.
        Dispatch(router, "Also here: Raijin, Helper.");
        Assert.Equal(2, _sent.Count);
        _sent.Clear();

        // Both joined the party, then it dissolved.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.IsInParty = true;
        party.Members.Clear();
        party.IsInParty = false; // triggers the map flush.

        // Both should now be re-invite-eligible immediately.
        Dispatch(router, "Also here: Raijin, Helper.");
        Assert.Equal(2, _sent.Count);
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Helper\r");
    }

    // ===== Invite-as-wait-signal (loop hold) =====

    [Fact]
    public void InviteWait_WhileLooping_AssertsPartyInviteGate()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");

        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
    }

    [Fact]
    public void InviteWait_NotLooping_DoesNotHoldLoop()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        Dispatch(router, "Also here: Raijin.");

        // Invite still goes out, but no loop hold when we're not looping.
        Assert.Single(_sent);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void InviteWait_ZeroWindow_DisablesHold()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.Zero;
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");

        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void InviteWait_TimeoutWithoutJoin_UninvitesAndResumes()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();
        Assert.True(coord.IsPaused);

        // Before the window elapses — still holding, no uninvite.
        _partyNow = () => t0.AddSeconds(80);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.True(coord.IsPaused);
        Assert.DoesNotContain(_sent,
            b => Encoding.Latin1.GetString(b) == "uninvite Raijin\r");

        // Past the window — uninvite the no-show and release the gate.
        _partyNow = () => t0.AddSeconds(91);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "uninvite Raijin\r");
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void InviteWait_TargetJoins_ReleasesGateWithoutUninvite()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");
        _sent.Clear();
        Assert.True(coord.IsPaused);

        // Production leader path: invite adds a placeholder row (still
        // holding), then acceptance flips IsInvited false on the same row.
        PartyMember row = new() { Name = "Raijin", IsInvited = true };
        party.Members.Add(row);
        Assert.True(coord.IsPaused);
        row.IsInvited = false;

        Assert.False(coord.IsPaused);

        // Even past the window, no uninvite — they joined.
        _partyNow = () => t0.AddSeconds(120);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.DoesNotContain(_sent,
            b => Encoding.Latin1.GetString(b) == "uninvite Raijin\r");
    }

    [Fact]
    public void InviteWait_PartyDissolves_ReleasesGate()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");
        Assert.True(coord.IsPaused);

        // Leading a party, the hold stands; the whole party going releases it.
        party.SelfIsLeader = true;
        party.IsInParty = true;
        Assert.True(coord.IsPaused);

        party.IsInParty = false;

        Assert.False(coord.IsPaused);
    }

    // ===== Party-split teleport reform (000851) =====

    [Fact]
    public void NotePartySplitTeleport_HoldsGate_ButDefersInviteUntilArrival()
    {
        // A chime-style CMD teleport relays every follower through `.@party ring
        // chime` but dissolves the follow chain on arrival. The leader must
        // re-invite each former member and hold the movement gate until they
        // reform — even mid one-shot walk-to (isLooping false), because a split
        // can happen while walking into the mansion, not just under a loop.
        // Crucially the invite is DEFERRED: the teleport lands the leader first
        // and flashes the followers in a beat later, so inviting at cross-time
        // races ahead of their arrival ("You don't see X here!") and is lost.
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.Members.Add(new PartyMember { Name = "Forged" });

        engine.NotePartySplitTeleport();

        // Gate holds immediately (the walker must pause for the reform), but
        // NOTHING is invited yet — the members haven't materialised.
        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
        Assert.Empty(_sent);

        // Each member's teleport-arrival line fires their withheld invite.
        Dispatch(router, "Raijin appears in a blinding flash of light!");
        Dispatch(router, "Forged appears in a blinding flash of light!");

        Assert.Equal(2, _sent.Count);
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Forged\r");
    }

    // The split itself drops every member from the roster ("X is no longer following
    // you."): that's what the reform is waiting out, so the hold stays until they're
    // re-invited and rejoin — before, it released at once and the leader walked on
    // alone.
    [Fact]
    public void SplitReform_KeepsHoldingWhenTheSplitDropsTheMembers()
    {
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        PartyMember raijin = new() { Name = "Raijin" };
        party.Members.Add(raijin);

        engine.NotePartySplitTeleport();
        party.Members.Remove(raijin);

        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
        Dispatch(router, "Raijin appears in a blinding flash of light!");
        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);

        party.Members.Add(new PartyMember { Name = "Raijin" });
        Assert.DoesNotContain(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
    }

    // Report paradigm-20260930-192042 (`go hole`, one follower): the split drops the
    // follower AND disbands the party. The hold outlives both; once the leader has
    // landed, the redisplay backstop's "Also here:" re-invites the follower.
    [Fact]
    public void SplitReform_OneFollowerDisband_StillHoldsAndReinvitesOnLanding()
    {
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(20);
        engine.SetMovementGate(coord, isLooping: () => false);
        RoomKey here = new(16, 542);
        engine.SetRoomProbe(() => here);

        party.SelfIsLeader = true;
        PartyMember self = new() { Name = "Tristian", IsSelf = true };
        PartyMember eddie = new() { Name = "Eddie" };
        party.Members.Add(self);
        party.Members.Add(eddie);

        engine.NotePartySplitTeleport();
        here = new RoomKey(16, 543);
        party.Members.Remove(eddie);          // "Eddie is no longer following you."
        party.SelfIsLeader = false;           // "Your party has been disbanded."
        party.Members.Remove(self);

        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);
        Dispatch(router, "Also here: Eddie.");

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Eddie\r");
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
    }

    // A delayed teleport (Darkwood's `go vortex`, `adddelay 5`) leaves everyone in the
    // origin room for seconds after the leader's keyword: members seen there haven't
    // crossed, so they're neither re-invited nor counted as arrived, and the redisplay
    // backstop waits. Once the leader lands elsewhere the reform runs as usual.
    [Fact]
    public void SplitReform_WaitsForTheLeaderToLeaveTheOriginRoom()
    {
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);
        RoomKey here = new(3, 784);
        engine.SetRoomProbe(() => here);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);
        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);

        here = new RoomKey(3, 740);
        Dispatch(router, "Raijin appears in a blinding flash of light!");

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
    }

    // ===== Leader-side reconnect reform =====

    [Fact]
    public void LeaderReconnectReform_DefersInvite_ThenInvitesEachSeenInRoom()
    {
        // A leader-drop DISSOLVES the party (live roster empty), and the followers stayed
        // put in the room. On reconnect we re-invite them from the REMEMBERED list — but
        // deferred until each is observed present ("Also here:"), since inviting an absent
        // player is lost.
        var (engine, router, _, _) = Setup();

        engine.NoteLeaderReconnectReform(new[] { "Raijin", "Forged" });
        Assert.Empty(_sent);   // nothing sent until they're observed

        Dispatch(router, "Also here: Raijin and Forged.");

        Assert.Equal(2, _sent.Count);
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(_sent,
            b => Encoding.Latin1.GetString(b) == "invite Forged\r");
    }

    [Fact]
    public void LeaderReconnectReform_OnlyInvitesTheOnesPresent()
    {
        // Raijin stayed in the room; Forged wandered off. Only the present one is invited;
        // Forged stays pending until observed (or the reform is aborted).
        var (engine, router, _, _) = Setup();
        engine.NoteLeaderReconnectReform(new[] { "Raijin", "Forged" });

        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // Report paradigm-20260923-092317: a `look <direction>` peek listed the followers
    // in the next room, the reform took that as their arrival and spent its invite
    // from the wrong room ("You don't see X here!"). A peek's listing is nobody
    // here; the invite waits for a display of the room we stand in.
    [Fact]
    public void LeaderReconnectReform_PeekedAlsoHere_DoesNotSpendTheInvite()
    {
        var (engine, router, _, _) = Setup();
        bool peeking = true;
        engine.SetPeekProbe(() => peeking);
        engine.NoteLeaderReconnectReform(new[] { "Raijin", "Forged" });

        Dispatch(router, "Also here: Raijin and Forged.");   // the next room, seen through a look

        Assert.Empty(_sent);

        peeking = false;
        Dispatch(router, "Also here: Raijin and Forged.");   // our own room

        Assert.Equal(2, _sent.Count);
        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
    }

    // The reform looks at the room once, two seconds in. A look the game answered
    // with no room leaves the peek reading up for a few seconds, and that one
    // redisplay was then passed over as a peek with nothing to follow it. A listing
    // passed over while members are still waited for asks for another look.
    [Fact]
    public void LeaderReconnectReform_ItsRedisplayReadAsAPeek_LooksAgain()
    {
        var (engine, router, _, _) = Setup();
        bool peeking = true;
        engine.SetPeekProbe(() => peeking);
        engine.NoteLeaderReconnectReform(new[] { "Raijin" });
        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);                // the one redisplay goes out
        Assert.DoesNotContain(AutoPartyManager.ReformRedisplayDelay, _partyTimers.PendingDelays);

        Dispatch(router, "Also here: Raijin.");             // its answer, read as a peek

        Assert.DoesNotContain(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(AutoPartyManager.ReformRedisplayDelay, _partyTimers.PendingDelays);

        peeking = false;
        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);
        Dispatch(router, "Also here: Raijin.");

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
    }

    // The same goes for the everyday invite-on-seen: a flagged player seen through a
    // look is a room away.
    [Fact]
    public void InviteOnSeen_PeekedAlsoHere_DoesNotInvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        engine.SetPeekProbe(() => true);

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void SplitReform_StrangerFlashLine_DoesNotInvite()
    {
        // "appears in a blinding flash of light!" fires for any player recalling
        // into the room, not just reforming members. Only the snapshotted former
        // members get the deferred invite; a stranger's flash is ignored.
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Wanderer appears in a blinding flash of light!");

        Assert.Empty(_sent);
    }

    [Fact]
    public void SplitReform_MemberAlreadyPresent_InvitesOnAlsoHere()
    {
        // A member who teleported in AHEAD of the leader emits no flash line the
        // leader can see, so the "Also here:" room listing is their arrival
        // signal and must fire the withheld invite — even without the per-player
        // InviteToPartyIfSeen flag that the ordinary auto-invite path requires.
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(_sent);

        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SplitReform_ArrivalFiresInviteOnce_NoDoubleInvite()
    {
        // A member can be both listed "Also here:" and emit a flash line. The
        // deferred invite must go out exactly once — the pending-set removal
        // makes the second signal a no-op.
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Raijin appears in a blinding flash of light!");
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void IsReformSettling_TrueWhileMembersPending_FalseBeforeReform()
    {
        // The reform-settling signal TrapDelegationManager consults to skip its
        // race-probe look. False before a split, true while members are pending.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        Assert.False(engine.IsReformSettling);

        engine.NotePartySplitTeleport();

        Assert.True(engine.IsReformSettling);
    }

    [Fact]
    public void SplitReform_RedisplayBackstop_SendsBareCr_WhenMemberStillPending()
    {
        // The fixed settle-timer backstop: a member who teleported in ahead of us
        // and whose arrival we never witnessed is still pending — a bare CR
        // redisplays the room so the "Also here:" line surfaces them.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(_sent);   // invite deferred, nothing yet

        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SplitReform_RedisplayBackstop_NoOp_WhenAllMembersWitnessed()
    {
        // Every member's arrival was already witnessed → nothing pending → the
        // backstop redisplay is a no-op, so it can't nudge the resumed walk.
        var (engine, router, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Raijin appears in a blinding flash of light!");

        int before = _sent.Count;
        _partyTimers.RunDue(AutoPartyManager.ReformRedisplayDelay);

        Assert.Equal(before, _sent.Count);
        Assert.DoesNotContain(_sent,
            b => Encoding.Latin1.GetString(b) == "\r");
    }

    [Fact]
    public void NotePartySplitTeleport_NonLeader_NoReform()
    {
        // A follower who crossed the same teleport has nobody to re-invite —
        // only the leader reforms the party.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = false;
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();

        Assert.Empty(_sent);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void AbortReformWaits_ReleasesGate_AfterReformHold()
    {
        // Stopping the walk mid-reform must drop the re-invite hold so the
        // PartyInvite gate releases — otherwise the user stays pinned by a
        // "waiting for invitee to join" gate they can never clear and can't
        // start a fresh walk elsewhere.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);

        engine.AbortReformWaits("walk stopped");

        Assert.False(coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
    }

    // ===== Uninvite suppression =====

    [Fact]
    public void Uninvite_SuppressesAutoInviteForTheUninvitedPlayer()
    {
        // The leader's "X has been removed from your followers" line
        // confirms an uninvite landed. Auto-invite of that player
        // should be suppressed for the next UninviteSuppression window,
        // so the next "Also here: X" line doesn't immediately re-add
        // them and re-fire the @join nag.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.UninviteSuppression = TimeSpan.FromMinutes(60);

        Dispatch(router, "Raijin has been removed from your followers.");
        _sent.Clear();

        // 10 min later — still inside the suppression window.
        _partyNow = () => t0.AddMinutes(10);
        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(_sent);
    }

    [Fact]
    public void Uninvite_SuppressionExpires_AllowsReinviteAfterWindow()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.UninviteSuppression = TimeSpan.FromMinutes(60);

        Dispatch(router, "Raijin has been removed from your followers.");
        _sent.Clear();

        // Past the suppression window AND past the regular 60s
        // re-invite cooldown.
        _partyNow = () => t0.AddMinutes(61);
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== Trainer-menu exit re-invite =====

    [Fact]
    public void TrainerMenuExited_ReInvitesDroppedRosterMembers()
    {
        // Simulate the full leader-side scenario: party of two, leader
        // visits trainer-stats menu, comes back, follower's view has
        // dissolved (so the row is gone from State.Members), but the
        // [Invited] hold is still active server-side. AutoParty should
        // see MenuExited and re-fire `invite Raijin` for the dropped
        // roster name.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        party.SelfIsLeader = true;   // leader-side reform — only a leader re-invites its followers
        TrainerMenuTracker tracker = new(router, party, now: () => Now);
        _partyNow = () => Now;
        AutoPartyManager engine = new(router, players, party, tracker, schedule: _partyTimers.Arm, now: () => _partyNow());
        engine.SetWireSender(_sent.Add);

        // Snapshot the roster the menu captured at entry — simulates
        // "Raijin was in the party before we visited the trainer".
        party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);

        // Drop Raijin to model the follower-view dissolution that
        // happened while we were in the menu.
        party.Members.Clear();
        _sent.Clear();

        // Exit the menu — next in-game prompt fires MenuExited.
        Dispatch(router, "[HP=33]:");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void TrainerMenuExited_StillInParty_DoesNotResendInvite()
    {
        // If the roster member never left State.Members across the
        // menu trip, there's nothing to re-invite.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        party.SelfIsLeader = true;   // leader-side reform — only a leader re-invites its followers
        TrainerMenuTracker tracker = new(router, party, now: () => Now);
        _partyNow = () => Now;
        AutoPartyManager engine = new(router, players, party, tracker, schedule: _partyTimers.Arm, now: () => _partyNow());
        engine.SetWireSender(_sent.Add);

        party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        _sent.Clear();

        Dispatch(router, "[HP=33]:");

        Assert.Empty(_sent);
    }

    [Fact]
    public void TrainerMenuExited_InvitedPlaceholder_ReInvites()
    {
        // The reported stuck state: a joined follower at menu entry comes back
        // as an [Invited] placeholder (their follower-side view dissolved during
        // the trainer trip, leaving only the leader's hot invite slot). That
        // placeholder must NOT count as "still joined" — AutoParty re-invites so
        // the follower re-forms rather than sitting at [Invited] forever.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        party.SelfIsLeader = true;   // leader-side reform — only a leader re-invites its followers
        TrainerMenuTracker tracker = new(router, party, now: () => Now);
        _partyNow = () => Now;
        AutoPartyManager engine = new(router, players, party, tracker, schedule: _partyTimers.Arm, now: () => _partyNow());
        engine.SetWireSender(_sent.Add);

        party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);

        // Follower view dissolved → row is now a bare [Invited] placeholder.
        party.Members.Clear();
        party.Members.Add(new PartyMember { Name = "Raijin WuzHere", IsInvited = true });
        _sent.Clear();

        Dispatch(router, "[HP=33]:");

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void TrainerMenuExited_AsFollower_DoesNotReInviteLeader()
    {
        // Report stock-20260731-015726: a follower's menu-entry roster includes its
        // LEADER; on menu exit it must NOT `invite <leader>` (the leader re-invites
        // the follower, who auto-joins via JoinPartyIfInvited). Only a leader
        // reforms — before the fix a follower fired `invite <leader>` and tangled
        // the rejoin.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        PartyState party = new();
        party.SelfIsLeader = false;      // we're a follower
        party.LeaderName = "MudPlay";
        TrainerMenuTracker tracker = new(router, party, now: () => Now);
        _partyNow = () => Now;
        AutoPartyManager engine = new(router, players, party, tracker, schedule: _partyTimers.Arm, now: () => _partyNow());
        engine.SetWireSender(_sent.Add);

        // Roster at menu entry holds our leader.
        party.Members.Add(new PartyMember { Name = "MudPlay" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.Contains("MudPlay", tracker.RosterAtMenuEntry);

        // Follower-side view dissolved during the trip.
        party.Members.Clear();
        _sent.Clear();

        Dispatch(router, "[HP=33]:");

        Assert.Empty(_sent);   // no invite — the leader reforms
    }

    [Fact]
    public void Uninvite_CancelsActiveNagInFlight()
    {
        // Uninvite arriving mid-nag should kill the nag — the player
        // we're nagging is the one we just kicked.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        _partyNow = () => t0.AddSeconds(5);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Equal(2, _sent.Count); // invite + 1st @join

        Dispatch(router, "Raijin has been removed from your followers.");
        _sent.Clear();

        // Further ticks — no more @join because the nag was cancelled.
        _partyNow = () => t0.AddSeconds(20);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        _partyNow = () => t0.AddSeconds(40);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    // ===== Manual-invite path: @join nag starts on the server echo =======
    // Tehshortbus's screenshot showed Raijin sitting at [Invited] in par
    // after a manual `invite Raijin` typed at the prompt, but the @join
    // nag never spun up. Pre-fix StartNag only fired from TryAutoInvite
    // (gated on InviteToPartyIfSeen) and OnTrainerMenuExited; the manual
    // path was uncovered. Subscribing to PartyYouInvited closes the gap.

    [Fact]
    public void YouInvited_StartsNag_EvenWithoutInviteOnSeenFlag()
    {
        // No customization for Raijin — pure manual invite. The server
        // echo for `invite Raijin` should still spin up the @join nag.
        var (engine, router, _, _) = Setup();
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "You have invited Raijin to follow you.");

        // First nag fires after JoinNagInitialDelay.
        _partyNow = () => t0.AddSeconds(6);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void YouInvited_AlreadyArmedNag_NoDuplicate()
    {
        // TryAutoInvite path fires StartNag immediately; the matching
        // server echo arrives a tick later. The handler must detect
        // the in-flight nag and skip — no duplicate state, no double
        // sends.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        // Auto-path fires the invite + arms the nag.
        Dispatch(router, "Also here: Raijin.");
        Assert.Single(_sent, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        _sent.Clear();

        // Server echo arrives a few seconds on — handler is idempotent. A nag
        // re-armed by the echo would start at t0+3 and not fire until t0+8.
        _partyNow = () => t0.AddSeconds(3);
        Dispatch(router, "You have invited Raijin to follow you.");

        // Advance past the initial delay and tick — only ONE @join
        // should fire (single armed nag entry).
        _partyNow = () => t0.AddSeconds(6);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent);
    }

    [Fact]
    public void YouInvited_AsFollower_DoesNotStartNag()
    {
        // Defense-in-depth: we shouldn't be inviting people as a
        // follower (the server would reject the command), but if a
        // spoof echo arrived we shouldn't act on it.
        var (engine, router, _, party) = Setup();
        party.IsInParty    = true;
        party.SelfIsLeader = false;
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        _partyNow = () => t0.AddSeconds(6);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void YouInvited_PlayerAlreadyJoined_NoNag()
    {
        // Edge case: invitee accepted between the invite send and the
        // echo (race). Skip nag.
        var (engine, router, _, party) = Setup();
        party.Members.Add(new PartyMember { Name = "Raijin", IsInvited = false });
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        _partyNow = () => t0.AddSeconds(6);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Empty(_sent);
    }

    [Fact]
    public void YouInvited_PendingInvitedRow_StillStartsNag()
    {
        // Common case: PartyManager.OnYouInvited has already added an
        // IsInvited=true row to the roster on the same echo line.
        // AutoPartyManager's handler should still arm the nag.
        var (engine, router, _, party) = Setup();
        party.Members.Add(new PartyMember { Name = "Raijin", IsInvited = true });
        DateTime t0 = Now;
        _partyNow = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        _partyNow = () => t0.AddSeconds(6);
        _partyTimers.RunDue(AutoPartyManager.NagTick);
        Assert.Single(_sent, b => Encoding.Latin1.GetString(b) == "/Raijin @join\r");
    }

    // ===== Party-split teleport reform: plain arrival + disband survival =====

    [Fact]
    public void SplitReform_PlainArrivalLine_FiresDeferredInvite()
    {
        // A "go hole"-style CMD teleport lands members with a plain "walks into
        // the room from nowhere" arrival — no "blinding flash" line at all. The
        // withheld invite must fire on that classified Player arrival, not only on
        // the flash / "Also here:" signals.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(_sent);

        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SplitReform_MonsterArrival_DoesNotFireReformInvite()
    {
        // OnPlayerArrival is fed every classified arrival — a Monster arrival that
        // happens to carry a pending member's name must not fire the invite. Only
        // the watcher's Player classification counts.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Monster, "east", default));

        Assert.Empty(_sent);
    }

    [Fact]
    public void SplitReform_CardinalArrival_DoesNotFireReformInvite_NowhereDoes()
    {
        // Regression: a reform member following the leader into the pre-teleport
        // staging room arrives from a cardinal direction ("walks into the room from
        // the east"). That stale arrival can be processed after the split arms but
        // before the leader's teleport confirms — firing the withheld invite on it
        // races `invite X` ahead of X crossing the hole ("You don't see X here!").
        // Only the "from nowhere" teleport materialization must trigger the invite.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();

        // Cardinal follow-in (staging room) must NOT fire the invite.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "east", default));
        Assert.Empty(_sent);

        // The real through-hole arrival ("from nowhere") fires it.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));
        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SplitReform_PartyDisbands_ReformSurvives_ReinvitesOnArrival()
    {
        // The "go hole" teleport answers with "Your party has been disbanded." and
        // drops us to non-leader BEFORE the members walk in. That disband must NOT
        // clear the in-flight reform: the gate stays held and each member's later
        // arrival still fires the withheld re-invite. Regression — the disband
        // used to nuke the reform, so the leader reformed nobody and walked off.
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.IsInParty    = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.True(coord.IsPaused);

        // Server disband arrives before the member does.
        party.SelfIsLeader = false;
        party.IsInParty    = false;

        // Reform survived — still holding, nothing prematurely invited.
        Assert.True(coord.IsPaused);
        Assert.Empty(_sent);

        // Member finally walks in — the withheld invite goes out and reforms.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SplitReform_TransientFollowerFlip_KeepsReformHold()
    {
        // Crossing the teleport churns the party state — we can momentarily read
        // as a follower before the disband settles. That transient flip must not
        // cancel the reform (the pre-fix "became a follower" clear was the exact
        // line that released the gate and let the walker leave alone).
        var (engine, _, _, party) = Setup();
        MovementCoordinator coord = new();
        engine.SetMovementGate(coord, isLooping: () => false);

        party.SelfIsLeader = true;
        party.IsInParty    = true;
        party.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.True(coord.IsPaused);

        // Transient "became a follower" flip mid-teleport.
        party.SelfIsLeader = false;

        // Still holding for the reform — the flip didn't release the gate.
        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);

        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));
        Assert.Single(_sent);
    }

    // ----- a follower an exit refused: Stock keeps them on the leader's list -----
    //
    // GAME_MECHANICS "A follower who can't move is left behind". With no probe
    // saying a member was left behind on our list (Paradigm) nothing differs from
    // what it always was.

    // A member who followed us and now reads [Invited] is seen in the room while a
    // loop runs: re-invited, and the loop held for them. Returns the loop's gate.
    private MovementCoordinator InvitedRowSeenWhileLooping(
        AutoPartyManager engine, MessageRouter router, PlayerDatabase players, PartyState party, DateTime t0)
    {
        SeedPlayer(players, "Tank", inviteOnSeen: true);
        party.Members.Add(new PartyMember { Name = "Tank", IsInvited = true });
        party.IsInParty = true;
        party.SelfIsLeader = true;
        _partyNow = () => t0;
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);
        Dispatch(router, "Also here: Tank.");
        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "invite Tank\r");
        Assert.True(coord.IsPaused);
        _sent.Clear();
        return coord;
    }

    [Fact]
    public void Paradigm_InviteWaitRunsOut_ForAnInvitedRow_Uninvites()
    {
        var (engine, router, players, party) = Setup();
        DateTime t0 = Now;
        MovementCoordinator coord = InvitedRowSeenWhileLooping(engine, router, players, party, t0);

        _partyNow = () => t0.AddSeconds(91);
        _partyTimers.RunDue(AutoPartyManager.NagTick);

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "uninvite Tank\r");
        Assert.False(coord.IsPaused);
    }

    // On Stock a member who followed us and was left behind at an exit is still on
    // our list, and a `join` of theirs needs no invite. An uninvite would take
    // that from them: the loop resumes without one.
    [Fact]
    public void Stock_InviteWaitRunsOut_ForAMemberLeftBehindOnOurList_ResumesWithoutUninviting()
    {
        var (engine, router, players, party) = Setup();
        engine.LeftBehindOnOurList = given => given == "Tank";
        DateTime t0 = Now;
        MovementCoordinator coord = InvitedRowSeenWhileLooping(engine, router, players, party, t0);

        _partyNow = () => t0.AddSeconds(91);
        _partyTimers.RunDue(AutoPartyManager.NagTick);

        Assert.DoesNotContain(_sent, b => Encoding.Latin1.GetString(b) == "uninvite Tank\r");
        Assert.False(coord.IsPaused);

        // And the loop isn't held for them again on the next lap.
        _sent.Clear();
        _partyNow = () => t0.AddSeconds(200);
        Dispatch(router, "Also here: Tank.");
        Assert.Empty(_sent);
        Assert.False(coord.IsPaused);
    }

    // Somebody we invited who never joined is uninvited on Stock as anywhere.
    [Fact]
    public void Stock_InviteWaitRunsOut_ForSomeoneWhoNeverJoined_Uninvites()
    {
        var (engine, router, players, party) = Setup();
        engine.LeftBehindOnOurList = given => given == "Somebody";
        DateTime t0 = Now;
        MovementCoordinator coord = InvitedRowSeenWhileLooping(engine, router, players, party, t0);

        _partyNow = () => t0.AddSeconds(91);
        _partyTimers.RunDue(AutoPartyManager.NagTick);

        Assert.Contains(_sent, b => Encoding.Latin1.GetString(b) == "uninvite Tank\r");
        Assert.False(coord.IsPaused);
    }
}
