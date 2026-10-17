using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

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
    // (e.g. HealthManager auto-rest, message-engine flag
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

    // ===== The held-reason set =====
    // The one way this protocol fails is a reason that never clears: the set never
    // empties and every later @ok is suppressed. HeldReasons names what is still
    // holding (the bug report shows it).

    [Fact]
    public void Ok_GoesOutOnlyWhenTheLastReasonClears()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health);
        sync.RequestWait(WaitReason.Poison);

        sync.RequestOk(WaitReason.Health);                 // Poison still holds
        Assert.Single(wire);                               // nothing new on the wire
        sync.RequestOk(WaitReason.Poison);                 // last one out
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void HeldReasons_NamesWhatIsKeepingTheLeaderPaused()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        Assert.Empty(sync.HeldReasons);
        sync.RequestWait(WaitReason.Blindness);
        sync.RequestWait(WaitReason.Poison);
        // Enum order, not alphabetical: Poison is declared before Blindness.
        Assert.Equal(new[] { WaitReason.Poison, WaitReason.Blindness },
                     sync.HeldReasons.OrderBy(r => r).ToArray());

        sync.RequestOk(WaitReason.Blindness);
        Assert.Equal(new[] { WaitReason.Poison }, sync.HeldReasons.ToArray());
        Assert.True(sync.IsHoldingWait);

        sync.RequestOk(WaitReason.Poison);
        Assert.Empty(sync.HeldReasons);
        Assert.False(sync.IsHoldingWait);
    }

    // A reason that never clears means a later wait/release cycle sends nothing and
    // the leader stays paused. Nothing auto-releases it: that would send the leader
    // on while the follower still can't move.
    [Fact]
    public void AStuckReason_SuppressesEveryLaterOk()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Held);        // never released
        Assert.Single(wire);

        for (int i = 0; i < 3; i++)
        {
            sync.RequestWait(WaitReason.Health);
            sync.RequestOk(WaitReason.Health);
        }
        Assert.Single(wire);                       // not one @ok in three cycles
        Assert.Equal(new[] { WaitReason.Held }, sync.HeldReasons.ToArray());

        sync.RequestOk(WaitReason.Held);           // and it recovers at once
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    // ===== Wait reasons on the wire =====
    // The note after the token is MegaMUD's wording for the reason.

    [Theory]
    [InlineData(WaitReason.Poison,    "(waiting on message condition)")]
    [InlineData(WaitReason.Disease,   "(waiting on message condition)")]
    [InlineData(WaitReason.Blindness, "(blinded)")]
    [InlineData(WaitReason.Confusion, "(confused)")]
    [InlineData(WaitReason.Held,      "(can't move)")]
    [InlineData(WaitReason.TooHeavy,  "(too heavy to move)")]
    public void RequestWait_CarriesTheReason(WaitReason reason, string note)
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(reason);
        Assert.Equal($"/Leader @wait {note}\r", LastWire(wire));
    }

    // Health covers two pools, so its note comes from HealthManager, which knows
    // which one tripped.
    [Fact]
    public void RequestWait_Health_IsBareUnlessTheCallerNamesThePool()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Theory]
    [InlineData("(HP's too low)")]
    [InlineData("(mana's too low)")]
    public void RequestWait_Health_WithAPoolNote_SendsIt(string note)
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health, note: note);
        Assert.Equal($"/Leader @wait {note}\r", LastWire(wire));
    }

    [Fact]
    public void RequestWait_ExplicitNote_BeatsTheDefault()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Held, note: "(pinned down)");
        Assert.Equal("/Leader @wait (pinned down)\r", LastWire(wire));
    }

    // Every @wait names its reason, so a reason added without a note fails here.
    // Health is the exception at this level: its note comes from HealthManager.
    [Fact]
    public void EveryReasonExceptHealth_HasANote()
    {
        foreach (WaitReason r in Enum.GetValues<WaitReason>())
        {
            if (r == WaitReason.Health) continue;
            var (sync, party, wire) = Setup();
            party.IsInParty = true;
            party.LeaderName = "Leader";
            sync.RequestWait(r);
            Assert.True(LastWire(wire).Contains('('),
                $"{r} telepathed a bare @wait: give it a note in PartyRestSync.DefaultNote");
        }
    }

    // Only the first reason telepaths, so its note is the one the leader sees.
    [Fact]
    public void SecondReason_IsDedupedSoItsNoteNeverReachesTheWire()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Blindness);
        sync.RequestWait(WaitReason.Held);
        Assert.Single(wire);
        Assert.Equal($"/Leader @wait {PartyRestSync.BlindNote}\r", LastWire(wire));
    }

    [Fact]
    public void Resend_ReTelepathsAWaitAlreadyHeld_AndStillOnlyOneOk()
    {
        // The leader may have timed out the first wait; a resend re-asks. The reason
        // is still tracked once, so a single @ok balances it.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health, resend: true);
        sync.RequestWait(WaitReason.Health, resend: true);
        Assert.Equal(2, wire.Count);
        Assert.Equal("/Leader @wait\r", LastWire(wire));

        sync.RequestOk(WaitReason.Health);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void Resend_Solo_SendsNothing()
    {
        var (sync, _, wire) = Setup();
        sync.RequestWait(WaitReason.Health, resend: true);
        Assert.Empty(wire);
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
        Assert.Equal("bg @heal\r", LastWire(wire));
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

}

public sealed class PartyEssentialHandlersPauseGateTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, PartyEssentialHandlers handlers, PartyState party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        PartyEssentialHandlers handlers = new(engine, player, party);
        return (engine, handlers, party);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

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

        ChatLines.Hear(_routers[engine], Telepath("FollowerA", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("FollowerB", "@wait"));

        Assert.True(handlers.IsPaused);
        Assert.Single(events); // edge-only — only one transition fired
    }

    [Fact]
    public void IsPaused_GoesFalseOnlyWhenLastOkArrives()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        ChatLines.Hear(_routers[engine], Telepath("FollowerA", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("FollowerB", "@wait"));
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        ChatLines.Hear(_routers[engine], Telepath("FollowerA", "@ok"));
        Assert.True(handlers.IsPaused);   // FollowerB still waiting
        Assert.Empty(events);

        ChatLines.Hear(_routers[engine], Telepath("FollowerB", "@ok"));
        Assert.False(handlers.IsPaused);
        Assert.Equal(new[] { false }, events);
    }
}
