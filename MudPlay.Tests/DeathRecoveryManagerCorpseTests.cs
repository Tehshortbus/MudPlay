using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Paradigm's corpse grab: which display it reads the corpse from, and which
// display may say the corpse is gone. The harness defaults to Paradigm.
public sealed partial class DeathRecoveryManagerTests
{
    private static GraphHarness DiedAtTheGates(bool autoRecover = true)
    {
        GraphHarness h = new();
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = autoRecover;
        h.Tracker.NoteRoomObserved(Obs3());   // standing next door (1/3)
        h.Sent.Clear();
        return h;
    }

    // ----- walking in -------------------------------------------------

    [Fact]
    public void Paradigm_WalkIn_RecoversOffTheArrivalsOwnDisplay()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");     // the floor line, ahead of the exits line
        Assert.Empty(h.Sent);
        h.EnterGates();

        Assert.Equal(new[] { "recover corpse Ermias" }, h.Sent.ToArray());
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_AFriendsCorpseRecoveredMeanwhile_DoesNotCloseOurPile()
    {
        // The same closing line names whose corpse it was (user, 2026-10-17).
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);

        h.Feed.Line("You have recovered the corpse of Raijin.");
        Assert.NotEqual(DeathRecoveryStatus.Recovered, h.Latest.Status);

        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_TheCorpseWeAskedFor_ClosesOurPileWhenTheStoredNameIsStale()
    {
        // The stored name can be stale (report stock-20260828-104653) and the live
        // one not yet known; the corpse our own command named is ours all the same.
        using GraphHarness h = DiedAtTheGates();
        string? live = "Ermias";
        h.Recovery.AttachLiveSelfName(() => live);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);

        live = null;
        h.Profile.Current!.Name = "Stale";
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_TheCorpseAskedForBeforeADeath_DoesNotCloseTheNextPile()
    {
        using GraphHarness h = DiedAtTheGates();
        string? live = null;
        h.Recovery.AttachLiveSelfName(() => live);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);

