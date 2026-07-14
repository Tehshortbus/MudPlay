using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RemoteCommandManagerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Build a self-contained remote-command engine + the minimum
    /// surrounding state. The test PlayerDatabase starts empty —
    /// individual tests seed it via RecordObservation / EditCustomization.
    /// </summary>
    private static (RemoteCommandManager engine, PartyState party, PlayerDatabase players) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        return (engine, party, players);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static ChatLogEntry Gossip(string sender, string msg) =>
        new(Now, ChatChannel.Gossip, sender, msg, $"{sender} gossips: {msg}");

    private static ChatLogEntry Yell(string sender, string msg) =>
        new(Now, ChatChannel.Yell, sender, msg, $"{sender} yells: {msg}");

    private static ChatLogEntry Local(string sender, string msg) =>
        new(Now, ChatChannel.Local, sender, msg, $"{sender} says: {msg}");

    private static ChatLogEntry Gangpath(string sender, string msg) =>
        new(Now, ChatChannel.Gangpath, sender, msg, $"{sender} gangpaths: {msg}");

    private static ChatLogEntry Broadcast(string sender, string msg) =>
        new(Now, ChatChannel.Broadcast, sender, msg, $"BROADCAST: {msg}");

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
        engine.DispatchForTests(Telepath("Stranger", "@health"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void RegisterHandler_RequiresAtPrefix()
    {
        var (engine, _, _) = Setup();
        Assert.Throws<ArgumentException>(() =>
            engine.RegisterHandler("health", PlayerRemoteControls.QueryHealthStatus, _ => { }));
    }

    [Fact]
    public void RegisterHandler_IncrementsHandlerCount()
    {
        var (engine, _, _) = Setup();
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, _ => { });
        engine.RegisterHandler("@where",  PlayerRemoteControls.QueryLocation,     _ => { });
        Assert.Equal(2, engine.HandlerCount);
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

        engine.DispatchForTests(Telepath("Friend", "@equip-fighting"));

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

        engine.DispatchForTests(Telepath("Friend", "@equip-tank now please"));

        Assert.Equal(new[] { "tank", "now", "please" }, captured);
    }

    [Fact]
    public void PrefixHandler_NotMatchedWithoutRemainder()
    {
        // "@equip-" exactly (no suffix) needs a strict prefix to match, so
        // it falls through to the unknown-command denial path.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.ExecuteCommands);
        engine.FailureMessage = "denied";

        bool fired = false;
        engine.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands,
            _ => fired = true);

        engine.DispatchForTests(Telepath("Friend", "@equip-"));

        Assert.False(fired);
        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Friend {denied}\r", Encoding.Latin1.GetString(sent));
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

        engine.DispatchForTests(Telepath("Friend", "@equip-foo"));

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

        engine.DispatchForTests(Telepath("Friend", "@equip-fighting"));

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

        engine.DispatchForTests(Telepath("Stranger", "@equip-fighting"));

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

        engine.DispatchForTests(Telepath("Trusted", "@equip-reroll"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
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

        engine.WarnOnDenial = false; // suppress the now-unknown-command reply
        engine.DispatchForTests(Telepath("Friend", "@equip-fighting"));
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

        engine.DispatchForTests(Telepath("Friend", "@health"));

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

        engine.DispatchForTests(Telepath("Stranger", "@health"));

        Assert.False(fired);
    }

    [Fact]
    public void Handler_DeniedWhenSenderUnknown()
    {
        var (engine, _, _) = Setup();
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => fired = true);

        engine.DispatchForTests(Telepath("NeverSeen", "@health"));

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

        engine.DispatchForTests(Telepath("Buddy", "@party rest"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyWhitelist_DeniesNonPartyMember()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        engine.DispatchForTests(Telepath("Stranger", "@party rest"));

        Assert.False(fired);
    }

    // ===== @party-specific party-member fallback ========================
    // @party in the production catalog sits at QueryHealthStatus so
    // non-party callers with that grant can use it as a status-query
    // alias for @par. The engine adds an @party-specific party-member
    // fallback so the Phase 6 "base @party always allowed inside an
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

        engine.DispatchForTests(Telepath("Buddy", "@party"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyFallback_NonPartyWithGrant_StillReachesHandler()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        engine.DispatchForTests(Telepath("Friend", "@party"));

        Assert.True(fired);
    }

    [Fact]
    public void PartyFallback_NonPartyWithoutGrant_DeniedAtEngine()
    {
        var (engine, _, _) = Setup();
        // Stranger isn't in the party AND has no per-player grant.

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.QueryHealthStatus, _ => fired = true);

        engine.DispatchForTests(Telepath("Stranger", "@party"));

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

        engine.DispatchForTests(Telepath("Buddy", "@party"));

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

        engine.DispatchForTests(Telepath("Buddy", "@party"));

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

        engine.DispatchForTests(Telepath("Buddy", "@reset"));

        Assert.True(fired);
    }

    [Fact]
    public void Reset_NonPartyWithoutGrant_DeniedAtEngine()
    {
        var (engine, _, _) = Setup();
        // Stranger isn't in the party AND has no AlterSettings grant.

        bool fired = false;
        engine.RegisterHandler("@reset", PlayerRemoteControls.AlterSettings, _ => fired = true);

        engine.DispatchForTests(Telepath("Stranger", "@reset"));

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

        engine.DispatchForTests(Telepath("Buddy", "@wait"));
        engine.DispatchForTests(Telepath("Buddy", "@ok"));
        engine.DispatchForTests(Telepath("Buddy", "@comeback"));
        engine.DispatchForTests(Telepath("Buddy", "@share"));

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

        engine.DispatchForTests(Telepath("Buddy", "@health"));

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

        engine.DispatchForTests(Telepath("Buddy", "@health"));

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

        engine.DispatchForTests(Telepath("Stranger", "@health"));

        Assert.False(fired);
    }

    // ===== Hard-blocks =====

    [Fact]
    public void HardBlock_RerollAlwaysDeniedRegardlessOfFlags()
    {
        var (engine, _, players) = Setup();
        // Give the sender EVERY permission — hard-block must still win.
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do reroll"));

        Assert.False(fired);
    }

    [Fact]
    public void HardBlock_PartyRerollAlwaysDenied()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        engine.DispatchForTests(Telepath("Buddy", "@party reroll"));

        Assert.False(fired);
    }

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

        engine.DispatchForTests(Telepath("Buddy", "@party suicide"));

        Assert.True(fired);
        Assert.Empty(engine.LastSentForTests);  // no redirect, no denial
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

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(engine.LastSentForTests[^1]);
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

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(engine.LastSentForTests[^1]);
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

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void ForcibleSuicide_DoSuicide_TokenMatchAlsoCatchesNestedArgs()
    {
        // Defensive: any arg containing "suicide" trips the token
        // match. Reply text is the @do redirect even when the arg
        // isn't literally "suicide" — close enough; the block is
        // correct.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do par suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(engine.LastSentForTests[^1]);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        Assert.False(fired);
        string reply = Encoding.Latin1.GetString(engine.LastSentForTests[^1]);
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

        engine.DispatchForTests(Telepath("Trusted", "@do reroll"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);  // no reply, ever
    }

    [Fact]
    public void HardBlock_PartyReroll_AlwaysSilent()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        engine.DispatchForTests(Telepath("Buddy", "@party reroll"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Friend",  "@health"));
        engine.DispatchForTests(Gangpath("Friend",  "@health"));
        engine.DispatchForTests(Local("Friend",     "@health"));

        Assert.Equal(3, engine.LastSentForTests.Count);
        Assert.Equal("/Friend {plain}\r", Encoding.Latin1.GetString(engine.LastSentForTests[0]));
        Assert.Equal("gang {plain}\r",    Encoding.Latin1.GetString(engine.LastSentForTests[1]));
        Assert.Equal(".{plain}\r",        Encoding.Latin1.GetString(engine.LastSentForTests[2]));
    }

    [Fact]
    public void Reply_TelepathRoutesViaTelepathCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("HP 100/100"));

        engine.DispatchForTests(Telepath("Friend", "@health"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        string wire = Encoding.Latin1.GetString(sent);
        // Engine wraps every reply in { } at SendReply time — per user
        // direction every remote response carries the curly-brace
        // meta-line convention.
        Assert.Equal("/Friend {HP 100/100}\r", wire);
    }

    [Fact]
    public void Reply_GangpathRoutesViaGangCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("hi"));

        engine.DispatchForTests(Gangpath("Friend", "@health"));

        string wire = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        Assert.Equal("gang {hi}\r", wire);
    }

    [Fact]
    public void Reply_LocalRoutesViaSayPrecursor()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("hi"));

        engine.DispatchForTests(Local("Friend", "@health"));

        string wire = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        // Say channel answers with the period say-precursor, not the `say` verb.
        Assert.Equal(".{hi}\r", wire);
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

        engine.DispatchForTests(Gossip("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Yell_IsIgnoredEvenWithAuthorisedSender()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => { fired = true; ctx.Reply("hi"); });

        engine.DispatchForTests(Yell("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Broadcast_IsIgnoredEvenWithAuthorisedSender()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        bool fired = false;
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => fired = true);

        engine.DispatchForTests(Broadcast("Friend", "@health"));

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

        engine.DispatchForTests(Telepath("Friend", "@goto Newhaven Cabin"));

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

        engine.DispatchForTests(Telepath("Friend", "hello there"));

        Assert.False(fired);
    }

    [Fact]
    public void HandlerThrowing_DoesNotTearDownEngine()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            _ => throw new InvalidOperationException("boom"));

        // Should swallow the exception and continue. A second dispatch
        // proves the engine is still alive.
        engine.DispatchForTests(Telepath("Friend", "@health"));
        engine.DispatchForTests(Telepath("Friend", "@health"));
        // No assertion needed — the test passes if no exception escapes.
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

        engine.DispatchForTests(Telepath("Buddy", "@poisoned"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void IgnoredToken_MatchedCaseInsensitively()
    {
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = true;
        engine.FailureMessage = "denied";
        engine.RegisterIgnored("@poisoned");

        engine.DispatchForTests(Telepath("Buddy", "@POISONED"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void UnregisteredToken_StillReplies_ProvingIgnoreIsTargeted()
    {
        // Sanity companion: an @-command that ISN'T reserved still hits the
        // unknown-command denial path — proves the swallow is scoped to the
        // registered token, not a blanket mute.
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = true;
        engine.FailureMessage = "denied";
        engine.RegisterIgnored("@poisoned");

        engine.DispatchForTests(Telepath("Buddy", "@blind"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Buddy {denied}\r", Encoding.Latin1.GetString(sent));
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

        engine.DispatchForTests(Telepath("Friend", "@health"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Friend", "@health"));   // muted
        engine.DispatchForTests(Gangpath("Friend", "@health"));   // passes
        engine.DispatchForTests(Local("Friend", "@health"));      // passes

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

        engine.DispatchForTests(Telepath("Buddy", "@party rest"));

        Assert.False(fired);
    }

    [Fact]
    public void WarnOnDenial_SendsFailureMessageOnUnknownCommand()
    {
        var (engine, _, _) = Setup();
        // FailureMessage is bare text — engine wraps in { } at send.
        engine.FailureMessage = "nope";

        engine.DispatchForTests(Telepath("Stranger", "@unknown"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Stranger {nope}\r", Encoding.Latin1.GetString(sent));
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

        engine.DispatchForTests(Telepath("Stranger", "@health"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Stranger {denied}\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void WarnOnDenial_SendsFailureMessageOnPartyWhitelistDenial()
    {
        var (engine, _, _) = Setup();
        engine.FailureMessage = "denied";
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => { });

        // Stranger isn't in the party → party-whitelist denial path.
        engine.DispatchForTests(Telepath("Stranger", "@party rest"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("/Stranger {denied}\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void WarnOnDenial_StaysSilentOnHardBlock()
    {
        // Unconditional hard-blocks (reroll, @party set suicide) must
        // never produce a reply — never advertise the block to a
        // malicious caller. Sender has every flag; engine
        // WarnOnDenial is on. The user-configured suicide lives
        // threshold is a SEPARATE path that DOES reply — see
        // SuicidePolicyBlock_TelepathsBackReason for that test.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => { });

        engine.DispatchForTests(Telepath("Trusted", "@do reroll"));

        Assert.Empty(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        Assert.False(handlerFired);   // gate stopped it
        byte[] sent = Assert.Single(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        Assert.False(handlerFired);
        byte[] sent = Assert.Single(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        Assert.True(handlerFired);
        Assert.Empty(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Trusted", "@suicide"));

        Assert.Empty(engine.LastSentForTests);
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

        engine.DispatchForTests(Telepath("Buddy", "@party set suicide hunter2"));

        Assert.False(fired);
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void WarnOnDenial_StaysSilentWhenChannelDisabled()
    {
        // The user explicitly muted the channel — don't tell every spammer
        // on that channel why they got ignored.
        var (engine, _, _) = Setup();
        engine.DisableTelepathChannel = true;

        engine.DispatchForTests(Telepath("Stranger", "@unknown"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void WarnOnDenialOff_NoFailureMessageSent()
    {
        var (engine, _, _) = Setup();
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("Stranger", "@unknown"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void EmptyFailureMessage_StaysSilentEvenWhenWarnOn()
    {
        // Empty message would otherwise serialise to "/Stranger \r" which
        // is just noise. Engine treats blank as opt-out.
        var (engine, _, _) = Setup();
        engine.FailureMessage = "   ";

        engine.DispatchForTests(Telepath("Stranger", "@unknown"));

        Assert.Empty(engine.LastSentForTests);
    }
}
