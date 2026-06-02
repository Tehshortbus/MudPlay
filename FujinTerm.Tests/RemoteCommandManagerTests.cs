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
    public void Reply_TelepathRoutesViaTelepathCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("HP 100/100"));

        engine.DispatchForTests(Telepath("Friend", "@health"));

        byte[] sent = Assert.Single(engine.LastSentForTests);
        string wire = Encoding.Latin1.GetString(sent);
        Assert.Equal("/Friend HP 100/100\r", wire);
    }

    [Fact]
    public void Reply_GossipRoutesViaGossipCommand()
    {
        var (engine, _, players) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus,
            ctx => ctx.Reply("hi"));

        engine.DispatchForTests(Gossip("Friend", "@health"));

        string wire = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        Assert.Equal("gos hi\r", wire);
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
}
