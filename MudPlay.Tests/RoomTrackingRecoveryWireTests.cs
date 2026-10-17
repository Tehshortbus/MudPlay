using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Position recovery while the map is unsure of the room, driven over the wire:
// the passive grid re-localiser folding each display with the move it answers, the
// recovery gate's tier-2 forward footprint, and the cap on the tier-3 walk-back.
// Each move goes out through a wire sender and comes back as game text through
// LineExtractor (TrackerWireRig).
public sealed class RoomTrackingRecoveryWireTests
{
    // ----- the pair world -------------------------------------------------

    // One walk, every room of it named "Hall". A room's exits are its way in and
    // its way on, and the walk turns at every step, so no two rooms of one copy
    // read alike. The walk is laid out twice, A and B, so every display fits
    // exactly its two copies and only a footprint can tell them apart: B's step
    // `split` leads to a Cellar instead, and B's room past it (reached only from
    // the Cellar) reads exactly like A's.
    private static readonly Direction[] Walk =
        { Direction.E, Direction.N, Direction.NE, Direction.SE, Direction.U, Direction.E, Direction.S };

    private static RoomKey A(int i) => new(1, 100 + i);
    private static RoomKey B(int i) => new(1, 200 + i);
    private static readonly RoomKey Cellar = new(1, 300);

    private static string PairWorld(int split, int textExitAt = -1, int decoyAt = -1)
    {
        List<string> rooms = new();
        foreach (bool copyA in new[] { true, false })
        {
            for (int i = 0; i <= Walk.Length; i++)
            {
                RoomKey me = copyA ? A(i) : B(i);
                List<(string, string)> exits = new();
                if (i > 0)
                    exits.Add((Walk[i - 1].Opposite().ToString(), Ref(copyA ? A(i - 1) : B(i - 1))));
                if (i < Walk.Length)
                {
                    RoomKey on = !copyA && i == split ? Cellar : copyA ? A(i + 1) : B(i + 1);
                    exits.Add((Walk[i].ToString(), Ref(on) + (i == textExitAt ? " (Text: go path)" : "")));
                }
                // A room B's copy has and A's lacks: shown, it fits B and the decoy.
                if (!copyA && i == decoyAt) exits.Add(("D", Ref(Cellar)));
                rooms.Add(TrackerWireRig.Room(me.Map, me.Room, "Hall", exits.ToArray()));
            }
        }
        rooms.Add(TrackerWireRig.Room(Cellar.Map, Cellar.Room, "Cellar",
            ("N", Ref(B(split))), ("S", Ref(B(split + 1)))));
        if (decoyAt >= 0)
        {
            // Reads as B's room at decoyAt does, so that display fits two rooms and
            // the tracker can't settle on it by name and exits.
            List<(string, string)> exits = new();
            if (decoyAt > 0) exits.Add((Walk[decoyAt - 1].Opposite().ToString(), Ref(Cellar)));
            if (decoyAt < Walk.Length) exits.Add((Walk[decoyAt].ToString(), Ref(Cellar)));
            exits.Add(("D", Ref(Cellar)));
            rooms.Add(TrackerWireRig.Room(1, 400, "Hall", exits.ToArray()));
        }
        return TrackerWireRig.Rooms(rooms);
    }

    private static string Ref(RoomKey key) => $"{key.Map}/{key.Room}";

    private static string Token(int step) => Walk[step].ToToken();

    // A rig standing in A's first room with the map unsure of it: its display fits
    // A(0) and B(0), so the passive footprint seeds from both.
    private static TrackerWireRig StartInPair(string world)
    {
        TrackerWireRig rig = new(world) { Position = A(0) };
        rig.ShowRoom();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        Assert.StartsWith("active — 2 candidates", rig.Tracker.PassiveGridStatus);
        return rig;
    }

    private static void AssertLocatedAt(TrackerWireRig rig, RoomKey room)
    {
        Assert.Equal(room, rig.Position);
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        Assert.Equal(room, rig.Believed);
    }

    // ----- passive grid locate -------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_PlainWalk_ReanchorsOneStepPastTheSplit(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = echo;

        rig.Do(Token(0));
        rig.Do(Token(1));
        Assert.NotEqual(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        rig.Do(Token(2));

        AssertLocatedAt(rig, A(3));
        Assert.Empty(rig.Failures);
    }

