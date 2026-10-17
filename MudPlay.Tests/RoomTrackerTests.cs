using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomTracker's confidence FSM — every state-transition path through
// RoomTracker, including the move-blocked recovery
// and the manual Tier-3 override.
public sealed class RoomTrackerTests : IDisposable
{
    private readonly string _root;

    public RoomTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomtracker-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------

    // 1/1 Town Gates ↔ 1/3 North Square ↔ 1/4 Inn.
    // Town Gates: N → 1/3, E → 1/4 (Door)
    // North Square: S → 1/1
    // Inn: W → 1/1, N → 1/5
    // Cellar (1/5) is name-and-exits unique.
    // 2/1 + 2/3 are an ambiguous-name pair for the Suspect case.
    // 3/1 "Darkwood Forest, Main Road" has a hidden `go path` text exit
    // (S → 3/2) plus visible {SE, W}; 3/2 + 3/3 are an ambiguous
    // "Darkwood Forest" {SW} pair that only the go-path edge disambiguates.
    // 4/1–4/3 are an identically-named-and-exited "Cleared Fields" {N, S}
    // corridor (a 3-cycle) — the ambiguous case where a source re-look
    // display also matches the predicted move target.
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
            "NE": "0", "NW": "0", "SE": "0", "SW": "3/12", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 1, "Name": "Cleared Fields",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "4/2", "S": "4/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 2, "Name": "Cleared Fields",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "4/3", "S": "4/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 3, "Name": "Cleared Fields",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "4/1", "S": "4/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 5, "Room Number": 1, "Name": "Shadowed Vale",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "5/3", "S": "5/2 (Text: go path)", "E": "0", "W": "5/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 5, "Room Number": 2, "Name": "Shadowed Vale",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "5/1", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
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

    // report paradigm-20260921-145800: ObservedExitDirections (the "obvious exits" set
    // the walker consults to skip a redundant `sea`) must describe the room we actually
    // SAW. A real observation sets it; a room change that ISN'T a live observation — a
    // blind/dark dead-reckon advance, an `rm` manual locate, a recovery — clears it, so
    // the walker runs the search a hidden exit needs instead of skipping it on a stale
    // set (which made it ram a wall and thrash).
    [Fact]
    public void ObservedExitDirections_SetByObservation_ClearedByDeadReckonedLocate()
    {
        RoomTracker tracker = NewTracker();

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        Assert.NotNull(tracker.State.ObservedExitDirections);
        Assert.Contains(Direction.N, tracker.State.ObservedExitDirections!);
        Assert.Contains(Direction.E, tracker.State.ObservedExitDirections!);

        // Manual locate to a room we didn't observe — the previous room's exit set is
        // now stale and must be dropped so the walker doesn't trust it.
        tracker.SetLocated(new RoomKey(2, 1));
        Assert.Null(tracker.State.ObservedExitDirections);
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

    [Fact]
    public void PeekArmedDuringPendingMove_ConfirmsMove_ThenDropsPeek()
    {
        // report paradigm-20260813-201720 ("move and l <dir> too fast"): a peek
        // armed the instant after a move must NOT eat the move's confirming
        // display. The server answers in order, so the move's landing arrives
        // before the peek's adjacent-room display — the peek drops the SECOND, not
        // the first. Before the fix, the peek ate the move's landing and the peek's
        // own display then mismatched the pending move → Suspect.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);

        tracker.NoteMoveSent(Direction.N, t0.AddMilliseconds(10));   // predicted target North Square 1/3 {S}
        tracker.NoteLookSent(t0.AddMilliseconds(20));                // peek armed the instant after the move

        // The move's confirming display lands FIRST — it matches the predicted
        // target, so it must confirm the move rather than being dropped as a peek.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t0.AddMilliseconds(1500));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);

        // The look's adjacent-room display lands SECOND — THIS is the peek, and it
        // is dropped without disturbing the just-confirmed position or bumping Suspect.
        tracker.NoteRoomObserved(Obs("Inn", Direction.N, Direction.W), t0.AddMilliseconds(1600));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.Equal(0, tracker.State.SuspectStrikes);
    }

    // `n` then `l e` typed quickly: the consumers that act on a room display before
    // its exits line (the party invites, the combat roster) ask whether the display
    // is a peek. The move was sent before the look and the game has echoed it, so
    // the display arriving is the room just entered; the one after it is the peek.
    [Fact]
    public void PeekArmedDuringPendingMove_TheMovesOwnDisplayIsNotThePeek()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddMilliseconds(10));
        tracker.NoteLookSent(t0.AddMilliseconds(20));
        tracker.NoteInboundMoveEcho("n", t0.AddMilliseconds(900));

