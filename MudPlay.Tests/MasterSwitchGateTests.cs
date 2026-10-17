using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The master switch at the entry points of systems that have no toggle of their
// own: with it off each does nothing, with it on each behaves as before. The
// remote-command, trigger, event, hang-up and PvP gates are pinned beside their
// own tests; this file covers the holds and the small senders.
public sealed class MasterSwitchGateTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);

    // ----- Movement holds --------------------------------------------------

    [Fact]
    public void Holds_SwitchGoesOff_AreReleased_AndComeBackWhenItIsOn()
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.HeldGate);
        c.AssertGate(MovementCoordinator.PartyWaitGate);
        c.AssertGate(MovementCoordinator.UserGate);
        c.AssertGate(MovementCoordinator.AutoAllGate);

        IReadOnlyList<string> released = c.ParkHoldsForMasterSwitch();

        Assert.Equal(new[] { MovementCoordinator.HeldGate, MovementCoordinator.PartyWaitGate }.OrderBy(g => g),
            released.OrderBy(g => g));
        Assert.False(c.IsGateAsserted(MovementCoordinator.HeldGate));
        Assert.False(c.IsGateAsserted(MovementCoordinator.PartyWaitGate));
        // The user's pause and the switch's own freeze are not automatic holds.
        Assert.True(c.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.True(c.IsGateAsserted(MovementCoordinator.AutoAllGate));

        c.RestoreHoldsAfterMasterSwitch();

        Assert.True(c.IsGateAsserted(MovementCoordinator.HeldGate));
        Assert.True(c.IsGateAsserted(MovementCoordinator.PartyWaitGate));
        Assert.Empty(c.ParkedGates);
    }

    [Theory]
    [InlineData(MovementCoordinator.HeldGate)]
    [InlineData(MovementCoordinator.ConfusionGate)]
    [InlineData(MovementCoordinator.FearGate)]
    [InlineData(MovementCoordinator.PartyVitalsGate)]
    [InlineData(MovementCoordinator.PartyWaitGate)]
    [InlineData(MovementCoordinator.TooHeavyGate)]
    [InlineData(MovementCoordinator.CombatGate)]
    [InlineData(MovementCoordinator.HealthRecoveryGate)]
    public void Hold_AssertedWhileOff_IsNotAsserted_ButIsOwedWhenTheSwitchComesOn(string gate)
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.AutoAllGate);
        c.ParkHoldsForMasterSwitch();

        c.AssertGate(gate, "owner", "came due while off");

        Assert.False(c.IsGateAsserted(gate));
        Assert.Contains(gate, c.ParkedGates);

        // Put back before the freeze lifts, so nothing moves ahead of it.
        List<bool> pauseEvents = new();
        c.PauseStateChanged += pauseEvents.Add;
        c.RestoreHoldsAfterMasterSwitch();
        c.ClearGate(MovementCoordinator.AutoAllGate);

        Assert.True(c.IsGateAsserted(gate));
        Assert.True(c.IsPaused);
        Assert.Empty(pauseEvents);
    }

    [Fact]
    public void Hold_ClearedWhileOff_IsNotOwedAfterwards()
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.HeldGate);
        c.AssertGate(MovementCoordinator.AutoAllGate);
        c.ParkHoldsForMasterSwitch();

        c.ClearGate(MovementCoordinator.HeldGate);
        c.RestoreHoldsAfterMasterSwitch();
        c.ClearGate(MovementCoordinator.AutoAllGate);

        Assert.False(c.IsGateAsserted(MovementCoordinator.HeldGate));
        Assert.False(c.IsPaused);
    }

    [Fact]
    public void SwitchOn_HoldsAssertAsBefore()
    {
        MovementCoordinator c = new();

        c.AssertGate(MovementCoordinator.HeldGate);

        Assert.True(c.IsGateAsserted(MovementCoordinator.HeldGate));
        Assert.Empty(c.ParkedGates);
    }

    [Fact]
    public void UserPause_WhileOff_StillHolds()
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.AutoAllGate);
        c.ParkHoldsForMasterSwitch();

        c.AssertGate(MovementCoordinator.UserGate);

        Assert.True(c.IsGateAsserted(MovementCoordinator.UserGate));
    }

    // ----- @wait / @ok -----------------------------------------------------

    private static (PartyRestSync Sync, List<string> Wire, Action<bool> SetOff) FollowerRestSync()
    {
        PartyState party = new() { IsInParty = true, SelfIsLeader = false, LeaderName = "Tank" };
        PartyRestSync sync = new(party);
        List<string> wire = new();
        sync.SetWireSender(bytes => wire.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        bool off = false;
        sync.MasterSwitchOff = () => off;
        return (sync, wire, value => off = value);
    }

    [Fact]
    public void PartyWait_SwitchOff_SendsNothing_AndSettlesWhenItIsBackOn()
    {
        var (sync, wire, setOff) = FollowerRestSync();
        setOff(true);

        sync.RequestWait(WaitReason.Held);
        sync.RequestHeal();
        Assert.Empty(wire);

        setOff(false);
        sync.ResyncAfterMasterSwitch();
        Assert.Equal($"/Tank @wait {PartyRestSync.HeldNote}", Assert.Single(wire));
    }

    // A wait already out is released as the switch goes off (a release is not a
    // hold), so the leader isn't left standing; after that nothing is sent.
    [Fact]
    public void PartyWait_OutWhenTheSwitchGoesOff_IsReleased_ThenQuiet()
    {
        var (sync, wire, setOff) = FollowerRestSync();
        sync.RequestWait(WaitReason.Held);

        setOff(true);
        sync.ReleaseForMasterSwitch();
        Assert.Equal(new[] { $"/Tank @wait {PartyRestSync.HeldNote}", "/Tank @ok" }, wire);

        // Cleared while off: nothing more to say, then or afterwards.
        sync.RequestOk(WaitReason.Held);
        setOff(false);
        sync.ResyncAfterMasterSwitch();
        Assert.Equal(2, wire.Count);
    }

    [Fact]
    public void PartyWait_StillHeldWhenTheSwitchComesOn_IsAskedOnce_WithItsReason()
    {
        var (sync, wire, setOff) = FollowerRestSync();
        sync.RequestWait(WaitReason.Blindness);
        setOff(true);
        sync.ReleaseForMasterSwitch();

        setOff(false);
        sync.ResyncAfterMasterSwitch();
        sync.ResyncAfterMasterSwitch();

        Assert.Equal(new[]
        {
            $"/Tank @wait {PartyRestSync.BlindNote}", "/Tank @ok", $"/Tank @wait {PartyRestSync.BlindNote}",
        }, wire);
    }

    // The health engine re-asks for itself as it is re-run, with the pool's note;
    // the resync that follows must not add a second, bare @wait.
    [Fact]
    public void PartyWait_HealthReasksOnSwitchOn_NoSecondBareWait()
    {
        var (sync, wire, setOff) = FollowerRestSync();
        sync.RequestWait(WaitReason.Health, resend: true, note: PartyRestSync.ManaNote);
        setOff(true);
        sync.ReleaseForMasterSwitch();
        wire.Clear();

        setOff(false);
        sync.RequestWait(WaitReason.Health, resend: true, note: PartyRestSync.ManaNote);
        sync.ResyncAfterMasterSwitch();

        Assert.Equal($"/Tank @wait {PartyRestSync.ManaNote}", Assert.Single(wire));
    }

    // An @ok owed to one leader is not owed to the next.
    [Fact]
    public void PartyWait_LeaderChanges_NothingIsReleasedToTheNewOne()
    {
        PartyState party = new() { IsInParty = true, SelfIsLeader = false, LeaderName = "Tank" };
        PartyRestSync sync = new(party);
        List<string> wire = new();
        sync.SetWireSender(bytes => wire.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        sync.RequestWait(WaitReason.Held);

        party.LeaderName = "Cleric";
        sync.ReleaseForMasterSwitch();

        Assert.Equal($"/Tank @wait {PartyRestSync.HeldNote}", Assert.Single(wire));
    }

    [Fact]
    public void PartyWait_SwitchOn_SendsAsBefore()
    {
        var (sync, wire, _) = FollowerRestSync();

        sync.RequestWait(WaitReason.Held);
        sync.RequestOk(WaitReason.Held);

        Assert.Equal(new[] { $"/Tank @wait {PartyRestSync.HeldNote}", "/Tank @ok" }, wire);
    }

    // ----- Small senders ---------------------------------------------------

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void PostDeathInventoryRead_FollowsTheSwitch_AndStaysOwed(bool off, int expected)
    {
        int asked = 0;
        bool switchOff = off;
        PostDeathInventoryRefresh refresh = new(() => { }, () => asked++) { MasterSwitchOff = () => switchOff };
        refresh.OnDeath();

        refresh.OnRoomKnown();
        Assert.Equal(expected, asked);

        // Held, the read is still owed: it goes out once the switch is back on.
        switchOff = false;
        refresh.OnRoomKnown();
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Greet_FollowsTheSwitch(bool off)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        LogService log = new();
        using RoomEntityClassifier classifier = new(router, new MonsterMessageStore(), players, log);
        List<string> sent = new();
        bool switchOff = off;
        using GreetManager greet = new(classifier, players, new PartyState(), () => null)
        {
            Enabled = true,
            MasterSwitchOff = () => switchOff,
        };
        greet.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes)));
        players.Players.Add(new PlayerRecord(
            GivenName: "Bob", FamilyName: "", Class: "Warrior", Race: "Human", Alignment: "Neutral",
            Title: null, Gang: null, Role: null, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow));

        router.Dispatch(Line("Also here: Bob."));

        Assert.Equal(off ? Array.Empty<string>() : new[] { "greet Bob\r", "look Bob\r" }, sent);

        // Nothing was recorded for the skipped greet, so it is made at the next
        // sighting once the switch is back on.
        switchOff = false;
        router.Dispatch(Line("Also here: Bob."));
        Assert.Equal(new[] { "greet Bob\r", "look Bob\r" }, sent);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void LookBack_FollowsTheSwitch(bool off, int expected)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        LogService log = new();
        using RoomEntityClassifier classifier = new(router, new MonsterMessageStore(), players, log);
        using RoomEntryWatcher entry = new(router, classifier, log);
        List<string> sent = new();
        using PlayerLookManager look = new(router, entry, players, new PartyState(), () => null)
        {
            LookBackWhenLookedAt = true,
            MasterSwitchOff = () => off,
        };
        look.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes)));

        router.Dispatch(Line("Bob is looking at you."));

        Assert.Equal(expected, sent.Count);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void TelepathDivert_FollowsTheSwitch(bool off, int expected)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, new PartyState(), players);
        DivertHandler handler = new(engine, chat);
        List<byte[]> forwarded = new();
        handler.SetWireSender(forwarded.Add);
        players.RecordObservation("Bob", null, null, null, null, null, null, DateTime.UtcNow);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.DivertConversations));
        router.Dispatch(new LineExtractor.EmittedLine("Bob telepaths: @divert Raijin",
            new CellAttributes["Bob telepaths: @divert Raijin".Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        handler.MasterSwitchOff = () => off;
        router.Dispatch(new LineExtractor.EmittedLine("Ann telepaths: hello there",
            new CellAttributes["Ann telepaths: hello there".Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        Assert.Equal(expected, forwarded.Count);
    }
}
