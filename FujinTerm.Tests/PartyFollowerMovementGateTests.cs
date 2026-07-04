using System.Linq;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyFollowerMovementGateTests
{
    private sealed class Harness
    {
        public readonly PartyState Party = new();
        public readonly MovementCoordinator Coordinator = new();
        public readonly PartyFollowerMovementGate Gate;

        public Harness()
        {
            Gate = new PartyFollowerMovementGate(Party, Coordinator);
        }

        public bool FollowerGateAsserted =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.FollowerGate);
    }

    [Fact]
    public void Solo_DoesNotAssert()
    {
        var h = new Harness();
        // IsInParty false by default — nothing to hold.
        Assert.False(h.Coordinator.IsPaused);
        Assert.False(h.FollowerGateAsserted);
    }

    [Fact]
    public void Leader_DoesNotAssert()
    {
        var h = new Harness();
        h.Party.SelfIsLeader = true;
        h.Party.IsInParty = true;

        Assert.False(h.FollowerGateAsserted);
    }

    [Fact]
    public void Follower_AssertsGate()
    {
        var h = new Harness();
        h.Party.SelfIsLeader = false;
        h.Party.IsInParty = true;

        Assert.True(h.FollowerGateAsserted);
        Assert.True(h.Coordinator.IsPaused);
    }

    [Fact]
    public void ConstructedWhileFollower_AssertsImmediately()
    {
        var party = new PartyState { IsInParty = true, SelfIsLeader = false };
        var coordinator = new MovementCoordinator();

        _ = new PartyFollowerMovementGate(party, coordinator);

        Assert.Contains(MovementCoordinator.FollowerGate, coordinator.AssertedGates);
    }

    [Fact]
    public void BecomingLeaderClearsGate()
    {
        var h = new Harness();
        h.Party.IsInParty = true;   // follower (SelfIsLeader defaults false)
        Assert.True(h.FollowerGateAsserted);

        h.Party.SelfIsLeader = true;   // promoted to leader

        Assert.False(h.FollowerGateAsserted);
        Assert.False(h.Coordinator.IsPaused);
    }

    [Fact]
    public void LeavingPartyClearsGate()
    {
        var h = new Harness();
        h.Party.IsInParty = true;   // follower
        Assert.True(h.FollowerGateAsserted);

        h.Party.IsInParty = false;   // party dissolved / left

        Assert.False(h.FollowerGateAsserted);
    }

    [Fact]
    public void RepeatedFollowerSignals_AssertOnce()
    {
        var h = new Harness();
        h.Party.IsInParty = true;   // one assert
        // A redundant role re-broadcast (still follower) must not re-flip.
        h.Party.LeaderName = "Bob";
        h.Party.SelfIsLeader = false;

        int asserts = h.Coordinator.History.Count(
            e => e.Gate == MovementCoordinator.FollowerGate && e.Asserted);
        Assert.Equal(1, asserts);
    }

    [Fact]
    public void FollowerGate_ComposesWithOtherGates()
    {
        var h = new Harness();
        h.Coordinator.AssertGate(MovementCoordinator.UserGate);
        h.Party.IsInParty = true;   // follower — second gate

        Assert.True(h.Coordinator.IsPaused);

        // Leaving the party clears only the Follower gate; User still holds.
        h.Party.IsInParty = false;
        Assert.False(h.FollowerGateAsserted);
        Assert.True(h.Coordinator.IsPaused);
    }

    [Fact]
    public void Dispose_StopsReacting()
    {
        var h = new Harness();
        h.Gate.Dispose();

        h.Party.IsInParty = true;   // would assert if still subscribed

        Assert.False(h.FollowerGateAsserted);
    }
}
