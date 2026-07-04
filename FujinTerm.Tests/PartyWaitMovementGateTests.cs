using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

// The bridge that finally makes an inbound @wait actually hold our own movement:
// PartyEssentialHandlers only recorded the WaitingMembers set (for the PartyWindow
// chip); this asserts MovementCoordinator.PartyWaitGate so the loop / walk / lair
// engine holds until every waiting member sends @ok.
public sealed class PartyWaitMovementGateTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, PartyEssentialHandlers handlers,
                    PartyState party, MovementCoordinator coord, PartyWaitMovementGate gate) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartyEssentialHandlers handlers = new(engine, player, party);
        MovementCoordinator coord = new();
        PartyWaitMovementGate gate = new(handlers, coord);
        return (engine, handlers, party, coord, gate);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPartyMember(PartyState p, string name)
    {
        p.Members.Add(new PartyMember { Name = name });
        p.IsInParty = true;
    }

    [Fact]
    public void FreshGate_NotAsserted()
    {
        var (_, _, _, coord, _) = Setup();
        Assert.False(coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }

    [Fact]
    public void InboundWait_AssertsPartyWaitGate()
    {
        var (engine, _, party, coord, _) = Setup();
        SeedPartyMember(party, "Follower");

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }

    [Fact]
    public void LastOk_ClearsPartyWaitGate()
    {
        var (engine, _, party, coord, _) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        engine.DispatchForTests(Telepath("FollowerA", "@wait"));
        engine.DispatchForTests(Telepath("FollowerB", "@wait"));
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);

        engine.DispatchForTests(Telepath("FollowerA", "@ok"));
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates); // B still waits

        engine.DispatchForTests(Telepath("FollowerB", "@ok"));
        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void PartyWaitGate_ComposesWithOtherGates()
    {
        // Clearing the @wait must not resume movement while an unrelated gate
        // (a manual user pause here) is still asserting.
        var (engine, _, party, coord, _) = Setup();
        SeedPartyMember(party, "Follower");
        coord.AssertGate(MovementCoordinator.UserGate);
        engine.DispatchForTests(Telepath("Follower", "@wait"));

        engine.DispatchForTests(Telepath("Follower", "@ok"));

        Assert.True(coord.IsPaused); // user pause survives
        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
        Assert.Contains(MovementCoordinator.UserGate, coord.AssertedGates);
    }

    [Fact]
    public void Dispose_StopsTrackingFurtherWaits()
    {
        var (engine, _, party, coord, gate) = Setup();
        SeedPartyMember(party, "Follower");
        gate.Dispose();

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }
}
