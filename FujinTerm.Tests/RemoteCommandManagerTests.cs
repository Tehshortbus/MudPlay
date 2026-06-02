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

    [Fact]
    public void HardBlock_PartySuicideAlwaysDenied()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        // Even with high lives + permissive threshold, @party suicide is always blocked.
        engine.LivesProvider = () => 99;
        engine.MaxSuicideLivesThreshold = 0;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => fired = true);

        engine.DispatchForTests(Telepath("Buddy", "@party suicide"));

        Assert.False(fired);
    }

    [Fact]
    public void HardBlock_DoSuicide_BlockedWhenLivesUnknown()
    {
        // LivesProvider null = unknown = blocked (conservative default).
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
    }

    [Fact]
    public void HardBlock_DoSuicide_AllowedWhenLivesAboveThreshold()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 5;
        engine.MaxSuicideLivesThreshold = 3;

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.True(fired);
    }

    [Fact]
    public void HardBlock_DoSuicide_BlockedAtThreshold()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 3;       // exactly at threshold
        engine.MaxSuicideLivesThreshold = 3;

        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do suicide"));

        Assert.False(fired);
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
        Assert.Equal("say {plain}\r",     Encoding.Latin1.GetString(engine.LastSentForTests[2]));
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
    public void Reply_LocalRoutesViaSayCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("hi"));

        engine.DispatchForTests(Local("Friend", "@health"));

        string wire = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        Assert.Equal("say {hi}\r", wire);
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
    public void DisablePartyWhitelist_DeniesPartyHandlerEvenForActiveMember()
    {
        var (engine, party, _) = Setup();
        SeedPartyMember(party, "Buddy");
        engine.DisablePartyWhitelist = true;

        bool fired = false;
        engine.RegisterHandler("@party", PlayerRemoteControls.None,
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
        // Unconditional hard-blocks (reroll, @party suicide) must
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
    public void PartySuicide_StaysSilentEvenAboveThreshold()
    {
        // @party suicide is the unconditional hard-block path; should
        // never reach the policy-block reply even when lives are
        // plenty.
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Trusted", PlayerRemoteControls.All);
        engine.MaxSuicideLivesThreshold = 0;
        engine.LivesProvider = () => 99;
        engine.RegisterHandler("@party", PlayerRemoteControls.None, _ => { });

        engine.DispatchForTests(Telepath("Trusted", "@party suicide"));

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
