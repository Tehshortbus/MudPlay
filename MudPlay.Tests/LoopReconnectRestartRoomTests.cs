using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// A loop's restart after a reconnect, on the wire: the game's chunks go through the
// prompt scanner first and then the terminal, as MainWindowViewModel feeds them, so
// the reconnect's prompt is read ahead of the room display that came with it. The
// restart waits for that display, and plans its first step from the room it shows.
//
//   1/3 Tower
//     |N
//   1/2 Yard
//     |N
//   1/1 Gate ─E─ 1/4 Corridor ─E─ 1/5 Corridor ─E─ 1/6 Cellar
//
// The loop goes round Gate and Yard. The two corridors look the same.
public sealed class LoopReconnectRestartRoomTests
{
    private static readonly RoomKey Gate = new(1, 1);
    private static readonly RoomKey Yard = new(1, 2);
    private static readonly RoomKey Tower = new(1, 3);
    private static readonly RoomKey Corridor = new(1, 4);

    private static readonly string Rooms = TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 1, "Gate", ("N", "1/2"), ("E", "1/4")),
        TrackerWireRig.Room(1, 2, "Yard", ("S", "1/1"), ("N", "1/3")),
        TrackerWireRig.Room(1, 3, "Tower", ("S", "1/2")),
        TrackerWireRig.Room(1, 4, "Corridor", ("W", "1/1"), ("E", "1/5")),
        TrackerWireRig.Room(1, 5, "Corridor", ("W", "1/4"), ("E", "1/6")),
        TrackerWireRig.Room(1, 6, "Cellar", ("W", "1/5")),
    });

    private static Loop GateYard() => new("gy", new[] { Gate, Yard });

    private sealed class World : IDisposable
    {
        public TrackerWireRig Rig { get; }
        public LoopRunner Runner { get; }
        // The runner's and the `rm` resolver's timers.
        public ManualTimers Ticks { get; } = new();
        // The terminal notices the restart gives.
        public List<string> Notices { get; } = new();
        private readonly List<IDisposable> _dispose = new();

        // Logged in, standing in `at`. paradigm: a Paradigm realm, whose `rm` the
        // recovery gate's one-shot locate asks, wired as AppServices wires it.
        // located: the login's display placed us (false: a room the map can't tell).
        public World(RoomKey at, bool paradigm = false, bool located = true)
        {
            Rig = new TrackerWireRig(Rooms, prompt: "[HP=100/MA=50]:", paradigm: paradigm) { Position = at };
            Runner = new LoopRunner(Rig.Tracker, Rig.Coordinator, Rig.Prompts, Rig.Log, Rig.Graph, Rig.Gate,
                new BfsMapper(Rig.Graph), Rig.Walker, postToUi: Rig.Post, schedule: Ticks.Arm);
            Runner.SetWireSender(Rig.Send);
            Runner.SetReconnectNotice(Notices.Add);
            Runner.SetLinkTimes(() => Rig.LastSentAt, () => Rig.LastReadAt);
            ParadigmPositionResolver resolver = new(Rig.Router, Rig.Tracker, Rig.Gate, Rig.Cache, Rig.Log, schedule: Ticks.Arm);
            resolver.SetWireSender(Rig.Send);
            Rig.Gate.TryResyncOnce = resolver.RequestResyncOnce;
            _dispose.Add(resolver);
            Rig.ShowRoom();
            if (located) Assert.Equal(at, Rig.Believed);
        }

        // A leader's party, its reform after a reconnect and the hold a dropped
        // member puts on movement, wired as AppServices wires them.
        public PartyReformCoordinator? Reform { get; private set; }

        public void LeadParty(string follower)
        {
            PartyManager party = new(Rig.Router, new PartyState(), Rig.Log);
            Reform = new PartyReformCoordinator(Rig.Router, party, log: Rig.Log);
            PartyDisconnectMovementGate hold = new(party, Rig.Coordinator, Rig.Log);
            _dispose.AddRange(new IDisposable[] { hold, Reform, party });
            PartyReformCoordinator reform = Reform;
            Runner.SetReconnectReformProbe(() => reform.PendingReform.Count > 0);
            Rig.Show($"{follower} started to follow you.");
        }

        // The link drops, in the order MainWindowViewModel's handler tells the pieces.
        public void Drop()
        {
            Reform?.NoteDisconnected();
            Runner.NotifyDisconnected();
            Rig.Tracker.NoteConnectionLost();
            Rig.DropLink();
        }

        // Back in the game: the realm shows the room `at` and its prompt, in one chunk.
        public void Reconnect(RoomKey at)
        {
            Reform?.Arm();
            Rig.Position = at;
            Rig.ShowRoom();
        }

        public int Mark() => Rig.Sent.Count;
        public List<string> SentSince(int mark) => Rig.Sent.Skip(mark).ToList();

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_dispose)) d.Dispose();
            Rig.Dispose();
        }
    }

    // Back where the link dropped: the move sent before the drop never landed, and
    // the loop takes it again from the room the reconnect shows.
    [Fact]
    public void ReconnectIntoTheSameRoom_TheLoopRestartsFromIt()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        Assert.Equal("n", w.Rig.Sent.Last());          // never answered: the link drops
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
        w.Rig.Pump(limit: 1);
        Assert.Equal(Yard, w.Rig.Believed);
    }

    // The wifi goes first and the drop is counted later: the step goes unanswered,
    // and recovery gives the loop up on it before the client calls the link lost.
    // The server never answered its last command, so the failure was the link's: the
    // loop restarts after the reconnect, as one still going at the drop does (report
    // paradigm-20261031-051153).
    [Fact]
    public void LoopFailedOnALinkAlreadyDead_RestartsAfterTheReconnect()
    {
        using World w = new(Gate);
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(w.Runner.Start(GateYard()));
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(30));
        w.Runner.AbortFromRecoveryFailure("reverse move S got no answer in 20 s");
        Assert.Equal(LoopState.Idle, w.Runner.State);
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(10));
        w.Drop();
        Assert.Equal("gy", w.Runner.PendingReconnectResumeName);
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
    }

    // The game answered after the loop gave up: the link was up then, and the loop
    // stays given up when the link drops later.
    [Fact]
    public void LoopFailedWhileTheGameStillAnswered_IsNotRestartedByALaterDrop()
    {
        using World w = new(Gate);
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(w.Runner.Start(GateYard()));
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(30));
        w.Runner.AbortFromRecoveryFailure("reverse move S got no answer in 20 s");
        w.Rig.Do("look");
        w.Rig.Clock.Advance(TimeSpan.FromMinutes(5));
        w.Drop();

        Assert.Null(w.Runner.PendingReconnectResumeName);
        w.Reconnect(Gate);
        Assert.Equal(LoopState.Idle, w.Runner.State);
    }

    // The realm puts the character back in another room. Nothing may be planned from
    // the room known before the drop: the first step is the walk back from the room
    // the reconnect shows.
    [Fact]
    public void ReconnectIntoAnotherRoom_NoStepIsPlannedFromTheOldRoom()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Tower);

        Assert.Equal(Tower, w.Rig.Believed);
        Assert.Equal("s", Assert.Single(w.SentSince(mark)));    // Tower → Yard, not Gate's "n"
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        w.Rig.Pump(limit: 1);
        Assert.Equal(Yard, w.Rig.Believed);
    }

    // The reconnect shows a room the map can't tell from another. The loop doesn't
    // wait for good (user, 2026-10-17): it asks the game where we are (`rm`), and
    // restarts from the room the answer names.
    [Fact]
    public void ReconnectWhereTheTrackerIsUnsure_AsksTheGameAndRestartsFromItsAnswer()
    {
        using World w = new(Gate, paradigm: true);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Corridor);

        Assert.NotEqual(RoomConfidence.Confirmed, w.Rig.Tracker.State.Confidence);
        Assert.Equal(new[] { "rm" }, w.SentSince(mark));       // no step, a question
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeLocating);
        Assert.Equal("gy", w.Runner.PendingReconnectResumeName);

        w.Rig.NextAnswers.Enqueue("Location: 1,4");
        w.Rig.Pump(limit: 1);

        Assert.Equal(Corridor, w.Rig.Believed);
        Assert.Equal(new[] { "rm", "w" }, w.SentSince(mark));  // Corridor → Gate
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.False(w.Runner.ReconnectResumeLocating);
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // The game never answers the locate. The restart isn't left waiting, nor ended:
    // the loop starts from the room the map believes, as it did before the wait, and
    // its recovery puts it right. The terminal says so.
    [Fact]
    public void ReconnectWhereTheTrackerIsUnsure_TheLocateFails_StartsFromTheBelievedRoom()
    {
        using World w = new(Gate, paradigm: true);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();
        w.Reconnect(Corridor);
        Assert.True(w.Runner.ReconnectResumeLocating);

        w.Rig.Pump(limit: 1);                                 // `rm` answered with nothing it reads
        w.Ticks.Advance(TimeSpan.FromSeconds(4));             // the resolver's reply timeout

        Assert.Null(w.Runner.PendingReconnectResumeName);
        Assert.False(w.Runner.ReconnectResumeLocating);
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.Equal(new[] { "rm", "n" }, w.SentSince(mark));  // from Gate, the room believed
        Assert.Contains(w.Notices, n => n.StartsWith("[Loop 'gy' started from 1/1 after the reconnect, though the map isn't sure", StringComparison.Ordinal)
            && n.Contains("the game didn't say where we are"));
    }

    // A realm with nothing to ask (no `rm`, no sysop): the loop starts from the
    // room the map believes at once.
    [Fact]
    public void ReconnectWhereTheTrackerIsUnsure_WithNothingToAsk_StartsFromTheBelievedRoom()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Corridor);

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
        Assert.Contains(w.Notices, n => n.Contains("nothing to ask the game where we are with"));
    }

    // The link drops on a step between two rooms that look the same, and the
    // reconnect shows the one it left: the map can't say which, and on Stock with
    // nothing to ask the loop goes on from the room it left, the recovery gate on
    // hand, as before the restart waited for the room.
    [Fact]
    public void DropBetweenTwoLookalikeRooms_OnStock_TheLoopGoesOnFromTheRoomItLeft()
    {
        using World w = new(Gate);
        w.Rig.Do("e");
        w.Rig.Tracker.SetLocated(Corridor);                     // the user puts the map right
        Assert.True(w.Runner.Start(new Loop("cc", new[] { Corridor, new RoomKey(1, 5) })));
        Assert.Equal("e", w.Rig.Sent.Last());
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Corridor);

        Assert.Equal(RoomConfidence.Suspect, w.Rig.Tracker.State.Confidence);
        Assert.Equal(new[] { "e" }, w.SentSince(mark));
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.Single(w.Notices);
    }

    // No room believed at all (the map never placed us): nothing to start from. The
    // restart is ended, and the terminal says so.
    [Fact]
    public void ReconnectWithNoRoomBelievedAtAll_TheRestartIsEnded()
    {
        using World w = new(Corridor, located: false);
        Assert.Null(w.Rig.Tracker.State.CurrentRoom);
        w.Runner.ResumeOnReconnect(GateYard(), "a light buy the connection cut off");
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Corridor);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
        Assert.Equal(new[] { "[Loop 'gy' not started after the reconnect: the map has no room to start it from]" }, w.Notices);
    }

    // No room display and no further prompt (an idle character at full HP): the
    // wait has a time limit, and the loop starts from the room believed when it runs out.
    [Fact]
    public void ReconnectWithNoRoomDisplayAndNoMorePrompts_StartsWhenTheWaitRunsOut()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Rig.Show("Welcome back!");
        Assert.True(w.Runner.ReconnectResumeAwaitsRoom);
        Assert.Empty(w.SentSince(mark));

        w.Ticks.Advance(ReconnectRestartLimits.RoomWaitMax);

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.Contains(w.Notices, n => n.Contains("no room known within 12s"));
    }

    // The game's no-answer to a locate asked before the link dropped again belongs
    // to that wait: it neither starts the loop on the dead link nor ends the restart
    // owed to the next reconnect.
    [Fact]
    public void ALocatesNoAnswer_AfterAnotherDrop_LeavesTheNextRestartAlone()
    {
        using World w = new(Gate, paradigm: true);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        w.Reconnect(Corridor);
        Assert.True(w.Runner.ReconnectResumeLocating);
        int mark = w.Mark();

        w.Drop();                                             // the `rm` goes unanswered
        w.Ticks.Advance(TimeSpan.FromSeconds(4));             // its timeout fires offline

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.Equal("gy", w.Runner.PendingReconnectResumeName);
        Assert.Empty(w.Notices);

        w.Reconnect(Gate);
        Assert.Equal(new[] { "n" }, w.SentSince(mark));
    }

    // A reconnect whose room display hasn't come (the move sent before the drop still
    // unanswered): the first prompt waits for it, the next one asks the game.
    [Fact]
    public void ReconnectWithNoRoomDisplay_AsksTheGameAtTheNextPrompt()
    {
        using World w = new(Gate, paradigm: true);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        Assert.Equal(RoomConfidence.Pending, w.Rig.Tracker.State.Confidence);
        int mark = w.Mark();

        w.Rig.Show("Welcome back!");
        Assert.Empty(w.SentSince(mark));
        Assert.True(w.Runner.ReconnectResumeAwaitsRoom);

        w.Rig.Show("Somebody waves.");
        Assert.Equal(new[] { "rm" }, w.SentSince(mark));

        w.Rig.NextAnswers.Enqueue("Location: 1,1");
        w.Rig.Pump(limit: 1);
        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal(new[] { "rm", "n" }, w.SentSince(mark));
    }

    // The user's Stop while the restart waits on the locate calls it off; the
    // answer that comes after restarts nothing.
    [Fact]
    public void RestartWaitingForTheRoom_IsDroppedByAStop()
    {
        using World w = new(Gate, paradigm: true);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        w.Reconnect(Corridor);
        Assert.True(w.Runner.ReconnectResumeLocating);
        int mark = w.Mark();

        w.Runner.DropHeldReconnectResume("user stop from toolbar");
        w.Rig.NextAnswers.Enqueue("Location: 1,4");
        w.Rig.Pump(limit: 1);

        Assert.Equal(Corridor, w.Rig.Believed);
        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // A loop the user starts while the restart is still owed is the run: the one set
    // aside at the drop doesn't come back over it.
    [Fact]
    public void RestartStillOwed_GivesWayToALoopTheUserStarts()
    {
        using World w = new(Gate);
        bool off = true;
        w.Runner.MasterSwitchOff = () => off;
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        w.Reconnect(Gate);
        Assert.True(w.Runner.ReconnectResumeHeldForMasterSwitch);

        Assert.True(w.Runner.Start(new Loop("mine", new[] { Gate, Yard }), userStarted: true));
        off = false;
        w.Runner.ResumeAfterMasterSwitch();

        Assert.Equal("mine", w.Runner.CurrentLoop?.Name);
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // The master switch off at the reconnect: nothing restarts, and switched back on
    // the loop restarts from the room the reconnect showed.
    [Fact]
    public void ReconnectWithTheMasterSwitchOff_RestartsFromTheShownRoomWhenItIsBackOn()
    {
        using World w = new(Gate);
        bool off = true;
        w.Runner.MasterSwitchOff = () => off;
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Tower);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeHeldForMasterSwitch);

        off = false;
        w.Runner.ResumeAfterMasterSwitch();

        Assert.Equal("s", Assert.Single(w.SentSince(mark)));
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // A leader's reconnect with followers to gather (report paradigm-20260923-092317):
    // the reform reads the room display that came with the prompt, so its hold is up
    // by the time the loop restarts, and the loop takes no step until the follower
    // is back.
    [Fact]
    public void ReconnectWithAPartyReform_TheLoopRestartsBehindTheReformsHold()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Paused, w.Runner.State);
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, w.Rig.Coordinator.AssertedGates);
        Assert.False(w.Runner.ReconnectResumeHeldForReform);

        w.Rig.Show("Tank started to follow you.");

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
    }

    // A room too dark to display gives the reform nothing to read: the loop restarts
    // behind a hold of its own until the reform has had its look.
    [Fact]
    public void ReconnectIntoADarkRoomWithAPartyReform_TheLoopIsHeldForTheReform()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        w.Runner.ResumeOnReconnect(GateYard(), "a light buy the connection cut off");
        w.Drop();
        w.Rig.DarkRooms.Add(Gate);
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Paused, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeHeldForReform);
        Assert.Contains(MovementCoordinator.ReconnectReformGate, w.Rig.Coordinator.AssertedGates);
    }

    // A loop a light buy handed over when the connection cut it off (an errand had
    // stopped it, so nothing was running at the drop) restarts from the room the
    // reconnect shows, not the shop's.
    [Fact]
    public void ALoopHandedOverByALightBuy_RestartsFromTheShownRoom()
    {
        using World w = new(Tower);
        w.Drop();
        w.Runner.ResumeOnReconnect(GateYard(), "a light buy the connection cut off");
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal(new[] { "n" }, w.SentSince(mark));          // from the gate, not Tower's "s"
        Assert.Equal(LoopState.Running, w.Runner.State);
    }
}
