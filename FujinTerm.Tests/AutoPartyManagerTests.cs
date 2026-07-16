using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

#pragma warning disable CA1859 // Intentional interface-typed deps for readability in tests.

namespace FujinTerm.Tests;

public sealed class AutoPartyManagerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Self-contained engine + every dependency. Default patterns are
    /// seeded so the RoomAlsoHere / PartyInviteReceived regexes are
    /// resolvable. Tests dispatch via the router with synthesised
    /// EmittedLine values and inspect the engine's LastSentForTests.
    /// </summary>
    private static (AutoPartyManager engine, MessageRouter router, PlayerDatabase players, PartyState party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        PartyState party = new();
        AutoPartyManager engine = new(router, players, party);
        engine.NowProvider = () => Now;
        // Bind a no-op wire-sender so the engine doesn't short-circuit
        // before recording to LastSentForTests. The wire-sender-null
        // guard exists to prevent TTL burn during the startup window
        // where AutoPartyManager subscribes before MainWindowViewModel
        // binds the sender — tests model the post-bind state.
        engine.SetWireSender(_ => { });
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

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void AlsoHere_UnflaggedPlayer_NoInvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: false);

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void AlsoHere_UnknownPlayer_NoInvite()
    {
        // No customization record at all — the dialog never even saw
        // them, so default-deny.
        var (engine, router, _, _) = Setup();

        Dispatch(router, "Also here: Stranger.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void AlsoHere_PlayerAlreadyInParty_NoReinvite()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.IsInParty = true;

        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(engine.LastSentForTests);
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

        Assert.Equal(2, engine.LastSentForTests.Count);
        string a = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        string b = Encoding.Latin1.GetString(engine.LastSentForTests[1]);
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

        Assert.Single(engine.LastSentForTests);
    }

    [Fact]
    public void AlsoHere_TtlExpires_AllowsReinvite()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        Dispatch(router, "Also here: Raijin.");

        // Advance past the default 60s cooldown.
        engine.NowProvider = () => t0.AddSeconds(61);
        Dispatch(router, "Also here: Raijin.");

        Assert.Equal(2, engine.LastSentForTests.Count);
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

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== Accept-invite via "X has invited you to follow him/her" =====
    //
    // Real Playpen BBS wording (verified live, 2026-06-01 screenshot):
    //   "Fujin has invited you to follow him."
    // MajorMUD player characters are male or female only, so the
    // pronoun alternation is him / her — no "them" arm.

    [Fact]
    public void InviteReceived_FlaggedSender_SendsFollow()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Fujin", joinOnInvited: true);

        Dispatch(router, "Fujin has invited you to follow him.");

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("follow Fujin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void InviteReceived_UnflaggedSender_NoAccept()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Fujin", joinOnInvited: false);

        Dispatch(router, "Fujin has invited you to follow him.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void InviteReceived_UnknownSender_NoAccept()
    {
        var (engine, router, _, _) = Setup();

        Dispatch(router, "Stranger has invited you to follow her.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Theory]
    [InlineData("him")]
    [InlineData("her")]
    public void InviteReceived_PronounVariants_AllMatch(string pronoun)
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Fujin", joinOnInvited: true);

        Dispatch(router, $"Fujin has invited you to follow {pronoun}.");

        Assert.Single(engine.LastSentForTests);
    }

    [Fact]
    public void InviteReceived_NeuterPronoun_NoMatch()
    {
        // Monsters can be neuter ("it") but monsters don't issue
        // party invites — the pattern is player→player only. A line
        // ending in "follow it." is therefore noise, not an invite.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Fujin", joinOnInvited: true);

        Dispatch(router, "Fujin has invited you to follow it.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void InviteReceived_AlreadyInParty_NoDuplicateFollow()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Fujin", joinOnInvited: true);
        party.Members.Add(new PartyMember { Name = "Fujin" });
        party.IsInParty = true;

        Dispatch(router, "Fujin has invited you to follow him.");

        Assert.Empty(engine.LastSentForTests);
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
        PlayerDatabase players = new();
        PartyState party = new();
        AutoPartyManager engine = new(router, players, party) { NowProvider = () => Now };
        // NOTE: no SetWireSender. The pre-fix path would still record
        // to LastSentForTests (because that happened in SendWire after
        // setting the TTL), AND set _recentlyInvited[given]=now — so
        // the next dispatch within 60 s would TTL-suppress even after
        // the wire-sender was bound. Post-fix the engine bails before
        // either of those side-effects.
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        Dispatch(router, "Also here: Raijin.");
        Assert.Empty(engine.LastSentForTests);

        // Now bind the sender and re-fire — the TTL must NOT have been
        // burned, so this dispatch should produce the invite.
        engine.SetWireSender(_ => { });
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", System.Text.Encoding.Latin1.GetString(sent));
    }

    // ===== TTL housekeeping on party-roster changes =====

    [Fact]
    public void MemberLeavingParty_ClearsTheirInviteCooldown()
    {
        // Scenario from live test: Fujin auto-invites Raijin. Raijin
        // accepts; they're in the party. Raijin walks east; party
        // dissolves. Fujin walks back into Raijin's room — the next
        // "Also here:" line should re-invite Raijin without waiting
        // out the 60 s TTL. Before the fix, the stale cooldown entry
        // suppressed the re-invite for a full minute.
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);

        // Initial auto-invite — TTL stamped.
        Dispatch(router, "Also here: Raijin.");
        Assert.Single(engine.LastSentForTests);
        engine.LastSentForTests.Clear();

        // Raijin joins the party (engine ignores subsequent "Also here:"
        // lines while they're in our roster). Simulate by adding the row.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        Dispatch(router, "Also here: Raijin.");
        Assert.Empty(engine.LastSentForTests);

        // Raijin leaves the party — the membership-change subscriber
        // should drop "Raijin" from the cooldown map so a subsequent
        // sighting can re-invite immediately.
        party.Members.RemoveAt(0);

        Dispatch(router, "Also here: Raijin.");
        byte[] sent = Assert.Single(engine.LastSentForTests);
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

        Assert.Empty(engine.LastSentForTests);
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

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    // ===== @join nag escalation =====

    [Fact]
    public void JoinNag_FiresAfterInitialDelay()
    {
        // Within the initial delay window, no @join. Past it, one @join
        // fires. We tick the nag loop manually via the test seam to avoid
        // depending on the real DispatcherTimer.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear(); // ignore the initial invite

        // 3s in — no nag yet.
        engine.NowProvider = () => t0.AddSeconds(3);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);

        // 5s in — first @join.
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        byte[] first = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(first));
    }

    [Fact]
    public void JoinNag_ResendsAtFrequencyAfterFirst()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        // First @join at t+5s, then re-fire at t+15s, t+25s.
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        engine.NowProvider = () => t0.AddSeconds(15);
        engine.TickNagsForTests();
        engine.NowProvider = () => t0.AddSeconds(25);
        engine.TickNagsForTests();

        Assert.Equal(3, engine.LastSentForTests.Count);
        foreach (byte[] sent in engine.LastSentForTests)
            Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void JoinNag_OkTelepath_HaltsSendsButKeepsWaiting()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests); // first @join

        // Raijin replies {Ok} — further sends should halt even though
        // the cadence + cap windows would otherwise allow them.
        Dispatch(router, "Raijin telepaths: {Ok}");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        engine.NowProvider = () => t0.AddSeconds(40);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void JoinNag_NonBracedTextReply_AbortsEntireNag()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests);

        // Non-braced free text is a human replying (a decline) — kill the nag.
        Dispatch(router, "Raijin telepaths: nah I'm good");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        // @health reply arrives within the initial-delay window, before any @join.
        Dispatch(router, "Raijin telepaths: {HP=43/43,MA=15/34, Resting}");

        // Past the delay — the nag survived, so the first @join still fires.
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Raijin @join\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void ActiveNagSnapshot_ReflectsInFlightNagProgression()
    {
        // Backs the bug-report engine-state dump — the snapshot must mirror the
        // live nag as it progresses (armed → first send → {Ok} ack).
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
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

        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();          // first @join
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests);

        // Target joins — CollectionChanged.Add fires the cancel.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(120);

        // PartyManager.OnYouInvited adds the placeholder row, then the
        // server echo arms the nag (mirrors YouInvited_PendingInvitedRow).
        PartyMember row = new() { Name = "Raijin", IsInvited = true };
        party.Members.Add(row);
        Dispatch(router, "You have invited Raijin to follow you.");

        // First @join fires after the delay — nag is live.
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "/Raijin @join\r");
        engine.LastSentForTests.Clear();

        // Raijin accepts — PartyManager.OnFollowsYou flips the existing
        // row in place. This is a PropertyChanged, not a CollectionChanged.Add.
        row.IsInvited = false;

        // Subsequent ticks (past cadence + cap windows) produce no @join.
        engine.NowProvider = () => t0.AddSeconds(15);
        engine.TickNagsForTests();
        engine.NowProvider = () => t0.AddSeconds(25);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void JoinNag_TotalWindowExpired_StopsNagging()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();

        // Tick across the entire 55s window — sends fire until then.
        for (int t = 5; t <= 55; t += 10)
        {
            engine.NowProvider = () => t0.AddSeconds(t);
            engine.TickNagsForTests();
        }
        int sendsWithinWindow = engine.LastSentForTests.Count;

        engine.LastSentForTests.Clear();

        // Anything past 55s — nothing further.
        engine.NowProvider = () => t0.AddSeconds(70);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
        Assert.True(sendsWithinWindow >= 1);
    }

    [Fact]
    public void JoinNag_BecomingAFollowerMidFlow_AbortsAllNags()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        engine.LastSentForTests.Clear();

        // We accept someone else's invite mid-nag — became a follower.
        party.IsInParty    = true;
        party.SelfIsLeader = false;

        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
        Assert.Equal(2, engine.LastSentForTests.Count);
        engine.LastSentForTests.Clear();

        // Both joined the party, then it dissolved.
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.IsInParty = true;
        party.Members.Clear();
        party.IsInParty = false; // triggers the map flush.

        // Both should now be re-invite-eligible immediately.
        Dispatch(router, "Also here: Raijin, Helper.");
        Assert.Equal(2, engine.LastSentForTests.Count);
        Assert.Contains(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(engine.LastSentForTests,
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
        Assert.Single(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();
        Assert.True(coord.IsPaused);

        // Before the window elapses — still holding, no uninvite.
        engine.NowProvider = () => t0.AddSeconds(80);
        engine.TickNagsForTests();
        Assert.True(coord.IsPaused);
        Assert.DoesNotContain(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "uninvite Raijin\r");

        // Past the window — uninvite the no-show and release the gate.
        engine.NowProvider = () => t0.AddSeconds(91);
        engine.TickNagsForTests();
        Assert.Contains(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "uninvite Raijin\r");
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void InviteWait_TargetJoins_ReleasesGateWithoutUninvite()
    {
        var (engine, router, players, party) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        MovementCoordinator coord = new();
        engine.InviteWaitWindow = TimeSpan.FromSeconds(90);
        engine.SetMovementGate(coord, isLooping: () => true);

        Dispatch(router, "Also here: Raijin.");
        engine.LastSentForTests.Clear();
        Assert.True(coord.IsPaused);

        // Production leader path: invite adds a placeholder row (still
        // holding), then acceptance flips IsInvited false on the same row.
        PartyMember row = new() { Name = "Raijin", IsInvited = true };
        party.Members.Add(row);
        Assert.True(coord.IsPaused);
        row.IsInvited = false;

        Assert.False(coord.IsPaused);

        // Even past the window, no uninvite — they joined.
        engine.NowProvider = () => t0.AddSeconds(120);
        engine.TickNagsForTests();
        Assert.DoesNotContain(engine.LastSentForTests,
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

        // Whole-party wipe releases any pending loop hold.
        party.IsInParty = true;
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });
        party.Members.Add(new PartyMember { Name = "Forged" });

        engine.NotePartySplitTeleport();

        // Gate holds immediately (the walker must pause for the reform), but
        // NOTHING is invited yet — the members haven't materialised.
        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);
        Assert.Empty(engine.LastSentForTests);

        // Each member's teleport-arrival line fires their withheld invite.
        Dispatch(router, "Raijin appears in a blinding flash of light!");
        Dispatch(router, "Forged appears in a blinding flash of light!");

        Assert.Equal(2, engine.LastSentForTests.Count);
        Assert.Contains(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        Assert.Contains(engine.LastSentForTests,
            b => Encoding.Latin1.GetString(b) == "invite Forged\r");
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Wanderer appears in a blinding flash of light!");

        Assert.Empty(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(engine.LastSentForTests);

        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Raijin appears in a blinding flash of light!");
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(engine.LastSentForTests);   // invite deferred, nothing yet

        engine.FireReformRedisplayForTests();

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Dispatch(router, "Raijin appears in a blinding flash of light!");

        int before = engine.LastSentForTests.Count;
        engine.FireReformRedisplayForTests();

        Assert.Equal(before, engine.LastSentForTests.Count);
        Assert.DoesNotContain(engine.LastSentForTests,
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

        Assert.Empty(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
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
        engine.NowProvider = () => t0;
        engine.UninviteSuppression = TimeSpan.FromMinutes(60);

        Dispatch(router, "Raijin has been removed from your followers.");
        engine.LastSentForTests.Clear();

        // 10 min later — still inside the suppression window.
        engine.NowProvider = () => t0.AddMinutes(10);
        Dispatch(router, "Also here: Raijin.");

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Uninvite_SuppressionExpires_AllowsReinviteAfterWindow()
    {
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.UninviteSuppression = TimeSpan.FromMinutes(60);

        Dispatch(router, "Raijin has been removed from your followers.");
        engine.LastSentForTests.Clear();

        // Past the suppression window AND past the regular 60s
        // re-invite cooldown.
        engine.NowProvider = () => t0.AddMinutes(61);
        Dispatch(router, "Also here: Raijin.");

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        PlayerDatabase players = new();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        TrainerMenuTracker tracker = new(router, party) { NowProvider = () => Now };
        AutoPartyManager engine = new(router, players, party, tracker) { NowProvider = () => Now };
        engine.SetWireSender(_ => { });

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
        engine.LastSentForTests.Clear();

        // Exit the menu — next in-game prompt fires MenuExited.
        Dispatch(router, "[HP=33]:");

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void TrainerMenuExited_StillInParty_DoesNotResendInvite()
    {
        // If the roster member never left State.Members across the
        // menu trip, there's nothing to re-invite.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        TrainerMenuTracker tracker = new(router, party) { NowProvider = () => Now };
        AutoPartyManager engine = new(router, players, party, tracker) { NowProvider = () => Now };
        engine.SetWireSender(_ => { });

        party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        engine.LastSentForTests.Clear();

        Dispatch(router, "[HP=33]:");

        Assert.Empty(engine.LastSentForTests);
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
        PlayerDatabase players = new();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        PartyState party = new();
        TrainerMenuTracker tracker = new(router, party) { NowProvider = () => Now };
        AutoPartyManager engine = new(router, players, party, tracker) { NowProvider = () => Now };
        engine.SetWireSender(_ => { });

        party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);

        // Follower view dissolved → row is now a bare [Invited] placeholder.
        party.Members.Clear();
        party.Members.Add(new PartyMember { Name = "Raijin WuzHere", IsInvited = true });
        engine.LastSentForTests.Clear();

        Dispatch(router, "[HP=33]:");

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Uninvite_CancelsActiveNagInFlight()
    {
        // Uninvite arriving mid-nag should kill the nag — the player
        // we're nagging is the one we just kicked.
        var (engine, router, players, _) = Setup();
        SeedPlayer(players, "Raijin", inviteOnSeen: true);
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);

        Dispatch(router, "Also here: Raijin.");
        engine.NowProvider = () => t0.AddSeconds(5);
        engine.TickNagsForTests();
        Assert.Equal(2, engine.LastSentForTests.Count); // invite + 1st @join

        Dispatch(router, "Raijin has been removed from your followers.");
        engine.LastSentForTests.Clear();

        // Further ticks — no more @join because the nag was cancelled.
        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        engine.NowProvider = () => t0.AddSeconds(40);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        Dispatch(router, "You have invited Raijin to follow you.");

        // First nag fires after JoinNagInitialDelay.
        engine.NowProvider = () => t0.AddSeconds(6);
        engine.TickNagsForTests();
        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);
        engine.JoinNagFrequency    = TimeSpan.FromSeconds(10);
        engine.JoinNagMaxTotal     = TimeSpan.FromSeconds(55);

        // Auto-path fires the invite + arms the nag.
        Dispatch(router, "Also here: Raijin.");
        Assert.Single(engine.LastSentForTests, b => Encoding.Latin1.GetString(b) == "invite Raijin\r");
        engine.LastSentForTests.Clear();

        // Server echo arrives — handler is idempotent.
        Dispatch(router, "You have invited Raijin to follow you.");

        // Advance past the initial delay and tick — only ONE @join
        // should fire (single armed nag entry).
        engine.NowProvider = () => t0.AddSeconds(6);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        engine.NowProvider = () => t0.AddSeconds(6);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void YouInvited_PlayerAlreadyJoined_NoNag()
    {
        // Edge case: invitee accepted between the invite send and the
        // echo (race). Skip nag.
        var (engine, router, _, party) = Setup();
        party.Members.Add(new PartyMember { Name = "Raijin", IsInvited = false });
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        engine.NowProvider = () => t0.AddSeconds(6);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
        engine.NowProvider = () => t0;
        engine.JoinNagInitialDelay = TimeSpan.FromSeconds(5);

        Dispatch(router, "You have invited Raijin to follow you.");

        engine.NowProvider = () => t0.AddSeconds(6);
        engine.TickNagsForTests();
        Assert.Single(engine.LastSentForTests, b => Encoding.Latin1.GetString(b) == "/Raijin @join\r");
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.Empty(engine.LastSentForTests);

        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Monster, "east", default));

        Assert.Empty(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();

        // Cardinal follow-in (staging room) must NOT fire the invite.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "east", default));
        Assert.Empty(engine.LastSentForTests);

        // The real through-hole arrival ("from nowhere") fires it.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));
        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.True(coord.IsPaused);

        // Server disband arrives before the member does.
        party.SelfIsLeader = false;
        party.IsInParty    = false;

        // Reform survived — still holding, nothing prematurely invited.
        Assert.True(coord.IsPaused);
        Assert.Empty(engine.LastSentForTests);

        // Member finally walks in — the withheld invite goes out and reforms.
        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));

        byte[] sent = Assert.Single(engine.LastSentForTests);
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
        party.Members.Add(new PartyMember { Name = "Fujin", IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin" });

        engine.NotePartySplitTeleport();
        Assert.True(coord.IsPaused);

        // Transient "became a follower" flip mid-teleport.
        party.SelfIsLeader = false;

        // Still holding for the reform — the flip didn't release the gate.
        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyInviteGate, coord.AssertedGates);

        engine.OnPlayerArrival(new RoomEntryArrivalEvent("Raijin", EntityKind.Player, "nowhere", default));
        Assert.Single(engine.LastSentForTests);
    }
}