        // No closing line came; the character died again, and is back in a dark
        // death room with the grab armed and nothing sent.
        Die(h, new[] { new EquippedItem("longsword", "Weapon Hand") }, new[] { "rope" });
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        live = "Raijin";

        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.NotEqual(DeathRecoveryStatus.Recovered, h.Latest.Status);
        h.Feed.Line("You have recovered the corpse of Raijin.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkIn_ThenWalkingOn_TheNextRoomsFloorDoesNotMarkThePileMissing()
    {
        // The corpse is there and asked for. The walk goes on into a room with a
        // lantern on its floor, whose survey prints before that room confirms.
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.FeedSurvey("a lantern");
        h.Tracker.NoteRoomObserved(Obs3());

        Assert.NotEqual(DeathRecoveryStatus.Missing, h.Latest.Status);
        Assert.Equal(1, h.Sent.Count(s => s == "recover corpse Ermias"));
    }

    [Fact]
    public void Paradigm_GrabLeftArmed_ThenWalkingOn_TheNextRoomsFloorIsNotTheDeathRooms()
    {
        // Armed with nothing read (a dark death room), then on into a lit room with
        // an item on its floor. Taken for the death room's, that floor has no corpse
        // on it and the pile was written off.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Tracker.NoteMoveSent(Direction.N);
        h.FeedSurvey("a lantern");
        h.Tracker.NoteRoomObserved(Obs3());

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkIn_NoAutoRecover_SendsNothing_AndSaysNothingOfTheCorpse()
    {
        using GraphHarness h = DiedAtTheGates(autoRecover: false);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();                       // an empty floor, and nobody asked

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    // ----- the corpse really is gone -----------------------------------

    [Fact]
    public void Paradigm_WalkIn_CorpseGone_OtherThingsOnTheFloor_MarksMissingFromThatRoom()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a lantern");
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkIn_CorpseGone_EmptyFloor_MarksMissingFromThatRoom()
    {
        // An empty floor prints no survey at all. The room was shown, so "nothing on
        // the floor" is known; it is not a guess off the room walked into next.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkIn_AFloorOfTheRoomLeft_IsNotReadAsThisRooms()
    {
        // The room next door has our corpse's namesake on its floor (a re-display
        // read before the move went out). The death room shows an empty floor.
        using GraphHarness h = DiedAtTheGates();
        h.FeedSurvey("corpse of Ermias");     // read next door, before the move
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    // ----- arrivals nothing was shown for -------------------------------

    [Fact]
    public void Paradigm_DarkWalkIn_StaysArmed_AndTheLitRoomsDisplayRecovers()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // dark is not "no corpse"

        h.FeedSurvey("corpse of Ermias");     // a light, then a look
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Paradigm_DarkWalkIn_ThenLit_CorpseGone_MarksMissingFromThatDisplay()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();

        h.FeedSurvey("a lantern");
        // The floor line alone says nothing yet: which room it was of is only
        // known at the exits line.
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_BlindWalkIn_StaysArmedUntilTheRoomIsSeen()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteBlindMove();
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Paradigm_PlacedInTheDeathRoomWithoutADisplay_StaysArmed_NotMissing()
    {
        // A manual locate (and the same for a saved room at load, or a reconnect):
        // the tracker says the death room and nothing of it has been read.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Paradigm_ArmedGrab_TrackerSuspectInTheRoom_StillTakesItsFloor()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        h.Tracker.NoteRoomObserved(ObsNowhere());   // a display it can't place
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);

        h.FeedSurvey("corpse of Ermias");
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Paradigm_ArmedGrab_ALookThroughAnExit_ShowsTheNextRoomsFloor_NotThisOnes()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();

        h.Tracker.NoteLookSent();             // look north
        h.FeedSurvey("a lantern");            // the room next door, floor and all
        h.Tracker.NoteRoomObserved(Obs3());   // its exits line: dropped as a peek
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");     // then this room's own
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    // ----- Recover Now from inside the room ------------------------------

    [Fact]
    public void Paradigm_RecoverNow_InTheRoom_TheLookShowsNothingOnTheFloor_MarksMissing()
    {
        using GraphHarness h = DiedAtTheGates(autoRecover: false);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.EnterGates();                                      // the look's display: no floor line
        h.Feed.Line("Obvious exits: north");

        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("recover corpse"));
    }

    [Fact]
    public void Paradigm_RecoverNow_InTheRoom_AMoveSentAfterTheLook_ConcludesNothing()
    {
        using GraphHarness h = DiedAtTheGates(autoRecover: false);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Tracker.NoteMoveSent(Direction.N);                 // stepped off before the answer

        h.FeedSurvey("a lantern");                           // the next room's floor
        h.Feed.Line("Obvious exits: south");

        Assert.NotEqual(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void Paradigm_RecoverNow_OnAMissingPile_WhoseCorpseIsThereAfterAll_Recovers()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        h.Feed.Line("Obvious exits: north");

        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.NotEqual(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    // ----- handed back after the corpse was found gone --------------------

    // Dies at the gates, walks back in and finds the room bare: the pile is Missing.
    private static GraphHarness CorpseGoneOnArrival()
    {
        GraphHarness h = DiedAtTheGates();
        h.Recovery.AutoEquip = true;
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        h.Sent.Clear();
        return h;
    }

    [Fact]
    public void Paradigm_CorpseGoneOnArrival_ThenHandedBackByAPartyMember_IsStillCounted()
    {
        // The leader picked the corpse up; the follower walks into the death room,
        // finds no corpse (Missing), and is then handed the gear.
        using GraphHarness h = CorpseGoneOnArrival();

        h.Recovery.OnItemReceived("rusty dagger", "Nineteen");
        h.Recovery.OnItemReceived("torch", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("eq rusty dagger", h.Sent);
    }

    [Fact]
    public void HandBack_AfterALaterDeathThatDroppedNothing_GoesToThePileThatListsTheItem()
    {
        // The party-wipe case: the first pile is Missing (the leader has the
        // corpse), the follower dies again with nothing on, and then the first
        // pile's gear is handed over. The later death lists nothing, so it doesn't
        // stand in the way.
        using GraphHarness h = CorpseGoneOnArrival();
        DeathRecord first = h.Latest;

        Die(h, Array.Empty<EquippedItem>(), Array.Empty<string>());
        h.Recovery.OnItemReceived("rusty dagger", "Leader");
        h.Recovery.OnItemReceived("torch", "Leader");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Recovered, first.Status);
        Assert.Contains("eq rusty dagger", h.Sent);
        Assert.NotSame(first, h.Latest);
        Assert.Null(h.Latest.UnrecoveredItems);            // the later death's record is untouched
    }

    // Owner's ruling, 2026-10-10: a hand-back counts from anyone, party or not, at
    // any time. These two once pinned the opposite (a stranger's gift, and a gift
    // after the 30-minute window, left a Missing pile untouched).
    [Fact]
    public void AGift_FromSomeoneNotInTheParty_CountsTowardAMissingPile()
    {
        using GraphHarness h = CorpseGoneOnArrival();

        h.Recovery.OnItemReceived("rusty dagger", "Somebody");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "torch" }, h.Latest.UnrecoveredItems);
        Assert.Contains("eq rusty dagger", h.Sent);
    }

    [Fact]
    public void AGift_LongAfterThePileWasMarkedMissing_StillCountsTowardIt()
    {
        using GraphHarness h = CorpseGoneOnArrival();
        for (int i = 0; i < 1900; i++) h.Heartbeat();   // over half an hour of quiet

        h.Recovery.OnItemReceived("rusty dagger", "Somebody");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "torch" }, h.Latest.UnrecoveredItems);
        Assert.Contains("eq rusty dagger", h.Sent);
    }

    [Fact]
    public void AMissingPile_HandedFullyBackByAStranger_EndsRecovered()
    {
        using GraphHarness h = CorpseGoneOnArrival();

        h.Recovery.OnItemReceived("rusty dagger", "Somebody");
        h.Recovery.OnItemReceived("torch", "Somebody");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Equal("Handed back by Somebody.", h.Latest.RecoveryMessage);
        Assert.Null(h.Latest.UnrecoveredItems);
        Assert.False(h.Latest.HandedBack);
    }

    [Fact]
    public void AMissingPile_HandedPartlyBackByAStranger_IsPartialWithTheCountStillOut()
    {
        using GraphHarness h = CorpseGoneOnArrival();

        h.Recovery.OnItemReceived("torch", "Somebody");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal("Handed back by Somebody — 1 item(s) still out.", h.Latest.RecoveryMessage);
        Assert.Equal(new[] { "rusty dagger" }, h.Latest.UnrecoveredItems);
        Assert.True(h.Latest.HandedBack);
    }

    [Fact]
    public void AnOpenPile_TakesAHandBackFromAnyone_AsBefore()
    {
        using GraphHarness h = DiedAtTheGates();   // Active, never visited

        h.Recovery.OnItemReceived("torch", "Somebody");

        Assert.Equal(new[] { "rusty dagger" }, h.Latest.UnrecoveredItems);
    }

    // The room display as the app delivers it: the tracker takes the observation,
    // recovery then reads the exits line.
    private static void ShowDeathRoomAgain(GraphHarness h)
    {
        h.EnterGates();
        h.Feed.Line("Obvious exits: north");
    }

    // A hand-back typed by hand, a give at a time, with the follower standing in
    // the death room and a Missing pile. The first give settles to Partial.
    private static GraphHarness HandedTheTorchInTheDeathRoom()
    {
        GraphHarness h = DiedAtTheGates();
        h.Recovery.AutoEquip = true;
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        ShowDeathRoomAgain(h);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        h.Sent.Clear();

        h.Recovery.OnItemReceived("torch", "Leader");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "rusty dagger" }, h.Latest.UnrecoveredItems);
        return h;
    }

    [Fact]
    public void HandBack_InTheDeathRoom_TheRoomShownAgainBetweenGives_KeepsTheCount()
    {
        using GraphHarness h = HandedTheTorchInTheDeathRoom();

        ShowDeathRoomAgain(h);
        Assert.Equal(new[] { "rusty dagger" }, h.Latest.UnrecoveredItems);
        ShowDeathRoomAgain(h);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "rusty dagger" }, h.Latest.UnrecoveredItems);

