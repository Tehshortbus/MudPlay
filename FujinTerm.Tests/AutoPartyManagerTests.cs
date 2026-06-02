using System.Text;
using FujinTerm.Game;
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
    public void JoinNag_NonOkTelepath_AbortsEntireNag()
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

        // Anything other than {Ok} from the target — kill the nag.
        Dispatch(router, "Raijin telepaths: nah I'm good");
        engine.LastSentForTests.Clear();

        engine.NowProvider = () => t0.AddSeconds(20);
        engine.TickNagsForTests();
        Assert.Empty(engine.LastSentForTests);
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
}
