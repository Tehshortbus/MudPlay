using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The plain death-room pickup, which every death uses, in the cases where the room
// is not simply walked into lit with its floor on show. The rule they pin: an armed
// grab is never spent on a guess. When nothing was read, it stays armed for the next
// display of the room, as it did before the spill sweep existed.
public sealed partial class DeathRecoveryManagerTests
{
    private static RoomObservation ObsNowhere()
        => new("Some Other Place", new HashSet<Direction>(new[] { Direction.E, Direction.W }));

    // ----- Recover Now from inside the room ---------------------------

    [Fact]
    public void RecoverNow_InTheRoom_TrackerConfirmed_GetsTheFloor()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("a torch");
        h.EnterGates();
        h.Feed.Line("Obvious exits: north");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void RecoverNow_InTheRoom_TrackerSuspect_StillGetsTheFloor()
    {
        // The tracker saw a display it couldn't place and is Suspect, still in the
        // death room. The look's floor line arrives before the exits line that
        // re-confirms it. That floor is the room's: no move has gone out.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();                               // back in the death room, Auto-Recover off
        h.Tracker.NoteRoomObserved(ObsNowhere());
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
        h.FeedSurvey("a torch");
        h.EnterGates();
        h.Feed.Line("Obvious exits: north");
        Assert.Contains("get torch", h.Sent);

        h.Feed.Line("You took torch.");
        for (int i = 0; i < 3; i++) h.Heartbeat();
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));   // no sweep over a pile underfoot
    }

    [Fact]
    public void RecoverNow_InTheRoom_AMoveSentAfterTheLook_ItsDisplayIsNotTakenForThisRooms()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Tracker.NoteMoveSent(Direction.N);          // changed their mind and stepped off
        h.Sent.Clear();

        h.FeedSurvey("a torch");                      // the next room's floor, a torch like ours on it
        h.Feed.Line("Obvious exits: south");
        for (int i = 0; i < 3; i++) h.Heartbeat();

        Assert.Empty(h.Sent);
    }

    // ----- walking in -------------------------------------------------

    [Fact]
    public void Stock_DraggedInByAPartyLeader_Gets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteFollowMove(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ArrivalThatReanchorsASuspectTracker_Gets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteRoomObserved(ObsNowhere());
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_HostileInTheDeathRoom_DoesNotHoldThePickup()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("iron sword", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Hostiles = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
        h.Feed.Line("You took torch.");
        for (int i = 0; i < 4; i++) h.Heartbeat();
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "iron sword" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_TypedAheadArrival_ConfirmedInPlace_FloorIsNotTakenAsEmpty()
    {
        // The step into the death room lands with one more move still queued, the
        // floor list is emptied by that room change, and the extra move bounces: the
        // room confirms in place. Nothing was read at that moment, so nothing is
        // concluded, and the room's next display is acted on.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.Tracker.NoteMoveSentByObserver(Direction.S);   // one too many
        h.FeedSurvey("a torch");
        h.EnterGates();                               // lands, Pending
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        h.Tracker.NoteMoveBlocked();                  // the extra one bounces
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        for (int i = 0; i < 3; i++) h.Heartbeat();

        h.FeedSurvey("a torch");                      // a later display of the room
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_BlindWalkIn_StaysArmedUntilTheRoomIsSeen()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteBlindMove();
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);
        Assert.True(h.Tracker.EnteredBlind);
        for (int i = 0; i < 3; i++) h.Heartbeat();
        Assert.Empty(h.Sent);

        h.FeedSurvey("a torch");                      // sight is back and the room is looked at
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
        Assert.False(h.Tracker.EnteredBlind);
    }

    [Fact]
    public void Stock_DarkWalkIn_AFloorOfTheRoomLeftReadOnTheWay_DoesNotSpendTheGrab()
    {
        // A re-display of the room being left is still on its way when the move into
        // the dark death room goes out. It is newer than the move, yet it isn't the
        // death room's floor: in the dark nothing of it was shown.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.FeedSurvey("a lantern");
        h.Tracker.NoteDarkRoomEntered();
        for (int i = 0; i < 3; i++) h.Heartbeat();

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_EmptyFloorAtTheWalkIn_ALaterSurveyInTheRoomIsStillActedOn()
    {
        // Lit, displayed, no floor line: empty, and the pile is noted as not here.
        // The grab stays armed all the same: a search may show the room's hidden side.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Heartbeat();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
        h.Feed.Line("You took torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_EmptyFloorAtTheWalkIn_ASurveyBeforeTheNextHeartbeatIsActedOnToo()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ArmedGrab_DoesNotTakeTheNextRoomsFloorForTheDeathRooms()
    {
        // What makes staying armed safe: the next room prints its floor before it
        // confirms, with the move that leads there still pending. The death-room grab
        // leaves that survey alone. The room it belongs to borders the pile, so the
        // pass-through grab takes it when the room confirms: once, and counted.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();              // armed, in the dark

        h.Tracker.NoteMoveSent(Direction.N);          // walking on
        h.FeedSurvey("a torch");                      // 1/3's floor, our overflow on it
        Assert.Empty(h.Sent);

        h.Tracker.NoteRoomObserved(Obs3());
        Assert.Equal(new[] { "get torch" }, h.Sent.ToArray());
        h.Feed.Line("You took torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_ArmedAfterAnEmptyFloor_WalkingOn_TheNextRoomsOverflowIsGot()
    {
        // The death-room grab no longer takes the next room's entry survey for its
        // own, as it used to by accident. The pass-through grab for that room reads
        // the floor it walked in over instead of arming for a survey that has gone by.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.EnterGates();                                  // back in the death room, floor empty
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.FeedSurvey("a torch");
        h.Tracker.NoteRoomObserved(Obs3());
        for (int i = 0; i < 2; i++) h.Heartbeat();

        Assert.Equal(1, h.Sent.Count(s => s == "get torch"));
    }

    [Fact]
    public void Stock_WalkInFromANeighbour_AsksForEachFloorItemOnce()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());             // respawned next door: borders the pile
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Equal(1, h.Sent.Count(s => s == "get torch"));
    }

    [Fact]
    public void Stock_PassThroughGrabLeftArmedNextDoor_ThenTheDeathRoom_AsksOnce_AndCounts()
    {
        // The neighbour's floor was empty, so its pass-through grab stayed armed and
        // took the first survey to come: the death room's own, read before the room
        // confirmed. The death-room grab must not ask for the same floor again, and
        // the replies must still come off the pile.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch", "rope" });
        h.Latest.UnrecoveredItems = new List<string> { "torch", "rope" };
        h.Latest.Status = DeathRecoveryStatus.Partial;
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());             // next door, nothing on its floor: armed
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");                        // the death room's floor
        h.EnterGates();

        Assert.Equal(1, h.Sent.Count(s => s == "get torch"));
        h.Feed.Line("You took torch.");
        Assert.Equal(new[] { "rope" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_ArmedInTheDark_TrackerSuspect_AMoveThatBounced_ALookStillGets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();                 // armed in the dark
        h.Tracker.NoteRoomObserved(ObsNowhere());        // a display it can't place
        h.Tracker.NoteMoveSentByObserver(Direction.W);   // no such exit
        h.Tracker.NoteMoveBlocked();
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
    }

    // ----- a disconnect while armed ------------------------------------

    [Fact]
    public void Paradigm_ArmedGrab_SurvivesADisconnect()
    {
        // A Paradigm walk-in into the dark reads nothing and leaves the corpse grab
        // armed. The room isn't entered again after logging back in, so nothing
        // would arm it a second time.
        using GraphHarness h = new();   // Paradigm
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Recovery.NotifyDisconnected();
        h.FeedSurvey("corpse of Ermias");   // the room display after logging back in
        h.EnterGates();

        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Stock_ArmedGrabInTheDark_SurvivesADisconnect()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();

        h.Recovery.NotifyDisconnected();
        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ADisconnect_TakesTheSweepAway_NotThePickup()
    {
        // Recover Now, then the line drops before the look is answered. Back in, the
        // floor is still got; no sweep follows a recovery the connection cut short.
        using GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Recovery.NotifyDisconnected();
        h.Sent.Clear();

        h.FeedSurvey("an iron sword");
        Assert.Contains("get iron sword", h.Sent);
        h.Feed.Line("You took an iron sword.");
        for (int i = 0; i < 5; i++) h.Heartbeat();

        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    // ----- a Recover Now that never arrives -----------------------------

    [Fact]
    public void RecoverNow_WhoseRouteCardsWereClosed_DoesNotForceALaterWalkIn()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Tracker.NoteRoomObserved(Obs3());            // standing next door
        h.Recovery.SetDemandedWalk(_ => true);         // the cards went up
        Assert.True(h.Recovery.RecoverNow(h.Latest));

        h.Recovery.RecoverNowCardsClosed();
        h.Tracker.NoteMoveSentByObserver(Direction.S); // later, by hand, Auto-Recover off
        h.FeedSurvey("a torch");
        h.EnterGates();

        Assert.Empty(h.Sent);
    }

    // ----- the pass-through grab next door ------------------------------

    // A Stock pile with the sword got and a helm still out, standing in the death
    // room again after a step into the room north of it (1/3) and back.
    private static GraphHarness HelmStillOutNextDoor()
    {
        GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("an iron sword");
        h.EnterGates();
        h.Feed.Line("You took an iron sword.");
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Sent.Clear();
        return h;
    }

    [Fact]
    public void PassThroughGrab_WalkMovesOnAtOnce_PickupIsStillCounted()
    {
        // A loop steps into the room next door, whose floor holds the helm, and sends
        // its next move in the same breath as the arrival. Sending a move is a
        // transition of its own (same room, now pending), and the reply to the get
        // comes after it.
        using GraphHarness h = HelmStillOutNextDoor();
        h.Tracker.NoteMoveSent(Direction.N);
        h.FeedSurvey("a steel helm");
        h.Tracker.NoteRoomObserved(Obs3());
        Assert.Contains("get steel helm", h.Sent);

        h.Tracker.NoteMoveSent(Direction.S);
        h.Feed.Line("You took a steel helm.");

        Assert.True(h.Latest.UnrecoveredItems is null or { Count: 0 },
            $"still listed: {string.Join(", ", h.Latest.UnrecoveredItems ?? new())}; status {h.Latest.Status}");
    }

    [Fact]
    public void PassThroughGrab_StandingStill_PickupIsCounted()
    {
        using GraphHarness h = HelmStillOutNextDoor();
        h.Tracker.NoteMoveSent(Direction.N);
        h.FeedSurvey("a steel helm");
        h.Tracker.NoteRoomObserved(Obs3());
        h.Feed.Line("You took a steel helm.");

        Assert.True(h.Latest.UnrecoveredItems is null or { Count: 0 },
            $"still listed: {string.Join(", ", h.Latest.UnrecoveredItems ?? new())}");
    }

    [Fact]
    public void WalkInFromNextDoor_WithTheHelmOnTheDeathRoomFloor_GetsItOnce()
    {
        using GraphHarness h = HelmStillOutNextDoor();
        h.Tracker.NoteMoveSentByObserver(Direction.N);
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a steel helm");
        h.EnterGates();

        Assert.Equal(1, h.Sent.Count(s => s == "get steel helm"));
        h.Feed.Line("You took a steel helm.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }
}
