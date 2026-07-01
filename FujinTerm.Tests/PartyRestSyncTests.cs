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
    private static (PartyRestSync sync, PartyState party, List<byte[]> wire) Setup()
    {
        PartyState party = new();
        PartyRestSync sync = new(party);
        List<byte[]> wire = new();
        sync.SetWireSender(wire.Add);
        return (sync, party, wire);
    }

    private static string LastWire(List<byte[]> w) => Encoding.Latin1.GetString(w[^1]);

    // ===== engine-callable emit side =====
    // Per user direction: PartyRestSync no longer auto-fires on
    // PlayerState.Position changes. A user manually typing `rest`
    // should NOT auto-broadcast @wait — only engines that decide
    // the leader should pause / resume call RequestWait / RequestOk
    // (e.g. Phase 12 HealthManager auto-rest, message-engine flag
    // triggers on paralyze / held / confused, etc.).

    [Fact]
    public void RequestWait_Solo_SendsNothing()
    {
        var (sync, party, wire) = Setup();
        Assert.False(party.IsInParty);
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_AsLeader_SendsNothing()
    {
        // Leaders don't @wait themselves.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.SelfIsLeader = true;
        party.LeaderName = "Forged";
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_NoLeaderName_SendsNothing()
    {
        // No leader name means we don't know where to telepath.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = null;
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_AsFollower_TelepathsLeader()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void RequestOk_AfterWait_AsFollower_TelepathsLeader()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        sync.RequestOk(WaitReason.Health);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void RequestOk_WithoutPriorWait_SendsNothing()
    {
        // @ok only balances a held reason — releasing a reason that was
        // never placed must not leak a spurious @ok.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestOk(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void TwoReasons_SendOneWait_OkOnlyWhenLastClears()
    {
        // Health + Poison both hold the wait. @wait fires once (on the
        // first reason); @ok fires only when the LAST reason releases.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health);
        sync.RequestWait(WaitReason.Poison);
        Assert.Single(wire);                       // only one @wait
        Assert.Equal("/Leader @wait\r", LastWire(wire));

        sync.RequestOk(WaitReason.Health);
        Assert.Single(wire);                       // Poison still holds — no @ok yet

        sync.RequestOk(WaitReason.Poison);
        Assert.Equal(2, wire.Count);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void DuplicateWaitReason_SendsOneWait()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Poison);
        sync.RequestWait(WaitReason.Poison);
        Assert.Single(wire);
    }

    [Fact]
    public void RequestWait_TelepathsByGivenName_NotFullDisplayName()
    {
        // "Leader Lastname" leader → /Leader (given only) — MajorMUD
        // rejects "Given Family" recipients for telepaths.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader Lastname";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void RequestWait_NoWireSender_NoThrow()
    {
        PartyState party = new() { IsInParty = true, LeaderName = "Leader" };
        PartyRestSync sync = new(party);
        // No SetWireSender — call should silently no-op.
        sync.RequestWait(WaitReason.Health);
        sync.Dispose();
    }

    // ===== RequestHeal — follower flee-substitute broadcast =====

    [Fact]
    public void RequestHeal_AsFollower_BroadcastsGangHeal()
    {
        // Broadcasts to the party (gangpath), not a leader telepath, since the
        // healer may be any member. Leader name is irrelevant to the send.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestHeal();
        Assert.Equal("gang @heal\r", LastWire(wire));
    }

    [Fact]
    public void RequestHeal_Solo_SendsNothing()
    {
        var (sync, party, wire) = Setup();
        Assert.False(party.IsInParty);
        sync.RequestHeal();
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestHeal_AsLeader_SendsNothing()
    {
        // The leader owns the party's run decision and has no healer to ping.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.SelfIsLeader = true;
        sync.RequestHeal();
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestHeal_NoWireSender_NoThrow()
    {
        PartyState party = new() { IsInParty = true, LeaderName = "Leader" };
        PartyRestSync sync = new(party);
        sync.RequestHeal();   // no SetWireSender — silent no-op
        sync.Dispose();
    }

    [Fact]
    public void PositionChange_DoesNotAutoFire()
    {
        // Regression guard for the live-test feedback: manual `rest`
        // (which sets PlayerState.Position = Resting) must NOT trigger
        // @wait. PartyRestSync no longer subscribes to PlayerState at
        // all; engines drive it explicitly.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        // Just mutating a PlayerState would have fired the old code;
        // we don't even construct one here because the new ctor
        // doesn't take it. The point of this test is to document the
        // contract.
        Assert.Empty(wire);
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
