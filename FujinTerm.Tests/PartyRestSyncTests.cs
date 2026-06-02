using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyRestSyncTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (PartyRestSync sync, PlayerState player, PartyState party, List<byte[]> wire) Setup()
    {
        PlayerState player = new();
        PartyState party = new();
        PartyRestSync sync = new(player, party);
        List<byte[]> wire = new();
        sync.SetWireSender(wire.Add);
        return (sync, player, party, wire);
    }

    private static string LastWire(List<byte[]> w) => Encoding.Latin1.GetString(w[^1]);

    // ===== emit side: Standing → Resting / Meditating =====

    [Fact]
    public void Solo_PositionChange_SendsNothing()
    {
        var (_, player, party, wire) = Setup();
        Assert.False(party.IsInParty);

        player.Position = PlayerPosition.Resting;

        Assert.Empty(wire);
    }

    [Fact]
    public void Leader_PositionChange_SendsNothing()
    {
        // Leaders don't @wait themselves.
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.SelfIsLeader = true;
        party.LeaderName = "Forged";

        player.Position = PlayerPosition.Resting;

        Assert.Empty(wire);
    }

    [Fact]
    public void NoLeaderName_PositionChange_SendsNothing()
    {
        // No leader name means we don't know where to telepath.
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = null;

        player.Position = PlayerPosition.Resting;

        Assert.Empty(wire);
    }

    [Fact]
    public void Follower_StandingToResting_EmitsAtWait()
    {
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        // Default SelfIsLeader = false

        player.Position = PlayerPosition.Resting;

        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void Follower_StandingToMeditating_EmitsAtWait()
    {
        // Meditating counts as a rest state — same as Resting for the
        // leader's pause-gate purposes.
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        player.Position = PlayerPosition.Meditating;

        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void Follower_RestingToStanding_EmitsAtOk()
    {
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        // Drive both transitions through the real setter so PropertyChanged
        // actually fires for both — going from default Standing to Resting
        // first establishes the "we're resting" baseline.
        player.Position = PlayerPosition.Resting;
        wire.Clear();

        player.Position = PlayerPosition.Standing;

        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void Follower_RestingToMeditating_EmitsNothing()
    {
        // Both states are "resting" from the protocol's perspective —
        // no transition, no emission.
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        player.Position = PlayerPosition.Resting;
        wire.Clear();

        player.Position = PlayerPosition.Meditating;

        Assert.Empty(wire);
    }

    [Fact]
    public void Follower_SamePositionTwice_EmitsOnce()
    {
        var (_, player, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        player.Position = PlayerPosition.Resting;
        player.Position = PlayerPosition.Resting;

        Assert.Single(wire);
    }

    [Fact]
    public void NoWireSender_NoThrow()
    {
        PlayerState player = new();
        PartyState party = new() { IsInParty = true, LeaderName = "Leader" };
        PartyRestSync sync = new(player, party);
        // No SetWireSender — should still observe transitions without throwing.
        player.Position = PlayerPosition.Resting;
        sync.Dispose();
    }
}

public sealed class PartyEssentialHandlersPauseGateTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, PartyEssentialHandlers handlers, PartyState party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartyEssentialHandlers handlers = new(engine, player, party);
        return (engine, handlers, party);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPartyMember(PartyState p, string name)
    {
        p.Members.Add(new PartyMember { Name = name });
        p.IsInParty = true;
    }

    [Fact]
    public void IsPaused_IsFalseByDefault()
    {
        var (_, handlers, _) = Setup();
        Assert.False(handlers.IsPaused);
    }

    [Fact]
    public void IsPaused_GoesTrueOnWait_FiresChangedEvent()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "Follower");
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.True(handlers.IsPaused);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void IsPaused_StaysTrueOnSecondWait_NoExtraEvent()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("FollowerA", "@wait"));
        engine.DispatchForTests(Telepath("FollowerB", "@wait"));

        Assert.True(handlers.IsPaused);
        Assert.Single(events); // edge-only — only one transition fired
    }

    [Fact]
    public void IsPaused_GoesFalseOnlyWhenLastOkArrives()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        engine.DispatchForTests(Telepath("FollowerA", "@wait"));
        engine.DispatchForTests(Telepath("FollowerB", "@wait"));
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("FollowerA", "@ok"));
        Assert.True(handlers.IsPaused);   // FollowerB still waiting
        Assert.Empty(events);

        engine.DispatchForTests(Telepath("FollowerB", "@ok"));
        Assert.False(handlers.IsPaused);
        Assert.Equal(new[] { false }, events);
    }
}
