using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The bridge that finally makes an inbound @wait actually hold our own movement:
// PartyEssentialHandlers only recorded the WaitingMembers set (for the PartyWindow
// chip); this asserts MovementCoordinator.PartyWaitGate so the loop / walk / lair
// engine holds until every waiting member sends @ok.
public sealed class PartyWaitMovementGateTests
{
    // The clock the PartyWaitMovementGate reads: the real one unless a test sets its own.
    private Func<DateTime> _gateNow = () => DateTime.UtcNow;

    // The gate's 1 s expiry tick.
    private readonly ManualTimers _gateTimers = new();

    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, PartyEssentialHandlers handlers,
                    PartyState party, MovementCoordinator coord, PartyWaitMovementGate gate) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        PartyEssentialHandlers handlers = new(engine, player, party);
        MovementCoordinator coord = new();
        PartyWaitMovementGate gate = new(handlers, coord, now: () => _gateNow(), schedule: _gateTimers.Arm);
        return (engine, handlers, party, coord, gate);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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

    // ----- the master switch: recorded while off, never asserted, never answered -----

    private static void SwitchOff(RemoteCommandManager engine, MovementCoordinator coord, PartyWaitMovementGate gate)
    {
        engine.BlockedByMasterSwitch = _ => true;
        gate.MasterSwitchOff = () => true;
        coord.AssertGate(MovementCoordinator.AutoAllGate);
        coord.ParkHoldsForMasterSwitch();
    }

    private static void SwitchOn(RemoteCommandManager engine, MovementCoordinator coord, PartyWaitMovementGate gate)
    {
        engine.BlockedByMasterSwitch = _ => false;
        gate.MasterSwitchOff = () => false;
        coord.RestoreHoldsAfterMasterSwitch();
        coord.ClearGate(MovementCoordinator.AutoAllGate);
    }

    // A @wait dropped outright would have the leader walk off from a held
    // follower the moment the switch came back on.
    [Fact]
    public void WaitWhileSwitchOff_IsRecordedNotHeld_AndHoldsOnceItIsBackOn()
    {
        var (engine, handlers, party, coord, gate) = Setup();
        SeedPartyMember(party, "Follower");
        SwitchOff(engine, coord, gate);

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        Assert.Contains("Follower", handlers.WaitingMembers);
        Assert.False(coord.IsGateAsserted(MovementCoordinator.PartyWaitGate));
        Assert.Empty(_replies);

        SwitchOn(engine, coord, gate);
        Assert.True(coord.IsGateAsserted(MovementCoordinator.PartyWaitGate));
    }

    // And a dropped @ok would have the leader stand out the whole wait limit for
    // a follower who had already said it could go.
    [Fact]
    public void OkWhileSwitchOff_IsRecorded_SoNothingHoldsOnceItIsBackOn()
    {
        var (engine, handlers, party, coord, gate) = Setup();
        SeedPartyMember(party, "Follower");
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        Assert.True(coord.IsGateAsserted(MovementCoordinator.PartyWaitGate));
        SwitchOff(engine, coord, gate);

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));

        Assert.Empty(handlers.WaitingMembers);
        SwitchOn(engine, coord, gate);
        Assert.False(coord.IsPaused);
    }

    [Fact]
    public void WaitFromOutsideTheParty_WhileSwitchOff_IsNeitherRecordedNorAnswered()
    {
        var (engine, handlers, _, coord, gate) = Setup();
        SwitchOff(engine, coord, gate);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@wait"));

        Assert.Empty(handlers.WaitingMembers);
        Assert.Empty(_replies);
    }

    // The leader's wait limit counts from the switch coming back on, so a
    // follower still held then gets the whole of it.
    [Fact]
    public void WaitLimit_DoesNotRunWhileTheSwitchIsOff()
    {
        var (engine, handlers, party, coord, gate) = Setup();
        DateTime t = Now;
        _gateNow = () => t;
        gate.WaitWindow = TimeSpan.FromSeconds(30);
        SeedPartyMember(party, "Follower");
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        SwitchOff(engine, coord, gate);

        t = t.AddSeconds(120);
        _gateTimers.RunAll();
        Assert.Contains("Follower", handlers.WaitingMembers);

        SwitchOn(engine, coord, gate);
        t = t.AddSeconds(29);
        _gateTimers.RunAll();
        Assert.True(coord.IsGateAsserted(MovementCoordinator.PartyWaitGate));
        t = t.AddSeconds(2);
        _gateTimers.RunAll();
        Assert.False(coord.IsGateAsserted(MovementCoordinator.PartyWaitGate));
    }

    [Fact]
    public void InboundWait_AssertsPartyWaitGate()
    {
        var (engine, _, party, coord, _) = Setup();
        SeedPartyMember(party, "Follower");

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        Assert.True(coord.IsPaused);
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }

    [Fact]
    public void LastOk_ClearsPartyWaitGate()
    {
        var (engine, _, party, coord, _) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        ChatLines.Hear(_routers[engine], Telepath("FollowerA", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("FollowerB", "@wait"));
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);

        ChatLines.Hear(_routers[engine], Telepath("FollowerA", "@ok"));
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates); // B still waits

        ChatLines.Hear(_routers[engine], Telepath("FollowerB", "@ok"));
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
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));

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

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }

    // ----- Wait-timer expiry (second release path beside @ok) -----

    [Fact]
    public void WaitTimer_ExpiresAfterWindow_ForceReleasesWaitAndGate()
    {
        var (engine, handlers, party, coord, gate) = Setup();
        DateTime t = Now;
        _gateNow = () => t;
        gate.WaitWindow = TimeSpan.FromSeconds(30);
        SeedPartyMember(party, "Follower");
        PartyMember row = party.Members[0];

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
        Assert.True(row.IsWaiting);

        // 29s — not yet expired.
        t = Now.AddSeconds(29);
        _gateTimers.RunAll();
        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);

        // 31s — the leader gives up: gate, wait set, and roster chip all clear.
        t = Now.AddSeconds(31);
        _gateTimers.RunAll();
        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
        Assert.False(coord.IsPaused);
        Assert.Empty(handlers.WaitingMembers);
        Assert.False(row.IsWaiting);
    }

    [Fact]
    public void WaitTimer_ZeroWindow_NeverExpires()
    {
        var (engine, _, party, coord, gate) = Setup();
        DateTime t = Now;
        _gateNow = () => t;
        gate.WaitWindow = TimeSpan.Zero;   // timeout disabled — only @ok releases
        SeedPartyMember(party, "Follower");

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        t = Now.AddHours(1);
        _gateTimers.RunAll();

        Assert.Contains(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }

    [Fact]
    public void OkBeforeTimer_ReleasesWithoutWaitingForExpiry()
    {
        var (engine, _, party, coord, gate) = Setup();
        DateTime t = Now;
        _gateNow = () => t;
        gate.WaitWindow = TimeSpan.FromSeconds(30);
        SeedPartyMember(party, "Follower");

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));   // clears well before the window

        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);

        // A later tick past the old deadline must not resurrect or misfire.
        t = Now.AddSeconds(60);
        _gateTimers.RunAll();
        Assert.DoesNotContain(MovementCoordinator.PartyWaitGate, coord.AssertedGates);
    }
}
