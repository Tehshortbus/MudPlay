using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Leader-side PartyComebackManager. Two entry paths converge on the same
// stop-walk-recover flow:
//   - Path A (@comeback): a stranded follower telepaths @comeback <map>/<room>,
//     which the engine dispatches straight to the manager.
//   - Path B (@where probe): a dropped member re-enters the realm inside the
//     grace window, PartyManager raises MemberReturned, the leader telepaths
//     @where, and the location reply drives the same recovery.
// Both funnel through the party-full + return-distance gates before committing.
// @forget is the bidirectional teardown: either side drops the other from the
// roster (and the leader uses it to decline a recovery it can't make).
//
// Headless: the graph + tracker are driven directly, the recovery walk is
// exercised via the synchronous "already at destination" Finished path (the
// walker has no completion event we can pump, and DispatcherTimer ticks aren't
// pumped in the xUnit harness).
public sealed class PartyComebackManagerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public PartyComebackManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-comeback-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip; 1/3 carries a lair tag so the timer
    // store resolves against it. 1/1 + 1/3 are the two markers Auto-Lair
    // needs; 1/1 doubles as the start position.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [ { "GroupIndex": "1-1-1", "AvgDelay": 5 } ]
        """;

    private sealed class Harness : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required PartyState PartyState { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartyManager Party { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required PartyComebackManager Comeback { get; init; }
        // The clock the comeback manager reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> ComebackNow { get; set; } = () => DateTimeOffset.UtcNow;
        // The manager's CR-fallback, join-fallback and follow-wait timers.
        public required ManualTimers ComebackTimers { get; init; }
        // The game's prompts, as the loop runner reads them.
        public required WirePromptScanner Prompt { get; init; }
        // What Engine, Party and Comeback each put on the wire.
        public List<byte[]> EngineWire { get; } = new();
        public List<byte[]> PartyWire { get; } = new();
        public List<byte[]> ComebackWire { get; } = new();
        public required MovementCoordinator Coordinator { get; init; }

        public string LastReply => Encoding.Latin1.GetString(EngineWire[^1]);

        public void Dispose()
        {
            Comeback.Dispose();
            Lair.Dispose();
            Timers.Dispose();
            Party.Dispose();
            Engine.Dispose();
        }
    }

    // The strip, plus 1/4 west of 1/1: 1/4's way east into 1/1 needs an item.
    private const string GatedGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1 (Item: 474)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private Harness NewHarness(string graphJson = GraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), graphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), LairsJson);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState partyState = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, partyState, players);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        WirePromptScanner prompt = new();
        LoopRunner loop = new(tracker, coord, promptScanner: prompt, graph: graph, bfs: bfs, walker: walker);
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);
        MonsterMessageStore monsters = new();
        RoomEntityClassifier classifier = new(router, monsters, players, tracker);

        PartyManager party = new(router, partyState);
        Harness h = null!;
        ManualTimers comebackTimers = new();
        PartyComebackManager comeback =
            new(engine, party, tracker, classifier, walker, loop, lair, router, bfs, now: () => h.ComebackNow(),
                schedule: comebackTimers.Arm);

        h = new()
        {
            Router = router,
            PartyState = partyState,
            Players = players,
            Party = party,
            Engine = engine,
            Tracker = tracker,
            Walker = walker,
            Loop = loop,
            Lair = lair,
            Timers = timers,
            Comeback = comeback,
            ComebackTimers = comebackTimers,
            Prompt = prompt,
            Coordinator = coord,
        };
        engine.SetWireSender(h.EngineWire.Add);
        party.SetWireSender(h.PartyWire.Add);
        comeback.SetWireSender(h.ComebackWire.Add);
        return h;
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // Seat the sender as an active party member so the engine's party-whitelist
    // gate lets the (tier None) @comeback / @forget through.
    private static void SeatFollower(Harness h, string name)
    {
        h.PartyState.Members.Add(new PartyMember { Name = name });
        h.PartyState.IsInParty = true;
    }

    // Mark 1/1 + 1/3, locate at 1/1, start Auto-Lair — the resumable "running
    // engine" for these tests.
    private static void StartLair(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        Assert.True(h.Lair.IsActive);
    }

    // Those players stand in the room as it was last shown.
    private static void AlsoHere(Harness h, params string[] names)
    {
        foreach (string name in names)
            if (h.Players.Find(name) is null)
                h.Players.Players.Add(new MudPlay.Models.GameData.PlayerRecord(
                    GivenName: name, FamilyName: "", Class: "Warrior", Race: "Human", Alignment: "Neutral",
                    Title: null, Gang: null, Role: null, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow));
        h.Router.Dispatch(Line($"Also here: {string.Join(", ", names)}."));
    }

    private static bool Sent(Harness h, string fragment) =>
        h.ComebackWire.Exists(b => Encoding.Latin1.GetString(b).Contains(fragment));

    // ----- idle / busy guards ----------------------------------------

    [Fact]
    public void Idle_NoEngineRunning_RepliesCantImIdle()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));

        Assert.Contains("I can't I'm idle", h.LastReply);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // The follower's client reads this answer to give the follow up at once, so
    // it is sent with denials silenced too: unanswered, the follower would wait
    // out its whole window for a leader who had already said no.
    [Fact]
    public void Idle_WithDenialsSilenced_StillAnswers_BecauseTheFollowerReadsIt()
    {
        using Harness h = NewHarness();
        h.Engine.WarnOnDenial = false;
        SeatFollower(h, "Tank");

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));

        Assert.Equal("/Tank {I can't I'm idle}\r", h.LastReply);
    }

    // The answer in braces goes on the channel the request came in on. @forget is
    // a command, which every client that hears it acts on, so it goes by telepath
    // to the one member it is for.
    [Fact]
    public void Declined_OnGangpath_AnswersOnGangpath_AndForgetsByTelepath()
    {
        using Harness h = NewHarness();
        for (int i = 1; i <= 6; i++) SeatFollower(h, $"Member{i}");
        h.Router.Dispatch(Line("Straggler started to follow you."));
        h.Router.Dispatch(Line("Straggler stops following you."));
        StartLair(h);

        ChatLines.Hear(h.Router, new ChatLine(ChatChannel.Gangpath, "Straggler", "@comeback 1/3"));

        Assert.StartsWith("bg {my party is full", h.LastReply);
        Assert.True(Sent(h, "/Straggler @forget"));
        Assert.False(Sent(h, "bg @forget"));
    }

    // "I can't yet" follows the Warn setting like the loop's refusal (user,
    // 2026-10-10); the member is still kept for the fetch when the trip ends.
    [Fact]
    public void TrainTrip_WithDenialsSilenced_SaysNothing()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Engine.WarnOnDenial = false;
        h.Comeback.TrainTripRunning = () => true;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        int sentBefore = h.EngineWire.Count;

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));

        Assert.Equal(sentBefore, h.EngineWire.Count);
    }

    [Fact]
    public void SecondComeback_WhileBusy_RepliesAlreadyInProgress()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Contains("already in progress", h.LastReply);
    }

    // A follower who dropped and came back asks for a pickup: our hold for their
    // reconnect must not park the walk to them, or the leader never moves while
    // they wait on it.
    [Fact]
    public void ReturnedMember_Comeback_WalksDespiteTheirReconnectHold()
    {
        using Harness h = NewHarness();
        using PartyDisconnectMovementGate hold = new(h.Party, h.Coordinator);
        h.Comeback.ReleaseHolds = hold.Release;
        h.Router.Dispatch(Line("Tank started to follow you."));
        StartLair(h);
        h.Router.Dispatch(Line("Tank just disconnected!!!."));
        Assert.True(h.Coordinator.IsPaused);
        h.Tracker.SetLocated(new RoomKey(1, 1));   // the lair's first step settles

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains("coming to your location for pickup", h.LastReply);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.False(h.Coordinator.IsPaused);
    }

    // ----- left-behind eligibility (leader-side authorisation) -------

    [Fact]
    public void LeftBehindFollower_NotActiveMember_StillAuthorised()
    {
        using Harness h = NewHarness();

        // Tank joins then gets left behind (self-departs / disconnects). The
        // server drops them from our party, so they're NOT an active member —
        // but the grace-window stamp makes them recoverable.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank stops following you."));
        Assert.DoesNotContain(h.PartyState.Members,
            m => m.Name.Equals("Tank", StringComparison.OrdinalIgnoreCase));

        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        // Authorised via WasRecentlyPartied → recovery walk begins.
        Assert.Contains("coming to your location for pickup", h.LastReply);
        Assert.False(h.Lair.IsActive);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void DeliberatelyUninvited_Comeback_IsDenied()
    {
        using Harness h = NewHarness();

        // WE uninvited Tank — "removed from your followers" does NOT stamp
        // the grace window, so the @comeback is rejected: recovery never
        // runs and the engine keeps looping.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank has been removed from your followers."));

        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        // Denial signal is the engine staying alive — the walker is already
        // Walking from AutoLair's own move, independent of @comeback, so
        // Lair.IsActive (not WalkState) is what distinguishes deny from allow.
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void Stranger_NeverPartied_Comeback_IsDenied()
    {
        using Harness h = NewHarness();
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Stranger", "@comeback 1/3"));

        Assert.True(h.Lair.IsActive);
    }

    // ----- party-full / distance gates -------------------------------

    [Fact]
    public void PartyFull_ReturningMember_DeclinedAndForgotten()
    {
        using Harness h = NewHarness();
        for (int i = 1; i <= 6; i++) SeatFollower(h, $"Member{i}");

        // Straggler was with us, dropped, and we backfilled their slot to the
        // 6-member cap while they were gone.
        h.Router.Dispatch(Line("Straggler started to follow you."));
        h.Router.Dispatch(Line("Straggler stops following you."));
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Straggler", "@comeback 1/3"));

        Assert.Contains("full", h.LastReply);
        Assert.True(Sent(h, "/Straggler @forget"));
        Assert.True(h.Lair.IsActive);   // declined before the engine snapshot
    }

    [Fact]
    public void TooFar_ReturningMember_DeclinedAndForgotten()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        h.Comeback.ReturnDistanceRooms = 1;   // 1/1 → 1/3 is 2 hops, beyond reach
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains("rooms off", h.LastReply);
        Assert.True(Sent(h, "/Tank @forget"));
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void NoPath_ReturningMember_DeclinedAndForgotten()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // 2/1 is on a map that isn't in the graph — no route at all.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 2/1"));

        Assert.Contains("path to you", h.LastReply);
        Assert.True(Sent(h, "/Tank @forget"));
        Assert.True(h.Lair.IsActive);
    }

    // ----- explicit-room recovery ------------------------------------

    [Fact]
    public void ExplicitRoom_StopsEngine_AndWalksThere()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.False(h.Lair.IsActive);                       // engine stopped
        Assert.Equal(WalkState.Walking, h.Walker.State);     // recovery walk owns the wire
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Contains("coming to your location for pickup", h.LastReply);
    }

    [Fact]
    public void ExplicitRoom_AlreadyThere_ReInvitesFollower()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Auto-Lair's first move left the tracker Pending, so @comeback's
        // WalkTo(1/1) defers (raises Started, not Finished). The command first
        // stops Auto-Lair, so settling the tracker at 1/1 now drives the
        // walker's deferred dispatch into WalkToImmediate, where source==dest
        // raises Finished synchronously → re-invite leg.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Contains("invite Tank\r",
            Encoding.Latin1.GetString(h.PartyWire[^1]));
        Assert.Contains("re-inviting Tank", h.LastReply);
        Assert.False(h.Lair.IsActive);   // still busy — awaiting the follow confirmation
    }

    [Fact]
    public void FollowConfirmed_ResumesCapturedEngine()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));   // settle → Finished → re-invite
        Assert.False(h.Lair.IsActive);

        // "Tank started to follow you." → PartyManager raises
        // MemberFollowConfirmed → comeback resumes the captured lair.
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.True(h.Lair.IsActive);
        Assert.Contains("resuming", h.LastReply);
    }

    // Recovering a member mid-loop continues the same session: the loop resumes
    // without re-raising ReachedFirstWaypoint, whose consumer zeroes the session
    // stats and broadcasts @reset to the party.
    [Fact]
    public void FollowConfirmed_ResumesLoop_WithoutANewLoopStart()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        int loopStarts = 0;
        h.Loop.Event += e => { if (e.Kind == LoopEventKind.ReachedFirstWaypoint) loopStarts++; };
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("circuit", [new RoomKey(1, 1), new RoomKey(1, 3)])));
        Assert.Equal(1, loopStarts);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        Assert.Equal(LoopState.Idle, h.Loop.State);
        h.Tracker.SetLocated(new RoomKey(1, 1));   // settle → Finished → re-invite
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));   // settle any deferred loop entry

        Assert.NotEqual(LoopState.Idle, h.Loop.State);
        Assert.Equal(1, loopStarts);
    }

    // ----- repeated-failure backoff (report -154819) -----------------

    [Fact]
    public void RepeatedReachFailures_GivesUp_AndForgets()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Two recovery walks that start then fail to reach Tank (as if stranded past
        // a gate we can't cross). Each resumes the lair; the counter climbs to the
        // cap. Without the backoff, every fresh @comeback would restart the doomed
        // walk and keep hijacking our own navigation (report -154819).
        for (int i = 0; i < MaxFailuresToTrip; i++)
        {
            ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
            Assert.Equal(WalkState.Walking, h.Walker.State);      // recovery walk owns the wire
            h.Walker.AbortFromRecoveryFailure("gate we can't cross");   // → Failed → count++
            Assert.True(h.Lair.IsActive);                          // resumed our own run
        }

        // Over the cap now — decline outright + @forget, without stopping our engine
        // for another doomed walk.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Contains("tried reaching you", h.LastReply);
        Assert.True(Sent(h, "/Tank @forget"));
        Assert.Equal(WalkState.Idle, h.Walker.State);   // no recovery walk started
        Assert.True(h.Lair.IsActive);                    // our navigation left alone
    }

    [Fact]
    public void FollowConfirm_ClearsFailedRecoveryBackoff()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // One failed reach (count below the cap), then Tank actually rejoins.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        h.Walker.AbortFromRecoveryFailure("gate");
        h.Router.Dispatch(Line("Tank started to follow you."));   // clears the count

        // The very next @comeback still tries (walks), not declines — the follow-
        // confirm reset the backoff, so a later, reachable return isn't stranded.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.DoesNotContain("tried reaching you", h.LastReply);
    }

    // Mirrors PartyComebackManager.MaxFailedRecoveries (private) — the number of
    // failed reaches that trips the give-up.
    private const int MaxFailuresToTrip = 2;

    // ----- backtrack path --------------------------------------------

    [Fact]
    public void BareComeback_NoPathHistory_GoesIdle()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Only the current room is in history → nothing to backtrack along.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));

        Assert.Contains("no path history", h.LastReply);
        Assert.False(h.Lair.IsActive);   // stopped during snapshot, stays stopped
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // Report paradigm-20260928-074527: our backtrack gave up (they were offline)
    // and we went idle; their @comeback once back online must still recover them,
    // then resume what the backtrack stopped — not answer "I can't I'm idle".
    [Fact]
    public void ComebackAfterWeGaveUp_StillRecovers_ThenResumes()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));
        Assert.Contains("no path history", h.LastReply);
        Assert.False(h.Lair.IsActive);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        Assert.DoesNotContain("idle", h.LastReply);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.True(h.Lair.IsActive);
    }

    // They tell us where they are while we're still backtracking: go there instead.
    [Fact]
    public void RoomComebackDuringBacktrack_WalksToTheirRoom()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains("coming to your location", h.LastReply);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // ----- path C: a follower left behind by our move ----------------

    // Report paradigm-20260926-195517: a held follower couldn't move when the
    // leader's walker stepped on — "X is no longer following you." — and the
    // leader walked off without them.
    [Fact]
    public void LeftBehind_WhileEngineRuns_BacktracksForThem()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));   // where Tank is left
        StartLair(h);                               // we've moved on to 1/1
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.False(h.Lair.IsActive);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
        Assert.True(Sent(h, "/Tank {backtracking"));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // Settings → Party "Return distance" caps the backtrack too: our path holds
    // three rooms behind us: a reach of 2 walks back two of them, a reach of 30 all three.
    [Theory]
    [InlineData(2, 2)]
    [InlineData(30, 3)]
    public void Backtrack_IsCappedByReturnDistance(int reach, int rooms)
    {
        using Harness h = NewHarness();
        h.Comeback.ReturnDistanceRooms = reach;
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 3));
        StartLair(h);                               // 1/1: three rooms behind us
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.True(Sent(h, $"/Tank {{backtracking up to {rooms} room(s)"));
    }

    // User report (a token mid-walk split the party): the player set a new walk-to
    // over the backtrack, and every walk they finished afterwards sent them on to the
    // next backtrack room. Replacing our walk now calls the recovery off.
    [Fact]
    public void LeftBehind_UserStartsANewWalk_CallsTheRecoveryOff()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);

        h.Walker.WalkTo(new RoomKey(1, 3));            // the user's own walk-to

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Null(h.Comeback.ParkedResumeSummary);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.False(h.Lair.IsActive);                 // the replaced engine isn't resumed
    }

    // Our own token teleport drops everyone following us; that isn't a held member
    // to go back for.
    [Fact]
    public void LeftBehind_RightAfterOurOwnToken_DoesNotBacktrack()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Comeback.NoteOwnTeleport();
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
    }

    // Report paradigm-20261007-183903: the leader sat resting with a walk held and
    // sent a follower on with `@do se`. The game prints the same "no longer
    // following you" for a follower who walks off, and the leader went back for
    // someone who had left of their own accord. Only a drop right behind a move of
    // ours is a member left behind.
    [Fact]
    public void StopsFollowing_LongAfterOurLastMove_IsNotLeftBehind()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.ComebackNow = () => DateTimeOffset.UtcNow.AddSeconds(30);
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
        Assert.False(Sent(h, "/Tank {backtracking"));
    }

    // Reports paradigm-20261007-134824 and paradigm-20260929-221642: the member was
    // following again (caught up, or invited by hand) while the recovery was still
    // walking, and the walk carried on to a room nobody was in. A rejoin ends the
    // recovery whatever stage it has reached, and puts the stopped engine back.
    [Fact]
    public void LeftBehind_RejoinsWhileWeStillWalk_EndsTheRecovery()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);                               // 1/1, with 1/2 and 1/3 behind us
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);

        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);               // the engine we stopped runs again
        // Left behind is still left behind: they are asked for their @ok as usual.
        Assert.True(Sent(h, "/Tank @waiting"));

        // Reaching the room they were left in starts nothing over.
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.False(Sent(h, "re-inviting Tank"));
    }

    // The same while the user has the walk paused: the recovery walk sat waiting
    // behind the pause and woke on Resume, 100 s after the member was back.
    [Fact]
    public void Comeback_RejoinsWhileTheWalkIsPaused_NothingWakesOnResume()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 2)));    // the user's own walk-to
        h.Coordinator.AssertGate(MovementCoordinator.UserGate);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);

        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Coordinator.ClearGate(MovementCoordinator.UserGate);

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);   // their walk, not the pickup
    }

    // Report paradigm-20260929-221642: the walk-to was paused and the user stepped
    // on by hand, leaving a follower a room back. That move was theirs, so the
    // pickup is too — a recovery started here is a walk born paused.
    [Fact]
    public void LeftBehind_ByAMoveTypedByHand_IsLeftToThem()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Coordinator.AssertGate(MovementCoordinator.UserGate);     // the user's pause
        h.Tracker.SetLocated(new RoomKey(1, 1));
        // Their own step, a moment later (clear of the echo the engine's last step
        // is still owed, which a same-direction keystroke would be taken for).
        DateTimeOffset typedAt = DateTimeOffset.UtcNow.AddSeconds(3);
        h.Tracker.NoteMoveSentByObserver(Direction.N, typedAt);
        h.ComebackNow = () => typedAt.AddSeconds(1);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
        Assert.False(Sent(h, "/Tank {backtracking"));
    }

    // A leader back from a dropped link has its loop restarted but held until the
    // party reform has seen the room. A follower asking to be fetched in those
    // seconds finds a run to interrupt, not an idle leader, and the hold doesn't
    // park the walk to them.
    [Fact]
    public void Comeback_WhileTheLoopIsHeldAfterAReconnect_IsAnsweredAndWalked()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        h.Loop.SetReconnectReformProbe(() => true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("circuit", [new RoomKey(1, 1), new RoomKey(1, 3)])));
        h.Loop.NotifyDisconnected();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        Assert.Equal(LoopState.Paused, h.Loop.State);
        Assert.True(h.Loop.ReconnectResumeHeldForReform);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains("coming to your location for pickup", h.LastReply);
        Assert.False(h.Loop.ReconnectResumeHeldForReform);
        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    // The pause alone decides nothing. The same gate is raised by a party member's
    // remote stop, an errand, the route picker: a member the engine's own step left
    // behind just as one of those landed is still ours to go back for.
    [Fact]
    public void LeftBehind_ByAnEngineStepAsAPauseLands_IsStillGoneBackFor()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);                                               // the engine's step is on the wire
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Coordinator.AssertGate(MovementCoordinator.UserGate);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.True(Sent(h, "/Tank {backtracking"));
    }

    // The recovery interrupted a loop; a rejoin while the walk back is still under
    // way puts that loop back, as the same session.
    [Fact]
    public void LeftBehind_RejoinsWhileWeStillWalk_PutsTheLoopBack()
    {
        using Harness h = NewHarness();
        int loopStarts = 0;
        h.Loop.Event += e => { if (e.Kind == LoopEventKind.ReachedFirstWaypoint) loopStarts++; };
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Loop.Start(new Loop("circuit", [new RoomKey(1, 1), new RoomKey(1, 3)])));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(LoopState.Idle, h.Loop.State);

        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
        Assert.Equal(1, loopStarts);
    }

    // An @ok sent just before being left behind is distrusted because the member
    // then failed to move. One who has come to us while we were still on the way
    // back has moved: their @ok counts, and they are told we're waiting for it.
    [Fact]
    public void LeftBehind_RightAfterTheirOk_RejoinsWhileWeStillWalk_TheirOkCounts()
    {
        using Harness h = NewHarness();
        List<(string, bool)> held = new();
        h.Comeback.LeftBehindRejoined = (g, ignoreOk) => held.Add((g, ignoreOk));
        h.Comeback.OkedWithin = (_, _) => true;
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));

        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Equal(new[] { ("Tank", false) }, held);
        Assert.True(Sent(h, "/Tank @waiting"));
        Assert.False(Sent(h, "too early"));
    }

    [Fact]
    public void Cancel_DropsTheRecoveryAndParkedResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));

        h.Comeback.Cancel("test");

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Null(h.Comeback.ParkedResumeSummary);
    }

    [Fact]
    public void LeftBehind_Rejoins_HoldsForTheirOk_AndTellsThem()
    {
        using Harness h = NewHarness();
        List<string> held = new();
        h.Comeback.LeftBehindRejoined = (g, _) => held.Add(g);
        h.Tracker.SetLocated(new RoomKey(1, 2));   // where Tank is left
        StartLair(h);                               // we've moved on to 1/1
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Players.Players.Add(new MudPlay.Models.GameData.PlayerRecord(
            GivenName: "Tank", FamilyName: "", Class: "Warrior", Race: "Human", Alignment: "Neutral",
            Title: null, Gang: null, Role: null, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow));
        h.Router.Dispatch(Line("Also here: Tank."));  // Tank stands where we left him
        h.Tracker.SetLocated(new RoomKey(1, 2));   // backtracked into Tank's room
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Equal(new[] { "Tank" }, held);
        Assert.True(Sent(h, "/Tank @waiting"));
        Assert.True(Sent(h, "waiting for your @ok"));
        Assert.True(h.Lair.IsActive);   // resumed — the party @wait gate holds it
    }

    // Report paradigm-20260926-195517: the follower's @ok landed a second before
    // our move left them behind — so a fresh @ok proves nothing.
    [Fact]
    public void LeftBehind_RightAfterTheirOk_WaitsTheFullWindow()
    {
        using Harness h = NewHarness();
        List<(string, bool)> held = new();
        h.Comeback.LeftBehindRejoined = (g, ignoreOk) => held.Add((g, ignoreOk));
        h.Comeback.OkedWithin = (_, _) => true;
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Players.Players.Add(new MudPlay.Models.GameData.PlayerRecord(
            GivenName: "Tank", FamilyName: "", Class: "Warrior", Race: "Human", Alignment: "Neutral",
            Title: null, Gang: null, Role: null, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow));
        h.Router.Dispatch(Line("Also here: Tank."));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Equal(new[] { ("Tank", true) }, held);
        Assert.False(Sent(h, "/Tank @waiting"));
        Assert.True(Sent(h, "too early"));
    }

    [Fact]
    public void Comeback_Rejoins_DoesNotInferAHold()
    {
        using Harness h = NewHarness();
        List<string> held = new();
        h.Comeback.LeftBehindRejoined = (g, _) => held.Add(g);
        SeatFollower(h, "Tank");
        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Empty(held);
    }

    [Fact]
    public void LeftBehind_NoEngineRunning_StaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.False(Sent(h, "/Tank"));
        Assert.Null(h.Comeback.RecoveringMember);
    }

    [Fact]
    public void LeftBehind_ReInviteOff_KeepsGoing()
    {
        using Harness h = NewHarness();
        h.Party.AutoInviteEnabled = false;
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.True(h.Lair.IsActive);
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // ----- path B: leader probes @where on a member's return ---------

    [Fact]
    public void MemberReturns_LeaderProbesWhere()
    {
        using Harness h = NewHarness();
        StartLair(h);

        // Tank followed us, dropped, and re-enters the realm inside the grace
        // window → PartyManager raises MemberReturned → we first send a bare CR to
        // re-observe our room (report 193610), NOT @where yet.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank just disconnected!!!."));
        h.Router.Dispatch(Line("Tank just entered the Realm."));

        Assert.False(Sent(h, "/Tank @where"));   // held — CR re-observe first

        // Tank didn't rejoin via invite-on-seen (re-entered elsewhere), so the
        // fallback fires the @where probe.
        h.ComebackTimers.RunAll();
        Assert.True(Sent(h, "/Tank @where"));
    }

    [Fact]
    public void MemberReturns_RejoinsAfterCr_NoWhereProbe()
    {
        using Harness h = NewHarness();
        StartLair(h);

        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank just disconnected!!!."));
        h.Router.Dispatch(Line("Tank just entered the Realm."));
        Assert.False(Sent(h, "/Tank @where"));    // CR re-observe first

        // Tank was standing in our room; the CR's "Also here:" drove
        // AutoPartyManager's invite-on-seen and Tank re-followed — so by the time
        // the fallback fires he's back in the party and no @where is spent.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.ComebackTimers.RunAll();

        Assert.False(Sent(h, "/Tank @where"));
    }

    [Fact]
    public void MemberReturns_ProbeAnswered_RecoveryWalksToThem()
    {
        using Harness h = NewHarness();
        StartLair(h);

        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank just disconnected!!!."));
        h.Router.Dispatch(Line("Tank just entered the Realm."));
        // CR re-observe found no Tank in our room → fallback fires the @where probe.
        h.ComebackTimers.RunAll();
        // Leader probed @where; Tank answers with their location (the wrapped
        // MudPlay reply format PartyEssentialHandlers builds).
        h.Router.Dispatch(Line("Tank telepaths: {Throne Room (map 1, room 3); exits: north}"));

        Assert.False(h.Lair.IsActive);                    // paused for the pickup
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.True(Sent(h, "coming to your location for pickup"));
    }

    // ----- @forget (bidirectional teardown) --------------------------

    [Fact]
    public void Forget_NotRecovering_DropsMemberFromRoster()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@forget"));

        Assert.DoesNotContain(h.PartyState.Members,
            m => m.Name.Equals("Tank", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("forgetting Tank", h.LastReply);
        Assert.True(h.Lair.IsActive);   // no recovery in flight, engine untouched
    }

    [Fact]
    public void Forget_InvokesForgetLeaderCallback()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "MudPlay");
        List<string> forgotten = new();
        h.Comeback.ForgetLeaderCallback = forgotten.Add;

        ChatLines.Hear(h.Router, Telepath("MudPlay", "@forget"));

        Assert.Contains("MudPlay", forgotten);
    }

    [Fact]
    public void Forget_FromRecoveringMember_DropsThemAndResumes()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Pickup in flight (walking to 1/3), then Tank calls it off.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        ChatLines.Hear(h.Router, Telepath("Tank", "@forget"));

        Assert.DoesNotContain(h.PartyState.Members,
            m => m.Name.Equals("Tank", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("forgetting Tank", h.LastReply);
        Assert.True(h.Lair.IsActive);   // prior engine resumed
    }

    [Fact]
    public void Forget_WhileAwaitingFollow_DropsThemAndResumes()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Drive to the AwaitingFollow phase (re-invite sent, waiting).
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.False(h.Lair.IsActive);

        ChatLines.Hear(h.Router, Telepath("Tank", "@forget"));

        Assert.True(h.Lair.IsActive);   // resumed instead of hanging on follow
    }

    [Fact]
    public void Forget_FromDifferentMember_DropsThemButKeepsRecovery()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        SeatFollower(h, "Mage");
        StartLair(h);

        // Recovering Tank; Mage's @forget must not abandon Tank's pickup.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        ChatLines.Hear(h.Router, Telepath("Mage", "@forget"));

        Assert.DoesNotContain(h.PartyState.Members,
            m => m.Name.Equals("Mage", StringComparison.OrdinalIgnoreCase));
        Assert.False(h.Lair.IsActive);                    // Tank's pickup still in flight
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // ----- followers an exit turned away (leader's screen, Paradigm 2026-10-10) -----

    // One step through a gated exit left both followers behind, and with nobody
    // following the party is gone. Each asks to be fetched from outside any party
    // of ours; both are, one after the other, and then the engine is put back.
    [Fact]
    public void AllFollowersGated_PartyDisbanded_BothAreFetchedInTurn()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));   // where they are left
        StartLair(h);                               // we've moved on to 1/1
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Healer started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Router.Dispatch(Line("Healer is no longer following you."));
        h.Router.Dispatch(Line("Your party has been disbanded."));
        h.Router.Dispatch(Line("You are not in a party at the present time."));

        Assert.False(h.PartyState.IsInParty);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new[] { "Healer" }, h.Comeback.QueuedRecoveries);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));
        Assert.Contains("coming to your location", h.LastReply);
        ChatLines.Hear(h.Router, Telepath("Healer", "@comeback 1/2"));
        Assert.Contains("fetching Tank first, then you", h.LastReply);
        Assert.Equal(new[] { "Healer" }, h.Comeback.QueuedRecoveries);

        h.Tracker.SetLocated(new RoomKey(1, 2));   // reached the room they stand in
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.Equal("Healer", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.QueuedRecoveries);
        Assert.False(h.Lair.IsActive);              // not put back until both are along

        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Router.Dispatch(Line("Healer started to follow you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
    }

    // With one of two turned away the party stays; the one left asks and is fetched.
    [Fact]
    public void OneOfTwoFollowersGated_PartyStays_TheirComebackIsHonoured()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Healer started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.True(h.PartyState.IsInParty);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.QueuedRecoveries);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));

        Assert.Contains("coming to your location", h.LastReply);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
    }

    // A loop that goes through an exit the member can't pass would leave them there
    // again every lap: their request is refused and the loop carries on.
    [Fact]
    public void LoopThroughAGate_FollowerDroppedThere_IsRefused_AndTheLoopCarriesOn()
    {
        using Harness h = NewHarness(GatedGraphJson);
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
        Assert.Single(h.Comeback.LeftAtLoopGates);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Contains(PartyComebackManager.LoopGateRefusal, h.LastReply);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // The refusal is a denial: with denials silenced nothing is said, and the
    // loop still isn't stopped.
    [Fact]
    public void LoopThroughAGate_WithDenialsSilenced_RefusesWithoutAReply()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Engine.WarnOnDenial = false;
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));
        int sentBefore = h.EngineWire.Count;

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Equal(sentBefore, h.EngineWire.Count);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // Where the game tells a leader nothing, the room the member names does: it is
    // a room the loop's own circuit just left by an exit that admits only some.
    [Fact]
    public void LoopThroughAGate_NoLineFromTheGame_TheNamedRoomIsEnough()
    {
        using Harness h = NewHarness(GatedGraphJson);
        SeatFollower(h, "Tank");
        StartLoopAtTheGate(h);
        StepThroughTheGate(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Contains(PartyComebackManager.LoopGateRefusal, h.LastReply);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // The same with no room named (the member's map wasn't sure): the latest gated
    // exit the loop itself has crossed since they were last seen following answers.
    [Fact]
    public void LoopThroughAGate_NoLineFromTheGame_ABareRequestIsJudgedTheSameWay()
    {
        using Harness h = NewHarness(GatedGraphJson);
        SeatFollower(h, "Tank");
        StartLoopAtTheGate(h);
        StepThroughTheGate(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));

        Assert.Contains(PartyComebackManager.LoopGateRefusal, h.LastReply);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    // A member seen following after the loop's last crossing wasn't left at it.
    [Fact]
    public void LoopThroughAGate_MemberSeenFollowingSinceTheCrossing_IsGoneBackFor()
    {
        using Harness h = NewHarness(GatedGraphJson);
        StartLoopAtTheGate(h);
        StepThroughTheGate(h);
        h.ComebackNow = () => DateTimeOffset.UtcNow.AddSeconds(5);
        h.Router.Dispatch(Line("Tank started to follow you."));   // with us, this side of the gate

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback"));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // The gate was crossed before the loop began (a train trip's walk, the walk to
    // the circuit, a step by hand): it isn't the loop's, so the loop won't leave
    // them there again and the member is gone back for.
    [Fact]
    public void GateCrossedBeforeTheLoop_NotOnItsCircuit_MemberIsGoneBackFor()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Tracker.SetLocated(new RoomKey(1, 1));   // through the item exit, not on any loop
        h.Party.ExpectComebackFrom("Tank");
        Assert.True(h.Loop.Start(new Loop("circuit", [new RoomKey(1, 1), new RoomKey(1, 3)])));

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Contains("coming to your location", h.LastReply);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // Held, or too heavy, on the very step that takes the loop through a gated
    // exit: the exit didn't turn them away, so they are gone back for.
    [Fact]
    public void LoopThroughAGate_MemberWhoSignalledAHold_IsGoneBackFor()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.MemberSignalledHold = name => name == "Tank";
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.LeftAtLoopGates);
    }

    // What the leader holds of the member decides where it can: one the exit is
    // known to admit was stopped by something else; one it is known to turn away,
    // or can't be judged, counts as turned away.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void LoopThroughAGate_WhatIsKnownOfTheMemberDecides(bool? canPass, bool goneBackFor)
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.MemberCanPass = (_, exit) => exit.AdmitsOnlySome ? canPass : true;
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal(goneBackFor ? "Tank" : null, h.Comeback.RecoveringMember);
    }

    // A circuit that begins in 1/4 and leaves it east by the exit that needs an item.
    private static void StartLoopAtTheGate(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Loop.Start(new Loop("circuit", [new RoomKey(1, 4), new RoomKey(1, 3)])));
    }

    private static void StepThroughTheGate(Harness h)
    {
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.SetLocated(new RoomKey(1, 1));
    }

    // The same exit on a walk-to: the leader goes back, re-invites, and waits for
    // the follow.
    [Fact]
    public void WalkToThroughAGate_FollowerDroppedThere_IsGoneBackFor_AndReInvited()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));

        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains("coming to your location", h.LastReply);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);

        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.Contains("invite Tank", partyWire);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);   // waiting for the follow

        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // A follower who rejoins before their turn is nobody's to fetch.
    [Fact]
    public void QueuedFollower_BackBeforeTheirTurn_IsNotFetched()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Healer started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Router.Dispatch(Line("Healer is no longer following you."));

        h.Router.Dispatch(Line("Healer started to follow you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.QueuedRecoveries);
    }

    // A member waiting their turn who calls it off is not fetched.
    [Fact]
    public void QueuedFollower_WhoSendsForget_IsTakenOffTheQueue()
    {
        using Harness h = NewHarness();
        h.Engine.ForgetEligibility = h.Party.WasRecentlyPartied;   // as the app wires it
        h.Tracker.SetLocated(new RoomKey(1, 2));
        StartLair(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Healer started to follow you."));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Router.Dispatch(Line("Healer is no longer following you."));
        Assert.Equal(new[] { "Healer" }, h.Comeback.QueuedRecoveries);

        ChatLines.Hear(h.Router, Telepath("Healer", "@forget"));

        Assert.Empty(h.Comeback.QueuedRecoveries);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // Probe3_G1. What we hold of the member says the exit lets them through, and
    // the game keeps turning them away (a stale level, a purse the toll emptied).
    // Fetched once and dropped at the same exit again, they are gated there for
    // this loop run: refused from then on, however long after.
    [Fact]
    public void LoopThroughAGate_FetchedOnce_DroppedThereAgain_IsRefusedFromThenOn()
    {
        using Harness h = NewHarness(GatedGraphJson);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        h.Comeback.MemberCanPass = (_, _) => true;
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));

        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);       // lap one: gone back for
        AlsoHere(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);

        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);                // lap two: not again
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
        Assert.Equal(new[] { "Tank at 1/4" }, h.Comeback.LeftAtLoopGates);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains(PartyComebackManager.LoopGateRefusal, h.LastReply);

        now += h.Comeback.ComebackWindow + TimeSpan.FromMinutes(5);
        int sent = h.EngineWire.Count;
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Equal(sent + 1, h.EngineWire.Count);
        Assert.Contains(PartyComebackManager.LoopGateRefusal, h.LastReply);
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // The loop the user stops and starts again is a new run: the member is judged
    // afresh on it.
    [Fact]
    public void LoopRestartedByTheUser_AMemberProvenGated_IsJudgedAfresh()
    {
        using Harness h = NewHarness(GatedGraphJson);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        h.Comeback.MemberCanPass = (_, _) => true;
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));
        AlsoHere(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Router.Dispatch(Line("Tank started to follow you."));
        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Null(h.Comeback.RecoveringMember);

        h.Loop.Stop();
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.LeftAtLoopGates);
    }

    // The wait a member has out with us says why they were left, and is let go
    // when they are. It is read first by the same handler that lets it go, so the
    // reading can't depend on which listener of the game's line runs before which.
    [Fact]
    public void LeftBehind_TheMembersWaitIsReadBeforeItIsLetGo()
    {
        using Harness h = NewHarness(GatedGraphJson);
        HashSet<string> waiting = new() { "Tank" };
        h.Comeback.MemberSignalledHold = waiting.Contains;
        h.Comeback.ReleaseMemberWait = name => waiting.Remove(name);
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);   // the hold, not the exit
        Assert.Empty(waiting);
    }

    // An @ok a moment before being left behind is the premature-@ok case: they said
    // they were free and didn't move, so it reads as a hold.
    [Fact]
    public void LoopThroughAGate_AnOkAMomentBefore_ReadsAsAHold()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OkedWithin = (name, window) => name == "Tank" && window == TimeSpan.FromSeconds(5);
        StartLoopAtTheGate(h);
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // ----- a walk-to through an exit that turns a member away -------------------

    // The strip with two gated exits on the way from 1/4 to 1/3: east out of 1/4
    // and north out of 1/1.
    private const string TwoGatesGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Item: 475)", "S": "0", "E": "0", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1 (Item: 474)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A walk from 1/4 to 1/3 whose first step, east, drops Tank; the leader goes
    // back, re-invites, and the walk is under way again from 1/4.
    private static void WalkThroughTheGate_PickUpOnce(Harness h, Action tick)
    {
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        tick();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        AlsoHere(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.NotEqual(WalkState.Idle, h.Walker.State);
    }

    // The walk went back once. Turned away at the same exit again, it would be
    // every time: the walk ends there with a notice naming the member and the
    // exit, and no second pickup is started (user, 2026-10-10: "option A").
    [Fact]
    public void WalkToThroughAGate_DroppedThereAgainAfterThePickup_EndsTheWalk_WithANotice()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> notices = new();
        h.Comeback.Notice = notices.Add;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        WalkThroughTheGate_PickUpOnce(h, () => now += TimeSpan.FromSeconds(1));

        now += TimeSpan.FromSeconds(1);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        string notice = Assert.Single(notices);
        Assert.Contains("Tank", notice);
        Assert.Contains("east out of 1/4", notice);

        // Their request afterwards is answered by where that leaves the leader.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains("I can't I'm idle", h.LastReply);
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // Another gated exit further on is another matter: it gets its own one pickup.
    [Fact]
    public void WalkToThroughAGate_ADifferentExitLater_GetsItsOwnPickup()
    {
        using Harness h = NewHarness(TwoGatesGraphJson);
        List<string> notices = new();
        h.Comeback.Notice = notices.Add;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        WalkThroughTheGate_PickUpOnce(h, () => now += TimeSpan.FromSeconds(1));

        now += TimeSpan.FromSeconds(1);
        h.Tracker.SetLocated(new RoomKey(1, 1));               // through the first exit, Tank with us
        now += TimeSpan.FromSeconds(1);
        h.Tracker.SetLocated(new RoomKey(1, 2));               // through the second
        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(notices);
    }

    // Stock tells a leader nothing of the drop, so the member's request is the
    // first it hears of the second one: the walk ends, and the answer is the one
    // an idle leader gives.
    [Fact]
    public void WalkToThroughAGate_NoLineFromTheGame_TheSecondRequestEndsTheWalk()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> notices = new();
        h.Comeback.Notice = notices.Add;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        SeatFollower(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        now += TimeSpan.FromSeconds(1);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        now += TimeSpan.FromSeconds(1);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        h.Tracker.SetLocated(new RoomKey(1, 4));
        now += TimeSpan.FromSeconds(1);
        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Null(h.Comeback.RecoveringMember);

        now += TimeSpan.FromSeconds(1);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        now += TimeSpan.FromSeconds(1);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Single(notices);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Contains("I can't I'm idle", h.LastReply);
    }

    // ----- Auto-Lair and an exit that turns a member away -----------------------

    // Auto-Lair doesn't break off for them (user, 2026-10-10: "auto-lair will just
    // pick them up again on its next pass"). It carries on, answers their request
    // once, and invites them when a pass next finds them in a room it enters.
    [Fact]
    public void AutoLairThroughAGate_CarriesOn_AnswersOnce_AndInvitesOnSight()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
        Assert.Equal(new[] { "Tank" }, h.Comeback.InviteOnSight);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains(PartyComebackManager.LairGateReply, h.LastReply);
        int sent = h.EngineWire.Count;
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Equal(sent, h.EngineWire.Count);     // said once
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
        Assert.DoesNotContain("invite Tank", partyWire);

        AlsoHere(h, "Tank");                                     // a later pass finds them
        Assert.Single(partyWire, w => w == "invite Tank");
        AlsoHere(h, "Tank");                                     // the room shown again
        Assert.Single(partyWire, w => w == "invite Tank");

        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Empty(h.Comeback.InviteOnSight);
    }

    // Held on that same step, the exit isn't why: gone back for as before.
    [Fact]
    public void AutoLairThroughAGate_AMemberWhoSignalledAHold_IsGoneBackFor()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.MemberSignalledHold = name => name == "Tank";
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        h.Router.Dispatch(Line("Tank started to follow you."));
        StepThroughTheGate(h);

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.InviteOnSight);
    }

    // ----- a party train trip -------------------------------------------------

    // The trip's walk goes through an exit that turns a follower away. The game
    // tells the leader; the trip's walk is not stopped, and nobody is gone back for.
    [Fact]
    public void TrainTrip_FollowerDroppedOnTheWay_DoesNotStopTheTripsWalk()
    {
        using Harness h = NewHarness(GatedGraphJson);
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));     // the trip's walk
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));             // through the item exit

        h.Router.Dispatch(Line("Tank is no longer following you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Equal(new[] { "Tank at 1/4" }, h.Comeback.LeftOnTrainTrip);
    }

    // A member on a client that holds nothing back asks in the middle of the trip:
    // told once that they will be fetched after training, and kept, with the room
    // they name in place of the one we had.
    [Fact]
    public void TrainTrip_ComebackDuringTheTrip_IsAnsweredOnce_AndKept()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.TrainTripRunning = () => true;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));
        Assert.Contains(PartyComebackManager.TrainTripReply, h.LastReply);
        int replies = h.EngineWire.Count;
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));

        Assert.Equal(replies, h.EngineWire.Count);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new[] { "Tank at 1/2" }, h.Comeback.LeftOnTrainTrip);
    }

    // The trip is over and its engine is back: everyone it left is fetched, one
    // after the other, from the room each was left in.
    [Fact]
    public void TrainTrip_Over_EveryoneItLeftIsFetchedInTurn()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Healer started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Router.Dispatch(Line("Healer is no longer following you."));
        h.Walker.Stop("the trip's walk is done");
        tripOn = false;
        StartLair(h);                                        // the engine the trip had paused

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank", "Healer"]);

        Assert.Empty(h.Comeback.LeftOnTrainTrip);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new[] { "Healer" }, h.Comeback.QueuedRecoveries);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);

        AlsoHere(h, "Tank", "Healer");                       // both stand where the trip left them
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.Contains("invite Tank", partyWire);
        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Equal("Healer", h.Comeback.RecoveringMember);
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Router.Dispatch(Line("Healer started to follow you."));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
    }

    // The trip began with no walk or loop running, so there is none to go back to:
    // the fetch runs all the same. The trip was ours, so the fetch is.
    [Fact]
    public void TrainTrip_Over_WithNoEngineToReturnTo_StillFetches()
    {
        using Harness h = NewHarness(GatedGraphJson);
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 1)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        tripOn = false;
        Assert.Equal(WalkState.Idle, h.Walker.State);

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
    }

    // The leader's own train run with the party in tow keeps no roll: nobody is
    // gone back for in the middle of it, and whom the game said it dropped is
    // fetched when the run is over.
    [Fact]
    public void OwnTrainRun_WithNoRoll_KeepsWhomItLeft_AndFetchesAtItsEnd()
    {
        using Harness h = NewHarness(GatedGraphJson);
        bool runOn = true;
        h.Comeback.TrainTripRunning = () => runOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));       // the run's walk to its trainer
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        h.Walker.Stop("the run's walk is done");
        runOn = false;

        h.Comeback.TrainTripEnded(byItself: true, setOut: []);

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
    }

    // Probe3_T4. The user ended the trip (a second Stop, a run started over it).
    // The player has taken over, so nothing walks back on its own: who was left
    // is named once, and their own request is still a member's, answered by what
    // the leader is doing when it comes.
    [Fact]
    public void TrainTrip_TakenOver_NobodyIsFetched_TheyAreNamedOnce_AndStayMembers()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> notices = new();
        h.Comeback.Notice = notices.Add;
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Walker.Stop("user stop");
        tripOn = false;

        h.Comeback.TrainTripEnded(byItself: false, setOut: ["Tank", "Scout"]);

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        string notice = Assert.Single(notices);
        Assert.Contains("Tank at 1/4", notice);
        Assert.Contains("Scout (room not known)", notice);
        Assert.True(h.Party.WasRecentlyPartied("Tank"));
        Assert.True(h.Party.WasRecentlyPartied("Scout"));

        // Their request is answered by the leader's state: idle here.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains("I can't I'm idle", h.LastReply);
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // Probe3_T5. The trip ended further off than the return distance. The fetch is
    // the trip's own and isn't bound by it; a request out of the blue still is.
    [Fact]
    public void TrainTrip_Over_TheFetchIsNotBoundByTheReturnDistance()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> wire = new();
        h.Comeback.SetWireSender(b => wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Comeback.ReturnDistanceRooms = 1;
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 3));               // three rooms from 1/4
        tripOn = false;

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);

        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
        Assert.DoesNotContain(wire, w => w.Contains("@forget"));
    }

    [Fact]
    public void OrdinaryComeback_IsStillBoundByTheReturnDistance()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> wire = new();
        h.Comeback.SetWireSender(b => wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Comeback.ReturnDistanceRooms = 1;
        SeatFollower(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 3));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 1)));

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Contains(wire, w => w == "/Tank @forget");
    }

    // Probe3_T3. Stock tells a leader nothing of a follower it drops, so the trip
    // ends with nobody on its list. The roll knows who set out: the request that
    // follows `trip off` is the trip's fetch, though the leader has no engine to
    // go back to and the member is further off than the return distance.
    [Fact]
    public void TrainTrip_Over_AMemberTheGameNeverReported_IsFetchedOnTheirRequest_EvenIdle()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.ReturnDistanceRooms = 1;
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        SeatFollower(h, "Tank");                               // still on our list: no drop line came
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 3));
        tripOn = false;
        Assert.Equal(WalkState.Idle, h.Walker.State);

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);
        Assert.Null(h.Comeback.RecoveringMember);              // nowhere to go yet

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Contains("coming to your location", h.LastReply);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);
    }

    // The same request once the window a @comeback is taken in has passed is an
    // ordinary one again.
    [Fact]
    public void TrainTrip_Over_ARequestLongAfter_IsAnOrdinaryOne()
    {
        using Harness h = NewHarness(GatedGraphJson);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        SeatFollower(h, "Tank");
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);
        now += h.Comeback.ComebackWindow + TimeSpan.FromSeconds(1);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));

        Assert.Contains("I can't I'm idle", h.LastReply);
    }

    // The room the trip left them in was ours to guess (the one our step left when
    // the game dropped them). Empty on arrival, nobody is invited into it and the
    // engine isn't held for the follow wait: the member says where they are.
    [Fact]
    public void TrainTrip_Fetch_ARoomWeGuessed_IsCheckedOnArrival()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Walker.Stop("the trip's walk is done");
        tripOn = false;
        StartLair(h);
        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);

        h.Tracker.SetLocated(new RoomKey(1, 4));               // nobody here

        Assert.DoesNotContain("invite Tank", partyWire);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Empty(h.ComebackTimers.PendingDelays);
        Assert.True(h.Lair.IsActive);
        Assert.Equal(new[] { "Tank" }, h.Comeback.AwaitedAfterTrainTrip);

        // They say where they are: fetched as the trip's own.
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/2"));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
    }

    // On the way to the room we guessed, the member names another: go there.
    [Fact]
    public void TrainTrip_Fetch_TheMemberNamesAnotherRoomOnTheWay_WeGoThere()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 3));
        tripOn = false;
        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);
        Assert.Equal(new RoomKey(1, 4), h.Walker.Destination);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);

        // Named by them, so no looking round on arrival: invited at once.
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Contains("invite Tank", partyWire);
    }

    // The trip ended with the master switch off. Whom it left is kept, and fetched
    // when the switch is back on, by the rule a @comeback kept for the switch is
    // replayed by: only inside the window a @comeback is taken in.
    [Theory]
    [InlineData(30, true)]
    [InlineData(600, false)]
    public void TrainTrip_OverWithTheMasterSwitchOff_FetchesWhenItIsBackOn_IfStillFresh(int secondsOff, bool fetched)
    {
        using Harness h = NewHarness(GatedGraphJson);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        bool tripOn = true, masterOff = false;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Comeback.MasterSwitchOff = () => masterOff;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 1)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        tripOn = false;
        masterOff = true;

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Equal(new[] { "Tank at 1/4" }, h.Comeback.TripFetchOwed);

        now += TimeSpan.FromSeconds(secondsOff);
        masterOff = false;
        h.Comeback.SettleAfterMasterSwitch();

        Assert.Equal(fetched ? "Tank" : null, h.Comeback.RecoveringMember);
        Assert.Empty(h.Comeback.TripFetchOwed);
    }

    // Someone the trip left who is following again by its end is nobody's to fetch.
    [Fact]
    public void TrainTrip_Over_AMemberBackWithUs_IsNotFetched()
    {
        using Harness h = NewHarness(GatedGraphJson);
        bool tripOn = true;
        h.Comeback.TrainTripRunning = () => tripOn;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 1)));
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Router.Dispatch(Line("Tank is no longer following you."));
        h.Router.Dispatch(Line("Tank started to follow you."));
        tripOn = false;

        h.Comeback.TrainTripEnded(byItself: true, setOut: ["Tank"]);

        Assert.Null(h.Comeback.RecoveringMember);
    }

    // "If leading, wait only" at 0 means no limit. A re-invited follower who never
    // answers must still not hold the stopped engine for good.
    [Fact]
    public void FollowWaitSetToNoLimit_StillEndsForAFollowerWhoNeverAnswers()
    {
        using Harness h = NewHarness();
        h.Comeback.FollowWaitWindow = TimeSpan.Zero;
        SeatFollower(h, "Tank");
        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));   // there, and re-invited

        Assert.NotEmpty(h.ComebackTimers.PendingDelays);
        h.ComebackTimers.RunAll();

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.True(h.Lair.IsActive);
    }

    // ----- registration shape ----------------------------------------

    [Fact]
    public void Dispose_UnregistersHandlers()
    {
        Harness h = NewHarness();
        h.Comeback.Dispose();
        Assert.False(h.Engine.UnregisterHandler("@comeback"));   // already gone
        Assert.False(h.Engine.UnregisterHandler("@forget"));

        h.Lair.Dispose();
        h.Timers.Dispose();
        h.Party.Dispose();
        h.Engine.Dispose();
    }

    // ----- a follower an exit refused: Stock keeps them on the leader's list -----
    //
    // GAME_MECHANICS "A follower who can't move is left behind". With the realm
    // probe unset or false (Paradigm) nothing differs from what it always was;
    // the tests named Paradigm_ pin that.

    // Six rows, the member asking among them.
    private static void SeatSix(Harness h, string asking)
    {
        SeatFollower(h, asking);
        for (int i = 1; i <= 5; i++) SeatFollower(h, $"Member{i}");
    }

    [Fact]
    public void Paradigm_PartyOfSix_TheAskingMembersOwnRowCounts_PartyIsFull()
    {
        using Harness h = NewHarness();
        SeatSix(h, "Tank");
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains("my party is full", h.LastReply);
        Assert.True(Sent(h, "/Tank @forget"));
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // On Stock the member an exit turned away is still on our list: the sixth row
    // is their own seat, not one we refilled.
    [Fact]
    public void Stock_PartyOfSix_TheAskingMembersOwnRowIsTheirSeat_TheyAreFetched()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        SeatSix(h, "Tank");
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/3"));

        Assert.Contains(PartyComebackManager.ComingForPickup, h.LastReply);
        Assert.False(Sent(h, "/Tank @forget"));
        Assert.Equal("Tank", h.Comeback.RecoveringMember);
    }

    // A seventh asking, with no row of their own: the party is full on Stock too.
    [Fact]
    public void Stock_PartyOfSix_SomeoneWithNoRowAsks_PartyIsFull()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        for (int i = 1; i <= 6; i++) SeatFollower(h, $"Member{i}");
        h.Router.Dispatch(Line("Straggler started to follow you."));
        h.Router.Dispatch(Line("Straggler stops following you."));
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Straggler", "@comeback 1/3"));

        Assert.Contains("my party is full", h.LastReply);
    }

    // A member with a row on our roster asks from the room we stand in, so they
    // are reached at once (ExplicitRoom_AlreadyThere_ReInvitesFollower has why the
    // tracker is settled after the request). Returns what PartyManager sent.
    private static List<string> ReachAMemberStillOnOurRoster(Harness h)
    {
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        SeatFollower(h, "Tank");
        StartLair(h);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Contains("found you - re-inviting Tank", h.LastReply);
        return partyWire;
    }

    [Fact]
    public void Paradigm_ReachingAMemberWithARosterRow_Invites_AndAsksNobodyToJoin()
    {
        using Harness h = NewHarness();

        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.False(Sent(h, "/Tank @join"));
    }

    // On Stock a member still on our list needs no invite and is printed nothing
    // by one: their client is told to join.
    [Fact]
    public void Stock_ReachingAMemberStillOnOurList_TellsThemToJoin_AndSendsNoInvite()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;

        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        Assert.True(Sent(h, "/Tank @join"));
        Assert.Empty(partyWire);
        Assert.Equal(new[] { "Tank" }, h.Comeback.AskedToJoin);
        Assert.Equal("Tank", h.Comeback.RecoveringMember);       // waiting for the follow
    }

    [Fact]
    public void Stock_AskedToJoin_TheyFollow_NoInviteIsEverSent()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        h.Router.Dispatch(Line("Tank started to follow you."));
        now += TimeSpan.FromSeconds(30);
        h.ComebackTimers.RunAll();

        Assert.Empty(partyWire);
        Assert.Empty(h.Comeback.AskedToJoin);
        Assert.Null(h.Comeback.RecoveringMember);
    }

    // The fallback: the join didn't take (the game told them "You must be invited
    // first!", or their client doesn't take our @join), so they are invited as
    // they always were.
    [Fact]
    public void Stock_AskedToJoin_NoFollowInTime_TheyAreInvitedAfterAll()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        List<string> partyWire = ReachAMemberStillOnOurRoster(h);
        void Wait(double seconds)
        {
            now += TimeSpan.FromSeconds(seconds);
            h.ComebackTimers.Advance(TimeSpan.FromSeconds(seconds));
        }

        Wait(2);
        Assert.Empty(partyWire);                                 // still inside the wait

        Wait(4);
        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.Empty(h.Comeback.AskedToJoin);

        Wait(4);
        Assert.Single(partyWire);                                // once
        Assert.Equal("Tank", h.Comeback.RecoveringMember);       // the follow is still waited for
    }

    // A member who had a @wait out was held when we moved: the game took them off
    // our list, so they are invited outright.
    [Fact]
    public void Stock_AMemberWhoSignalledAHold_IsInvitedOutright()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        h.Comeback.MemberSignalledHold = name => name == "Tank";

        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.False(Sent(h, "/Tank @join"));
    }

    // No row on our roster: not on our list, so an invite, on Stock as anywhere.
    [Fact]
    public void Stock_AMemberWithNoRosterRow_IsInvited()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        SeatFollower(h, "Healer");
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank stops following you."));
        StartLair(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.False(Sent(h, "/Tank @join"));
    }

    // The invite has to be out before the pickup stops waiting: with "If leading,
    // wait only" at 3 s the join gets half of it, not the usual 5 s, which would
    // have let the pickup time out with no invite ever sent.
    [Fact]
    public void Stock_AShortFollowWait_InvitesHalfwayThroughIt_BeforeThePickupGivesUp()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        h.Comeback.FollowWaitWindow = TimeSpan.FromSeconds(3);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        List<string> partyWire = ReachAMemberStillOnOurRoster(h);
        Assert.True(Sent(h, "/Tank @join"));
        void Wait(double seconds)
        {
            now += TimeSpan.FromSeconds(seconds);
            h.ComebackTimers.Advance(TimeSpan.FromSeconds(seconds));
        }

        Wait(1);
        Assert.Empty(partyWire);

        Wait(0.6);
        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.Equal("Tank", h.Comeback.RecoveringMember);       // still inside the 3 s

        Wait(1.5);
        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Single(partyWire);
    }

    // "If leading, wait only" at 0: nothing is waited for, so the invite goes with
    // the @join, as it always went on its own.
    [Fact]
    public void Stock_NoFollowWaitSet_InvitesWithTheJoin()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        h.Comeback.FollowWaitWindow = TimeSpan.Zero;

        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        Assert.True(Sent(h, "/Tank @join"));
        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.Empty(h.Comeback.AskedToJoin);
    }

    // "Send @join nags to invited members" off: the pickup sends no @join on Stock
    // either, and invites as it always did.
    [Fact]
    public void Stock_JoinNagsSwitchedOff_ThePickupSendsNoAtJoin_AndInvitesAtOnce()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        h.Comeback.SendJoinToInvited = false;

        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        Assert.False(Sent(h, "/Tank @join"));
        Assert.Single(partyWire, w => w == "invite Tank");
        Assert.Empty(h.Comeback.AskedToJoin);
    }

    // A pickup the member calls off ends what it owed: nobody is invited
    // afterwards from wherever we have walked on to.
    [Fact]
    public void Stock_AskedToJoin_ThePickupIsCalledOff_NoLateInvite()
    {
        using Harness h = NewHarness();
        h.Comeback.OnStock = () => true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        List<string> partyWire = ReachAMemberStillOnOurRoster(h);

        ChatLines.Hear(h.Router, Telepath("Tank", "@forget"));
        now += TimeSpan.FromSeconds(30);
        h.ComebackTimers.RunAll();

        Assert.Null(h.Comeback.RecoveringMember);
        Assert.Empty(partyWire);
    }

    // The game told us nothing of the drop (as on Stock); the member's request
    // is how we know. They still have a row on our roster.
    private static List<string> LeftAtAnExitByAutoLair_NoLineFromTheGame(Harness h)
    {
        List<string> partyWire = new();
        h.Party.SetWireSender(b => partyWire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.ComebackNow = () => now;
        h.Tracker.SetLocated(new RoomKey(1, 4));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        h.Router.Dispatch(Line("Tank started to follow you."));
        now += TimeSpan.FromSeconds(1);
        StepThroughTheGate(h);
        now += TimeSpan.FromSeconds(1);
        ChatLines.Hear(h.Router, Telepath("Tank", "@comeback 1/4"));
        Assert.Contains(PartyComebackManager.LairGateReply, h.LastReply);
        Assert.Equal(new[] { "Tank" }, h.Comeback.InviteOnSight);
        return partyWire;
    }

    [Fact]
    public void Paradigm_AutoLairOnSight_PassesOverAMemberWithARosterRow()
    {
        using Harness h = NewHarness(GatedGraphJson);
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);

        AlsoHere(h, "Tank");

        Assert.DoesNotContain("invite Tank", partyWire);
        Assert.False(Sent(h, "/Tank @join"));
    }

    private static int JoinsAsked(Harness h, string given) =>
        h.ComebackWire.Count(b => Encoding.Latin1.GetString(b).Contains($"/{given} @join"));

    // On Stock the row never left, so it can't mean they are back: the pass that
    // finds them tells them to join, as it promised.
    [Fact]
    public void Stock_AutoLairOnSight_TellsAMemberStillOnOurListToJoin()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OnStock = () => true;
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);

        AlsoHere(h, "Tank");
        Assert.Equal(1, JoinsAsked(h, "Tank"));
        Assert.Empty(partyWire);
        AlsoHere(h, "Tank");                                     // the room shown again
        Assert.Equal(1, JoinsAsked(h, "Tank"));

        h.Router.Dispatch(Line("Tank started to follow you."));
        Assert.Empty(h.Comeback.InviteOnSight);
        Assert.Empty(h.Comeback.AskedToJoin);
        Assert.Empty(partyWire);
    }

    // With the @join nags switched off the pass that finds them invites, as on
    // Paradigm, and sends no @join.
    [Fact]
    public void Stock_AutoLairOnSight_JoinNagsSwitchedOff_Invites()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OnStock = () => true;
        h.Comeback.SendJoinToInvited = false;
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);

        AlsoHere(h, "Tank");

        Assert.Equal(0, JoinsAsked(h, "Tank"));
        Assert.Single(partyWire, w => w == "invite Tank");
    }

    // The join wait is over, as the clock the helper pinned would never show.
    private static void LetTheJoinWaitRunOut(Harness h)
    {
        DateTimeOffset later = DateTimeOffset.UtcNow.AddSeconds(30);
        h.ComebackNow = () => later;
        h.ComebackTimers.RunAll();
    }

    // Still standing with them when the join wait runs out: invited after all.
    [Fact]
    public void Stock_AutoLairOnSight_NoFollowAfterTheJoin_StillInTheRoom_InvitesAfterAll()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OnStock = () => true;
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);

        AlsoHere(h, "Tank");
        h.ComebackTimers.RunAll();
        Assert.Empty(partyWire);                                 // inside the join wait

        LetTheJoinWaitRunOut(h);

        Assert.Equal(1, JoinsAsked(h, "Tank"));
        Assert.Single(partyWire, w => w == "invite Tank");
    }

    // Auto-Lair has walked on by then: an invite from the next room reaches nobody
    // we stand with, so none is sent, and the pass that next finds them asks again.
    [Fact]
    public void Stock_AutoLairOnSight_WalkedOnBeforeTheJoinWaitRanOut_NoInvite_AndTheNextPassAsksAgain()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OnStock = () => true;
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);
        AlsoHere(h, "Tank");

        AlsoHere(h, "Healer");                                   // the next room: Tank isn't in it
        LetTheJoinWaitRunOut(h);

        Assert.Empty(partyWire);
        Assert.Empty(h.Comeback.AskedToJoin);
        Assert.Equal(new[] { "Tank" }, h.Comeback.InviteOnSight);   // still owed

        AlsoHere(h, "Tank");                                     // a pass finds them again
        Assert.Equal(2, JoinsAsked(h, "Tank"));
    }

    // Auto-Lair was stopped: nothing is owed any more, and no invite follows.
    [Fact]
    public void Stock_AutoLairOnSight_AutoLairStoppedBeforeTheJoinWaitRanOut_NoInvite()
    {
        using Harness h = NewHarness(GatedGraphJson);
        h.Comeback.OnStock = () => true;
        List<string> partyWire = LeftAtAnExitByAutoLair_NoLineFromTheGame(h);
        AlsoHere(h, "Tank");

        h.Lair.Stop("test");
        LetTheJoinWaitRunOut(h);

        Assert.Empty(partyWire);
        Assert.Empty(h.Comeback.AskedToJoin);
    }
}
