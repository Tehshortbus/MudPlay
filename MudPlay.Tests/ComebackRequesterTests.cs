using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Follower-side ComebackRequester: one @comeback to the leader when the party walks
// off without us, and none for an uninvite, a `leave`, a move of our own, a death or
// a teleport split (GAME_MECHANICS "@comeback (follower → leader)").
public sealed class ComebackRequesterTests : IDisposable
{
    private readonly string _root;

    public ComebackRequesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-comebackreq-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 has a plain exit north and an exit east that casts a spell on the walk.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/3 (Cast: pre-0, post-857)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The same rooms with a hole in 1/1: its command 100 is `go hole`, a teleport
    // to 1/4, which no exit reaches.
    private const string HoleGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "CMD": 100, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/3 (Cast: pre-0, post-857)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Below",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string HoleTbInfo = """
        [ { "Number": 100, "LinkTo": 0,
            "Action": "go hole:teleport 4 1\n",
            "Called From": "Room 1/1" } ]
        """;

    private const string ItemRefusal = "You do not have the appropriate item to go that direction!";
    private const string ParHeader = "The following people are in your travel party:";
    private const string BossFullRow = "  Boss Hogg                         (Warrior)                 [H:100%]   - Frontrank";
    private const string BossInvited = "  Boss Hogg                      (Warrior)    [Invited]";
    private const string OtherInvited = "  Other Guy                      (Mage)       [Invited]";

    // The wait for the leader to come: the requester's one timer of a minute or more.
    private static TimeSpan? Waiting(Harness h) =>
        h.Timers.PendingDelays.Where(d => d >= TimeSpan.FromMinutes(1)).Select(d => (TimeSpan?)d).LastOrDefault();

    private static void LapseTheWait(Harness h)
    {
        if (Waiting(h) is { } wait) h.Timers.RunDue(wait);
    }

    private sealed class Harness : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required PartyState Party { get; init; }
        public required ComebackRequester Requester { get; init; }
        // The requester's settle, retry and wait timers.
        public required ManualTimers Timers { get; init; }

        // The two things that move the map on these lines in the app: the follow
        // line books a drag, and a refusal the detector knows takes a move back.
        public required FollowMoveObserver FollowObserver { get; init; }
        public required MovementRefusalDetector RefusalDetector { get; init; }

        public bool MasterSwitchOn { get; set; } = true;
        public bool SelfDown { get; set; }
        public string? SendBlocked { get; set; }
        public bool InTrainTrip { get; set; }

        // Everything the requester (and whatever else a test binds) put on the wire.
        public List<string> Wire { get; } = new();
        // The requester's own part of it.
        public List<string> RequesterWire { get; } = new();

        private DateTimeOffset _clock = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

        // Advance the deterministic clock the requester reads.
        public void Advance(TimeSpan by) => _clock += by;

        public DateTimeOffset Now => _clock;

        public int Sent => RequesterWire.Count;

        public string? LastWire => RequesterWire.Count == 0 ? null : RequesterWire[^1];

        // The game's output through the client's input path: the router first and
        // the refusal detector after it, the order the app hands a line on in.
        public required ServerFeed Server { get; init; }

        public void Feed(string text) => Server.Line(text);

        public void Type(string line) => Requester.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"));

        public void Dispose()
        {
            Requester.Dispose();
            FollowObserver.Dispose();
            RefusalDetector.Dispose();
        }
    }

    // Solo, and not yet located: the state the "no longer following" line alone
    // speaks for.
    private Harness NewHarness(Func<bool>? isMovementPrevented = null, bool holeInA = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), holeInA ? HoleGraphJson : GraphJson);
        if (holeInA) File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), HoleTbInfo);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        PartyState party = new();
        FollowMoveObserver followObserver = new(router, tracker);
        Harness? h = null;
        ServerFeed server = new(router, clock: () => h!.Now);
        MovementRefusalDetector refusalDetector = new(server.Lines, tracker);

        ManualTimers timers = new();
        ComebackRequester requester = new(router, tracker,
            isMovementPrevented: isMovementPrevented,
            party: party,
            isSelfDown: () => h!.SelfDown,
            sendBlocked: () => h!.SendBlocked,
            inTrainTrip: _ => h!.InTrainTrip,
            now: () => h!.Now,
            schedule: timers.Arm);
        h = new Harness
        {
            Router = router,
            Tracker = tracker,
            Party = party,
            Requester = requester,
            Timers = timers,
            FollowObserver = followObserver,
            RefusalDetector = refusalDetector,
            Server = server,
        };
        requester.MasterSwitchOff = () => !h.MasterSwitchOn;
        requester.SetWireSender(b =>
        {
            string line = Encoding.Latin1.GetString(b).TrimEnd('\r');
            h.RequesterWire.Add(line);
            h.Wire.Add(line);
        });
        return h;
    }

    // Following Boss, standing in 1/1.
    private Harness NewFollower(Func<bool>? isMovementPrevented = null, bool holeInA = false)
    {
        Harness h = NewHarness(isMovementPrevented, holeInA);
        h.Party.IsInParty = true;
        h.Party.SelfIsLeader = false;
        h.Party.LeaderName = "Boss";
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        return h;
    }

    // ----- left-behind fires ------------------------------------------

    [Fact]
    public void StuckThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following MudPlay.");

        Assert.Equal("/MudPlay @comeback 1/1", h.LastWire);
    }

    // Stock's line for a stunned follower, printed before the same "no longer
    // following" line as the held one.
    [Fact]
    public void StunnedThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You are too stunned to move anywhere!");
        h.Feed("You are no longer following MudPlay.");

        Assert.Equal("/MudPlay @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void HeavyThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("Your pack is too heavy to move.");
        h.Feed("You are no longer following MudPlay.");

        Assert.Equal("/MudPlay @comeback 1/1", h.LastWire);
    }

    // The game's own two wordings: a follow move's refusal on Stock, and a typed
    // move's (GAME_MECHANICS "Too heavy to move (over max encumbrance)").
    [Theory]
    [InlineData("You are too heavy to move!")]
    [InlineData("You are too heavy to move anywhere!")]
    public void TheGamesTooHeavyLines_ThenNoLongerFollowing_SendComeback(string line)
    {
        using Harness h = NewFollower();

        h.Feed(line);
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void Confirmed_NoRoom_SendsBareComeback()
    {
        using Harness h = NewHarness();
        // No SetLocated → tracker confidence stays Unknown → bare @comeback.
        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following MudPlay.");

        Assert.Equal("/MudPlay @comeback", h.LastWire);
    }

    [Fact]
    public void RoomOnlyPredicted_SendsBareComeback()
    {
        using Harness h = NewFollower(isMovementPrevented: () => true);
        // A drag still in flight when a hold ends the follow: the room is a
        // prediction, not a place to send the leader.
        h.Tracker.NoteFollowMove(Direction.N, h.Now);

        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback", h.LastWire);
    }

    [Fact]
    public void KnockedDownThenNoLongerFollowing_SendsComeback()
    {
        // A knockdown / held affliction produces "You are flat on your back!", NOT one of
        // the two movement-failure lines — so without the live movement-prevented check the
        // break reads as deliberate. With it, a break while movement-prevented telepaths
        // @comeback (report paradigm-20260922-085609).
        using Harness h = NewHarness(isMovementPrevented: () => true);
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You are no longer following MudPlay.");

        Assert.Equal("/MudPlay @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void ExitRefusalThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewFollower();

        h.Feed(ItemRefusal);
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("appropriate item", h.Requester.LastIncidentSummary);
    }

    // The monsters' parting attack held us mid-move: the follow ends with no
    // refusal line, right behind the leader's own departure. The verdict waits for
    // the prompt, which is what shows a drop.
    [Fact]
    public void LeaderLeftThenNoLongerFollowing_SendsOnceThePromptIsIn()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Feed("You are no longer following Boss.");
        Assert.Null(h.LastWire);

        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // The same two lines when the parting attack dropped us: the HP that says so
    // comes with the prompt after them.
    [Fact]
    public void LeaderLeftThenNoLongerFollowing_ThenWeAreDown_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Feed("You are no longer following Boss.");
        h.SelfDown = true;
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("we died or dropped", h.Requester.LastIncidentSummary);
    }

    // ----- the three screens a follower sees (Paradigm, 2026-10-10) --------

    // The leader showed its mine pass and went in; this follower has none. The
    // follow line prints, the exit refuses, and the follow ends.
    [Fact]
    public void Screen_GatedAtAnExit_SendsOneComeback()
    {
        using Harness h = NewFollower();

        h.Feed("Other just left to the east.");
        h.Feed("-- Following your Party leader east --");
        h.Feed("You don't have a mine pass, so you can't enter the mines.");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal(new[] { "/Boss @comeback 1/1" }, h.Wire);
    }

    // The same shape with a refusal the client has never seen: the follow line,
    // no arrival, and the follow ends. The wording is the exit's own. The map had
    // booked the drag off the follow line and no refusal it knows took it back:
    // the request still names the room we stand in.
    [Fact]
    public void Screen_GatedAtAnExit_WithWordingNobodyKnows_SendsOneComeback()
    {
        using Harness h = NewFollower();

        h.Feed("Other just left to the north.");
        h.Feed("-- Following your Party leader north --");
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        h.Feed("The doorman looks you up and down and shakes his head.");
        h.Feed("You are no longer following Boss.");
        Assert.Null(h.LastWire);                            // waits for the prompt
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal(new[] { "/Boss @comeback 1/1" }, h.Wire);
        Assert.Contains("never arrived", h.Requester.LastIncidentSummary);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    // Followed into a room that shows nothing, then uninvited two seconds on: the
    // dark line was the arrival.
    [Theory]
    [InlineData("The room is pitch black - you can't see anything!")]
    [InlineData("The room is very dark - you can't see anything.")]
    [InlineData("You are blind.")]
    [InlineData("You are blind!")]   // a room too bright to see in (Probe3_S2b)
    public void FollowedIntoARoomThatShowsNothing_ThenUninvited_SendsNothing(string arrival)
    {
        using Harness h = NewFollower();

        h.Feed(" -- Following your Party leader north --");
        h.Feed(arrival);
        h.Advance(TimeSpan.FromSeconds(2));
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // `set follow blind` shows no room at all for a follow move. With nothing said
    // between the follow line and the end of the follow, no follow failed.
    [Fact]
    public void FollowLine_ThenNothing_ThenUninvited_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed(" -- Following your Party leader north --");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // A room the map couldn't place is still a room: its exits line is the arrival.
    [Fact]
    public void FollowLine_ARoomIsShown_ThenUninvited_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed(" -- Following your Party leader north --");
        h.Feed("Somewhere The Map Has Never Seen");
        h.Feed("Obvious exits: up, down.");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // The leader relayed a teleport keyword, a follow line and a line came, and the
    // follow ended: that is the leader's teleport, not an exit that turned us away.
    [Fact]
    public void FollowShape_BehindARelayedTeleport_IsTheLeadersSplit()
    {
        using Harness h = NewFollower(holeInA: true);

        h.Requester.NotePartyRelay("Boss", "go hole");
        h.Advance(TimeSpan.FromSeconds(4));
        h.Feed(" -- Following your Party leader north --");
        h.Feed("The chime's note hangs in the air.");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("party teleport", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void FollowLine_ThenTypedLeave_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed(" -- Following your Party leader north --");
        h.Type("leave party");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // A follow line whose move was made is no failed follow, however soon after
    // it the party is ended.
    [Fact]
    public void FollowLine_ThenArrival_ThenUninvited_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("-- Following your Party leader north --");
        h.Tracker.SetLocated(new RoomKey(1, 2), h.Now);
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // The follower leaves by its own command.
    [Fact]
    public void Screen_LeaveParty_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Type("leave party");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("left the party by command", h.Requester.LastIncidentSummary);
    }

    // The leader typed `uninvite <name>`: the follower sees the one line, with
    // nothing of its own before it.
    [Fact]
    public void Screen_Uninvited_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // Having asked for the pickup is the consent to take the invite that ends it:
    // a member an exit turned away is out of the party and has to be re-invited.
    [Fact]
    public void TheLeaderWeAsked_IsOneWhoseInviteWeTake_UntilWeFollowOrTheyDecline()
    {
        using Harness h = NewFollower();
        Assert.False(h.Requester.IsLeaderWeAsked("Boss"));

        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.True(h.Requester.IsLeaderWeAsked("Boss Hogg"));
        Assert.False(h.Requester.IsLeaderWeAsked("Stranger"));

        h.Feed("You are now following Boss.");
        Assert.False(h.Requester.IsLeaderWeAsked("Boss"));

        h.Advance(TimeSpan.FromSeconds(30));
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        h.Feed("Boss telepaths: {I can't, my loop goes through an exit you can't pass}");
        Assert.False(h.Requester.IsLeaderWeAsked("Boss"));
        Assert.Equal(2, h.Sent);
    }

    // ----- a refusal nobody asked for (the follow ends without a line) -----

    [Fact]
    public void UnpromptedExitRefusal_SendsOnceSettled()
    {
        using Harness h = NewFollower();

        h.Feed(ItemRefusal);
        Assert.Null(h.LastWire);
        Assert.Equal("Boss", h.Requester.PendingCheckFor);

        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("appropriate item", h.Requester.LastIncidentSummary);
        Assert.Null(h.Requester.PendingCheckFor);
    }

    [Theory]
    [InlineData("There is a closed door in that direction!")]
    [InlineData("The door is closed!")]
    [InlineData("The gate is Closed!")]
    [InlineData("You do not have enough to cover the toll of 50 gold crowns.")]
    [InlineData("You have not progressed far enough to go through this exit!")]
    [InlineData("You may not go through this exit!")]
    [InlineData("You are too good to go through this exit!")]
    [InlineData("You are not permitted in that room!")]
    public void EveryMoveOnlyRefusal_CountsAsARefusedFollowMove(string line)
    {
        using Harness h = NewFollower();

        h.Feed(line);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // `look <dir>` at a shut door answers with this; no move was refused.
    [Fact]
    public void ClosedDoorLookReply_IsNotARefusedMove()
    {
        using Harness h = NewFollower();

        h.Feed("The door is closed in that direction!");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
    }

    // A move of our own still unanswered owns the refusal, however late it comes.
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(45)]
    public void RefusalAnsweringOurOwnTypedMove_IsNotAFollowMove(int secondsLate)
    {
        using Harness h = NewFollower();

        h.Tracker.NoteMoveSentByObserver(Direction.S, h.Now);
        h.Advance(TimeSpan.FromSeconds(secondsLate));
        h.Feed("There is no exit in that direction!");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
    }

    [Fact]
    public void RefusalAnsweringAnEnginesMove_IsNotAFollowMove()
    {
        using Harness h = NewFollower();

        h.Tracker.NoteMoveSent(Direction.N, h.Now);
        h.Advance(TimeSpan.FromSeconds(8));
        h.Feed(ItemRefusal);

        Assert.Null(h.Requester.PendingCheckFor);
    }

    // A leader's drag still waiting for its room is nobody's command here: the
    // refusal behind it answers the next follow move.
    [Fact]
    public void RefusalWithOnlyADragQueued_IsARefusedFollowMove()
    {
        using Harness h = NewFollower();

        h.Feed(" -- Following your Party leader north --");
        h.Tracker.NoteFollowMove(Direction.N, h.Now);
        h.Feed(ItemRefusal);

        Assert.Equal("Boss", h.Requester.PendingCheckFor);
    }

    // A typed room command can be refused in an exit's words.
    [Fact]
    public void RefusalRightAfterATypedCommand_IsNotAFollowMove()
    {
        using Harness h = NewFollower();

        h.Type("touch altar");
        h.Feed("A strange power holds you back!");

        Assert.Null(h.Requester.PendingCheckFor);
    }

    // Talk draws no exit refusal, so typing it hides nothing.
    [Theory]
    [InlineData("/Boss wait up")]
    [InlineData(".hello all")]
    [InlineData(">Boss this way?")]
    [InlineData("gos anyone selling a ring")]
    [InlineData("bg on my way")]
    public void RefusalRightAfterTypedTalk_IsStillARefusedFollowMove(string typed)
    {
        using Harness h = NewFollower();

        h.Type(typed);
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // ----- the leader seen leaving -------------------------------------------

    [Fact]
    public void LeaderLeft_NoFollowMove_SendsOnceSettled()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        Assert.Null(h.LastWire);

        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("Boss left north", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void LeaderLeft_ThenFollowLine_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Feed(" -- Following your Party leader north --");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
    }

    // An exit that prints no follow line (an item exit crossed with the item in
    // hand): the new room is the proof we went along.
    [Fact]
    public void LeaderLeft_AndWeStandElsewhere_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Tracker.SetLocated(new RoomKey(1, 2), h.Now);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // The same, with the map no longer sure of the room it had us in: that is not
    // a follower seen standing where the leader left it.
    [Fact]
    public void LeaderLeft_AndTheRoomIsNoLongerSure_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Tracker.NoteFollowMove(Direction.N, h.Now);   // any state that is not Confirmed
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void SomeoneElseLeaving_IsNotTheLeader()
    {
        using Harness h = NewFollower();

        h.Feed("Stranger just left to the north.");
        h.Feed("The orc rogue just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
    }

    // ----- our own `par` -----------------------------------------------------

    // On the list, following nobody: how the game leaves a follower whose follow
    // move it refused without a word. The request goes out, and this client goes
    // on believing it follows: the follower hold on its own walks stays up.
    [Fact]
    public void ParListingTheLeaderAsInvited_SendsComeback_AndWeStayAFollower()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        using PartyManager manager = new(h.Router, h.Party);
        manager.AttachLineExtractor(h.Server.Lines);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        MovementCoordinator coordinator = new();
        using PartyFollowerMovementGate gate = new(h.Party, coordinator);
        h.Feed("You are now following Boss.");

        h.Feed(ParHeader);
        h.Server.Rows(new[] { BossInvited });
        h.Server.Prompt("[HP=0/MA=0]:");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("[Invited]", h.Requester.LastIncidentSummary);
        Assert.False(h.Party.SelfIsLeader);
        Assert.Equal("Boss", h.Party.LeaderName);
        Assert.Contains(MovementCoordinator.FollowerGate, coordinator.AssertedGates);
    }

    // The line and refusal first, then the `par`: one request, and still a follower.
    [Fact]
    public void SilentLeftBehind_ThenOwnPar_IsOneRequest_AndTheFollowerHoldStays()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        using PartyManager manager = new(h.Router, h.Party);
        manager.AttachLineExtractor(h.Server.Lines);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        MovementCoordinator coordinator = new();
        using PartyFollowerMovementGate gate = new(h.Party, coordinator);
        h.Feed("You are now following Boss.");

        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal(1, h.Sent);

        h.Feed(ParHeader);
        h.Server.Rows(new[] { BossInvited });
        h.Server.Prompt("[HP=0/MA=0]:");

        Assert.Equal(1, h.Sent);
        Assert.False(h.Party.SelfIsLeader);
        Assert.Contains(MovementCoordinator.FollowerGate, coordinator.AssertedGates);
    }

    // Another member's row ahead of the leader's, and the same again on the next
    // poll: one request.
    [Fact]
    public void ParFirst_AnotherMembersRowAheadOfTheLeaders_IsOneRequest()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        using PartyManager manager = new(h.Router, h.Party);
        manager.AttachLineExtractor(h.Server.Lines);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        h.Feed("You are now following Boss.");

        h.Feed(ParHeader);
        h.Server.Rows(new[] { OtherInvited, BossInvited });
        h.Server.Prompt("[HP=0/MA=0]:");
        Assert.Equal(1, h.Sent);

        h.Advance(TimeSpan.FromSeconds(5));
        h.Feed(ParHeader);
        h.Server.Rows(new[] { OtherInvited, BossInvited });
        h.Server.Prompt("[HP=0/MA=0]:");

        Assert.Equal(1, h.Sent);
        Assert.False(h.Party.SelfIsLeader);
    }

    // Three in the party: another member's [Invited] row while we still follow says
    // nothing about us. We stay a follower, and being left behind later still asks.
    [Fact]
    public void AnotherMembersInvitedRow_WhileWeStillFollow_ChangesNothingForUs()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        using PartyManager manager = new(h.Router, h.Party);
        manager.AttachLineExtractor(h.Server.Lines);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        MovementCoordinator coordinator = new();
        using PartyFollowerMovementGate gate = new(h.Party, coordinator);
        h.Feed("You are now following Boss.");

        h.Feed("You are following Boss.");
        h.Feed(ParHeader);
        h.Server.Rows(new[] { OtherInvited, BossFullRow });
        h.Server.Prompt("[HP=0/MA=0]:");

        Assert.False(h.Party.SelfIsLeader);
        Assert.Equal("Boss", h.Party.LeaderName);
        Assert.Contains(MovementCoordinator.FollowerGate, coordinator.AssertedGates);
        Assert.Equal(0, h.Sent);

        h.Advance(TimeSpan.FromSeconds(30));
        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        Assert.Equal("Boss", h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal(1, h.Sent);

        h.Feed(ParHeader);
        h.Server.Rows(new[] { OtherInvited, BossInvited });
        h.Server.Prompt("[HP=0/MA=0]:");
        Assert.Equal(1, h.Sent);
        Assert.Contains(MovementCoordinator.FollowerGate, coordinator.AssertedGates);
    }

    [Fact]
    public void ParListingSomeoneElseAsInvited_SendsNothing()
    {
        using Harness h = NewHarness();
        using PartyManager manager = new(h.Router, h.Party);
        manager.AttachLineExtractor(h.Server.Lines);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        h.Feed("You are now following Boss.");

        h.Feed(ParHeader);
        h.Server.Rows(new[] { OtherInvited });

        Assert.Null(h.LastWire);
    }

    // ----- deliberate unfollow stays silent ---------------------------

    [Fact]
    public void NoLongerFollowing_WithoutFailure_StaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        // Deliberate uninvite / our own unfollow — no movement failure first.
        h.Feed("You are no longer following MudPlay.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void NoLongerFollowing_WithoutFailure_SaysWhatItCantTell()
    {
        using Harness h = NewFollower();

        h.Feed("You are no longer following Boss.");

        Assert.Contains("no sign you were left behind", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void FailureTooLongAgo_StaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Advance(TimeSpan.FromSeconds(10));     // past the 3s window
        h.Feed("You are no longer following MudPlay.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void QuotedHeavyChatLine_DoesNotArm()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        // A player gossiping the phrase is quoted — the ^[^"]* anchor
        // means it never matches MovementFailedHeavy, so no arming.
        h.Feed("Raijin gossips \"my pack is too heavy to move lol\".");
        h.Feed("You are no longer following MudPlay.");

        Assert.Null(h.LastWire);
    }

    // `leave` (alone or with one more word) and a bare `follow` end our own follow,
    // in every spelling the game takes: `le` … `leave`, `fo` … `follow`.
    [Theory]
    [InlineData("leave")]
    [InlineData("leave party")]
    [InlineData("le")]
    [InlineData("lea")]
    [InlineData("leav")]
    [InlineData("follow")]
    [InlineData("fo")]
    [InlineData("fol")]
    public void TypedLeave_IsTheUserLeaving(string typed)
    {
        // Held as well, which without the command would be a left-behind.
        using Harness h = NewFollower(isMovementPrevented: () => true);

        h.Type(typed);
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("left the party by command", h.Requester.LastIncidentSummary);
    }

    // With three words or more the game does nothing for `leave`, so the hold in
    // force when the follow ends is what ended it.
    [Fact]
    public void LeaveWithThreeWords_IsNotACommandTheGameTakes()
    {
        using Harness h = NewFollower(isMovementPrevented: () => true);

        h.Type("leave the party");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // The consent to take the invite of the leader we asked holds for that leader
    // alone, through an answer that is no decline, and lapses after ten minutes.
    [Fact]
    public void TheLeaderWeAsked_IsThatLeaderOnly_AndOnlyForAWhile()
    {
        using Harness h = NewFollower();
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        h.Feed("Boss telepaths: {follow timed out - resuming anyway}");
        h.Feed("Boss telepaths: {I can't yet, I'm on a train trip. I'll come for you when the training is done}");
        h.Advance(TimeSpan.FromMinutes(9));
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));
        Assert.False(h.Requester.IsLeaderWeAsked("Bossy"));
        Assert.False(h.Requester.IsLeaderWeAsked("Bos"));

        h.Advance(TimeSpan.FromMinutes(2));
        Assert.False(h.Requester.IsLeaderWeAsked("Boss"));
    }

    // `join <leader>` / `follow <leader>` is how a member rejoins: being left
    // behind right after it is a left-behind. `leave gang` leaves the gang.
    [Theory]
    [InlineData("join Boss")]
    [InlineData("follow Boss")]
    [InlineData("leave gang")]
    public void TypedJoinOrFollowSomeone_IsNotLeaving(string typed)
    {
        using Harness h = NewFollower();

        h.Type(typed);
        h.Feed("You are now following Boss");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // A `leave` from before we rejoined says nothing about the next split.
    [Fact]
    public void TypedLeave_IsForgottenOnceWeFollowAgain()
    {
        using Harness h = NewFollower();

        h.Type("leave");
        h.Feed("You are no longer following Boss.");
        h.Feed("You are now following Boss.");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void MoveOfOurOwn_IsTheUserLeaving()
    {
        using Harness h = NewFollower();

        h.Tracker.NoteMoveSentByObserver(Direction.S, h.Now);   // typed, no engine claim
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("Boss just left to the north.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("a move of your own", h.Requester.LastIncidentSummary);
    }

    // A typed move the game turned away moved nobody: the party leaving right
    // after it still left us behind.
    [Fact]
    public void BonkedMoveOfOurOwn_DoesNotExcuseTheLeader()
    {
        using Harness h = NewFollower();

        h.Tracker.NoteMoveSentByObserver(Direction.S, h.Now);
        h.Feed("There is no exit in that direction!");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("Boss just left to the north.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.StartsWith("/Boss @comeback", h.LastWire);
    }

    // A relayed party command that is no teleport (a hand-over before an item
    // gate, a rest) says nothing about why the follow ends ten seconds later.
    [Theory]
    [InlineData("Boss", "rest")]
    [InlineData("Boss", "give brass key to Boss")]
    [InlineData("Other", "ring chime")]
    public void RelayedPartyCommand_DoesNotExcuseBeingLeftAtAnItemExit(string sender, string command)
    {
        using Harness h = NewFollower();

        h.Requester.NotePartyRelay(sender, command);
        h.Advance(TimeSpan.FromSeconds(10));
        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Advance(TimeSpan.FromSeconds(2));
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // The leader stepped through an exit that casts on the walk and the follow
    // ended: the spell teleported them, which drops every follower.
    [Fact]
    public void LeaderLeftThroughACastingExit_IsATeleportSplit()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the east.");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("casts a spell", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void OurOwnDeath_IsNotALeftBehind()
    {
        using Harness h = NewFollower();
        h.SelfDown = true;

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("we died or dropped", h.Requester.LastIncidentSummary);
    }

    [Theory]
    [InlineData("Boss is dead.")]
    [InlineData("Boss drops to the ground!")]
    public void LeadersDeath_IsNotALeftBehind(string line)
    {
        using Harness h = NewFollower();

        h.Feed(line);
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
        Assert.Contains("Boss died or dropped", h.Requester.LastIncidentSummary);
    }

    // ----- disabled / consume guards ----------------------------------

    [Fact]
    public void Disabled_DetectsButDoesNotSend()
    {
        using Harness h = NewHarness();
        h.Requester.Enabled = false;
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following MudPlay.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void FailureIsConsumed_SecondUnfollowStaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following MudPlay.");
        Assert.Equal("/MudPlay @comeback 1/1", h.LastWire);

        int sentCount = h.Sent;
        // A later, unrelated unfollow must not reuse the consumed failure.
        h.Feed("You are no longer following Raijin.");
        Assert.Equal(sentCount, h.Sent);
    }

    [Fact]
    public void Solo_SendsNothing()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed(ItemRefusal);
        h.Feed("Boss just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
    }

    [Fact]
    public void AsLeader_SendsNothing()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);
        h.Party.IsInParty = true;
        h.Party.SelfIsLeader = true;
        h.Party.LeaderName = "Self";

        h.Feed(ItemRefusal);
        h.Feed("Stranger just left to the north.");
        h.Feed("Self just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
    }

    // The party dissolving under a pending check (our own `par` says we're in no
    // party) means we weren't a follower left standing.
    [Fact]
    public void PartyGoneBeforeTheSettle_SendsNothing()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Party.IsInParty = false;
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Null(h.LastWire);
    }

    // ----- a request that can't go out yet ---------------------------------------

    // The master switch off, the send gate held, the board's menu: the request
    // waits, and goes out when the client can send again.
    [Fact]
    public void MasterSwitchOff_HoldsTheRequest_AndSendsItWhenBackOn()
    {
        using Harness h = NewFollower();
        h.MasterSwitchOn = false;

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.RetryInterval);
        Assert.Null(h.LastWire);
        Assert.Contains("master switch", h.Requester.LastIncidentSummary);
        Assert.NotNull(h.Requester.HeldBack);

        h.MasterSwitchOn = true;
        h.Advance(TimeSpan.FromSeconds(30));
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Null(h.Requester.HeldBack);
    }

    [Fact]
    public void SendGateHeld_HoldsTheRequest_AndSendsItOnce()
    {
        using Harness h = NewFollower();
        h.SendBlocked = "the client's sends are held";

        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);

        h.SendBlocked = null;
        h.Advance(TimeSpan.FromSeconds(30));
        h.Feed(ItemRefusal);                 // more of the same evidence adds nothing
        h.Timers.RunDue(ComebackRequester.SettleTime);
        h.Timers.RunDue(ComebackRequester.RetryInterval);
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Equal(1, h.Sent);
        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // Past the "accept @comeback for" window the leader has moved on.
    [Fact]
    public void HeldRequest_IsDroppedOnceItIsNoLongerFresh()
    {
        using Harness h = NewFollower();
        h.Requester.RetryWindow = TimeSpan.FromMinutes(2);
        h.MasterSwitchOn = false;
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        h.Advance(TimeSpan.FromMinutes(3));
        h.MasterSwitchOn = true;
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Null(h.LastWire);
        Assert.Null(h.Requester.HeldBack);
    }

    [Fact]
    public void HeldRequest_IsDroppedWhenWeFollowAgain()
    {
        using Harness h = NewFollower();
        h.MasterSwitchOn = false;
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        h.Feed("You are now following Boss.");
        h.MasterSwitchOn = true;
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Null(h.LastWire);
    }

    // During the leader's party train trip nothing is asked; the request goes out
    // when the trip is over, however long it ran.
    [Fact]
    public void TrainTrip_HoldsTheRequest_UntilTheTripIsOver()
    {
        using Harness h = NewFollower();
        h.Requester.RetryWindow = TimeSpan.FromMinutes(2);
        h.InTrainTrip = true;

        h.Feed("Boss just left to the north.");
        h.Feed("You have not progressed far enough to go through this exit!");
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);
        Assert.Contains("party train trip", h.Requester.LastIncidentSummary);

        h.Advance(TimeSpan.FromMinutes(6));
        h.Timers.RunDue(ComebackRequester.RetryInterval);
        Assert.Null(h.LastWire);

        h.InTrainTrip = false;
        h.Advance(TimeSpan.FromSeconds(5));
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // A dropped link hands the split to the reconnect's own request: nothing here
    // adds a second one before we follow again.
    [Fact]
    public void AfterADisconnect_TheReconnectsRequestIsTheOnlyOne()
    {
        using Harness h = NewFollower();

        h.Requester.NoteDisconnected();
        h.Advance(TimeSpan.FromSeconds(40));
        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);

        h.Feed("You are now following Boss.");
        h.Advance(TimeSpan.FromSeconds(20));
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // ----- one request per incident --------------------------------------------

    [Fact]
    public void OneRequestPerIncident_UntilFollowingAgain()
    {
        using Harness h = NewFollower();

        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal(1, h.Sent);

        // Still standing there: more of the same evidence asks nothing more.
        h.Advance(TimeSpan.FromSeconds(20));
        h.Feed(ItemRefusal);
        h.Feed("Boss just left to the north.");
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal(1, h.Sent);

        // Picked up, and left behind a second time.
        h.Feed("You are now following Boss.");
        h.Advance(TimeSpan.FromSeconds(20));
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal(2, h.Sent);
    }

    // The same exit on every lap of the leader's loop: each rejoin and refusal is
    // a split of its own, so each asks once. Whether a leader goes on coming back
    // for it is the leader's to decide; nothing here damps it.
    [Fact]
    public void TheSameExitEveryLap_AsksOncePerLap()
    {
        using Harness h = NewFollower();

        for (int lap = 0; lap < 5; lap++)
        {
            h.Feed("Boss just left to the north.");
            h.Feed(ItemRefusal);
            h.Advance(TimeSpan.FromSeconds(2));
            h.Timers.RunDue(ComebackRequester.SettleTime);
            h.Advance(TimeSpan.FromSeconds(20));
            h.Feed("You are now following Boss");
            h.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(5, h.Sent);
    }

    // The refusal and the leader's departure are one split, however they arrive.
    [Fact]
    public void RefusalAndDepartureTogether_AreOneRequest()
    {
        using Harness h = NewFollower();

        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal(1, h.Sent);
        Assert.Contains("Boss left north", h.Requester.LastIncidentSummary);
        Assert.Contains("appropriate item", h.Requester.LastIncidentSummary);
    }

    // An idle leader says so; the follower logs it and asks no second time.
    [Fact]
    public void LeadersDecline_IsKept_AndNotAnswered()
    {
        using Harness h = NewFollower();
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);

        h.Feed("Stranger telepaths: {I can't I'm idle}");
        Assert.DoesNotContain("idle", h.Requester.LastIncidentSummary);

        h.Feed("Boss telepaths: {I can't I'm idle}");

        Assert.Contains("leader answered: I can't I'm idle", h.Requester.LastIncidentSummary);
        Assert.Equal(1, h.Sent);
    }

    // A leader's client can be sending us a loop, or answering one we sent, while
    // we wait on a @comeback. Those lines name the loop, and a loop can be named
    // like a refusal: none of them is the leader's answer.
    [Theory]
    [InlineData("@loopresult 9da659db saved as 'I can't come'")]
    [InlineData("@loopresult 9da659db already have it as 'forget me 2'")]
    [InlineData("sending loop 'my party is full' (3 rooms, 1 line)")]
    [InlineData("preparing to send: path failed, yes to confirm, no to deny (code 9da659db)")]
    [InlineData("loop send of 'going idle' cancelled")]
    public void LoopSendLineNamingALoop_IsNoAnswerToTheComeback(string line)
    {
        using Harness h = NewFollower();
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        string before = h.Requester.LastIncidentSummary;

        h.Feed($"Boss telepaths: {{{line}}}");

        Assert.Equal(before, h.Requester.LastIncidentSummary);
        Assert.DoesNotContain("leader answered", h.Requester.LastIncidentSummary);
        Assert.Equal(1, h.Sent);
    }

    // The leader's client can as well be answering an @events or @event of ours.
    // Those replies name an event, and an event can be named like a refusal.
    [Theory]
    [InlineData("events: I can't come on, Bank off")]
    [InlineData("events (all disabled) (2/3): forget me off, Bank on")]
    [InlineData("event 'going idle' switched off")]
    [InlineData("event 'my party is full' is on: fires on a timer, sends a command")]
    [InlineData("no event named 'path failed'")]
    [InlineData("event name 'come' matches 2: 'I can't come', 'I can't come 2'")]
    [InlineData("events: 1 I can't come on, 2 Bank off")]
    [InlineData("event 2 'I can't come' is already off")]
    [InlineData("event 2 (unnamed) switched off; the run it has under way finishes")]
    [InlineData("no event number 9: there are 4")]
    public void EventReplyNamingAnEvent_IsNoAnswerToTheComeback(string line)
    {
        using Harness h = NewFollower();
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        string before = h.Requester.LastIncidentSummary;

        h.Feed($"Boss telepaths: {{{line}}}");

        Assert.Equal(before, h.Requester.LastIncidentSummary);
        Assert.DoesNotContain("leader answered", h.Requester.LastIncidentSummary);
        Assert.Equal(1, h.Sent);
    }

    // ----- held and left behind --------------------------------------------------

    // A follower too heavy to move tells the leader to wait, and then, left behind
    // anyway, asks them back: the two telepaths the Stock capture shows, in order.
    [Fact]
    public void TooHeavyAndLeftBehind_SendsWaitThenOneComeback()
    {
        using Harness h = NewFollower();
        using InventoryManager inventory = new(log: null);
        PartyRestSync rest = new(h.Party);
        rest.SetWireSender(b => h.Wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        using TooHeavyWaitSignal heavy = new(h.Router, inventory, rest, new MovementCoordinator());
        heavy.SetWireSender(_ => { });

        h.Feed("Boss just left to the north.");
        h.Feed("You are too heavy to move!");
        h.Feed("You are no longer following Boss.");
        h.Timers.RunDue(ComebackRequester.SettleTime);

        Assert.Equal(new[] { "/Boss @wait (too heavy to move)", "/Boss @comeback 1/1" }, h.Wire);
    }

    // ----- the follow the game ended without a line (Stock) ------------------

    // Stock: an exit turned us away and the game said nothing more, so we still
    // count ourselves a follower. The request went out; what ends the follow on
    // our side is recorded here as PartyManager.NoteFollowGivenUp would clear it.
    private static List<string> AskedOnStock(Harness h)
    {
        List<string> givenUp = new();
        h.Requester.FollowGivenUp = (leader, _) =>
        {
            givenUp.Add(leader);
            h.Party.IsInParty = false;
            h.Party.LeaderName = null;
        };
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Empty(givenUp);
        return givenUp;
    }

    // The leader refuses: nobody is coming, so we stop counting ourselves as
    // following (user, 2026-10-10: "option A").
    [Theory]
    [InlineData("I can't I'm idle")]
    [InlineData("I can't, my loop goes through an exit you can't pass")]
    [InlineData("my party is full - can't take you back")]
    [InlineData("you're 40 rooms off (limit 30) - can't come, forget me")]
    public void Stock_TheLeaderRefuses_TheFollowIsGivenUp(string refusal)
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);

        h.Feed($"Boss telepaths: {{{refusal}}}");

        Assert.Equal(new[] { "Boss" }, givenUp);
        Assert.False(h.Party.IsInParty);
        Assert.Null(Waiting(h));
        Assert.Contains("follow given up", h.Requester.LastIncidentSummary);
    }

    // "I can't yet" is no refusal: a train trip comes when the training is done,
    // and Auto-Lair invites on its next pass. The follow stands, and is waited on
    // for longer than a request with no answer is.
    [Theory]
    [InlineData("I can't yet, I'm on a train trip. I'll come for you when the training is done")]
    [InlineData("I can't yet, an exit on my way turned you away. I'll invite you when my next pass finds you")]
    [InlineData("coming to your location for pickup")]
    public void Stock_TheLeaderIsComingOrComingLater_TheFollowStands(string answer)
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);
        Assert.Equal(h.Requester.RetryWindow, Waiting(h));

        h.Feed($"Boss telepaths: {{{answer}}}");

        Assert.Empty(givenUp);
        Assert.True(h.Party.IsInParty);
        Assert.Equal(TimeSpan.FromMinutes(15), Waiting(h));
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));
    }

    // Every answer a leader gives that says it is coming, now or later, in the
    // words this version sends.
    [Theory]
    [InlineData("coming to your location for pickup")]
    [InlineData("backtracking up to 12 room(s) to find you")]
    [InlineData("comeback already in progress")]
    [InlineData("comeback already in progress - fetching Tank first, then you")]
    [InlineData("I can't yet, I'm on a train trip. I'll come for you when the training is done")]
    [InlineData("I can't yet, an exit on my way turned you away. I'll invite you when my next pass finds you")]
    [InlineData("didn't find you where the trip left you - @comeback with your room and I'll come")]
    [InlineData("found you - re-inviting Me")]
    [InlineData("got you - resuming")]
    [InlineData("got you - waiting for your @ok")]
    public void Stock_ARecognisedComingAnswer_IsWaitedOnForLonger(string answer)
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);

        h.Feed($"Boss telepaths: {{{answer}}}");

        Assert.Empty(givenUp);
        Assert.Equal(TimeSpan.FromMinutes(15), Waiting(h));
    }

    // The leader's own constants are among them: what it sends is what we read.
    [Fact]
    public void TheLeadersOwnAnswers_AreRecognised()
    {
        Assert.True(PartyComebackManager.SaysComingOrLater(PartyComebackManager.ComingForPickup));
        Assert.True(PartyComebackManager.SaysComingOrLater(PartyComebackManager.AlreadyInProgress));
        Assert.True(PartyComebackManager.SaysComingOrLater(PartyComebackManager.TrainTripReply));
        Assert.True(PartyComebackManager.SaysComingOrLater(PartyComebackManager.LairGateReply));
        Assert.False(PartyComebackManager.SaysComingOrLater(PartyComebackManager.LoopGateRefusal));
    }

    // Anything else in braces from the leader promises nothing: a generic denial
    // (a leader that doesn't take our @comeback), the reply to some other command,
    // free text. The wait already running is left as it is, not stretched.
    [Theory]
    [InlineData("command invalid or not allowed")]
    [InlineData("HP=120/150,MA=40/60")]
    [InlineData("idle; Town Square (map 1, room 1); no ailments")]
    [InlineData("ok")]
    [InlineData("be right there after this fight")]
    public void Stock_AnyOtherBracedTelepath_LeavesTheWaitAlone(string other)
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);
        Assert.Equal(h.Requester.RetryWindow, Waiting(h));

        h.Feed($"Boss telepaths: {{{other}}}");

        Assert.Empty(givenUp);
        Assert.True(h.Party.IsInParty);
        Assert.Equal(h.Requester.RetryWindow, Waiting(h));
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));
    }

    // Nobody answers and nobody comes inside the time a leader takes the request.
    [Fact]
    public void Stock_NobodyComesWithinTheWindow_TheFollowIsGivenUp()
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);

        LapseTheWait(h);

        Assert.Equal(new[] { "Boss" }, givenUp);
        Assert.False(h.Party.IsInParty);
        // The invite, should the leader come after all, is still one we asked for.
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));
    }

    // The leader came, invited, and went back to its own business when we didn't
    // follow in time: nobody is coming now, but its invite is still wanted.
    [Fact]
    public void Stock_TheLeaderCameAndGaveUp_TheFollowIsGivenUp_TheInviteStillWanted()
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);
        h.Feed("Boss telepaths: {coming to your location for pickup}");

        h.Feed("Boss telepaths: {follow timed out - resuming anyway}");

        Assert.Equal(new[] { "Boss" }, givenUp);
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));
    }

    // Following again ends the wait: nothing is given up afterwards.
    [Fact]
    public void Stock_FollowingAgain_EndsTheWait()
    {
        using Harness h = NewFollower();
        List<string> givenUp = AskedOnStock(h);

        h.Feed("You are now following Boss");
        Assert.Null(Waiting(h));
        LapseTheWait(h);

        Assert.Empty(givenUp);
        Assert.True(h.Party.IsInParty);
    }

    // A request that could never be sent is no pickup either: once it is dropped,
    // so is the follow.
    [Fact]
    public void Stock_TheRequestCouldNeverBeSent_TheFollowIsGivenUp()
    {
        using Harness h = NewFollower();
        List<string> givenUp = new();
        h.Requester.FollowGivenUp = (leader, _) => givenUp.Add(leader);
        h.MasterSwitchOn = false;
        h.Feed(ItemRefusal);
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Null(h.LastWire);

        h.Advance(h.Requester.RetryWindow + TimeSpan.FromSeconds(1));
        h.Timers.RunDue(ComebackRequester.RetryInterval);

        Assert.Equal(new[] { "Boss" }, givenUp);
    }

    // Paradigm ends the follow with its own line, and the party state goes with
    // it. A refusal, or the window running out, then has nothing left to end.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Paradigm_TheGamesLineEndedTheFollow_NothingIsGivenUpTwice(bool refused)
    {
        using Harness h = NewFollower();
        List<string> givenUp = new();
        h.Requester.FollowGivenUp = (leader, _) => givenUp.Add(leader);
        h.Feed("Boss just left to the east.");
        h.Feed(" -- Following your Party leader east --");
        h.Feed("You don't have a mine pass, so you can't enter the mines.");
        h.Feed("You are no longer following Boss.");
        h.Party.IsInParty = false;          // as PartyManager clears it on that line
        h.Party.LeaderName = null;
        h.Timers.RunDue(ComebackRequester.SettleTime);
        Assert.Equal("/Boss @comeback 1/1", h.LastWire);

        if (refused) h.Feed("Boss telepaths: {I can't, my loop goes through an exit you can't pass}");
        else LapseTheWait(h);

        Assert.Empty(givenUp);
        Assert.Equal(1, h.Sent);
    }

    // ----- joining the leader ourselves (Stock) ---------------------------------
    //
    // GAME_MECHANICS "A follower who can't move is left behind": on Stock a
    // follower an exit refused is still on the leader's list, and an invite to
    // someone on the list prints them nothing. With the realm probe unset or
    // false (Paradigm) nothing differs from what it always was.

    [Fact]
    public void Paradigm_TheLeaderSaysItFoundUs_WeSendNothing_AndWaitForTheInvite()
    {
        using Harness h = NewFollower();
        AskedOnStock(h);

        h.Feed("Boss telepaths: {found you - re-inviting Me}");

        Assert.Equal(1, h.Sent);                       // the @comeback, and nothing since
        Assert.DoesNotContain("join Boss", h.Wire);
    }

    // Stock: an exit turned us away with no word more, we asked, and the leader
    // says it has found us. We are still on its list, so we join ourselves.
    [Fact]
    public void Stock_TheLeaderSaysItFoundUs_WeJoinOurselves()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        AskedOnStock(h);

        h.Feed("Boss telepaths: {found you - re-inviting Me}");

        Assert.Equal("join Boss", h.LastWire);
        Assert.True(h.Requester.JoinJustSentTo("Boss"));
    }

    // The leader we asked is in the room with us: a leader who walked back by
    // hand, or one whose client says nothing.
    [Fact]
    public void Stock_TheLeaderWeAskedIsSeenInTheRoom_WeJoinOurselves_Once()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        AskedOnStock(h);

        h.Requester.NotePlayerInRoom("Other");
        Assert.Equal(1, h.Sent);                       // not the leader we asked
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal("join Boss", h.LastWire);
        h.Requester.NotePlayerInRoom("Boss");          // the room shown again
        Assert.Equal(2, h.Sent);

        h.Advance(TimeSpan.FromSeconds(30));           // they had stepped out; here again
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal(3, h.Sent);
    }

    // The fallback: the game says we are off the list after all. Nothing more is
    // tried, the roster is told, and the leader's invite is still ours to take.
    [Fact]
    public void Stock_TheGameSaysInvitedFirst_WeStopJoining_AndWaitForTheInvite()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        List<string> offTheList = new();
        h.Requester.NotOnTheirList = offTheList.Add;
        AskedOnStock(h);
        h.Feed("Boss telepaths: {found you - re-inviting Me}");
        Assert.Equal("join Boss", h.LastWire);

        h.Feed("You must be invited first!");

        Assert.Equal(new[] { "Boss" }, offTheList);
        Assert.True(h.Requester.IsLeaderWeAsked("Boss"));        // their invite is taken
        h.Advance(TimeSpan.FromSeconds(30));
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal(2, h.Sent);                                 // no second join
        Assert.Contains("must be invited first", h.Requester.JoinSummary);
    }

    // The same line to a join the player typed themselves is not ours to read.
    [Fact]
    public void Stock_InvitedFirst_WithNoJoinOfOurs_ChangesNothing()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        List<string> offTheList = new();
        h.Requester.NotOnTheirList = offTheList.Add;
        AskedOnStock(h);

        h.Feed("You must be invited first!");

        Assert.Empty(offTheList);
        h.Feed("Boss telepaths: {found you - re-inviting Me}");
        Assert.Equal("join Boss", h.LastWire);
    }

    // The game told us we stopped following (a hold or a too-heavy drop takes a
    // follower off the list): only an invite brings us back, so no join is sent.
    [Fact]
    public void Stock_ToldWeNoLongerFollow_WeAreOffTheList_AndWaitForTheInvite()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");
        h.Party.IsInParty = false;                     // as PartyManager clears it on that line
        h.Party.LeaderName = null;
        Assert.Equal("/Boss @comeback 1/1", h.LastWire);

        h.Feed("Boss telepaths: {found you - re-inviting Me}");
        h.Requester.NotePlayerInRoom("Boss");

        Assert.Equal(1, h.Sent);
    }

    // After the follow was given up the roster is solo, and still counts us on the
    // leader's list (PartyManager.ListedWithLeader): the leader turning up is
    // enough.
    [Fact]
    public void Stock_AfterTheFollowWasGivenUp_StillListed_TheLeaderTurnsUp_WeJoin()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        AskedOnStock(h);
        string? listedWith = null;
        h.Requester.ListedWith = () => listedWith;
        LapseTheWait(h);          // nobody came in time: given up
        Assert.False(h.Party.IsInParty);

        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal(1, h.Sent);                       // the roster doesn't count us listed

        listedWith = "Boss";
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal("join Boss", h.LastWire);
    }

    // A leader who refused is not joined: it said it isn't taking us.
    [Fact]
    public void Stock_ALeaderWhoRefused_IsNotJoined()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        h.Requester.ListedWith = () => "Boss";
        AskedOnStock(h);
        h.Feed("Boss telepaths: {my party is full - can't take you back}");

        h.Requester.NotePlayerInRoom("Boss");

        Assert.Equal(1, h.Sent);
    }

    // With the master switch off, or a send the client can't make, nothing goes out.
    [Fact]
    public void Stock_MasterSwitchOff_OrSendsHeld_NoJoin()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        AskedOnStock(h);

        h.MasterSwitchOn = false;
        h.Requester.NotePlayerInRoom("Boss");
        h.MasterSwitchOn = true;
        h.SendBlocked = "we are at the board's menu";
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal(1, h.Sent);

        h.SendBlocked = null;
        h.Requester.NotePlayerInRoom("Boss");
        Assert.Equal("join Boss", h.LastWire);
    }

    // Our link dropped: leaving the game took us off every list, so no join of
    // ours is tried until we have followed again, and nothing is recorded as the
    // game having refused one.
    [Fact]
    public void Stock_AfterTheLinkDropped_NoJoinIsTried_UntilWeFollowAgain()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        AskedOnStock(h);
        h.Requester.NoteDisconnected();

        h.Requester.NotePlayerInRoom("Boss");

        Assert.Equal(1, h.Sent);
        Assert.Null(h.Requester.JoinSummary);
    }

    // The leader's @join had us send the join (PartyEssentialHandlers): ours isn't
    // sent on top of it, and the game's answer to it is read here all the same.
    [Fact]
    public void Stock_AJoinSentForTheLeadersAtJoin_IsNotDoubled_AndItsRefusalIsRead()
    {
        using Harness h = NewFollower();
        h.Requester.OnStock = () => true;
        List<string> offTheList = new();
        h.Requester.NotOnTheirList = offTheList.Add;
        AskedOnStock(h);

        h.Requester.NoteJoinSent("Boss");
        h.Feed("Boss telepaths: {found you - re-inviting Me}");
        Assert.Equal(1, h.Sent);

        h.Feed("You must be invited first!");
        Assert.Equal(new[] { "Boss" }, offTheList);
    }
}
