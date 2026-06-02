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
}
