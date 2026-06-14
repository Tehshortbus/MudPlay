using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the @divert handler's behaviour: an authorised sender (with the
/// DivertConversations permission) telepathing <c>@divert &lt;player&gt;</c>
/// arms forwarding; every subsequent incoming telepath is repeated to that
/// target as <c>&lt;sender&gt; telepathed: &lt;message&gt;</c>. Bare
/// <c>@divert</c> stops. The divert target's own telepaths are skipped (no
/// echo); the <c>@divert</c> control verb is never forwarded; all other
/// @-command telepaths ARE forwarded.
/// </summary>
public sealed class DivertHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, DivertHandler handler, PlayerDatabase players, List<byte[]> forwardWire, RemoteCommandManager engine) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        DivertHandler handler = new(engine, chat);
        List<byte[]> forwardWire = new();
        handler.SetWireSender(forwardWire.Add);
        return (router, handler, players, forwardWire, engine);
    }

    /// <summary>Replay an incoming telepath line so ChatRouter classifies it.</summary>
    private static void Telepath(MessageRouter router, string sender, string message) =>
        router.Dispatch(Line($"{sender} telepaths: {message}"));

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    [Fact]
    public void Divert_FromAuthorisedSender_StartsAndReplies()
    {
        var (router, handler, players, _, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");

        Assert.Equal("Raijin", handler.TargetForTests);
        // Engine reply routes back to the sender on the telepath channel.
        Assert.Equal("/Bob {Now diverting telepaths to: Raijin}\r",
            Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests)));
    }

    [Fact]
    public void Divert_NoArg_StopsAndReplies()
    {
        var (router, handler, players, _, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        engine.LastSentForTests.Clear();
        Telepath(router, "Bob", "@divert");

        Assert.Null(handler.TargetForTests);
        Assert.Equal("/Bob {No longer diverting telepaths}\r",
            Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests)));
    }

    [Fact]
    public void Divert_ForwardsIncomingTelepath()
    {
        var (router, _, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        Telepath(router, "Carl", "hello there");

        Assert.Equal("/Raijin Carl telepathed: hello there\r",
            Encoding.Latin1.GetString(Assert.Single(forwardWire)));
    }

    [Fact]
    public void Divert_SkipsTargetsOwnTelepath()
    {
        // Diverting to Raijin; Raijin's own telepath isn't echoed back.
        var (router, _, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        Telepath(router, "Raijin", "anyone home?");

        Assert.Empty(forwardWire);
    }

    [Fact]
    public void Divert_ForwardsAtCommandTelepath()
    {
        // Per user direction, @-command telepaths (other than @divert) are
        // forwarded verbatim — the engine still processes them locally.
        var (router, _, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        Telepath(router, "Carl", "@where");

        Assert.Equal("/Raijin Carl telepathed: @where\r",
            Encoding.Latin1.GetString(Assert.Single(forwardWire)));
    }

    [Fact]
    public void Divert_DoesNotForwardDivertCommandItself()
    {
        // The engine subscribes to EntryClassified before the handler, so by
        // the time the handler sees the starting "@divert Raijin" line the
        // target is already set — the @divert guard must keep it unforwarded.
        var (router, _, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");

        Assert.Empty(forwardWire);
    }

    [Fact]
    public void Divert_NotActive_DoesNotForward()
    {
        var (router, _, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Carl", "hello there");

        Assert.Empty(forwardWire);
    }

    [Fact]
    public void Divert_FromUnauthorisedSender_DoesNotArm()
    {
        // Stranger has every permission EXCEPT DivertConversations.
        var (router, handler, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.DivertConversations);

        Telepath(router, "Stranger", "@divert Raijin");
        Telepath(router, "Carl", "hello there");

        Assert.Null(handler.TargetForTests);
        Assert.Empty(forwardWire);
    }

    [Fact]
    public void Divert_RetargetsToNewPlayer()
    {
        var (router, handler, players, forwardWire, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        Telepath(router, "Bob", "@divert Susanoo");
        Assert.Equal("Susanoo", handler.TargetForTests);

        Telepath(router, "Carl", "hi");
        Assert.Equal("/Susanoo Carl telepathed: hi\r",
            Encoding.Latin1.GetString(Assert.Single(forwardWire)));
    }

    [Fact]
    public void Dispose_StopsForwardingAndUnregisters()
    {
        var (router, handler, players, forwardWire, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        handler.Dispose();
        Telepath(router, "Carl", "hello there");

        Assert.Empty(forwardWire);
        Assert.False(engine.UnregisterHandler("@divert")); // already gone
    }

    [Fact]
    public void Divert_WithoutWireSender_ForwardIsNoop()
    {
        // Pre-binding (or tests that intentionally don't bind), forwarding
        // is silently skipped — no exception, no wire output.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        using DivertHandler handler = new(engine, chat);   // no SetWireSender
        SeedPlayer(players, "Bob", PlayerRemoteControls.DivertConversations);

        Telepath(router, "Bob", "@divert Raijin");
        Telepath(router, "Carl", "hello there");

        Assert.Equal("Raijin", handler.TargetForTests);   // armed, just can't forward
    }
}
