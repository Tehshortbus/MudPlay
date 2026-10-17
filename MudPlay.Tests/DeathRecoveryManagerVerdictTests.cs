using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// When a pile may be called Missing, and when it must not be: each case here was
// a way a pile was written off wrongly, or left unfinishable, plus the controls
// beside them.
public sealed partial class DeathRecoveryManagerTests
{
    // ----- the verdict waits for the display's exits line ---------------

    [Fact]
    public void ArmedGrab_RelocatedWithoutAMove_TheOtherRoomsFloorDoesNotMarkThePileMissing()
    {
        // Armed in the death room with nothing read (placed there with no display),
        // then somewhere else with no move sent: a teleport, fear, a summon, or the
        // room the character really stands in at login. That room's floor line
        // prints before its exits line, so the tracker still says "death room" when
        // the floor is read.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("a lantern");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        h.Tracker.NoteRoomObserved(Obs3());           // its exits line: North Square

        Assert.Equal(new RoomKey(1, 3), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DarkWalkIn_ThenRelocatedWithoutAMove_DoesNotMarkThePileMissing()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();

        h.FeedSurvey("a lantern");
        h.Tracker.NoteRoomObserved(Obs3());

        Assert.Equal(new RoomKey(1, 3), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    [Fact]
    public void ArmedGrab_AFloorWithTheCorpse_IsActedOnAtTheFloorLine()
    {
        // The positive needs no waiting: the command names a corpse the game has
        // just shown.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.FeedSurvey("corpse of Ermias");

        Assert.Equal(new[] { "recover corpse Ermias" }, h.Sent.ToArray());
    }

    [Fact]
    public void ArmedGrab_TheDeathRoomShownWithABareFloor_MarksMissingAtItsExitsLine()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.EnterGates();                               // the room, shown: no floor line

        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        Assert.Contains("nothing on its floor", h.Latest.RecoveryMessage);
    }

    // ----- Recover Now from inside the room ------------------------------

    [Fact]
    public void RecoverNow_InTheRoom_APeeksExitsLineConcludesNothing_AndTheCorpseIsThenRecovered()
    {
        // A `look <dir>` typed just before is still unanswered. Its display (the
        // room next door, bare floor) ends in an exits line; the room parser hands
        // that line to the tracker first, which sets the peek aside, and recovery
        // reads the same line after.
        using GraphHarness h = DiedAtTheGates(autoRecover: false);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Sent.Clear();
        h.Tracker.NoteLookSent();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.True(h.Tracker.IsPeekSuppressed());

        h.Tracker.NoteRoomObserved(Obs3());
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);
        h.Feed.Line("Obvious exits: south");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");                   // the real look
        h.EnterGates();
        h.Feed.Line("Obvious exits: north");

        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.NotEqual(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void RecoverNow_InTheRoom_APeekFirst_ThenTheLooksOwnBareDisplay_MarksMissing()
    {
        // The peek's floor and exits line conclude nothing; the look's own display
        // of this room, with no floor line, does.
        using GraphHarness h = DiedAtTheGates(autoRecover: false);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Tracker.NoteLookSent();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("a lantern");                    // the peek's floor
        h.Tracker.NoteRoomObserved(Obs3());           // set aside as a peek
        h.Feed.Line("Obvious exits: south");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.EnterGates();                               // the real look: bare floor
        h.Feed.Line("Obvious exits: north");

        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void RecoverNow_FromElsewhere_OnAMissingPile_LooksAtTheRoomAgainOnArrival()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();                               // bare floor: Missing
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();
        h.Recovery.AutoRecover = false;

        h.Recovery.SetDemandedWalk(_ => true);
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");             // it is there after all
        h.EnterGates();

        Assert.Equal(new[] { "recover corpse Ermias" }, h.Sent.ToArray());
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void RecoverNow_FromElsewhere_OnAMissingPile_StillGone_StaysMissing_AndIsNotLeftPending()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Recovery.AutoRecover = false;
        h.Recovery.SetDemandedWalk(_ => true);
        Assert.True(h.Recovery.RecoverNow(h.Latest));

        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a lantern");
        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
        Assert.Contains("on arriving", h.Latest.RecoveryMessage);

        // The request was spent on that arrival: a later walk-in by hand, with
        // Auto-Recover off, is not it arriving again.
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    // ----- a verdict taken back on the same visit ------------------------

    [Fact]
    public void MarkedMissingOnArrival_ThenTheCorpseIsShownOnTheSameVisit_IsReopenedAndRecovered()
    {
        // Where two rooms read alike, a re-display of the room just left can be
        // taken for the arrival; the real arrival's display follows, as a display
        // of the room the map already has us in.
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        Assert.Equal(new[] { "recover corpse Ermias" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    // Auto-All went off between the verdict and the display that takes it back:
    // the record is put right, but no `recover corpse` goes out by itself. Recover
    // Now, the user's own press, still gets it.
    [Fact]
    public void MarkedMissing_ThenTheCorpseIsShown_WithAutoAllOff_IsReopenedButNotAskedFor()
    {
        using GraphHarness h = DiedAtTheGates();
        bool autoOn = true;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);

        autoOn = false;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Contains("Auto-All is off", h.Latest.RecoveryMessage);

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("corpse of Ermias");
        Assert.Equal(new[] { "look", "recover corpse Ermias" }, h.Sent.ToArray());
    }

    [Fact]
    public void MarkedMissing_ThenLeftAndComeBackTo_IsNotReopenedByItself()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());

        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    // ----- what counts as a display --------------------------------------

    [Fact]
    public void ReconnectMidMove_LandedInTheDeathRoom_IsADisplayedArrival()
    {
        // A drop with a move in flight: the login display settles where the move
        // landed, and it is a display of that room.
        using GraphHarness h = DiedAtTheGates();
        bool? displayed = null;
        h.Tracker.StateChanged += t =>
        {
            if (t.NewConfidence == RoomConfidence.Confirmed && t.NewRoom?.Key.Room == 1) displayed = t.Displayed;
        };
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteConnectionLost();
        h.EnterGates();                               // Town Gates, bare floor

        Assert.True(displayed);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void Stock_PlacedInTheDeathRoomWithoutADisplay_IsNotSettledAsAnEmptyFloor()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Heartbeat(); h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.DoesNotContain("not in this room", h.Latest.RecoveryMessage);

        h.FeedSurvey("a torch");                      // the room's first display
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
    }

    // ----- whose corpse --------------------------------------------------

    [Fact]
    public void WalkIn_TwoCorpsesOnTheFloor_OnlyOursIsAskedFor()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermiasson, corpse of Ermias and a lantern");
        h.EnterGates();
        Assert.Equal(new[] { "recover corpse Ermias" }, h.Sent.ToArray());
    }

    [Fact]
    public void WalkIn_OnlySomeoneElsesCorpse_MarksMissing_AndSendsNothing()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Bob");
        h.EnterGates();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void WalkIn_TheCorpseStillThereOnALaterVisit_IsAskedForOncePerVisit()
    {
        using GraphHarness h = DiedAtTheGates();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");             // a re-display while still here
        h.EnterGates();
        Assert.Equal(1, h.Sent.Count(s => s == "recover corpse Ermias"));

        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Equal(2, h.Sent.Count(s => s == "recover corpse Ermias"));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    // ----- copies and keys -----------------------------------------------

    [Fact]
    public void WornPieceAndTwoSparesInThePack_ThreeCopiesRecorded_OneWornAgain()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("longsword", "Weapon Hand") }, new[] { "2 longsword" });
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Equal(new[] { "longsword", "longsword", "longsword" }, h.Latest.UnrecoveredItems);
        h.Sent.Clear();
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(new[] { "eq longsword" }, h.Sent.ToArray());
    }

    [Fact]
    public void Stock_AKeyTheClientFiledInThePack_IsStillWaitedFor_WhereItsItemWouldStay()
    {
        // Nothing told the client the item was a key, so it sits in the pack list.
        // In the game it is on the key ring, which the stays rule never reaches.
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("iron key");
        h.KeyItems.Add("iron key");
        h.EnterGates();
        h.Snapshot = h.SnapWith(Array.Empty<EquippedItem>(), new[] { "iron key", "ration" });
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");
        h.Sent.Clear();
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a ration and an iron key");
        h.EnterGates();

        Assert.Contains("get iron key", h.Sent);
    }

    [Fact]
    public void Stock_AKeyTypeItemThatWasWorn_AndStays_IsNotWaitedFor()
    {
        // Worn, it is one of the inventory slots the stays test is made on.
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("golden idol");
        h.KeyItems.Add("golden idol");
        h.EnterGates();
        h.Snapshot = h.SnapWith(new[] { new EquippedItem("golden idol", "Neck") }, new[] { "ration" });
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");
        h.Sent.Clear();
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a ration");
        h.EnterGates();

        Assert.Equal(new[] { "ration" }, h.Latest.UnrecoveredItems);
    }

}