        Assert.False(tracker.IsPeekSuppressed(t0.AddMilliseconds(1000)));   // the entered room's lines

        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t0.AddMilliseconds(1100));

        Assert.True(tracker.IsPeekSuppressed(t0.AddMilliseconds(1200)));    // now the peek's
    }

    // A move the game hasn't echoed says nothing: it may never have been answered
    // (a stalled step with a look-sweep peeking past it), so the peek reading stands.
    [Fact]
    public void PeekArmedDuringPendingMove_MoveNotEchoed_StillReadsAsAPeek()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddMilliseconds(10));
        tracker.NoteLookSent(t0.AddMilliseconds(20));

        Assert.True(tracker.IsPeekSuppressed(t0.AddMilliseconds(1000)));
    }

    // A look sent before the move is answered before it.
    [Fact]
    public void PeekArmedBeforeAMove_ThePeekComesFirst()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);
        tracker.NoteLookSent(t0.AddMilliseconds(10));
        tracker.NoteMoveSent(Direction.N, t0.AddMilliseconds(20));
        tracker.NoteInboundMoveEcho("n", t0.AddMilliseconds(30));

        Assert.True(tracker.IsPeekSuppressed(t0.AddMilliseconds(100)));
    }

    // The engines' own steps are not manual; a keystroke is (as is anything else
    // that reaches the wire without an engine announcing it), and a party leader's
    // drag is neither.
    [Fact]
    public void LastMoveWasManual_TellsAKeystrokeFromAnEngineStepOrADrag()
    {
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E), t0);

        tracker.NoteMoveSent(Direction.N, t0);
        Assert.False(tracker.LastMoveWasManual);
        tracker.NoteMoveSentByObserver(Direction.N, t0.AddMilliseconds(5));     // that step's own echo
        Assert.False(tracker.LastMoveWasManual);

        tracker.NoteMoveSentByObserver(Direction.S, t0.AddSeconds(5));
        Assert.True(tracker.LastMoveWasManual);

        tracker.NoteFollowMove(Direction.N, t0.AddSeconds(6));
        Assert.False(tracker.LastMoveWasManual);
    }

    // ----- manual-move signal (pauses navigation) --------------------

    [Fact]
    public void ManualCardinalMove_FiresManualMoveObserved()
    {
        RoomTracker tracker = NewTracker();
        int fired = 0;
        tracker.ManualMoveObserved += _ => fired++;

        // A hand-typed cardinal — no engine pre-announce, so no echo claim to consume.
        tracker.NoteMoveSentByObserver(Direction.N);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ManualMoveObserved_CarriesWhatWasTyped()
    {
        RoomTracker tracker = NewTracker();
        List<string> typed = new();
        tracker.ManualMoveObserved += typed.Add;

        tracker.NoteMoveSentByObserver(Direction.U);
        tracker.NoteMoveSentByObserver("go path");

        Assert.Equal(new[] { "u", "go path" }, typed);
    }

    // A loop waypoint's own command worded like a text exit isn't the user taking over;
    // the same words typed afterwards are.
    [Fact]
    public void EngineCommandWordedAsAMove_NotATypedMove()
    {
        RoomTracker tracker = NewTracker();
        List<string> typed = new();
        tracker.ManualMoveObserved += typed.Add;

        tracker.NoteEngineCommandSent("go path");
        tracker.NoteMoveSentByObserver("go path");
        Assert.Empty(typed);

        tracker.NoteMoveSentByObserver("go path");
        Assert.Equal(new[] { "go path" }, typed);
    }

    [Fact]
    public void EngineCardinalMove_DoesNotFireManualMoveObserved()
    {
        RoomTracker tracker = NewTracker();
        int fired = 0;
        tracker.ManualMoveObserved += _ => fired++;

        // The walker pre-announces (arms the echo claim); its bytes then echo back
        // through the observer and are consumed — not treated as a manual step.
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteMoveSent(Direction.N, t0);
        tracker.NoteMoveSentByObserver(Direction.N, t0.AddMilliseconds(5));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ManualTextExitMove_FiresManualMoveObserved()
    {
        RoomTracker tracker = NewTracker();
        int fired = 0;
        tracker.ManualMoveObserved += _ => fired++;

        tracker.NoteMoveSentByObserver("go path");   // hand-typed text exit, no engine claim

        Assert.Equal(1, fired);
    }

    // ----- Unknown → Confirmed ---------------------------------------

    [Fact]
    public void FirstObservation_OneOfOne_PromotesToConfirmed()
    {
        RoomTracker tracker = NewTracker();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.NotNull(tracker.State.CurrentRoom);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void FirstObservation_Ambiguous_LandsSuspect()
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

    // ----- Confirmed → Pending → Confirmed (confirmed move) ----------

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
    public void PendingMove_UnexpectedButUniqueRoom_RelandsConfirmed()
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

    // A carrier loss usually shows as a move that is never answered. That move stayed
    // queued across the reconnect: the login display of the same room read as a
    // passive re-look, the tracker sat in Pending for good, and a walk started from
    // there waited on a move the server had never seen.
    [Fact]
    public void Reconnect_TheMoveInFlightNeverLanded_ConfirmsWhereWeStand()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);

        tracker.NoteConnectionLost();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));   // the login display

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.HasQueuedMoves);

        // The next move is judged on its own, not against the dead one.
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Reconnect_TheMoveInFlightHadLanded_ConfirmsAtItsTarget()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);

        tracker.NoteConnectionLost();
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.HasQueuedMoves);
    }

    // An identically-named corridor: the display can't say whether the move landed,
    // so the position is marked unsure rather than assumed.
    [Fact]
    public void Reconnect_SourceAndTargetReadTheSame_GoesSuspect_WithNothingQueued()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(4, 1));
        tracker.NoteMoveSent(Direction.N);

        tracker.NoteConnectionLost();
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S));

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.False(tracker.HasQueuedMoves);
    }

    // Nothing in flight at the drop: the login display is an ordinary re-look.
    [Fact]
    public void Reconnect_WithNothingInFlight_ChangesNothing()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));

        tracker.NoteConnectionLost();
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // Without the drop, a same-room display while a move is pending is still only a
    // passive re-look.
    [Fact]
    public void Pending_PassiveSourceRedisplay_StaysPending_ThenRealMoveConfirms()
    {
        // Tracker is Confirmed at Town Gates (1/1). We send N. CONFIRMED game
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
    public void Pending_AmbiguousCorridor_FastSourceRedisplay_StaysPending_ThenRealArrivalConfirms()
    {
        // The double-move bug: in an identically-named corridor the source
        // room and the predicted move target share name AND exits, so a
        // passive re-look redisplay of the source (a post-combat redraw that
        // landed while the move was still in flight) satisfies the predicted-
        // target match too. The tell is the server's command ECHO: a stray
        // re-look carries none, so the tracker stays Pending (not phantom-confirm
        // and fire the next loop step from the source); the move's real, echoed
        // outcome still confirms.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.SetLocated(new RoomKey(4, 1), t0);          // Cleared Fields, {N, S}
        tracker.NoteMoveSent(Direction.N, t0);               // predicted target 4/2 (same name + exits)

        // Source re-look 100ms later — no echo of our "n" yet, so it is not the
        // landing and the tracker stays put.
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S),
            t0.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(4, 1), tracker.State.CurrentRoom!.Key);

        // The server echoes "n"; the next matching display is the real arrival and
        // confirms the move to 4/2.
        tracker.NoteInboundMoveEcho("n", t0.AddMilliseconds(1400));
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S),
            t0.AddMilliseconds(1500));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(4, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Pending_FastFollowDrag_SameNameCorridor_ConfirmsNotReLook()
    {
        // Report paradigm-20260811-122610: a party follower dragged through an
        // identical "Cleared Fields" {N,S} corridor. The game drags a follower with
        // NO byte round-trip, so the confirming display lands almost instantly (8ms
        // in the report) — exactly what the timing-based re-look guard above (the
        // double-move test) discards. But a follow-drag never emits a stray re-look:
        // the game only redisplays on a real arrival, so its fast redisplay IS the
        // move. `NoteFollowMove` flags the pending move exempt from that guard, so it
        // confirms the neighbour instead of stranding the anchor a room back and
        // desyncing down the corridor. Same sequence + timing as the double-move test,
        // opposite outcome — the difference is drag vs. self-typed move.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.SetLocated(new RoomKey(4, 1), t0);          // Cleared Fields, {N, S}
        tracker.NoteFollowMove(Direction.N, t0);            // dragged N → predicted 4/2

        // Same-named neighbour redisplays 100ms later — for a self-typed move this
        // would be held pending an echo, but a follow-drag is exempt (no typed
        // command to echo) and its redisplay is always the real arrival.
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S),
            t0.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(4, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Pending_EchoedMove_SameNameCorridor_ConfirmsEvenWhenFast()
    {
        // The echo short-circuits the old timing hold: once the server has echoed
        // the move, a same-named-neighbour redisplay IS the landing and confirms
        // immediately, no matter how fast it arrives. (Without an echo this same
        // fast redisplay is held — see Pending_AmbiguousCorridor_FastSourceRedisplay.)
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.SetLocated(new RoomKey(4, 1), t0);          // Cleared Fields, {N, S}
        tracker.NoteMoveSent(Direction.N, t0);               // predicted target 4/2 (same name + exits)

        tracker.NoteInboundMoveEcho("n", t0.AddMilliseconds(30));
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S),
            t0.AddMilliseconds(50));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(4, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Pending_FastGoPath_SameNameNeighbour_DifferentExits_Confirms()
    {
        // Report paradigm-20260811-104042: a `go path` from a "Shadowed Vale" {W}
        // into a same-named "Shadowed Vale" {N} redisplayed 164ms later — under the
        // ambiguity floor. The source's CUMULATIVE graph mask covers N (an earlier
        // visit walked it), so BOTH source and predicted target subset-match the {N}
        // redisplay, and the old timing-only guard mistook the real arrival for a
        // passive re-look and stayed Pending forever (the loop sat until the player
        // forced a redisplay). The redisplay's exits {N} DIFFER from the source's
        // last live display {W}, so it's a genuine move — the go-path target confirms.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.SetLocated(new RoomKey(5, 1), t0);            // Shadowed Vale, graph {N,S,W}

        // Source's live display shows only {W} — the last-observation baseline the
        // exit-set discriminator compares against.
        tracker.NoteRoomObserved(Obs("Shadowed Vale", Direction.W), t0);
        Assert.Equal(new RoomKey(5, 1), tracker.State.CurrentRoom!.Key);

        tracker.NoteMoveSent("go path", whenUtc: t0.AddMilliseconds(10));   // → 5/2

        // Fast redisplay of the same-named neighbour with different exits {N}.
        tracker.NoteRoomObserved(Obs("Shadowed Vale", Direction.N), t0.AddMilliseconds(174));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(5, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void PersistCurrentRoomForSave_FlushesPredictedNeighbourPosition()
    {
        // Report paradigm-20260805-224603: on Paradigm's Graveyard every room shares
        // the name "Graveyard", so grinding moves confirm by predicted-neighbour, not
        // a 1-of-1 match — and the mid-session persistence gate deliberately leaves
        // the on-disk anchor at the last strict locate (`rm`). Reopening then landed
        // at that stale anchor. PersistCurrentRoomForSave (wired to ProfileSaving /
        // save-on-close) must stamp the LIVE confirmed room so the next session
        // hydrates where the player actually is. Modelled on the "Cleared Fields"
        // identically-named 3-cycle, the local Graveyard analog.
        RoomTracker tracker = NewTracker();
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(4, 1) };
        tracker.Hydrate(profile);                             // Confirmed at 4/1, anchor 4/1
        Assert.Equal(new RoomKey(4, 1), tracker.State.CurrentRoom!.Key);

        // Move N into the identically-named 4/2 — a predicted-neighbour confirm,
        // gated on the server echoing our "n".
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.NoteMoveSent(Direction.N, t0);
        tracker.NoteInboundMoveEcho("n", t0.AddMilliseconds(1400));
        tracker.NoteRoomObserved(Obs("Cleared Fields", Direction.N, Direction.S),
            t0.AddMilliseconds(1500));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(4, 2), tracker.State.CurrentRoom!.Key);

        // The predicted move did NOT advance the on-disk anchor (mid-session gate).
        Assert.Equal(4, profile.LastKnownRoom!.Map);
        Assert.Equal(1, profile.LastKnownRoom!.Room);

        // A save now flushes the true live position.
        tracker.PersistCurrentRoomForSave();
        Assert.Equal(4, profile.LastKnownRoom!.Map);
        Assert.Equal(2, profile.LastKnownRoom!.Room);
    }

    [Fact]
    public void PersistCurrentRoomForSave_UnknownPosition_LeavesAnchorIntact()
    {
        // A Suspect/Unknown view at save time must not overwrite a good anchor with a
        // guess — the flush is a no-op unless the position is Confirmed. Hydrating an
        // anchor the active graph can't resolve leaves the tracker Unknown.
        RoomTracker tracker = NewTracker();
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(9, 9) };
        tracker.Hydrate(profile);                             // 9/9 not in graph → stays Unknown
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);

        tracker.PersistCurrentRoomForSave();
        Assert.Equal(9, profile.LastKnownRoom!.Map);
        Assert.Equal(9, profile.LastKnownRoom!.Room);
    }

    [Fact]
    public void Pending_FastArrival_DifferentName_ConfirmsRegardlessOfTiming()
    {
        // The timing gate is scoped to the ambiguous case only: it fires
        // solely when the observation ALSO matches the source room. A fast
        // arrival at a differently-named predicted target is a genuine hop
        // and must confirm immediately — otherwise the guard would stall
        // every quick move, not just the corridor re-look.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        tracker.SetLocated(new RoomKey(1, 1), t0);          // Town Gates {N, E}
        tracker.NoteMoveSent(Direction.N, t0);               // predicted target 1/3 North Square {S}

        // Arrival lands 50ms later — under the floor, but names differ, so
        // it is unambiguous and must confirm.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S), t0.AddMilliseconds(50));

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
    public void Confirmed_SameRoomRedisplay_WithFewerVisibleExits_StaysConfirmed()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));        // {N, E}

        // No move sent; re-observe Town Gates with only N visible.
        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- Pending → Confirmed (move blocked) ------------------------

    [Fact]
    public void MoveBlocked_RevertsToConfirmed_AtPreviousRoom()
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

    // ----- Confirmed → another room (silent desync) -----------------

    [Fact]
    public void ConfirmedObservation_DifferentRoom_RelandsViaCandidate()
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
    public void ConfirmedObservation_SameAsCurrent_RefreshesTimestampOnly()
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
        // strike counter but preserves the anchor room — the UI stays on it.
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

    [Fact]
    public void PendingMiss_UniqueNameThroughDoor_ReanchorsInsteadOfStriking()
    {
        // 141605: dark-corridor dead reckoning drifts, then the player surfaces
        // in a uniquely-named lit room whose visible exits are a SUBSET of the
        // graph's (a door dropped a bit). Here the tracker predicted Cellar
        // (Inn + N → 1/5) but actually lands on name-unique "Town Gates" seen
        // with only {N}. The exact (Name, ExitMask) search misses (graph Town
        // Gates is {N,E}); the Pending path used to just bump a Suspect strike
        // and leave the map frozen at the Inn. It now falls back to the same
        // door-tolerant name-unique re-anchor the Confirmed / Suspect paths use
        // and latches Confirmed at 1/1.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 4));    // Inn — N predicts Cellar (1/5)
        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

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
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(3, 1),
            RecentSteps = new List<MudPlay.Models.Profile.DirectionDto>
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

    // ----- blind-move advance (starved display, player-blind not room-dark) --

    [Fact]
    public void BlindMove_PendingMoveToMappedNeighbour_Advances_DoesNotFlagDark()
    {
        // A move made while blinded prints only "You are blind." — no name, no
        // exits — yet the move traverses. Project onto the graph edge exactly
        // like a dark room, but the room isn't dark: IsInDarkRoom stays false.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));       // Town Gates, N → 1/3
        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteBlindMove();

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.IsInDarkRoom);
    }

    [Fact]
    public void BlindMove_MultipleMovesInFlight_LandsAtHead_StaysPending()
    {
        // Two moves queued; the blind line confirms only the head (N → 1/3),
        // leaving the second in flight, so posture stays Pending at the new room.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);           // head — Town Gates → 1/3
        tracker.NoteMoveSent(Direction.E);           // still queued

        tracker.NoteBlindMove();

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.IsInDarkRoom);
    }

    [Fact]
    public void BlindMove_UnmappedEdge_HoldsSource()
    {
        // The pending move has no graph edge out of the source — we can't
        // fabricate a landing, so hold the last anchor and stay Pending.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));       // Town Gates has no S exit
        tracker.NoteMoveSent(Direction.S);

        tracker.NoteBlindMove();

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.IsInDarkRoom);
    }

    [Fact]
    public void BlindMove_WhileConfirmedNoMove_HoldsRoom()
    {
        // A blind echo while standing still (no pending move) carries nothing to
        // advance on — keep the confirmed room intact, never flag darkness.
        RoomTracker tracker = NewTracker();
        tracker.SetLocated(new RoomKey(1, 1));

        tracker.NoteBlindMove();

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
        Assert.False(tracker.IsInDarkRoom);
    }

    [Fact]
    public void BlindMove_AsLookPeek_Dropped_NoAdvance()
    {
        // A look-direction peek while blind renders the same line from the lit
        // room we stand in. The peek window drops it — no advance.
        RoomTracker tracker = NewTracker();
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteLookSent(t);

        tracker.NoteBlindMove(t.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
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
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(1, 1),
        };

        tracker.Hydrate(profile);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Hydrate_UnknownRoomKey_StaysUnknown()
    {
        RoomTracker tracker = NewTracker();
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(999, 999),
        };

        tracker.Hydrate(profile);

        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
    }

    [Fact]
    public void NoteMoveSent_AfterHydrate_PersistsStepToProfile()
    {
        RoomTracker tracker = NewTracker();
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(1, 1),
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
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(2, 2),
            RecentSteps = new List<MudPlay.Models.Profile.DirectionDto>
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
        var profile = new MudPlay.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new MudPlay.Models.Profile.RoomRef(1, 1),
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

    // ----- death capture ---------------------------------------------

    [Fact]
    public void NoteDeath_CapturesDeathpileFromInventorySnapshot()
    {
        RoomTracker tracker = NewTracker();
        var profile = new MudPlay.Models.Profile.CharacterProfile();
        tracker.Hydrate(profile);

        var snapshot = new MudPlay.Game.Inventory.InventorySnapshot(
            MudPlay.Game.Inventory.CurrencyHoldings.Empty,
            MudPlay.Game.Inventory.EncumbranceReading.Empty,
            new[] { new MudPlay.Game.Inventory.EquippedItem("rusty dagger", "Weapon Hand") },
            new[] { "torch", "ration" },
            DateTimeOffset.UtcNow);
        tracker.AttachInventorySnapshot(() => snapshot);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));  // die at 1/1
        tracker.NoteDeath(2, "You now have 2 lives remaining.");

        MudPlay.Models.Profile.DeathRecord rec = Assert.Single(profile.DeathHistory!);
        Assert.Equal(1, rec.Room!.Map);
        Assert.Equal(1, rec.Room!.Room);

        MudPlay.Models.Profile.DeathItem worn = Assert.Single(rec.EquippedAtDeath!);
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
        var profile = new MudPlay.Models.Profile.CharacterProfile();
        tracker.Hydrate(profile);

        tracker.NoteRoomObserved(Obs("Town Gates", Direction.N, Direction.E));
        tracker.NoteDeath(2, "You now have 2 lives remaining.");

        MudPlay.Models.Profile.DeathRecord rec = Assert.Single(profile.DeathHistory!);
        Assert.Null(rec.EquippedAtDeath);
        Assert.Null(rec.LostItems);
    }

    // ----- fear moves ------------------------------------------------

    // 9/1 Wasteland Edge {E→9/2, N→9/3}; 9/2 Black Wasteland {W}; 9/3 Ash Plain {S};
    // 9/4 is a second Black Wasteland {W} elsewhere, so the name alone can't place us.
    private const string FearGraphJson = """
        [
          { "Map Number": 9, "Room Number": 1, "Name": "Wasteland Edge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "9/3", "S": "0", "E": "9/2", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 2, "Name": "Black Wasteland", "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "9/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 3, "Name": "Ash Plain", "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "9/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 4, "Name": "Black Wasteland", "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "9/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 5, "Name": "Cinder Flats", "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "9/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomTracker NewFearTracker(Func<bool> feared)
    {
        Directory.CreateDirectory(Path.Combine(_root, "fear"));
        File.WriteAllText(Path.Combine(_root, "fear", "Rooms.json"), FearGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("fear");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("fear");
        RoomTracker tracker = new(graph);
        tracker.SetFearProbe(feared);
        return tracker;
    }

    // Fear runs us through an obvious exit with no echo. While afraid, a display that
    // matches exactly one neighbour behind those exits is followed there; without fear
    // the same look-alike display can't be placed and drops to Suspect.
    [Fact]
    public void Feared_UnexpectedDisplay_FollowsTheMatchingNeighbour()
    {
        bool feared = true;
        RoomTracker tracker = NewFearTracker(() => feared);
        tracker.NoteRoomObserved(Obs("Wasteland Edge", Direction.E, Direction.N));
        Assert.Equal(new RoomKey(9, 1), tracker.State.CurrentRoom?.Key);

        tracker.NoteRoomObserved(Obs("Black Wasteland", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(9, 2), tracker.State.CurrentRoom?.Key);

        feared = false;
        RoomTracker calm = NewFearTracker(() => feared);
        calm.NoteRoomObserved(Obs("Wasteland Edge", Direction.E, Direction.N));
        calm.NoteRoomObserved(Obs("Black Wasteland", Direction.W));
        Assert.NotEqual(RoomConfidence.Confirmed, calm.State.Confidence);
    }
}
