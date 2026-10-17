using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The engines walk by room numbers, which are other rooms under another game-data
// set and may be after the active one is imported again. Either stops a loop,
// Auto-Lair and a walk-to in progress, drops what was held to start by itself, and
// starts nothing again; a SwitchSet to the name already active, which the cache
// answers with no new load, does nothing. And the set a
// running loop is stamped with is the one it was started on, through every re-plan
// of the same run, and the snapshot of the last run is not run under another set.
public sealed class GameDataSwitchStopsMovementTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-setswitch-stop-" + Path.GetRandomFileName());
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (IDisposable d in _dispose) d.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string Room(int room, string name, string n = "0", string s = "0", string e = "0", string w = "0") => $$"""
          { "Map Number": 1, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "{{n}}", "S": "{{s}}", "E": "{{e}}", "W": "{{w}}",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // alpha: 1/1 ↔ 1/2 ↔ 1/3 north to south. beta: the same room numbers wired east
    // to west, so a plan made on one is wrong on the other.
    private static readonly string Alpha =
        $"[\n{Room(1, "A", n: "1/2")},\n{Room(2, "B", n: "1/3", s: "1/1")},\n{Room(3, "C", s: "1/2")}\n]";
    private static readonly string Beta =
        $"[\n{Room(1, "Other A", e: "1/2")},\n{Room(2, "Other B", e: "1/3", w: "1/1")},\n{Room(3, "Other C", w: "1/2")}\n]";

    private sealed class AvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    private sealed class Harness
    {
        public required GameDataCache Cache { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required EngineRecoveryGate Gate { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }
        public required AutoLairManager Lairs { get; init; }
        public required MovementController Controller { get; init; }
        public required AvoidFilter Filter { get; init; }
        public required LogService Log { get; init; }
        // The timers the engines keep, and the game's prompts as they read them.
        public required ManualTimers Ticks { get; init; }
        public required WirePromptScanner Prompt { get; init; }
        // Every command an engine or the recovery gate put on the wire, in order.
        public List<string> Sent { get; } = new();

        // What the stop said it ended at the last change of game data, and whether the
        // handler that makes it is in play.
        public string? Ended;
        public bool StopOnChange = true;

        // The whole of what the last change of game data came to (null when there
        // was nothing to say), and the profile event the next one is made for.
        public StopOnGameDataChange.Outcome? Outcome;
        public string? Because;

        // The item needs of a route, which the app forgets after the stop for another
        // set (ForgetNeeds false leaves that out), and each call made after a stop,
        // with whether every engine was at rest by then.
        public NeedsRegistry Needs { get; } = new();
        public bool ForgetNeeds = true;
        public List<(GameDataLoadChange Change, bool AtRest)> AfterStops { get; } = new();

        // The graph on another set with no stop made: what the stop is there to
        // prevent, for the tests of what a run does without it.
        public void GraphOn(string set)
        {
            StopOnChange = false;
            Cache.SwitchSet(set);
            StopOnChange = true;
        }

        // Another set becomes the active one, handled in the app's order (OnAlpha).
        public string? SwitchTo(string? set)
        {
            Ended = null;
            Outcome = null;
            Cache.SwitchSet(set);
            return Ended;
        }

        // The active set is read in again, as an import over it does, and as the
        // Game Data menu's pick of the set already ticked does.
        public string? ReadInAgain()
        {
            Ended = null;
            Outcome = null;
            Cache.ReloadActiveSet();
            return Ended;
        }

        // What would move a run that was still alive, forty times over: the character
        // found in a room, the game naming it, a bonked move, prompts, the master
        // switch coming back on, and every timer the engines keep.
        public void LetTimePass()
        {
            for (int round = 0; round < 40; round++)
            {
                RoomKey at = new(1, 1 + round % 3);
                Tracker.SetLocated(at);
                Gate.NoteAuthoritativePosition(at);
                Tracker.NoteMoveBlocked();
                Gate.OnCombatTick();
                Runner.ResumeAfterMasterSwitch();
                Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
                Ticks.RunAll();
                Controller.Resume();
                Controller.NoteErrandStateChanged();
            }
        }

        public bool Idle => Runner.State == LoopState.Idle && !Lairs.IsActive && Walker.State == WalkState.Idle
            && Controller.IsIdle;
    }

    // startOn null: a client with no game data yet, as it is built at start, ahead
    // of the first set its profile applies.
    private Harness OnAlpha(string? startOn = "alpha")
    {
        foreach ((string set, string rooms) in new[] { ("alpha", Alpha), ("beta", Beta) })
        {
            Directory.CreateDirectory(Path.Combine(_root, set));
            File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), rooms);
        }
        GameDataCache cache = new(_root);
        if (startOn is not null) cache.SwitchSet(startOn);
        RoomGraphManager graph = new(cache);
        if (startOn is not null) graph.OnActiveSetChanged(startOn);
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        LogService log = new();
        EngineRecoveryGate gate = new(graph, tracker);
        AvoidFilter filter = new();
        WirePromptScanner prompt = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord, log: log, promptScanner: prompt, recovery: gate);
        ManualTimers ticks = new();
        walker.SetVoyageScheduler(ticks.Arm);
        LoopRunner runner = new(tracker, coord, promptScanner: prompt, log: log, graph: graph, recovery: gate, bfs: bfs,
            filter: filter, postToUi: a => a(), schedule: ticks.Arm);
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lairs = new(walker, tracker, graph, bfs, timers, log, coord, schedule: ticks.Arm);
        MovementController controller = new(walker, runner, lairs, coord, log);
        _dispose.Add(controller);
        _dispose.Add(lairs);
        _dispose.Add(timers);
        Harness h = new()
        {
            Cache = cache, Graph = graph, Tracker = tracker, Bfs = bfs, Gate = gate, Walker = walker,
            Runner = runner, Lairs = lairs, Controller = controller, Filter = filter, Log = log, Ticks = ticks, Prompt = prompt,
        };
        void Send(byte[] b) => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r'));
        walker.SetWireSender(Send);
        runner.SetWireSender(Send);
        gate.SetWireSender(Send);
        gate.TryResync = _ => true;
        gate.TryResyncOnce = (_, _, _) => false;

        // The subscriptions AppServices makes, in its order: the stop, which is the
        // decision the app's own handler asks (StopOnGameDataChange); the graph, whose
        // reload tells the tracker and the path cache; then the marks. Only the
        // subscribing is a copy: the app itself can't be built here.
        // What the app does after the stop is a copy too (its
        // ForgetRouteItemsOfOtherGameData): the item needs go when the set is another.
        StopOnGameDataChange stop = new(() => cache.ActiveLoad, () => h.StopOnChange ? controller : null,
            afterStop: change =>
            {
                h.AfterStops.Add((change, h.Idle));
                if (h.ForgetNeeds && !change.ReadInAgain) h.Needs.Clear(NeedKind.PathItem);
            });
        cache.ActiveSetChanged += _ =>
        {
            h.Outcome = stop.OnActiveSetChanged(h.Because);
            h.Ended = h.Outcome?.Ended;
        };
        cache.ActiveSetChanged += graph.OnActiveSetChanged;
        graph.GraphReloaded += () => tracker.OnGraphReloaded();
        graph.GraphReloaded += bfs.OnGraphReloaded;
        cache.ActiveSetChanged += set => lairs.DropMarksOfOtherGameData(set);
        return h;
    }

    private static Loop Ab() => new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

    private const string Reason = "game data changed from 'alpha' to 'beta'";

    // ----- a switch to another set stops what is walking ----------------------------

    [Fact]
    public void ARunningLoop_IsStopped_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        Assert.Equal(LoopState.Running, h.Runner.State);

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab' stopped", ended);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Null(h.Runner.CurrentLoop);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == $"Loop 'ab' stopped: {Reason}");
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner" && e.Message.EndsWith($"reason={Reason}", StringComparison.Ordinal));

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void AutoLairInProgress_IsStopped_ItsMarksGo_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3));
        Assert.True(h.Lairs.Start());

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Auto-Lair stopped", ended);
        Assert.False(h.Lairs.IsActive);
        Assert.Empty(h.Lairs.Marked);
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "AutoLair" && e.Message == $"stop: {Reason}");

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void AWalkTo_IsStopped_WithTheReasonOnItsLastEvent_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Walk to 1/3 stopped", ended);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Null(h.Walker.Journey);
        // The bug report's "Last walk event" line reads this.
        Assert.Equal(WalkEventKind.Stopped, h.Walker.LastEvent?.Kind);
        Assert.Equal(Reason, h.Walker.LastEvent?.Detail);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    // A SwitchSet to the name already active: the cache raises nothing, and the stop
    // asked with the same name and no fresh load does nothing either. (The Game Data
    // menu's pick of the ticked set is a reload, covered by the reload tests.)
    [Fact]
    public void SwitchingToTheSetAlreadyActive_IsANoOp()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        int stops = 0;
        h.Controller.Stopping += () => stops++;

        Assert.Null(h.SwitchTo("alpha"));
        Assert.Null(h.SwitchTo("ALPHA"));
        Assert.Null(h.Controller.StopForGameDataChange("alpha", "alpha"));

        Assert.Equal(0, stops);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    // ----- the active set read in again stops it too --------------------------------

    private const string ReadAgain = "game data 'alpha' was read in again";

    [Fact]
    public void TheActiveSetReadInAgain_StopsARunningLoop_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        string? ended = h.ReadInAgain();

        Assert.Equal("Loop 'ab' stopped", ended);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == $"Loop 'ab' stopped: {ReadAgain}");

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
        // By name it is still this set's: it may be run again when asked.
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
        Assert.Null(h.Runner.RefusalFor(h.Runner.LastRunLoop!));
    }

    [Fact]
    public void TheActiveSetReadInAgain_StopsAutoLair_AndKeepsItsMarks()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3), overrideRespawnSeconds: 90);
        Assert.True(h.Lairs.Start());

        string? ended = h.ReadInAgain();

        Assert.Equal("Auto-Lair stopped", ended);
        Assert.False(h.Lairs.IsActive);
        Assert.Equal(2, h.Lairs.Marked.Count);
        Assert.Equal(90, h.Lairs.GetOverride(new RoomKey(1, 3)));
        Assert.Equal("alpha", h.Lairs.MarksGameData);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void TheActiveSetReadInAgain_StopsAWalkTo_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        string? ended = h.ReadInAgain();

        Assert.Equal("Walk to 1/3 stopped", ended);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(ReadAgain, h.Walker.LastEvent?.Detail);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void TheActiveSetReadInAgain_DropsALoopHeldForAReconnect()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        string[] sent = h.Sent.ToArray();

        string? ended = h.ReadInAgain();

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.Null(h.Runner.PendingReconnectResumeName);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void WithNothingRunning_TheStopIsStillMade_AndNothingIsSaid()
    {
        Harness h = OnAlpha();
        int stops = 0;
        string? during = null;
        List<string> endings = new();
        h.Controller.Stopping += () => { stops++; during = h.Controller.GameDataChangeStop; };
        h.Controller.EndingForGameDataChange += (reason, _) => endings.Add(reason);

        Assert.Null(h.SwitchTo("beta"));

        // What the Stop button does with nothing running, with the reason to read.
        Assert.Equal(1, stops);
        Assert.Equal(Reason, during);
        Assert.Null(h.Controller.GameDataChangeStop);
        Assert.Equal(new[] { Reason }, endings);
        Assert.True(h.Idle);
        Assert.Empty(h.Sent);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    // What a subscriber ended is named with the rest.
    [Fact]
    public void WhatASubscriberEnded_IsNamedInTheNotice()
    {
        Harness h = OnAlpha();
        h.Controller.EndingForGameDataChange += (_, stopped) => stopped.Add("the party pick-up");

        Assert.Equal("The party pick-up stopped", h.SwitchTo("beta"));
    }

    // ----- what would start a run again by itself is dropped ------------------------

    // The link dropped with a loop running; the set changed before the way back in.
    [Fact]
    public void ALoopHeldForAReconnect_IsDropped_AndTheFirstPromptBackStartsNothing()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        Assert.Equal("ab", h.Runner.PendingReconnectResumeName);
        string[] sent = h.Sent.ToArray();

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.Null(h.Runner.PendingReconnectResumeName);
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "Movement"
            && e.Message == $"Loop 'ab', held for a reconnect, dropped: {Reason}");

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    // Back in the game with the master switch off, the restart waits for the switch.
    [Fact]
    public void ALoopHeldForTheMasterSwitch_IsDropped_AndTheSwitchComingBackOnStartsNothing()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        bool off = true;
        h.Runner.MasterSwitchOff = () => off;
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        Assert.True(h.Runner.ReconnectResumeHeldForMasterSwitch);
        string[] sent = h.Sent.ToArray();

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.False(h.Runner.ReconnectResumeHeldForMasterSwitch);

        off = false;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.ResumeAfterMasterSwitch();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void ALoopLoadedToStart_IsDropped()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(Ab());

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', loaded to start, dropped", ended);
        Assert.Null(h.Runner.StagedLoop);
    }

    [Fact]
    public void ARunStopped_AndAnotherHeld_AreBothNamed()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(new Loop("next", new[] { new RoomKey(1, 2), new RoomKey(1, 3) }));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        Assert.Equal("Loop 'ab' stopped; loop 'next', loaded to start, dropped", h.SwitchTo("beta"));
    }

    // ----- the snapshot of the last run is not run again under another set -----------

    [Fact]
    public void TheLastRunLoop_IsRefusedUnderAnotherSet_AndKeepsItsStamp()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.SwitchTo("beta");
        Loop last = h.Runner.LastRunLoop!;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        string[] sent = h.Sent.ToArray();

        // What "@loop last" asks before it starts anything, and replies with.
        Assert.Equal(
            "loop 'ab' was last run on game data 'alpha', and 'beta' is active now: its rooms are that game data's",
            h.Runner.RefusalFor(last));
        Assert.False(h.Runner.Start(last));

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal(sent, h.Sent);
        Assert.Same(last, h.Runner.LastRunLoop);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void TheLastRunLoop_RunsAgainOnItsOwnSet()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.Stop();
        Loop last = h.Runner.LastRunLoop!;

        Assert.Null(h.Runner.RefusalFor(last));
        Assert.True(h.Runner.Start(last));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void AnErrandUnderWay_IsEndedWithTheWalkItDrives()
    {
        Harness h = OnAlpha();
        List<string> abandoned = new();
        string? errand = "the stash transfer";
        h.Controller.SetErrandHooks(() => errand, reason => { abandoned.Add(reason); errand = null; }, _ => Task.FromResult(true));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Walk to 1/3 and the stash transfer stopped", ended);
        Assert.Equal(new[] { Reason }, abandoned);
        Assert.True(h.Idle);
    }

    // A walk the user started while Stop held an errand waits on "Resume first?".
    // It was a walk on the last set's rooms: the answer starts nothing.
    [Fact]
    public void ARunWaitingOnTheResumeQuestion_IsNotStartedOnceAnswered()
    {
        Harness h = OnAlpha();
        string? errand = "the stash transfer";
        TaskCompletionSource<bool> answer = new();
        h.Controller.SetErrandHooks(() => errand, _ => errand = null, _ => answer.Task);
        Assert.True(h.Controller.HoldErrandOnStop());
        bool started = false;
        // The question is awaited under the pump, so the answer is acted on here, when
        // the test lets it, and not before the assertions or after them.
        ContinuationPump pump = new();
        pump.Run(() => h.Controller.StartUserRun(() => started = true));

        string? ended = h.SwitchTo("beta");
        answer.SetResult(false);
        Assert.Equal(1, pump.Drain());   // the answer was taken

        Assert.Equal("The stash transfer stopped", ended);
        Assert.False(started);
        Assert.Null(h.Controller.SuspendedErrand);
    }

    // ----- a walk still being planned is not started on the old data's room ----------

    // The route cards plan off the UI thread and wait on the party and the bank
    // (RouteChoicePrompt, which needs the whole app and can't be built here). This
    // is the shape of it: the plan is awaited, then asked whether the game data
    // overtook it, and only then made a walk. The stop the change makes finds no
    // walk to stop while the plan is out, and the walker itself takes any room
    // number: without the question the walk below starts, on the new set's rooms.
    private static async Task<string?> PlanThenWalk(Harness h, RoomKey destination, Task planned, bool ask = true)
    {
        RoutePlanInFlight inFlight = new(destination, () => h.Cache.ActiveLoad, h.Log);
        await planned;
        if (ask && inFlight.Dropped() is { } notice) return notice;
        h.Walker.WalkTo(destination);
        return null;
    }

    [Fact]
    public async Task APlanInFlightAcrossASwitch_StartsNoWalk_AndSaysWhy()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TaskCompletionSource planned = new();
        Task<string?> walk = PlanThenWalk(h, new RoomKey(1, 3), planned.Task);

        // Nothing is walking yet, so the stop has nothing to say.
        Assert.Null(h.SwitchTo("beta"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        planned.SetResult();

        Assert.Equal(
            "[Walk to 1/3 not started: the game data changed from 'alpha' to 'beta' while its route was being planned]",
            await walk);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Empty(h.Sent);
        Assert.Single(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "RoutePick"
            && e.Message == "route pick -> 1/3: dropped, no walk started: the game data changed from 'alpha' to 'beta' while its route was being planned");
        h.LetTimePass();
        Assert.Empty(h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public async Task APlanInFlightAcrossAReimport_StartsNoWalk_AndSaysWhy()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TaskCompletionSource planned = new();
        Task<string?> walk = PlanThenWalk(h, new RoomKey(1, 3), planned.Task);

        Assert.Null(h.ReadInAgain());
        h.Tracker.SetLocated(new RoomKey(1, 1));
        planned.SetResult();

        Assert.Equal(
            "[Walk to 1/3 not started: game data 'alpha' was read in again while its route was being planned]",
            await walk);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Empty(h.Sent);
    }

    // Away to another set and back is not the load the plan was made on either.
    [Fact]
    public async Task APlanInFlightAcrossASwitchAwayAndBack_StartsNoWalk()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TaskCompletionSource planned = new();
        Task<string?> walk = PlanThenWalk(h, new RoomKey(1, 3), planned.Task);

        h.SwitchTo("beta");
        h.SwitchTo("alpha");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        planned.SetResult();

        Assert.NotNull(await walk);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task APlanInFlightWithNoChangeOfGameData_StartsItsWalk()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TaskCompletionSource planned = new();
        Task<string?> walk = PlanThenWalk(h, new RoomKey(1, 3), planned.Task);

        // A SwitchSet to the name already active is no new load.
        h.SwitchTo("alpha");
        h.SwitchTo("ALPHA");
        planned.SetResult();

        Assert.Null(await walk);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Equal(new[] { "n" }, h.Sent);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "RoutePick");
    }

    // What the question is there for: the same plan, not asked, walks off on the
    // other set's rooms after the stop has been and gone.
    [Fact]
    public async Task WithoutThatQuestion_ThePlanStartsAWalkOnTheOtherSetsRooms()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TaskCompletionSource planned = new();
        Task<string?> walk = PlanThenWalk(h, new RoomKey(1, 3), planned.Task, ask: false);

        Assert.Null(h.SwitchTo("beta"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        planned.SetResult();
        await walk;

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new[] { "e" }, h.Sent);
    }

    // ----- which changes of game data the stop is made for ---------------------------

    // The client is built with no game data, and the first set arrives with the
    // profile it loads at start: nothing is running or held, and nothing is said.
    [Fact]
    public void TheFirstSetAppliedAtStart_SaysNothing()
    {
        Harness h = OnAlpha(startOn: null);

        Assert.Null(h.SwitchTo("alpha"));

        Assert.Null(h.Outcome);
        Assert.True(h.Idle);
        Assert.Empty(h.Sent);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    [Fact]
    public void ASwitch_IsSaidAsAChangeFromOneSetToTheOther()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(Ab());

        h.SwitchTo("beta");

        Assert.Equal(
            new StopOnGameDataChange.Outcome("Loop 'ab', loaded to start, dropped", "the game data changed from 'alpha' to 'beta'"),
            h.Outcome);
        Assert.Equal(
            "[Loop 'ab', loaded to start, dropped: the game data changed from 'alpha' to 'beta']", h.Outcome?.Notice);
    }

    // An import over the active set, a rewrite of it in Manage Sets and the Game Data
    // menu's pick of the set already ticked all reach the cache as ReloadActiveSet.
    [Fact]
    public void TheActiveSetReadInAgain_IsSaidAsReadInAgain()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(Ab());

        h.ReadInAgain();

        Assert.Equal(
            "[Loop 'ab', loaded to start, dropped: game data 'alpha' was read in again]", h.Outcome?.Notice);
    }

    [Fact]
    public void TheGameDataUnloaded_IsSaidAsAChangeToNone()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(Ab());

        h.SwitchTo(null);

        Assert.Equal("the game data changed from 'alpha' to '(none)'", h.Outcome?.Why);
    }

    // Until the movement controller is built nothing is running and no stop is made,
    // but the load is followed: the first change it does see is told from the set
    // that was active then, not from the one the client came up on.
    [Fact]
    public void AChangeBeforeTheControllerExists_IsFollowed_SoTheNextIsToldFromIt()
    {
        Harness h = OnAlpha();
        h.GraphOn("beta");
        h.Runner.Stage(Ab());

        Assert.Null(h.SwitchTo("BETA"));
        Assert.Equal("ab", h.Runner.StagedLoop?.Name);

        h.SwitchTo("alpha");
        Assert.Equal("the game data changed from 'beta' to 'alpha'", h.Outcome?.Why);
    }

    // ----- what is let go once the stop has been made --------------------------------

    [Fact]
    public void AfterTheStop_TheCallFindsEveryEngineAtRest_AndIsToldTheChange()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        h.SwitchTo("beta");
        h.ReadInAgain();
        h.SwitchTo("BETA");

        Assert.Equal(
            new[]
            {
                (new GameDataLoadChange(GameDataLoadChange.Kind.AnotherSet, "alpha", "beta"), true),
                // Made with nothing running too, as the stop itself is.
                (new GameDataLoadChange(GameDataLoadChange.Kind.ReadInAgain, "beta", "beta"), true),
            },
            h.AfterStops);
    }

    // A route's item need is kept by item number, and the floor pickup `get`s
    // whatever carries a wanted number when it is seen on the floor. Under another
    // set number 42 is another item.
    private static PathItemFloorCollector FloorPickup(Harness h, List<string> got) =>
        new(h.Needs,
            isOnFloor: _ => true,
            itemName: id => h.Cache.ActiveSet == "alpha" ? $"brass key {id}" : $"silver ring {id}",
            send: got.Add);

    [Fact]
    public void ARouteItemNeed_IsForgottenUnderAnotherSet_AndNothingIsPickedUpForIt()
    {
        Harness h = OnAlpha();
        List<string> got = new();
        PathItemFloorCollector pickup = FloorPickup(h, got);
        h.Needs.Post(NeedKind.PathItem, "42", "test");
        h.Needs.Post(NeedKind.LightSource, "any", "test");

        h.SwitchTo("beta");
        pickup.CollectRevealed();

        Assert.Empty(got);
        Assert.Empty(h.Needs.Outstanding(NeedKind.PathItem));
        // A need that names no record of the game data is still a need.
        Assert.Single(h.Needs.Outstanding(NeedKind.LightSource));
    }

    // What forgetting it guards: the need left standing has the other set's item
    // of that number picked up off the floor, with nothing running.
    [Fact]
    public void WithoutThat_TheNeedHasTheOtherSetsItemPickedUp()
    {
        Harness h = OnAlpha();
        h.ForgetNeeds = false;
        List<string> got = new();
        PathItemFloorCollector pickup = FloorPickup(h, got);
        h.Needs.Post(NeedKind.PathItem, "42", "test");

        h.SwitchTo("beta");
        pickup.CollectRevealed();

        Assert.Equal(new[] { "get silver ring 42" }, got);
    }

    // The same set read in again keeps its item numbers, and the need with them.
    [Fact]
    public void ARouteItemNeed_IsKeptWhenTheSameSetIsReadInAgain()
    {
        Harness h = OnAlpha();
        List<string> got = new();
        PathItemFloorCollector pickup = FloorPickup(h, got);
        h.Needs.Post(NeedKind.PathItem, "42", "test");

        h.ReadInAgain();
        pickup.CollectRevealed();

        Assert.Equal(new[] { "get brass key 42" }, got);
        Assert.Single(h.Needs.Outstanding(NeedKind.PathItem));
    }

    // ----- a profile closed or loaded switches the set too ---------------------------

    // Loading another profile closes the one loaded, and with no profile the set is
    // the first BBS's: a swap between two characters on alpha can pass through
    // beta. What was held for the character that left is dropped at the close, and
    // said to have gone with the profile; coming back to alpha finds nothing left.
    [Fact]
    public void ASwapThroughAnotherSet_DropsWhatWasHeld_AndSaysItWasTheProfile()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        h.Runner.Stage(new Loop("next", new[] { new RoomKey(1, 2), new RoomKey(1, 3) }));
        string[] sent = h.Sent.ToArray();

        h.Because = "the profile was closed";
        h.SwitchTo("beta");

        Assert.Equal(
            "[Loop 'ab', held for a reconnect, and loop 'next', loaded to start, dropped: "
            + "the profile was closed, and with it the game data changed from 'alpha' to 'beta']",
            h.Outcome?.Notice);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == "Loop 'ab', held for a reconnect, and loop 'next', loaded to start, dropped: "
                + "the profile was closed, and with it game data changed from 'alpha' to 'beta'");
        Assert.Null(h.Runner.PendingReconnectResumeName);
        Assert.Null(h.Runner.StagedLoop);

        h.Because = "a profile was loaded";
        Assert.Null(h.SwitchTo("alpha"));
        Assert.Null(h.Outcome);

        // The next character's first prompt in the game starts nothing of the last one's.
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    // The reason every cancel is given, and the walk's last event keeps, leads with
    // the profile too.
    [Fact]
    public void AStopMadeForAProfileEvent_GivesThatReasonToWhatItEnds()
    {
        Harness h = OnAlpha();
        List<string> endings = new();
        h.Controller.EndingForGameDataChange += (reason, _) => endings.Add(reason);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        h.Because = "a profile was loaded";
        h.SwitchTo("beta");

        const string reason = "a profile was loaded, and with it game data changed from 'alpha' to 'beta'";
        Assert.Equal(new[] { reason }, endings);
        Assert.Equal(reason, h.Walker.LastEvent?.Detail);
        Assert.Equal("Walk to 1/3 stopped", h.Ended);
    }

    // ----- the stamp of a running loop is the run's ---------------------------------

    [Fact]
    public void AFreshStart_StampsTheSetActiveThen()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
        h.Runner.Stop();

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        Assert.Equal("beta", h.Runner.LastRunLoopGameData);
    }

    // The graph changes under a run with nothing stopping it (what the stop is there
    // to prevent): the recovery that re-plans the loop must not make it this set's.
    [Fact]
    public void ARecoveryReroute_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Gate.NoteAuthoritativePosition(new RoomKey(1, 1));

        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner"
            && e.Message.StartsWith("recovery reroute:", StringComparison.Ordinal));
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void AResumeAfterADetour_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = Ab();
        h.Runner.Start(loop);
        h.Runner.Stop("detour", willResume: true);

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Runner.ResumeAfterDetour(loop));

        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void AnAvoidListReroute_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("ac", new[] { new RoomKey(1, 1), new RoomKey(1, 3) }));

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        // The room the circle starts from: on the path whatever the graph makes of it.
        h.Filter.Avoided.Add(new RoomKey(1, 1));
        h.Runner.NotifyAvoidedChanged();

        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner"
            && e.Message.StartsWith("avoid-list changed; loop 'ac' path crosses an avoided room", StringComparison.Ordinal));
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    // ----- a profile swap drops the loops held for the character that left ---------

    // The profile events as ProfileService raises them, told to the guard the app
    // subscribes. Its posts run when the test says.
    private sealed class Swap
    {
        public string? Character = "bbs/Alice";
        public List<string> Notices { get; } = new();
        public List<Action> Posted { get; } = new();
        public HeldLoopsOnProfileSwap Guard = null!;   // set by SwapOn

        public void RunPosted()
        {
            Action[] posted = Posted.ToArray();
            Posted.Clear();
            foreach (Action a in posted) a();
        }

        // ProfileService.Load over a loaded profile: it closes, then the next loads.
        public void Load(string character)
        {
            Character = null;
            Guard.OnProfileClosed();
            Character = character;
            Guard.OnProfileLoaded();
            RunPosted();
        }

        // Save As naming the draft, a re-home, its BBS renamed: ProfileRenamed.
        public void Rename(string character)
        {
            Character = character;
            Guard.OnProfileRenamed();
        }

        public void Close()
        {
            Character = null;
            Guard.OnProfileClosed();
            RunPosted();
        }
    }

    private static Swap SwapOn(Harness h)
    {
        Swap swap = new();
        swap.Guard = new HeldLoopsOnProfileSwap(
            () => h.Controller, () => swap.Character, swap.Notices.Add, swap.Posted.Add);
        swap.Guard.OnProfileLoaded();
        return swap;
    }

    // Disconnect with a loop running, load another character, reconnect: nothing starts.
    [Fact]
    public void ALoopHeldForAReconnect_IsDropped_WhenAnotherCharacterIsLoaded()
    {
        Harness h = OnAlpha();
        Swap swap = SwapOn(h);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        string[] sent = h.Sent.ToArray();

        swap.Load("bbs/Bob");

        Assert.Equal(new[] { "[Loop 'ab', held for a reconnect, dropped: another character was loaded]" }, swap.Notices);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == "Loop 'ab', held for a reconnect, dropped: another character was loaded");
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));
        h.LetTimePass();
        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    // The same character loaded again keeps it: the reconnect restarts the loop as ever.
    [Fact]
    public void ALoopHeldForAReconnect_IsKept_WhenTheSameCharacterIsLoadedAgain()
    {
        Harness h = OnAlpha();
        Swap swap = SwapOn(h);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();

        swap.Load("bbs/Alice");
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));

        Assert.Empty(swap.Notices);
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    // Named after its loop was set aside, the character loaded again under that name
    // is the same one.
    [Fact]
    public void ALoopHeldForAReconnect_IsKept_WhenTheRenamedCharacterIsLoadedAgain()
    {
        Harness h = OnAlpha();
        Swap swap = SwapOn(h);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();

        swap.Rename("bbs/Alicia");
        swap.Load("bbs/Alicia");
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));

        Assert.Empty(swap.Notices);
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void ALoopLoadedToStart_IsDropped_WhenAnotherCharacterIsLoaded()
    {
        Harness h = OnAlpha();
        Swap swap = SwapOn(h);
        h.Runner.Stage(Ab());

        swap.Load("bbs/Bob");

        Assert.Equal(new[] { "[Loop 'ab', loaded to start, dropped: another character was loaded]" }, swap.Notices);
        Assert.Null(h.Runner.StagedLoop);
    }

    // A close with nothing loaded after it is decided once the close has unwound.
    [Fact]
    public void ALoopHeldForAReconnect_IsDropped_WhenTheCharacterIsClosed()
    {
        Harness h = OnAlpha();
        Swap swap = SwapOn(h);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();

        swap.Close();

        Assert.Equal(new[] { "[Loop 'ab', held for a reconnect, dropped: the character was closed]" }, swap.Notices);
        Assert.Null(h.Runner.PendingReconnectResumeName);
    }

    // ----- what the stop asks the holders of a held start ----------------------------

    // One holder failing doesn't keep the stop from being made, or the others from
    // being let go.
    [Fact]
    public void AHolderThatThrows_DoesNotKeepTheStopFromBeingMade()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        string? droppedFor = null;
        h.Controller.AddHeldStart(() => throw new InvalidOperationException("boom"), _ => { });
        h.Controller.AddHeldStart(() => "walk to 1/3, queued in the Navigation window,", why => droppedFor = why);

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab' stopped; walk to 1/3, queued in the Navigation window, dropped", ended);
        Assert.Equal(Reason, droppedFor);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Warn && e.Source == "Movement"
            && e.Message.Contains("boom", StringComparison.Ordinal));
    }
}