        h.Recovery.OnItemReceived("rusty dagger", "Leader");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Null(h.Latest.UnrecoveredItems);
        Assert.False(h.Latest.HandedBack);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("recover corpse"));
    }

    [Fact]
    public void HandBack_InTheDeathRoom_ThenTheCorpseIsPutDown_IsRecoveredOnce()
    {
        using GraphHarness h = HandedTheTorchInTheDeathRoom();

        ShowDeathRoomAgain(h);
        ShowDeathRoomAgain(h);                // a bare floor twice does not write the pile off
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");
        ShowDeathRoomAgain(h);

        Assert.Equal(1, h.Sent.Count(s => s == "recover corpse Ermias"));
    }

    // ----- over the real walker ------------------------------------------

    [Fact]
    public void Paradigm_WalkThatEndsInTheDeathRoom_RecoversOnArrival()
    {
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "corpse of Ermias");
        w.H.Recovery.AutoRecover = true;
        w.Enter(4);

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(6);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Equal(1, w.H.Sent.Count(s => s == "recover corpse Ermias"));
        w.H.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkThroughTheDeathRoom_RecoversInPassing_AndTheNextFloorChangesNothing()
    {
        // Died in the lane (2), walking from the dead end (4) to the crossing (1),
        // which has a lantern on its floor and no corpse.
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(2);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(2, "corpse of Ermias");
        w.Put(1, "a lantern");
        w.H.Recovery.AutoRecover = true;
        w.Enter(4);

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(6);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Equal(1, w.H.Sent.Count(s => s == "recover corpse Ermias"));
        Assert.NotEqual(DeathRecoveryStatus.Missing, w.H.Latest.Status);
    }

    [Fact]
    public void Paradigm_WalkThroughTheDeathRoom_CorpseGone_IsMarkedMissingThere_NotOffTheNextRoom()
    {
        // Died in the lane (2); its floor is bare. A corpse of the same name lies in
        // the room walked into next. The verdict is the death room's own, and the
        // other room's corpse is not asked for.
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(2);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "corpse of Ermias");
        w.H.Recovery.AutoRecover = true;
        w.Enter(4);

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(6);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("recover corpse"));
        Assert.Equal(DeathRecoveryStatus.Missing, w.H.Latest.Status);
    }

    // ----- the lit light and the keys ------------------------------------

    // Dies at the gates wearing a longsword, a torch lit and two kinds of key on
    // the ring; nothing in the pack.
    private static void DieWithALightAndKeys(GraphHarness h)
    {
        h.EnterGates();
        h.Snapshot = h.SnapWith(new[] { new EquippedItem("longsword", "Weapon Hand") }, Array.Empty<string>())
            with
            {
                ReadiedLight = new ReadiedLight("torch", 12),
                Keys = new[] { "2 black star key", "iron key" },
            };
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");
        h.Sent.Clear();
    }

    [Fact]
    public void Stock_LitLightAndKeys_AreGotBack_AndTheLightIsNotLitAgain()
    {
        using GraphHarness h = new() { Paradigm = false };
        DieWithALightAndKeys(h);
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        Assert.Equal(new[] { "2 black star key", "iron key", "torch" },
            h.Latest.LostItems!.Select(i => i.Name).ToArray());

        h.FeedSurvey("a longsword, a torch, 2 black star key, and an iron key");
        h.EnterGates();
        Assert.Equal(new[] { "longsword", "black star key", "black star key", "iron key", "torch" },
            h.Latest.UnrecoveredItems);
        Assert.Equal(2, h.Sent.Count(s => s == "get black star key"));
        Assert.Contains("get torch", h.Sent);
        Assert.Contains("get iron key", h.Sent);

        h.Sent.Clear();
        h.Feed.Line("You took longsword.");
        h.Feed.Line("You took torch.");
        h.Feed.Line("You took black star key.");
        h.Feed.Line("You took black star key.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // a key is still out
        h.Feed.Line("You took iron key.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Equal(new[] { "eq longsword" }, h.Sent.ToArray());     // no `use torch`
    }

    [Fact]
    public void Stock_AKeyOnTheRing_IsWaitedFor_EvenIfItsItemStaysWithTheCharacter()
    {
        // The engine's loyal / cursed test is made on the pack only. A pack item
        // that stays is not waited for; a key of a name that "stays" still drops.
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("iron key");
        h.Stays.Add("signet ring");
        h.EnterGates();
        h.Snapshot = h.SnapWith(Array.Empty<EquippedItem>(), new[] { "signet ring", "ration" })
            with { Keys = new[] { "iron key" } };
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");
        h.Sent.Clear();
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a ration and an iron key");
        h.EnterGates();

        Assert.Equal(new[] { "ration", "iron key" }, h.Latest.UnrecoveredItems);
        Assert.Contains("get iron key", h.Sent);
        Assert.DoesNotContain("get signet ring", h.Sent);
    }

    [Fact]
    public void Paradigm_CorpseRecovered_CountsTheLightAndTheKeys_AndWearsOnlyTheGear()
    {
        using GraphHarness h = new();
        DieWithALightAndKeys(h);
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        h.Sent.Clear();
        h.Feed.Line("You have recovered the corpse of Ermias.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("5 item(s)", h.Latest.RecoveryMessage);
        Assert.Equal(new[] { "eq longsword" }, h.Sent.ToArray());
    }

    [Fact]
    public void HandedBack_AKeyFromThePile_IsStruckOff()
    {
        using GraphHarness h = new();
        DieWithALightAndKeys(h);

        h.Recovery.OnItemReceived("iron key", "Nineteen");

        Assert.DoesNotContain("iron key", h.Latest.UnrecoveredItems!);
        Assert.Equal(2, h.Latest.UnrecoveredItems!.Count(n => n == "black star key"));
    }

    // ----- a spare of something worn -------------------------------------

    [Fact]
    public void Paradigm_WornPieceAndItsSpare_BothCounted_OneGoesBackOn()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("longsword", "Weapon Hand") }, new[] { "longsword", "torch" });
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Equal(new[] { "longsword", "longsword", "torch" }, h.Latest.UnrecoveredItems);

        h.Sent.Clear();
        h.Feed.Line("You have recovered the corpse of Ermias.");

        Assert.Equal(new[] { "eq longsword" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("3 item(s)", h.Latest.RecoveryMessage);
    }

    [Fact]
    public void Stock_WornPieceAndItsSpare_BothGot_OneGoesBackOn()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("longsword", "Weapon Hand") }, new[] { "longsword" });
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("2 longsword");
        h.EnterGates();
        Assert.Equal(2, h.Sent.Count(s => s == "get longsword"));

        h.Sent.Clear();
        h.Feed.Line("You took longsword.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // the other copy is still out
        h.Feed.Line("You took longsword.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Equal(new[] { "eq longsword" }, h.Sent.ToArray());
    }

    [Fact]
    public void Stock_WornPieceAndItsSpare_OnlyOneFound_ThatOneIsWorn()
    {
        // One of the two copies is on this floor, the other spilled. The copy got
        // back is the weapon to hold; waiting for the spare would leave it unarmed.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("longsword", "Weapon Hand") }, new[] { "longsword" });
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("a longsword");
        h.EnterGates();
        Assert.Contains("get longsword", h.Sent);

        h.Sent.Clear();
        h.Feed.Line("You took a longsword.");   // one answer: one copy was there
        for (int i = 0; i < 4; i++) h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "longsword" }, h.Latest.UnrecoveredItems);
        Assert.Equal(1, h.Sent.Count(s => s == "eq longsword"));
    }

    [Fact]
    public void Stock_WornPiece_NoSpare_StillOut_IsNotWorn()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("longsword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("a steel helm");
        h.EnterGates();

        h.Sent.Clear();
        h.Feed.Line("You took a steel helm.");
        for (int i = 0; i < 4; i++) h.Heartbeat();

        Assert.Contains("wear steel helm", h.Sent);
        Assert.DoesNotContain("eq longsword", h.Sent);
    }
}
