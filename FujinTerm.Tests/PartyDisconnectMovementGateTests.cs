using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

// Leader-side hold that stops us sprinting off when a party follower drops
// connection. The gate rides PartyManager.MemberDisconnected (asserts) and
// MemberFollowConfirmed (clears when the returning member re-parties); the
// reconnect grace window is the second release path. Drives membership through
// the real router lines so the whole PartyManager → gate wiring is exercised.
public sealed class PartyDisconnectMovementGateTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (MessageRouter router, PartyManager mgr,
                    MovementCoordinator coord, PartyDisconnectMovementGate gate) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);
        MovementCoordinator coord = new();
        PartyDisconnectMovementGate gate = new(mgr, coord);
        return (router, mgr, coord, gate);
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    [Fact]
    public void FreshGate_NotAsserted()
    {
        var (_, _, coord, _) = Setup();
        Assert.False(coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }

    [Fact]
    public void FollowerDrop_WhenWeLead_AssertsGate()
    {
        var (router, mgr, coord, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        Assert.True(mgr.State.SelfIsLeader);

        router.Dispatch(Line("Helper just disconnected!!!."));

        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }

    [Fact]
    public void Reconnect_AndRefollow_ClearsGate()
    {
        var (router, mgr, coord, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);

        // Member reconnected and re-partied — MemberFollowConfirmed fires.
        router.Dispatch(Line("Helper started to follow you."));

        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void UnrelatedRefollow_DoesNotClearGate()
    {
        // A join from someone who wasn't one of our pending drops must not
        // release the hold for the member we're still waiting on.
        var (router, mgr, coord, _) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));

        router.Dispatch(Line("Stranger started to follow you."));

        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }

    [Fact]
    public void MultipleDrops_HoldUntilAllReturn()
    {
        var (router, mgr, coord, _) = Setup();
        router.Dispatch(Line("Alpha started to follow you."));
        router.Dispatch(Line("Beta started to follow you."));

        router.Dispatch(Line("Alpha just disconnected!!!."));
        router.Dispatch(Line("Beta just disconnected!!!."));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);

        router.Dispatch(Line("Alpha started to follow you."));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates); // Beta still out

        router.Dispatch(Line("Beta started to follow you."));
        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }

    [Fact]
    public void GraceWindow_Expiry_ClearsGate()
    {
        var (router, mgr, coord, gate) = Setup();
        DateTime t = Now;
        gate.NowProvider = () => t;
        gate.GraceWindow = TimeSpan.FromSeconds(90);
        router.Dispatch(Line("Helper started to follow you."));

        router.Dispatch(Line("Helper just disconnected!!!."));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);

        // 89s — still holding.
        t = Now.AddSeconds(89);
        gate.TickForTests();
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);

        // 91s — member never came back, give up and resume.
        t = Now.AddSeconds(91);
        gate.TickForTests();
        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void ZeroGraceWindow_NeverExpires()
    {
        // Timeout disabled — the hold persists until the member re-follows.
        var (router, mgr, coord, gate) = Setup();
        DateTime t = Now;
        gate.NowProvider = () => t;
        gate.GraceWindow = TimeSpan.Zero;
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));

        t = Now.AddHours(1);
        gate.TickForTests();

        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }

    [Fact]
    public void FollowerDrop_WhenWeFollow_DoesNotAssertGate()
    {
        // We follow Fujin (leader). Fujin dropping dissolves the party — no
        // MemberDisconnected fires, so the gate never engages. (A follower's own
        // movement is held by the FollowerGate instead.)
        var (router, mgr, coord, _) = Setup();
        router.Dispatch(Line("You are now following Fujin."));
        Assert.False(mgr.State.SelfIsLeader);

        router.Dispatch(Line("Fujin just disconnected!!!."));

        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void Gate_ComposesWithOtherGates()
    {
        // Clearing our hold on reconnect must not resume movement while an
        // unrelated gate (a manual user pause) is still asserting.
        var (router, mgr, coord, _) = Setup();
        coord.AssertGate(MovementCoordinator.UserGate);
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));

        router.Dispatch(Line("Helper started to follow you."));

        Assert.True(coord.IsPaused); // user pause survives
        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
        Assert.Contains(MovementCoordinator.UserGate, coord.AssertedGates);
    }

    [Fact]
    public void Dispose_ClearsAssertedGate()
    {
        var (router, mgr, coord, gate) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        router.Dispatch(Line("Helper just disconnected!!!."));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);

        gate.Dispose();

        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void Dispose_StopsTrackingFurtherDrops()
    {
        var (router, mgr, coord, gate) = Setup();
        router.Dispatch(Line("Helper started to follow you."));
        gate.Dispose();

        router.Dispatch(Line("Helper just disconnected!!!."));

        Assert.DoesNotContain(MovementCoordinator.MemberDisconnectGate, coord.AssertedGates);
    }
}
