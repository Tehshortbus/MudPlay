using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class RemoteCommandManagerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // What the engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

    // Build a self-contained remote-command engine + the minimum
    // surrounding state. The test PlayerDatabase starts empty —
    // individual tests seed it via RecordObservation / EditCustomization.
    private (RemoteCommandManager engine, PartyState party, PlayerDatabase players) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_sent.Add);
        return (engine, party, players);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static ChatLine Gossip(string sender, string msg) =>
        new(ChatChannel.Gossip, sender, msg);

    private static ChatLine Yell(string sender, string msg) =>
        new(ChatChannel.Yell, sender, msg);

    private static ChatLine Local(string sender, string msg) =>
        new(ChatChannel.Local, sender, msg);

    private static ChatLine Gangpath(string sender, string msg) =>
        new(ChatChannel.Gangpath, sender, msg);

    private static ChatLine Broadcast(string sender, string msg) =>
        new(ChatChannel.Broadcast, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static void SeedPartyMember(PartyState p, string name)
    {
        // Use the same path PartyManager uses internally — manipulate the
        // ObservableCollection directly with a fresh PartyMember row.
        p.Members.Add(new PartyMember { Name = name });
        p.IsInParty = true;
    }

    // ===== Engine pipeline basics =====

    [Fact]
    public void NoHandlersRegistered_DoesNothing()
    {
        var (engine, _, _) = Setup();
        // Opt out of the WarnOnDenial reply so this test still proves the
        // original invariant: an engine with no handlers neither fires nor
        // sends. The dedicated WarnOnDenial-on tests below cover the
        // failure-message reply path.
        engine.WarnOnDenial = false;
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Empty(_sent);
    }

    [Fact]
    public void RegisterHandler_RequiresAtPrefix()
    {
        var (engine, _, _) = Setup();
        Assert.Throws<ArgumentException>(() =>
            engine.RegisterHandler("health", PlayerRemoteControls.QueryHealthStatus, _ => { }));
    }

    [Fact]
    public void UnregisterHandler_RemovesIt()
    {
        var (engine, _, _) = Setup();
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => { });
        Assert.True(engine.UnregisterHandler("@health"));
        Assert.False(engine.UnregisterHandler("@health"));
    }

    // ===== Prefix-form handlers (@equip-<set>) =====
    // RegisterPrefixHandler routes a family of suffix-form commands sharing
    // one prefix; the text after the prefix is folded in as Args[0]. Prefix
    // handlers are consulted only after an exact-handler miss, and only when
    // the inbound command carries a non-empty remainder after the prefix.

    [Fact]
    public void RegisterPrefixHandler_RequiresAtPrefix()
    {
        var (engine, _, _) = Setup();
        Assert.Throws<ArgumentException>(() =>
            engine.RegisterPrefixHandler("equip-", PlayerRemoteControls.ExecuteCommands, _ => { }));
    }

    [Fact]
    public void PrefixHandler_FoldsSuffixIntoLeadingArg()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);

        IReadOnlyList<string>? captured = null;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            ctx => captured = ctx.Args);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-fighting"));

        Assert.Equal(new[] { "fighting" }, captured);
    }

    [Fact]
    public void PrefixHandler_SuffixPrependedAheadOfRemainingArgs()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);

        IReadOnlyList<string>? captured = null;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            ctx => captured = ctx.Args);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-tank now please"));

        Assert.Equal(new[] { "tank", "now", "please" }, captured);
    }

    [Fact]
    public void PrefixHandler_NotMatchedWithoutRemainder()
    {
        // "@equip-" exactly (no suffix) matches no complete command, so it falls
        // through to the unknown-command path — which stays SILENT even with the
        // warn setting on (an @-token that matches nothing is treated as chat
        // noise, never a command to bounce a reply at).
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = true;
        engine.FailureMessage = "denied";

        bool fired = false;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void PrefixHandler_ExactHandlerWins()
    {
        // An exact registration for the full command beats a prefix whose
        // suffix would otherwise match — exact lookup runs first.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);

        string? hit = null;
        engine.RegisterHandler("@equip-foo", PlayerRemoteControls.ExecuteCommands,
            _ => hit = "exact");
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => hit = "prefix");

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-foo"));

        Assert.Equal("exact", hit);
    }

    [Fact]
    public void PrefixHandler_FiresWhenSenderHasFlag()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);

        bool fired = false;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-fighting"));

        Assert.True(fired);
    }

    [Fact]
    public void PrefixHandler_DeniedWhenSenderLacksFlag()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.QueryVersion); // not ExecuteCommands

        bool fired = false;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@equip-fighting"));

        Assert.False(fired);
    }

    [Fact]
    public void PrefixHandler_RerollSuffixHardBlockedSilently()
    {
        // Hard-blocks scan the raw command token and run BEFORE handler
        // lookup, so a degenerate "@equip-reroll" trips the reroll
        // hard-block and never reaches the prefix handler — silently.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@equip-reroll"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void UnregisterPrefixHandler_RemovesIt()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);

        int fired = 0;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired++);

        Assert.True(engine.UnregisterPrefixHandler("@equip-"));
        Assert.False(engine.UnregisterPrefixHandler("@equip-"));

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@equip-fighting"));
        Assert.Equal(0, fired);
    }

    // ===== Permission gating =====

    [Fact]
    public void Handler_FiresWhenSenderHasRequiredFlag()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired = true; Assert.Equal("Friend", ctx.Sender); });

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));

        Assert.True(fired);
    }

    [Fact]
    public void Handler_DeniedWhenSenderLacksFlag()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.QueryVersion); // only version

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));

        Assert.False(fired);
    }

    [Fact]
    public void Handler_DeniedWhenSenderUnknown()
    {
        var (engine, _, _) = Setup();
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("NeverSeen", "@health"));

        Assert.False(fired);
    }

    // ===== Party-whitelist (requiredCategory == None) =====

    [Fact]
    public void PartyWhitelist_AllowsActivePartyMember()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party rest"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyWhitelist_DeniesNonPartyMember()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@party rest"));

        Assert.False(fired);
    }

    // ===== @party-specific party-member fallback ========================
    // @party in the production catalog sits at QueryHealthStatus so
    // non-party callers with that grant can use it as a status-query
    // alias for @par. The engine adds an @party-specific party-member
    // fallback so the "base @party always allowed inside an
    // active party" rule still holds even for party members who lack
    // an explicit per-player grant.

    [Fact]
    public void PartyFallback_PartyMemberWithoutGrant_StillReachesHandler()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        // No SeedPlayer call — Buddy has zero per-player grants.

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyFallback_NonPartyWithGrant_StillReachesHandler()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@party"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyFallback_NonPartyWithoutGrant_DeniedAtEngine()
    {
        var (engine, _, _) = Setup();
        // Stranger isn't in the party AND has no per-player grant.

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@party"));

        Assert.False(fired);
    }

    [Fact]
    public void PartyFallback_DisallowPartyDirectives_RevokesMemberFallback()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.DisallowPartyDirectives = true;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party"));

        // No per-player grant + @party directives disallowed = engine denies.
        Assert.False(fired);
    }

    [Fact]
    public void PartyFallback_DisallowPartyDirectives_DoesNotBlockExplicitGrant()
    {
        // DisallowPartyDirectives only kills the party-member-without-grant
        // @party path; a sender (party member or not) who DOES carry the
        // per-player QueryHealthStatus grant is still admitted.
        var (engine, party, players) = Setup();
        SeedPartyMember(party, "Buddy");
        SeedPlayer(players, "Buddy", PlayerRemoteControls.QueryHealthStatus);
        engine.DisallowPartyDirectives = true;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party"));

        Assert.True(fired);
    }

    // ===== @reset party-member fallback (report 222201) =================
    // @reset is filed under AlterSettings for permission-grouping, but it's a
    // party-rhythm function (leader zeroes everyone's per-lap counters), so an
    // active party member may issue it without the AlterSettings grant.

    [Fact]
    public void ResetFallback_PartyMemberWithoutGrant_StillReachesHandler()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        // No SeedPlayer — Buddy carries no AlterSettings grant.

        bool fired = false;
        engine.RegisterHandler("@reset", PlayerRemoteControls.AlterSettings, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@reset"));

        Assert.True(fired);
    }

    [Fact]
    public void Reset_NonPartyWithoutGrant_DeniedAtEngine()
    {
        var (engine, _, _) = Setup();
        // Stranger isn't in the party AND has no AlterSettings grant.

        bool fired = false;
        engine.RegisterHandler("@reset", PlayerRemoteControls.AlterSettings, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@reset"));

        Assert.False(fired);
    }

    [Fact]
    public void DisallowPartyDirectives_LeavesCoordinationWhitelistIntact()
    {
        // The toggle narrows ONLY the @party directive path. The party
        // coordination signals (@wait / @ok / @comeback / @share) ride the
        // None-tier whitelist and still fire for an active member with no
        // per-player grant, even while @party directives are disallowed.
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.DisallowPartyDirectives = true;

        int fired = 0;
        foreach (string cmd in new[] { "@wait", "@ok", "@comeback", "@share" })
            engine.RegisterHandler(cmd, PlayerRemoteControls.None, _ => fired++);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@ok"));
        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@comeback"));
        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@share"));

        Assert.Equal(4, fired);
    }

    [Fact]
    public void HealthFallback_PartyMemberWithoutGrant_StillReachesHandler()
    {
        // Checking a member's HP/MA/lives is a party social baseline —
        // @health (QueryHealthStatus) auto-grants for an active party
        // member even with no per-player flag, mirroring @par.
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@health"));

        Assert.True(fired);
    }

    [Fact]
    public void HealthFallback_DisallowPartyDirectives_DoesNotBlockHealthQuery()
    {
        // DisallowPartyDirectives gates only @party's action sub-commands;
        // @health is a pure query, so a party member can still ask even
        // with directives disallowed.
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.DisallowPartyDirectives = true;

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@health"));

        Assert.True(fired);
    }

    [Fact]
    public void HealthFallback_NonPartyWithoutGrant_StillDenied()
    {
        // The baseline is party-scoped: a stranger with no grant is still
        // denied @health.
        var (engine, _, _) = Setup();

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));

        Assert.False(fired);
    }

    // ===== Hard-blocks =====

    // ===== Forcible @do / @party suicide redirect =====
    // Both unconditionally blocked even at lives > threshold + full
    // permissions. Reply (gated on WarnOnDenial) hints the sender at
    // the dedicated @suicide handler, which has its own elevated
    // SysopCommands grant + the lives-threshold gate + stored
    // password contract.

    [Fact]
    public void PartySuicide_PlainSuicideRelays_NotBlocked()
    {
        // Per user policy @party is a passthrough that blocks only the `set
        // suicide` phrase and reroll. A plain `suicide` is NOT force-blocked —
        // it reaches the @party handler like any other relayed command, even at
        // low lives + a strict threshold (those gate @suicide, not the relay).
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.LivesProvider = () => 99;
        engine.MaxSuicideLivesThreshold = 0;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party suicide"));

        Assert.True(fired);
        Assert.Empty(_sent);  // no redirect, no denial
    }

    [Fact]
    public void ForcibleSuicide_DoSuicide_AlwaysBlockedWithRedirect_EvenAboveThreshold()
    {
        // Pre-fix this scenario PASSED through to the @do handler
        // (lives 5 > threshold 3 satisfied the policy gate). User
        // direction: forcible-death verbs route exclusively through
        // @suicide, no exceptions.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 5;
        engine.MaxSuicideLivesThreshold = 3;

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(_sent[^1]);
        Assert.Contains("@do suicide is not allowed, use @suicide", reply);
    }

    [Fact]
    public void ForcibleSuicide_DoSuicide_AlwaysBlockedWithRedirect_EvenWithLivesUnknown()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        // LivesProvider null — redirect doesn't depend on lives.

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(_sent[^1]);
        Assert.Contains("@do suicide is not allowed, use @suicide", reply);
    }

    [Fact]
    public void ForcibleSuicide_DoSuicide_WarnOnDenialOff_SilentlyBlocked()
    {
        // Master gate suppresses the redirect reply too — same as
        // every other denial path.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 9;
        engine.MaxSuicideLivesThreshold = 3;
        engine.WarnOnDenial = false;

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void ForcibleSuicide_DoSuicide_TheWordAnywhereInTheArgsIsBlocked()
    {
        // "suicide" as a whole word anywhere in @do's args trips the block,
        // not only as the first word; the reply is the @do redirect.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do par suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(_sent[^1]);
        Assert.Contains("@do suicide is not allowed", reply);
    }

    [Fact]
    public void DirectSuicide_NotCaughtByRedirect_PolicyGateAppliesNormally()
    {
        // The redirect only fires for the @do prefix. Direct @suicide
        // flows through to the lives-threshold policy block.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 5;
        engine.MaxSuicideLivesThreshold = 3;

        bool fired = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.True(fired);  // policy gate satisfied (5 > 3)
    }

    [Fact]
    public void DirectSuicide_BlockedByPolicy_DoesNotMentionRedirect()
    {
        // Direct @suicide blocked by lives threshold — reply should
        // be the policy-block text ("suicide blocked, N lives <=
        // threshold M"), NOT the redirect.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 3;
        engine.MaxSuicideLivesThreshold = 3;

        bool fired = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(_sent[^1]);
        Assert.Contains("suicide blocked", reply);
        Assert.DoesNotContain("use @suicide", reply);
    }

    // ===== Reroll family — hard-block stays silent =====

    [Fact]
    public void HardBlock_DoReroll_AlwaysSilent()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do reroll"));

        Assert.False(fired);
        Assert.Empty(_sent);  // no reply, ever
    }

    [Fact]
    public void HardBlock_PartyReroll_AlwaysSilent()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party reroll"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    // ===== Channel routing + Reply =====

    [Fact]
    public void Reply_AlwaysWrappedInBraces_RegardlessOfChannel()
    {
        // Per user direction: every remote-command response is
        // encapsulated in { } on the wire. Handlers provide bare text;
        // the engine adds the braces in SendReply so handlers and
        // configured failure messages don't need to remember the
        // convention.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("plain"));

        ChatLines.Hear(_routers[engine], Telepath("Friend",  "@health"));
        ChatLines.Hear(_routers[engine], Gangpath("Friend",  "@health"));
        ChatLines.Hear(_routers[engine], Local("Friend",     "@health"));

        Assert.Equal(3, _sent.Count);
        Assert.Equal("/Friend {plain}\r", Encoding.Latin1.GetString(_sent[0]));
        Assert.Equal("bg {plain}\r",      Encoding.Latin1.GetString(_sent[1]));
        Assert.Equal(">Friend {plain}\r", Encoding.Latin1.GetString(_sent[2]));
    }

    // ===== Channel scope — noise-channel ignores =====
    //
    // Per user direction (verified live): remote commands fire from
    // Telepath / Gangpath / Local only. Realm-wide noise channels
    // (Gossip — also carries auctions, Yell — shouts), system-level
    // (Broadcast / RealmEvent), and our own TelepathOutgoing echo
    // are all silently ignored regardless of whether a handler is
    // registered or the sender is fully authorised.

    [Fact]
    public void Gossip_IsIgnoredEvenWithAuthorisedSender()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired = true; ctx.Reply("hi"); });

        ChatLines.Hear(_routers[engine], Gossip("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Yell_IsIgnoredEvenWithAuthorisedSender()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired = true; ctx.Reply("hi"); });

        ChatLines.Hear(_routers[engine], Yell("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    // The real yell lines from other players, fed through the pattern catalog and
    // ChatRouter into the engine: yells are chat now, but still carry no command.
    // "Someone" is granted too, so the name alone cannot be what stops it. The say
    // line is the control: the same text on a command channel does run.
    [Theory]
    [InlineData("From Alder (Yelling): \"@health\"")]
    [InlineData("Someone yells from the north \"@health\"")]
    [InlineData("Someone yells from above \"@health\"")]
    [InlineData("Someone yells from below \"@health\"")]
    [InlineData("Alder yells \"@health\"")]
    public void RealYellLines_RunNothingAndSendNothing(string wireLine)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_sent.Add);
        SeedPlayer(players, "Alder", PlayerRemoteControls.QueryHealthStatus);
        SeedPlayer(players, "Someone", PlayerRemoteControls.QueryHealthStatus);
        int fired = 0;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired++; ctx.Reply("hi"); });
        List<ChatLogEntry> heard = new();
        chat.EntryClassified += heard.Add;

        void Hear(string text) => router.Dispatch(new MudPlay.Terminal.LineExtractor.EmittedLine(
            text, new MudPlay.Terminal.CellAttributes[text.Length], Now, IsPromptLine: false, IsChat: true));

        Hear(wireLine);

        Assert.Equal(ChatChannel.Yell, Assert.Single(heard).Channel);
        Assert.Equal(0, fired);
        Assert.Empty(_sent);

        Hear("Alder says \"@health\"");

        Assert.Equal(1, fired);
        Assert.Single(_sent);
    }

    [Fact]
    public void Broadcast_IsIgnoredEvenWithAuthorisedSender()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => fired = true);

        ChatLines.Hear(_routers[engine], Broadcast("Friend", "@health"));

        Assert.False(fired);
    }

    // ===== Arg parsing =====

    [Fact]
    public void Args_AreSplitOnWhitespace()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.MovePlayer);

        IReadOnlyList<string>? capturedArgs = null;
        engine.RegisterHandler("@goto", PlayerRemoteControls.MovePlayer,
            ctx => capturedArgs = ctx.Args);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@goto Newhaven Cabin"));

        Assert.NotNull(capturedArgs);
        Assert.Equal(new[] { "Newhaven", "Cabin" }, capturedArgs);
    }

    [Fact]
    public void NonAtPrefixedMessage_IsIgnored()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "hello there"));

        Assert.False(fired);
    }

    [Fact]
    public void HandlerThrowing_DoesNotTearDownEngine()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        int calls = 0;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => { calls++; throw new InvalidOperationException("boom"); });

        // The engine swallows the exception; the second dispatch still reaches
        // the handler.
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));

        Assert.Equal(2, calls);
    }

    // ===== Ignored announce tokens =====
    //
    // Party ailment-sync announces (@poisoned, @blind, …) arrive on the same
    // chat channels as remote commands but are consumed by PartyAilmentTracker's
    // own subscription. RegisterIgnored reserves each token so the engine
    // swallows it silently — no handler dispatch, no denial reply — even with
    // WarnOnDenial on. Regression guard for the live report where a party member
    // bounced "{command invalid or not allowed}" back at every @poisoned announce.

    [Fact]
    public void IgnoredToken_SwallowedSilently_EvenWithWarnOnDenialAndFailureMessage()
    {
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = true;
        engine.FailureMessage = "denied";
        engine.RegisterIgnored("@poisoned");

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@poisoned"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void IgnoredToken_MatchedCaseInsensitively()
    {
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = true;
        engine.FailureMessage = "denied";
        engine.RegisterIgnored("@poisoned");

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@POISONED"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void NonIgnoredCommand_StillDispatches_ProvingIgnoreIsTargeted()
    {
        // Sanity companion: registering an ignored token doesn't blanket-mute the
        // engine — a DIFFERENT, registered command still dispatches normally.
        // (An unknown non-ignored @-token is silently ignored too now, so prove
        // the scope with a handler that fires rather than a denial reply.)
        var (engine, _, players) = Setup();
        engine.RegisterIgnored("@poisoned");
        SeedPlayer(players, "Buddy", PlayerRemoteControls.QueryHealthStatus);

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@health"));

        Assert.True(fired);
    }

    // ===== Settings.Talk knobs =====
    //
    // The TalkSectionViewModel pushes the loaded character's TalkSettings
    // into the live engine via ApplyToServices. These tests assert each
    // knob has the documented engine-side effect.

    [Fact]
    public void MasterDisable_DropsEveryInboundCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.MasterDisable = true;

        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired = true; ctx.Reply("hi"); });

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void DisableTelepathChannel_SilencesTelepathOnly()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.DisableTelepathChannel = true;

        int fireCount = 0;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => fireCount++);

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));   // muted
        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@health"));   // passes
        ChatLines.Hear(_routers[engine], Local("Friend", "@health"));      // passes

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void DisallowPartyDirectives_DeniesPartyHandlerEvenForActiveMember()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.DisallowPartyDirectives = true;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus,
            _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party rest"));

        Assert.False(fired);
    }

    [Fact]
    public void WarnOnDenial_StaysSilentOnUnknownCommand()
    {
        // An @-token matching no registered command is almost always ordinary
        // chat that merely starts with '@' (someone said "@because" in gang
        // chat), so it must NEVER draw a reply — not even with warn-on-invalid
        // on. Unlike the per-player / party-whitelist denials below, an unknown
        // token isn't a real command attempt. WarnOnDenial governs those, not this.
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = true;
        engine.FailureMessage = "nope";

        ChatLines.Hear(_routers[engine], Telepath("Chatter", "@because it was funny"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void WarnOnDenial_SendsFailureMessageOnPerPlayerDenial()
    {
        var (engine, _, players) = Setup();
        // Sender has version flag only; @health requires QueryHealthStatus.
        SeedPlayer(players, "Stranger", PlayerRemoteControls.QueryVersion);
        engine.FailureMessage = "denied";
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("/Stranger {denied}\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void WarnOnDenial_SendsFailureMessageOnPartyWhitelistDenial()
    {
        var (engine, _, _) = Setup();
        engine.FailureMessage = "denied";
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => { });

        // Stranger isn't in the party → party-whitelist denial path.
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@party rest"));

        byte[] sent = Assert.Single(_sent);
        Assert.Equal("/Stranger {denied}\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SuicidePolicyBlock_TelepathsBackReason()
    {
        // User-configured suicide threshold is a POLICY block (not a
        // safety hard-block). Distinct from reroll: the caller is
        // typically a trusted ally who needs to know why their
        // command isn't firing. Reply contains the live numbers so
        // they can see exactly where the threshold sits.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.SysopCommands);
        engine.MaxSuicideLivesThreshold = 5;
        engine.LivesProvider = () => 4;   // below threshold
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        byte[] sent = Assert.Single(_sent);
        Assert.Equal(
            "/Trusted {suicide blocked, 4 lives <= threshold 5}\r",
            Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SuicidePolicyBlock_AtThreshold_RepliesAndBlocks()
    {
        // Boundary: lives == threshold means "blocked" per the
        // `lives <= threshold` rule.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.SysopCommands);
        engine.MaxSuicideLivesThreshold = 5;
        engine.LivesProvider = () => 5;   // at threshold
        bool handlerFired = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands,
            _ => handlerFired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.False(handlerFired);   // gate stopped it
        byte[] sent = Assert.Single(_sent);
        Assert.Equal(
            "/Trusted {suicide blocked, 5 lives <= threshold 5}\r",
            Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SuicidePolicyBlock_LivesUnknown_RepliesUnknownAndBlocks()
    {
        // LivesProvider returns null → we don't trust the unknown
        // state, default to blocked, tell the sender so they know
        // to wait for us to `stat` first.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.SysopCommands);
        engine.LivesProvider = () => null;
        bool handlerFired = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands,
            _ => handlerFired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.False(handlerFired);
        byte[] sent = Assert.Single(_sent);
        Assert.Equal(
            "/Trusted {suicide blocked, lives unknown to client}\r",
            Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void SuicidePolicyBlock_AboveThreshold_FiresHandlerNoReply()
    {
        // Sanity: lives > threshold means the policy block doesn't
        // engage — the handler fires normally, no reply leaks.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.SysopCommands);
        engine.MaxSuicideLivesThreshold = 3;
        engine.LivesProvider = () => 9;
        bool handlerFired = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands,
            _ => handlerFired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.True(handlerFired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void SuicidePolicyBlock_WarnOnDenialOff_StaysSilent()
    {
        // WarnOnDenial is the master gate for ALL invalid / denial
        // replies — specific reasons included. When unchecked, the
        // policy-block reply is suppressed.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.SysopCommands);
        engine.MaxSuicideLivesThreshold = 5;
        engine.LivesProvider = () => 4;
        engine.WarnOnDenial = false;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@suicide"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void PartySetSuicide_HardBlockedSilently()
    {
        // `set suicide` is the one suicide payload @party refuses — it arms
        // unattended auto-suicide, so it's an unconditional silent hard-block
        // (like reroll): the handler never runs and nothing is replied, even
        // with every flag granted and WarnOnDenial on.
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Buddy", "@party set suicide hunter2"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void WarnOnDenial_StaysSilentWhenChannelDisabled()
    {
        // The user explicitly muted the channel — don't tell every spammer
        // on that channel why they got ignored.
        var (engine, _, _) = Setup();
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => { });
        engine.DisableTelepathChannel = true;

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Empty(_sent);

        engine.DisableTelepathChannel = false;
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Single(_sent);
    }

    [Fact]
    public void WarnOnDenialOff_NoFailureMessageSent()
    {
        var (engine, _, _) = Setup();
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => { });
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Empty(_sent);

        engine.WarnOnDenial = true;
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Single(_sent);
    }

    [Fact]
    public void EmptyFailureMessage_StaysSilentEvenWhenWarnOn()
    {
        // Empty message would otherwise serialise to "/Stranger \r" which
        // is just noise. Engine treats blank as opt-out.
        var (engine, _, _) = Setup();
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => { });
        string failure = engine.FailureMessage;
        engine.FailureMessage = "   ";

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Empty(_sent);

        engine.FailureMessage = failure;
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@health"));
        Assert.Single(_sent);
    }

    // ===== Public-channel self-echo =====

    [Fact]
    public void OwnGangpathEcho_IsSkipped_NoDenialBounce()
    {
        // A gangpath'd @timer sync echoes back tagged with our character name (public
        // channels never use "You"). Without the self-name guard the engine would run it,
        // deny it (no self-grant), and bounce "command invalid" at the whole gang — the
        // reported bug. The given-name echo must match our "Given Family" self-name.
        var (engine, _, _) = Setup();
        bool fired = false;
        engine.RegisterHandler("@timer", PlayerRemoteControls.QueryBossTimers, _ => fired = true);
        engine.SelfNameProvider = () => "Raijin WuzHere";

        ChatLines.Hear(_routers[engine], Gangpath("Raijin", "@timer sync"));

        Assert.False(fired);
        Assert.Empty(_sent);
    }

    [Fact]
    public void AnotherPlayersGangpath_StillProcessed_WhenSelfNameSet()
    {
        // The self-name guard must not swallow a real inbound command from someone else.
        var (engine, _, players) = Setup();
        bool fired = false;
        engine.RegisterHandler("@timer", PlayerRemoteControls.QueryBossTimers, _ => fired = true);
        engine.SelfNameProvider = () => "Raijin";
        SeedPlayer(players, "Fujin", PlayerRemoteControls.QueryBossTimers);

        ChatLines.Hear(_routers[engine], Gangpath("Fujin", "@timer sync"));

        Assert.True(fired);
    }

    // ===== &@ relay-back =====

    private List<string> Sent() =>
        _sent.Select(b => Encoding.Latin1.GetString(b)).ToList();

    [Fact]
    public void RelayBack_Granted_EchoesTheCommandToTheSenderBare()
    {
        var (engine, _, players) = Setup();
        bool ranHere = false;
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, _ => ranHere = true);
        SeedPlayer(players, "Leader", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@invite"));

        Assert.Equal(new[] { "/Leader @invite\r" }, Sent());
        Assert.False(ranHere);   // relayed, not run on this client
    }

    [Fact]
    public void RelayBack_KeepsArgs_AndRepliesOnTheInboundChannel()
    {
        var (engine, _, players) = Setup();
        engine.RegisterHandler("@where", PlayerRemoteControls.QueryLocation, _ => { });
        SeedPlayer(players, "Leader", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Gangpath("Leader", "&@where now"));
        ChatLines.Hear(_routers[engine], Local("Leader", "&@where"));

        Assert.Equal(new[] { "bg @where now\r", ">Leader @where\r" }, Sent());
    }

    [Fact]
    public void RelayBack_WithoutExecuteGrant_Denied()
    {
        var (engine, _, players) = Setup();
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, _ => { });
        // Holding the command's own grant isn't enough — relay-back is the @do tier.
        SeedPlayer(players, "Leader", PlayerRemoteControls.RequestInvite);

        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@invite"));

        Assert.Equal(new[] { "/Leader {command invalid or not allowed}\r" }, Sent());
    }

    [Fact]
    public void RelayBack_Denied_SilentWhenWarnOnDenialOff()
    {
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = false;
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "&@invite"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void RelayBack_UnknownOrNestedCommand_Ignored()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Leader", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@nosuchthing"));
        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@&@invite"));
        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void RelayBack_HardBlockedPayload_SilentlyDropped()
    {
        var (engine, _, players) = Setup();
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => { });
        SeedPlayer(players, "Leader", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@do reroll"));

        Assert.Empty(_sent);
    }

    [Fact]
    public void RelayBack_MasterDisable_Ignored()
    {
        var (engine, _, players) = Setup();
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, _ => { });
        SeedPlayer(players, "Leader", PlayerRemoteControls.ExecuteCommands);
        engine.MasterDisable = true;

        ChatLines.Hear(_routers[engine], Telepath("Leader", "&@invite"));

        Assert.Empty(_sent);
    }
}
