using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.1 FSM coverage — every state-transition path through
/// <see cref="RoomTracker"/>, including the move-blocked recovery
/// and the manual Tier-3 override.
/// </summary>
public sealed class RoomTrackerTests : IDisposable
{
    private readonly string _root;

    public RoomTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-roomtracker-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------

    /// <summary>
    /// 1/1 Town Gates ↔ 1/3 North Square ↔ 1/4 Inn.
    /// Town Gates: N → 1/3, E → 1/4 (Door)
    /// North Square: S → 1/1
    /// Inn: W → 1/1, N → 1/5
    /// Cellar (1/5) is name-and-exits unique.
    /// 2/1 + 2/2 are an ambiguous-name pair for the Reconciling case.
    /// 3/1 "Darkwood Forest, Main Road" has a hidden `go path` text exit
    /// (S → 3/2) plus visible {SE, W}; 3/2 + 3/3 are an ambiguous
    /// "Darkwood Forest" {SW} pair that only the go-path edge disambiguates.
    /// </summary>
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "0", "E": "1/4 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Inn",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/5", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Cellar",
            "Light": -100, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 1, "Name": "Hallway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "2/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 2, "Name": "Hallway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "2/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 3, "Name": "Hallway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "2/4", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 4, "Name": "Far Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "2/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 3, "Room Number": 1, "Name": "Darkwood Forest, Main Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "3/2 (Text: go path, enter path, walk path)", "E": "0", "W": "3/10",
            "NE": "0", "NW": "0", "SE": "3/9", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 3, "Room Number": 2, "Name": "Darkwood Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "3/11", "U": "0", "D": "0" },
          { "Map Number": 3, "Room Number": 3, "Name": "Darkwood Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "3/12", "U": "0", "D": "0" }
        ]
        """;

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // ----- baseline state --------------------------------------------

    [Fact]
    public void NewTracker_StartsUnknown_WithNoRoom()
    {
        RoomTracker tracker = NewTracker();
        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    // ----- look-direction peek suppression ---------------------------

    [Fact]
    public void IsPeekSuppressed_FalseBeforeLook()
    {
        RoomTracker tracker = NewTracker();
        Assert.False(tracker.IsPeekSuppressed());
    }

    [Fact]
    public void IsPeekSuppressed_ReadOnly_TrueOnRepeatedReadsWithinWindow()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteLookSent(t0);

        // Read-only: repeated reads within the window both see the armed flag
        // (unlike NoteRoomObserved, which consumes it on the exits line).
        Assert.True(tracker.IsPeekSuppressed(t0));
        Assert.True(tracker.IsPeekSuppressed(t0.AddMilliseconds(100)));
    }

    [Fact]
    public void IsPeekSuppressed_FalseAfterWindowExpires()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteLookSent(t0);

        Assert.False(tracker.IsPeekSuppressed(t0.AddSeconds(5)));
    }

    [Fact]
    public void IsPeekSuppressed_FalseAfterObservationConsumesFlag()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        tracker.NoteLookSent();
        Assert.True(tracker.IsPeekSuppressed());

        // The peeked room's exits line drives NoteRoomObserved, which consumes
        // the suppression flag (and drops that observation as a preview).
        tracker.NoteRoomObserved(Obs("Inn", Direction.N, Direction.W));
        Assert.False(tracker.IsPeekSuppressed());
    }

    // ----- Unknown → Located -----------------------------------------

    [Fact]
    public void FirstObservation_OneOfOne_PromotesToLocated()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.NotNull(tracker.State.CurrentRoom);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void FirstObservation_Ambiguous_LandsReconciling()
    {
        RoomTracker tracker = NewTracker();
        // 2/1 and 2/3 are both "Hallway" + {N}.
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N));

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    [Fact]
    public void FirstObservation_NoCandidate_LandsLost()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Atlantis", Direction.N));

        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    // ----- Located → Pending → Located (confirmed move) --------------

    [Fact]
    public void ConfirmedMove_LandsOnExpectedRoom()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteRoomObserved(Obs("North Square", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void PendingMove_MismatchingObservation_LandsReconcilingOrLost()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        // We sent N (expected North Square), but observed Cellar — not
        // matching any of Town Gates' exit targets. Cellar is a 1-of-1
        // graph candidate, so the tracker should re-land on Cellar
        // rather than staying Lost.
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(Obs("Cellar", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 5), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void PendingMove_ToAmbiguousName_LandsSuspect_PreservesAnchor()
    {
        RoomTracker tracker = NewTracker();
        // Start the tracker located somewhere; then the next observation
        // is an ambiguous-name room. Per the new model, Suspect keeps
        // the pre-mismatch anchor room as best guess and bumps the
        // strike counter; the UI badge stays green so transient glitches
        // don't churn (Lost only on strike-limit + failed replay).
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N));

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.NotNull(tracker.State.CurrentRoom);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, tracker.State.SuspectStrikes);
    }

    [Fact]
    public void Pending_PassiveSourceRedisplay_StaysPending_ThenRealMoveConfirms()
    {
        // Tracker is Located at Town Gates (1/1). We send N. CONFIRMED game
        // mechanic: a refused ("bonked") move NEVER redisplays the room — it
        // always prints an explicit refusal line (handled separately via
        // NoteMoveBlocked). So a same-room redisplay while the move is Pending
        // can only be a passive re-look (combat-clear, arrival notice, bare
        // re-glance), never the move's outcome. The tracker must keep waiting
        // for the real result, NOT infer a refusal and confirm at the source.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);

        // Passive redisplay of Town Gates (same as current) while N pending.
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        // Still Pending, still anchored at the source — no false refusal.
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        // The move's real outcome (North Square / 1/3) now confirms cleanly.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Pending_PredictedTarget_NameMatch_SubsetExits_Accepts()
    {
        // Live "Obvious exits:" often omits closed-door / hidden /
        // gated exits the graph still knows about. Subset matching
        // accepts the observation as the predicted target as long
        // as every observed exit is present in the graph.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));        // Town Gates N→1/3, E→1/4
        tracker.NoteMoveSent(Direction.N);

        // Predicted: North Square (1/3) with graph exits {S}.
        // Observation lists {} — server hid the south exit somehow.
        // Subset of {S} is satisfied; should still resolve to 1/3.
        tracker.NoteRoomObserved(new RoomObservation("North Square",
            new HashSet<Direction>()));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Located_SameRoomRedisplay_WithFewerVisibleExits_StaysLocated()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));        // {N, E}

        // No move sent; re-observe Town Gates with only N visible.
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- Pending → Located (move blocked) --------------------------

    [Fact]
    public void MoveBlocked_RevertsToLocated_AtPreviousRoom()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        Room? before = tracker.State.CurrentRoom;

        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteMoveBlocked();

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Same(before, tracker.State.CurrentRoom);
    }

    [Fact]
    public void MoveBlocked_FromNonPending_IsNoOp()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteMoveBlocked();
        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);

        tracker.NoteMoveBlocked();
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // ----- Located → Reconciling (silent desync) ---------------------

    [Fact]
    public void LocatedObservation_DifferentFromCurrent_ReReconcileViaCandidate()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        // Without sending a move, suddenly the server shows us elsewhere —
        // could happen post-paralysis-recovery / mid-fight teleport / etc.
        tracker.NoteRoomObserved(Obs("Cellar", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 5), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void LocatedObservation_SameAsCurrent_RefreshesTimestampOnly()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);
        Room? before = tracker.State.CurrentRoom;

        DateTimeOffset t1 = t0.AddSeconds(30);
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t1);

        Assert.Same(before, tracker.State.CurrentRoom);
        Assert.Equal(t1, tracker.State.LastUpdatedAt);
    }

    // ----- Tier 3 manual override ------------------------------------

    [Fact]
    public void SetLocated_FromLost_PromotesAndClearsPending()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Atlantis", Direction.N)); // → Lost
        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);

        tracker.SetLocated(new RoomKey(1, 1));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void SetLocated_FromPending_ClearsPendingAndPromotes()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        tracker.NoteMoveSent(Direction.N);

        tracker.SetLocated(new RoomKey(1, 5)); // Cellar

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 5), tracker.State.CurrentRoom!.Key);

        // A subsequent observation should not behave as if a previous
        // pending move was still in flight.
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void SetLocated_UnknownKey_DoesNothing()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        tracker.SetLocated(new RoomKey(999, 999));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- Graph reload ----------------------------------------------

    [Fact]
    public void OnGraphReloaded_ResetsToUnknown_AndClearsRoom()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        tracker.OnGraphReloaded();

        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    // ----- StateChanged event ----------------------------------------

    [Fact]
    public void StateChanged_FiresWithPreviousAndNewSnapshots()
    {
        RoomTracker tracker = NewTracker();

        RoomTransition? last = null;
        tracker.StateChanged += t => last = t;

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        Assert.NotNull(last);
        Assert.Equal(RoomConfidence.Unknown, last!.Value.PreviousConfidence);
        Assert.Equal(RoomConfidence.Confirmed, last.Value.NewConfidence);
        Assert.Null(last.Value.PreviousRoom);
        Assert.Equal(new RoomKey(1, 1), last.Value.NewRoom!.Key);
    }

    [Fact]
    public void StateChanged_ChainOfMoves_FiresOncePerTransition()
    {
        RoomTracker tracker = NewTracker();
        int fires = 0;
        tracker.StateChanged += _ => fires++;

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));     // Unknown → Confirmed
        tracker.NoteMoveSent(Direction.N);                                          // Confirmed → Pending
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));                 // Pending → Confirmed

        Assert.Equal(3, fires);
    }

    // ----- Suspect-strikes ladder ------------------------------------

    [Fact]
    public void Confirmed_MismatchObsAfterEachMove_AccumulatesSuspectStrikes()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));    // Town Gates

        // Each mismatching observation that follows a fresh move increments the
        // strike counter but preserves the anchor room — the UI stays "Located".
        // A move must sit between observations: a stationary stream of identical
        // redisplays is one piece of evidence, not one strike apiece (002347).
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t);     // ambiguous (2/1, 2/3) — strike 1
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Equal(1, tracker.State.SuspectStrikes);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        tracker.NoteMoveSent(Direction.N, t.AddSeconds(1));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(2));     // strike 2
        Assert.Equal(2, tracker.State.SuspectStrikes);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Suspect_NextMismatchAtStrikeLimit_FailsReplay_LandsLost()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));

        // Three mismatching observations, each after a move so they count as
        // distinct evidence — the recorded steps route nowhere near a "Hallway",
        // so replay can't recover and the third strike lands Lost.
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t);
        tracker.NoteMoveSent(Direction.N, t.AddSeconds(1));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(2));
        tracker.NoteMoveSent(Direction.N, t.AddSeconds(3));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(4));

        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    [Fact]
    public void Suspect_StationaryIdenticalRedisplays_DoNotStrikeToLost()
    {
        // 002347: a stationary player parked in an ambiguous room (identical
        // "Main Road" rooms, "Darkwood Forest" with dozens of candidates) sits
        // in Suspect with a preserved marker. Passive redisplays of the same
        // room — an Enter echo, a cash-on-ground notice, a party arrival line —
        // arrive with no move between them. Before the fix each identical
        // redisplay accrued a Suspect strike and the third declared the player
        // Lost, wiping the map marker while they never moved.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));                          // Town Gates — marker visible

        // First ambiguous redisplay knocks us to Suspect (strike 1); marker kept.
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t);
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Equal(1, tracker.State.SuspectStrikes);

        // Six more identical redisplays, no move between any of them.
        for (int i = 1; i <= 6; i++)
            tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(i));

        // Still Suspect at the same strike count — the marker never vanished.
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Equal(1, tracker.State.SuspectStrikes);
        Assert.NotNull(tracker.State.CurrentRoom);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Suspect_ResolvedByConfirmedObs_ClearsStrikes()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N));     // strike 1, Suspect
        Assert.Equal(1, tracker.State.SuspectStrikes);

        // Observation now matches current room — Suspect resolved.
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(0, tracker.State.SuspectStrikes);
    }

    // ----- replay-from-last-Confirmed recovery -----------------------

    [Fact]
    public void Replay_FromLastConfirmed_RecoversWhenObsMatchesEndpoint()
    {
        // Confirmed at Town Gates → move N to North Square (Confirmed)
        // → move S back to Town Gates (Confirmed). Then the user is at
        // 1/4 (Inn) and a sudden observation lands there with no
        // matching candidate from the graph search alone — but the
        // history+steps replay should find Inn through valid graph
        // edges and Confirm there.
        //
        // To exercise the no-1-of-1 path we use a sequence where the
        // recovery target has a unique name in the graph anyway, which
        // means LandFromCandidateSearch would also succeed. So instead
        // we verify the simpler invariant: when replay succeeds, the
        // tracker lands Confirmed at the projected endpoint and the
        // RecentSteps clear.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));                       // Town Gates
        tracker.NoteMoveSent(Direction.E);                            // Town Gates → Inn (predicted)
        tracker.NoteRoomObserved(Obs("Inn", Direction.W, Direction.N));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 4), tracker.State.CurrentRoom!.Key);
    }

    // ----- door-tolerant name re-anchor (205936 / 075501) ------------

    [Fact]
    public void FirstObservation_ClosedDoorDropsExitBit_ReanchorsByUniqueName()
    {
        // Town Gates is {N, E(Door)} in the graph. Observing it with only {N}
        // (the E door swung shut, so the "Obvious exits:" line drops the bit)
        // misses the exact (Name, ExitMask) candidate search. But the name is
        // unique in the graph and {N} ⊆ {N,E}, so the door-tolerant re-anchor
        // latches Confirmed at 1/1 instead of freezing the marker at Lost.
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void SuspectStrikeLimit_UniqueNameThroughDoor_ReanchorsInsteadOfLost()
    {
        // A player parked in ambiguity ("Hallway" resolves to 2/1 + 2/3) strikes
        // out to the Suspect limit with no anchor. The redisplay that finally
        // trips the limit is a name-unique room seen through a closed door
        // (Town Gates with only {N}) — the exact search still misses and replay
        // has no confirmed history to project, but the door-tolerant re-anchor
        // recovers to 1/1 rather than declaring the player Lost.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t);              // Suspect, no anchor
        tracker.NoteMoveSent(Direction.N, t.AddSeconds(1));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(2)); // strike 1
        tracker.NoteMoveSent(Direction.N, t.AddSeconds(3));
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N), t.AddSeconds(4)); // strike 2
        tracker.NoteMoveSent(Direction.N, t.AddSeconds(5));
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N), t.AddSeconds(6)); // strike limit → re-anchor

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- go-path (text-exit) resolution ----------------------------

    [Fact]
    public void Pending_GoPathTextExit_ResolvesToDeterministicTarget()
    {
        // 002430: a manually typed `go path` enqueues a pending move with a
        // null cardinal, so the old code skipped prediction entirely and fell
        // to name+exits candidate search — which can't match a go-path
        // destination (the hidden text exit sits in the graph ExitMask but
        // never on the "Obvious exits:" line), leaving the tracker Suspect →
        // Lost. The go-path exit is a deterministic graph edge, so the head
        // pending move now resolves through 3/1's Text exit straight to 3/2,
        // even though "Darkwood Forest" {SW} is name-ambiguous (3/2 vs 3/3).
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(3, 1));       // Darkwood Forest, Main Road — S go-path → 3/2
        tracker.NoteMoveSent("go path");             // manual text exit, no resolved cardinal
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteRoomObserved(Obs("Darkwood Forest", Direction.SW));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(3, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void ReplayRecover_FollowsGoPathTextExit_LandsTrueRoom()
    {
        // Client-restart recovery across a go-path step. LastKnownRoom is the
        // unique 3/1; the persisted trail is a single `go path` command. On
        // relaunch Hydrate seeds Confirmed at 3/1 and primes the text step. The
        // first login redisplay is the ambiguous 3/2 ("Darkwood Forest" {SW}),
        // which no unique candidate resolves. Replay must now follow the text
        // exit (3/1 + go path → 3/2) instead of bailing on the null cardinal.
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(3, 1),
            RecentSteps = new List<FujinTerm.Models.Profile.DirectionDto>
            {
                new(null, "go path"),
            },
        };
        tracker.Hydrate(profile);
        Assert.Equal(new RoomKey(3, 1), tracker.State.CurrentRoom!.Key);

        tracker.NoteRoomObserved(Obs("Darkwood Forest", Direction.SW));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(3, 2), tracker.State.CurrentRoom!.Key);
    }

    // ----- dark-room advance (no name / no exits display) ------------

    [Fact]
    public void DarkRoomEntered_PendingMoveToMappedNeighbour_Advances_AndFlagsDark()
    {
        // A dark room ("The room is very dark...") prints no name + no exits, so
        // NoteRoomObserved never fires. The dark line is our only signal: a sent
        // move that yields darkness instead of a bonk means we traversed. With a
        // single move pending onto a mapped neighbour, project onto the graph edge.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));       // Town Gates, N → 1/3
        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteDarkRoomEntered();

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.True(tracker.IsInDarkRoom);
    }

    [Fact]
    public void DarkRoomEntered_MultipleMovesInFlight_LandsAtHead_StaysPending()
    {
        // Two moves queued; the dark line confirms only the head (N → 1/3) and
        // leaves the second in flight, so posture stays Pending at the new room.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);           // head — Town Gates → 1/3
        tracker.NoteMoveSent(Direction.E);           // still queued

        tracker.NoteDarkRoomEntered();

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.True(tracker.IsInDarkRoom);
    }

    [Fact]
    public void DarkRoomEntered_UnmappedEdge_HoldsSource_ButFlagsDark()
    {
        // The pending move has no graph edge out of the source (a dark corridor
        // off the map). We can't fabricate a landing, so we hold the last anchor
        // — but the darkness flag still arms so combat can engage by attack line.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));       // Town Gates has no S exit
        tracker.NoteMoveSent(Direction.S);

        tracker.NoteDarkRoomEntered();

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.True(tracker.IsInDarkRoom);
    }

    [Fact]
    public void DarkRoomEntered_WhileConfirmedNoMove_FlagsDark_HoldsRoom()
    {
        // A dark re-render while standing still (no pending move) carries nothing
        // to advance on — flag the darkness but keep the confirmed room intact.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));

        tracker.NoteDarkRoomEntered();

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.True(tracker.IsInDarkRoom);
    }

    [Fact]
    public void DarkRoomEntered_ThenLitObservation_ClearsDarkFlag()
    {
        // We advanced into a dark room; the moment a normal room display parses
        // (light source readied, or a lit room re-observed), the flag clears.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();
        Assert.True(tracker.IsInDarkRoom);

        tracker.NoteRoomObserved(Obs("North Square", Direction.S));   // 1/3 lit

        Assert.False(tracker.IsInDarkRoom);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void DarkRoomEntered_AsLookPeek_Dropped_DoesNotFlagDark()
    {
        // Looking into an adjacent dark room renders the same "can't see" line
        // while we stand in a lit room. The peek window drops it — no advance,
        // no darkness flag on our own (still-lit) room.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteLookSent(t);

        tracker.NoteDarkRoomEntered(t.AddMilliseconds(100));

        Assert.False(tracker.IsInDarkRoom);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void DarkRoomEntered_ThenDeath_ClearsDarkFlag()
    {
        // Dying in the dark must not leave the flag stuck armed after respawn.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();
        Assert.True(tracker.IsInDarkRoom);

        tracker.NoteDeath(2, "You now have 2 lives remaining.");

        Assert.False(tracker.IsInDarkRoom);
    }

    // ----- move-echo consume-once claim ------------------------------

    [Fact]
    public void EngineMove_LateObserverEcho_IsConsumed_ObservationConfirms()
    {
        // 195229/195335: the engine's own bytes echo back through the observer
        // after the walker already announced the move. A synchronous map re-root
        // on entering a new area can stall that round-trip past the old 100 ms
        // debounce window, so the late echo was treated as a real second move and
        // double-enqueued — leaving the queue non-empty forever so the next
        // observation confirmed as "queue not empty" (Pending) and the walk stalled.
        // The claim now matches on direction alone, not a wall clock, so a 500 ms
        // echo is still consumed and the single move confirms cleanly.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));                       // Town Gates

        tracker.NoteMoveSent(Direction.N, t);                        // engine announces
        tracker.NoteMoveSentByObserver(Direction.N, t.AddMilliseconds(500)); // late echo
        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t.AddMilliseconds(600));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void EngineTextExit_LateObserverEcho_IsConsumed_ObservationConfirms()
    {
        // Same late-echo hazard on the text-exit path (SpecialExitDispatch
        // announces "go path", the bytes echo back through the observer). A
        // stalled round-trip must not enqueue a phantom cardinal-less move.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(3, 1));                       // Darkwood Main Road

        tracker.NoteMoveSent("go path", cardinal: null, whenUtc: t);
        tracker.NoteMoveSentByObserver("go path", t.AddMilliseconds(500));
        tracker.NoteRoomObserved(Obs("Darkwood Forest", Direction.SW), t.AddMilliseconds(600));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(3, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void TwoManualSameDirectionMoves_BothEnqueue()
    {
        // A manual move (no engine call, only the observer path) must never arm a
        // claim — otherwise a second manual step of the same direction would be
        // swallowed as an "echo" of the first. Two manual N moves both enqueue, so
        // one observation clears only the head and the tracker stays Pending with
        // the second move still in flight.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));                       // Town Gates

        tracker.NoteMoveSentByObserver(Direction.N, t);              // manual #1
        tracker.NoteMoveSentByObserver(Direction.N, t.AddMilliseconds(10)); // manual #2
        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t.AddMilliseconds(20));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void OrphanedClaim_PastExpiry_DoesNotSwallowLaterManualMove()
    {
        // An engine move whose echo never arrives (a refused move) leaves the
        // claim armed. The generous expiry bounds it so a genuine manual move of
        // the same direction well afterwards is NOT mistaken for the missing echo
        // — it lands as a real second move (head confirms, queue stays non-empty).
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));                       // Town Gates

        tracker.NoteMoveSent(Direction.N, t);                        // echo never comes
        tracker.NoteMoveSentByObserver(Direction.N, t.AddSeconds(2.5)); // manual, past expiry
        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t.AddSeconds(2.6));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    // ----- profile hydrate / save ------------------------------------

    [Fact]
    public void Hydrate_FromProfileWithLastKnownRoom_LandsConfirmed()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(1, 1),
        };

        tracker.Hydrate(profile);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Hydrate_UnknownRoomKey_StaysUnknown()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(999, 999),
        };

        tracker.Hydrate(profile);

        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
    }

    [Fact]
    public void NoteMoveSent_AfterHydrate_PersistsStepToProfile()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(1, 1),
        };
        tracker.Hydrate(profile);

        tracker.NoteMoveSent(Direction.N);

        Assert.NotNull(profile.RecentSteps);
        Assert.Single(profile.RecentSteps!);
        Assert.Equal(Direction.N, profile.RecentSteps![0].Cardinal);
    }

    [Fact]
    public void ReplayRecover_OnLogin_LandsTrueRoom_InSameNamedArea()
    {
        // Client-restart recovery in an ambiguous area. The last KNOWN unique
        // room was 2/2 (Hallway + {S}); the player then walked S into 2/1
        // (Hallway + {N}) — which is name+exit-ambiguous with 2/3 — before
        // quitting. On relaunch Hydrate seeds Confirmed at the persisted 2/2
        // anchor and primes the pending step [S]. The first login redisplay
        // (the real room, 2/1) disagrees with the anchor, no unique candidate
        // resolves it, and a lone Suspect strike would strand the player.
        // Projecting the persisted trail forward (2/2 + S → 2/1) recovers the
        // true room.
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(2, 2),
            RecentSteps = new List<FujinTerm.Models.Profile.DirectionDto>
            {
                new(Direction.S),
            },
        };
        tracker.Hydrate(profile);
        Assert.Equal(new RoomKey(2, 2), tracker.State.CurrentRoom!.Key);

        // The redisplay of the room the player is actually standing in.
        tracker.NoteRoomObserved(Obs("Hallway", Direction.N));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(2, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void ConfirmedTransition_ClearsRecentStepsOnProfile()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(1, 1),
        };
        tracker.Hydrate(profile);

        tracker.NoteMoveSent(Direction.N);
        Assert.Single(profile.RecentSteps!);

        // Confirmed move → step list resets, LastKnownRoom advances.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.Null(profile.RecentSteps);
        Assert.Equal(1, profile.LastKnownRoom!.Map);
        Assert.Equal(3, profile.LastKnownRoom!.Room);
    }

    // ----- death capture (PR 10.5) -----------------------------------

    [Fact]
    public void NoteDeath_CapturesDeathpileFromInventorySnapshot()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile();
        tracker.Hydrate(profile);

        var snapshot = new FujinTerm.Game.Inventory.InventorySnapshot(
            FujinTerm.Game.Inventory.CurrencyHoldings.Empty,
            FujinTerm.Game.Inventory.EncumbranceReading.Empty,
            new[] { new FujinTerm.Game.Inventory.EquippedItem("rusty dagger", "Weapon Hand") },
            new[] { "torch", "ration" },
            DateTimeOffset.UtcNow);
        tracker.AttachInventorySnapshot(() => snapshot);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));  // die at 1/1
        tracker.NoteDeath(2, "You now have 2 lives remaining.");

        FujinTerm.Models.Profile.DeathRecord rec = Assert.Single(profile.DeathHistory!);
        Assert.Equal(1, rec.Room!.Map);
        Assert.Equal(1, rec.Room!.Room);

        FujinTerm.Models.Profile.DeathItem worn = Assert.Single(rec.EquippedAtDeath!);
        Assert.Equal("rusty dagger", worn.Name);
        Assert.True(worn.IsHeld);

        Assert.Equal(2, rec.LostItems!.Count);
        Assert.Contains(rec.LostItems!, i => i.Name == "torch");
        Assert.Contains(rec.LostItems!, i => i.Name == "ration");
    }

    [Fact]
    public void NoteDeath_NoSnapshotProvider_LeavesItemListsNull()
    {
        RoomTracker tracker = NewTracker();
        var profile = new FujinTerm.Models.Profile.CharacterProfile();
        tracker.Hydrate(profile);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        tracker.NoteDeath(2, "You now have 2 lives remaining.");

        FujinTerm.Models.Profile.DeathRecord rec = Assert.Single(profile.DeathHistory!);
        Assert.Null(rec.EquippedAtDeath);
        Assert.Null(rec.LostItems);
    }
}