    // A party leader's drag is a move like a typed one: the follow line, then the room.
    [Fact]
    public void PassiveLocate_FollowDrag_FoldsLikeATypedMove()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));

        rig.Drag(Walk[0]);
        rig.Drag(Walk[1]);

        AssertLocatedAt(rig, A(2));
    }

    // a. A text exit (`go path`) is a move: each candidate follows its own exit of
    // that command. Folding nothing left the footprint a room behind the player.
    [Fact]
    public void PassiveLocate_TextExitMove_IsFoldedThroughEachCandidatesOwnExit()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2, textExitAt: 2));

        rig.Do(Token(0));
        rig.Do(Token(1));
        rig.Do("go path");

        AssertLocatedAt(rig, A(3));
    }

    // b. A move the game refused or dropped while the map is unsure is never
    // folded: the `look` after it is a redisplay of the room we never left, which
    // neither steps the footprint nor counts as a strike.
    [Theory]
    [InlineData("There is no exit in that direction!")]
    [InlineData("You can't go that way.")]
    [InlineData("You may not go through this exit!")]
    [InlineData("You are typing too quickly - command ignored")]
    public void PassiveLocate_RefusedMove_IsNotFoldedByTheLookAfterIt(string refusal)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        int strikes = rig.Tracker.State.SuspectStrikes;

        rig.NextAnswers.Enqueue(refusal);
        rig.Do(Token(0));
        rig.Do("l");

        Assert.Equal(A(0), rig.Position);
        Assert.Equal(strikes, rig.Tracker.State.SuspectStrikes);
        Assert.StartsWith("active — 2 candidates, depth 0", rig.Tracker.PassiveGridStatus);

        rig.Do(Token(0));
        AssertLocatedAt(rig, A(1));
    }

    // A move the game answers with nothing the tracker can read: a typed command
    // that prints only the prompt, or a line no refusal detector knows. It must be
    // retired, or every later display is read against the move before its own. Where
    // an echo marks the command being run (Stock), the next command's echo retires it
    // (it was echoed and no display came before the next one). With no echo, or on a
    // realm whose echo timing isn't recorded (Paradigm), a next move after a pause
    // longer than the bound (3.5 s at least) does.
    [Theory]
    [InlineData("enter portal", null, true, true)]
    [InlineData("climb rope", null, true, true)]
    [InlineData("enter portal", null, false, true)]
    [InlineData("enter portal", null, true, false)]
    [InlineData("e", "Something unseen stops you.", true, true)]
    [InlineData("e", "Something unseen stops you.", false, true)]
    [InlineData("e", "Something unseen stops you.", true, false)]
    public void PassiveLocate_AMoveWithNoAnswerTheTrackerReads_IsRetired(string command, string? answer, bool echo, bool stockEcho)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = echo;
        rig.EchoMarksExecution = stockEcho;
        if (answer is not null) rig.NextAnswers.Enqueue(answer);

        rig.Do(command);
        if (!echo || !stockEcho) rig.Clock.Advance(TimeSpan.FromSeconds(4));   // the player waits, then walks on
        for (int step = 0; step < 4; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(4));
    }

    // More unanswered moves than the queue holds (typed quickly, no echo, so none is
    // retired): the passive locate starts over rather than reading every later
    // display against an older move.
    [Fact]
    public void PassiveLocate_MoreUnansweredMovesThanTheQueueHolds_StartsOver()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;

        for (int i = 0; i < 16; i++) rig.Do("enter portal");
        Assert.Equal("idle", rig.Tracker.PassiveGridStatus);

        for (int step = 0; step < 3; step++) rig.Do(Token(step));
        AssertLocatedAt(rig, A(3));
    }

    // a. A typed command the room refuses with a line that also answers ordinary
    // commands ("You do not see that here."), sent just now, is refused as a move.
    // With no echo and no pause, nothing but the refusal can retire it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_ATypedCommandRefusedByTheRoom_IsNotFolded(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = echo;
        rig.NextAnswers.Enqueue("You do not see that here.");

        rig.Do("enter portal");
        for (int step = 0; step < 3; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(3));
    }

    // A command the game didn't echo (it printed only the prompt) is not retired by
    // the next move's echo, so that move's display is read against it and the
    // footprint exhausts. On Stock the move the display really answered was echoed,
    // so its display has come: it is dropped, and the leader's drags that follow
    // (which echo nothing) fold with their own displays, not a move behind.
    [Fact]
    public void PassiveLocate_AnExhaustedFootprint_DropsAMoveAlreadyEchoed()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = false;
        rig.Do("enter portal");
        rig.Echo = true;

        rig.Do(Token(0));
        rig.Drag(Walk[1]);
        rig.Drag(Walk[2]);

        AssertLocatedAt(rig, A(3));
    }

    // The same where an echo marks nothing (Paradigm): the move the display answered
    // went out more than a hop before it, so its display has come, and it is dropped.
    [Fact]
    public void PassiveLocate_AnExhaustedFootprint_DropsAMoveOlderThanAHop()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.EchoMarksExecution = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(1.5);
        rig.Do("enter portal");   // the game prints only the prompt

        for (int step = 0; step < 3; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(3));
    }

    // Three moves typed together, the first landing in a room that shows an exit the
    // map lacks: the footprint exhausts on it. The two moves behind it are still out
    // (not echoed yet, and sent within a hop), so they stay: the next display seeds
    // afresh and the one after folds with its own move.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_AnExhaustedFootprint_KeepsTheMovesStillOut(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = echo;
        rig.ExtraShownExits[A(1)] = new[] { Direction.D };

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Type(Token(2));
        rig.Pump();

        AssertLocatedAt(rig, A(3));
    }

    // A heavy character's hops: two moves typed together, answered 2–3 s apart with no
    // echo. Neither is taken for a move with no answer, each display folds with its
    // own move, and the footprint isn't settled before the second move lands.
    [Theory]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void PassiveLocate_SlowHops_TypedAheadMovesFoldInOrder(double hopSeconds)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(hopSeconds);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.Contains("got no answer"));
    }

    // The same, down to one room on the first display: the second move, landing one
    // slow hop later, is waited for rather than dropped, so the room settled on is
    // the one it reached.
    [Theory]
    [InlineData(2.0)]
    [InlineData(2.4)]
    [InlineData(3.0)]
    public void PassiveLocate_SlowHops_DownToOneWaitsForTheMoveStillOut(double hopSeconds)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        rig.Echo = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(hopSeconds);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.Contains("re-anchoring without them"));
    }

    // A character whose hop the travel model puts at 3.1 s (Paradigm, heavily
    // encumbered): the bound grows with it, so moves answered 5 s apart still fold.
    [Fact]
    public void PassiveLocate_TheBoundGrowsWithTheExpectedHop()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;
        rig.Tracker.SetExpectedHopProbe(() => TimeSpan.FromSeconds(3.1));
        rig.AnswerDelay = TimeSpan.FromSeconds(5);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
    }

    // On a realm where the game might echo a command as it's typed (Paradigm isn't
    // recorded), echoes of moves typed together arrive before either is answered.
    // There the echo retires nothing, so the moves still fold in order.
    [Fact]
    public void PassiveLocate_EchoesAtTypingOnAnUnrecordedRealm_RetireNothing()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.EchoWhenTyped = true;
        rig.EchoMarksExecution = false;

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
    }

    // c. Down to one room, waiting on a move that never got an answer: past the bound
    // (3.5 s at least), the next display re-anchors instead of being read as that move's.
    [Fact]
    public void PassiveLocate_DownToOne_AMoveWithNoAnswerDoesNotHoldTheReanchorForGood()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        rig.Echo = false;

        rig.Type(Token(0));
        rig.Type("enter portal");   // answered with only a prompt
        rig.Pump();
        Assert.NotEqual(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);

        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        rig.Do("l");

        AssertLocatedAt(rig, A(1));
    }

    // c. Two moves sent before either is answered: each display folds with its own
    // move, oldest first.
    [Fact]
    public void PassiveLocate_TwoMovesInFlight_FoldInTheOrderSent()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
    }

    // c. The footprint can come down to one room while a later move is still out.
    // It waits for that move's display, and lands where that move went.
    [Fact]
    public void PassiveLocate_DownToOneWithAMoveStillOut_WaitsForItsDisplay()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.Contains("waiting for 1 move(s) still in flight"));
    }

    // d. A move into a room that shows nothing (dark, or blind) still answers the
    // move in flight: the footprint dead-reckons it, and the next display folds
    // with the move that led to it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_MoveIntoADarkRoomOrBlind_IsDeadReckoned(bool dark)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        if (dark)
        {
            rig.DarkRooms.Add(A(1));
            rig.DarkRooms.Add(B(1));
        }
        else
            rig.NextMoveShows.Enqueue("You are blind.");

        rig.Do(Token(0));
        Assert.StartsWith("active — 2 candidates, depth 1", rig.Tracker.PassiveGridStatus);
        rig.Do(Token(1));

        AssertLocatedAt(rig, A(2));
    }

    // d. Lost, a display no room fits exactly (one of its exits isn't shown) still
    // answered a move, so it folds: the subset match keeps both copies, and the next
    // step separates them.
    [Fact]
    public void PassiveLocate_WhileLost_ADisplayNoRoomFitsExactly_StillFolds()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 4));
        rig.UnshownExits[A(4)] = new[] { Walk[3].Opposite() };

        for (int step = 0; step < 4; step++) rig.Do(Token(step));
        Assert.Equal(RoomConfidence.Lost, rig.Tracker.State.Confidence);
        Assert.StartsWith("active — 2 candidates, depth 4", rig.Tracker.PassiveGridStatus);

        rig.Do(Token(4));
        AssertLocatedAt(rig, A(5));
    }

    // ----- tier-2 forward footprint --------------------------------------

    // Stands in for the loop runner at a landing it didn't expect: it reports the
    // mismatch and carries on with its plan, each step put on the wire as the
    // engines do (announced to the tracker and the gate, then sent).
    private sealed class PlanEngine : IRecoverableEngine
    {
        private readonly TrackerWireRig _rig;
        public PlanEngine(TrackerWireRig rig) => _rig = rig;

        public Queue<Direction> Plan { get; } = new();
        public List<Direction> Backtracks { get; } = new();
        public List<string> Aborts { get; } = new();

        public string Name => "PlanEngine";
        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => Plan.Count > 0 ? Plan.Peek() : null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Plan.Take(count).ToList();

        // As LoopRunner does before every planned step: the gate may hold it, or take
        // over (tier 3). Returns whether the step went out.
        public bool Step()
        {
            if (!_rig.Gate.MayProceedWithPlannedStep()) return false;
            Direction dir = Plan.Dequeue();
            _rig.Tracker.NoteMoveSent(dir);
            _rig.Gate.NoteEngineStepSent(dir);
            _rig.Send(Encoding.Latin1.GetBytes(dir.ToToken() + "\r"));
            return true;
        }

        public void SendBacktrackMove(Direction direction)
        {
            Backtracks.Add(direction);
            _rig.Tracker.NoteMoveSent(direction);
            _rig.Send(Encoding.Latin1.GetBytes(direction.ToToken() + "\r"));
        }

        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) { }
        public void AbortFromRecoveryFailure(string detail) => Aborts.Add(detail);
    }

    private static (TrackerWireRig Rig, PlanEngine Engine) StartForward(string world)
    {
        TrackerWireRig rig = new(world) { Position = A(0) };
        PlanEngine engine = new(rig);
        rig.Tracker.SetLocated(A(0));   // sure of a room two copies share
        rig.ShowRoom();
        rig.Gate.Attach(engine);
        rig.Gate.NoteSuspectedMismatch("landed somewhere the plan didn't expect");
        Assert.Equal(TierLevel.Tier2, rig.Gate.CurrentTier);
        Assert.StartsWith("active — 2 candidates", rig.Gate.Tier2ForwardStatus);
        return (rig, engine);
    }

    // e. Each step, sent once the one before has landed (the gate holds a step the
    // room it would leave from has no exit for), folds with its own landing.
    [Fact]
    public void Tier2Forward_StepsInTurn_EachLandingFoldsItsOwnStep()
    {
        (TrackerWireRig rig, PlanEngine engine) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _ = rig;
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);

        Assert.True(engine.Step());
        rig.Pump();
        Assert.StartsWith("active — 2 candidates, depth 1", rig.Gate.Tier2ForwardStatus);
        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(TierLevel.Tier1, rig.Gate.CurrentTier);
        Assert.Equal(A(2), rig.Gate.Anchor);
        AssertLocatedAt(rig, A(2));
        Assert.Empty(engine.Backtracks);
    }

    // e. A step the tracker can't place (it goes Suspect) still folds, from what its
    // landing showed; and the room the footprint settles on is a deduction, so it
    // isn't written as the profile's saved room.
    [Fact]
    public void Tier2Forward_AStepLandingSuspect_FoldsFromItsDisplay_AndIsNotSavedAsTheAnchor()
    {
        // Really in copy B: its room 2 has an exit A's lacks (shared with a decoy),
        // so the tracker, holding A, can't place the landing.
        TrackerWireRig rig = new(PairWorld(split: 6, decoyAt: 2)) { Position = B(0) };
        using TrackerWireRig _ = rig;
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);   // sure of A(0); really in its twin
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        rig.Gate.NoteSuspectedMismatch("landed somewhere the plan didn't expect");
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);

        Assert.True(engine.Step());
        rig.Pump();
        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(TierLevel.Tier1, rig.Gate.CurrentTier);
        Assert.Equal(B(2), rig.Gate.Anchor);
        AssertLocatedAt(rig, B(2));
        Assert.Equal((A(0).Map, A(0).Room), (profile.LastKnownRoom!.Map, profile.LastKnownRoom.Room));
        Assert.Empty(engine.Backtracks);
    }

    // e. A step the game refuses never landed: the footprint is dropped rather than
    // folding it (the walker sends it again, and two steps would then count as landed
    // on the next landing).
    [Fact]
    public void Tier2Forward_ARefusedStep_DropsTheFootprintRatherThanFoldingIt()
    {
        (TrackerWireRig rig, PlanEngine engine) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _ = rig;
        engine.Plan.Enqueue(Walk[0]);
        rig.NextAnswers.Enqueue("There is no exit in that direction!");

        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(A(0), rig.Position);
        Assert.Equal("idle", rig.Gate.Tier2ForwardStatus);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Tier2.forward-dropped"));
    }

    // e. A teleport breaks the walk the forward footprint folds.
    [Fact]
    public void Tier2Forward_ATeleportStep_DropsTheFootprint()
    {
        (TrackerWireRig rig, _) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _rig = rig;

        rig.Gate.NoteEngineStepSent(Direction.Teleport);

        Assert.Equal("idle", rig.Gate.Tier2ForwardStatus);
    }

    // ----- tier-3 walk-back cap ------------------------------------------

    // Two long corridors of identical "Hall" rooms, one off a Gate (where the walk
    // starts), one off a Postern. No display along them can say which corridor it
    // is, so a walk-back along them never converges.
    private const int CorridorLength = 80;
    private static RoomKey CorridorA(int i) => new(1, 1000 + i);
    private static RoomKey CorridorB(int i) => new(1, 2000 + i);
    private static readonly RoomKey Gate = new(1, 1);

    private static string TwinCorridors()
    {
        List<string> rooms = new()
        {
            TrackerWireRig.Room(1, 1, "Gate", ("E", Ref(CorridorA(0)))),
            TrackerWireRig.Room(1, 2, "Postern", ("E", Ref(CorridorB(0)))),
        };
        (Func<int, RoomKey> At, RoomKey End)[] corridors = { (CorridorA, Gate), (CorridorB, new RoomKey(1, 2)) };
        foreach ((Func<int, RoomKey> at, RoomKey end) in corridors)
            for (int i = 0; i < CorridorLength; i++)
            {
                List<(string, string)> exits = new() { ("W", Ref(i == 0 ? end : at(i - 1))) };
                if (i < CorridorLength - 1) exits.Add(("E", Ref(at(i + 1))));
                RoomKey me = at(i);
                rooms.Add(TrackerWireRig.Room(me.Map, me.Room, "Hall", exits.ToArray()));
            }
        return TrackerWireRig.Rooms(rooms);
    }

    // The walker goes 72 rooms down a corridor before a room shows an exit the map
    // doesn't have. Well past the tier-2 budget, the gate walks back, but the
    // footprint can't converge in twin corridors: it gives up after 64 reverse
    // moves, the cap, rather than reversing all 72, and goes the way an exhausted
    // walk-back goes (the terminal `rm` ask first, then Lost).
    [Fact]
    public void Tier3_WalkBackThatNeverConverges_StopsAtTheCapAndDeclaresLost()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        List<string> terminalAsks = new();
        rig.Gate.TryResyncForced = reason => { terminalAsks.Add(reason); return false; };
        rig.ShowRoom();
        AssertLocatedAt(rig, Gate);
        rig.ExtraShownExits[CorridorA(71)] = new[] { Direction.N };

        Assert.True(rig.Walker.WalkTo(CorridorA(75)));
        rig.Pump();

        int start = rig.Log.Snapshot().ToList().FindIndex(e => e.Message.StartsWith("Tier3.start"));
        Assert.True(start >= 0, "the gate never started the walk-back");
        int reversesSent = rig.Log.Snapshot().Count(e => e.Message.StartsWith("Tier3.backtrack"));
        Assert.Equal(EngineRecoveryGate.Tier3DepthCeiling, reversesSent);
        Assert.Equal(CorridorA(71 - EngineRecoveryGate.Tier3DepthCeiling), rig.Position);

        RecoveryFailedEvent failed = Assert.Single(rig.Failures);
        Assert.Contains($"cap of {EngineRecoveryGate.Tier3DepthCeiling} reverse moves", failed.Detail);
        Assert.Contains(terminalAsks, r => r.Contains($"cap of {EngineRecoveryGate.Tier3DepthCeiling} reverse moves"));
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Tier3.cap:"));
    }
}
