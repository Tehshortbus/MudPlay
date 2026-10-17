using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A switch to another game-data set with no engine running at that instant: what
// holds a run of the last set's rooms, to put back by itself later, is ended with
// the stop. An event standing in a wait, and a party pick-up waiting for its member.
// (The loop held for a reconnect or the master switch is the controller's own:
// GameDataSwitchStopsMovementTests.) The wiring here is the one AppServices makes.
public sealed class GameDataSwitchEndsHeldRunsTests : IDisposable
{
    private const string TestLoopSet = "test-setswitch-held";
    private const string Reason = "game data changed from 'alpha' to 'beta'";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-setswitch-held-" + Path.GetRandomFileName());
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (IDisposable d in _dispose) d.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(TestLoopSet), recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ----- an event standing in a wait ----------------------------------------------

    private MovementController ControllerOver(EventEngineHarness h)
    {
        MovementController controller = new(h.Walker, h.Runner, h.AutoLair, h.Coordinator, h.Log);
        _dispose.Add(controller);
        controller.Stopping += () => h.Events.NoteStop(controller.GameDataChangeStop ?? "stopped by the user");
        controller.EndingForGameDataChange += (reason, stopped) =>
        {
            if (h.Events.RunningEventName is { } running) stopped.Add($"event '{running}'");
            h.Events.NoteStop(reason);
        };
        return controller;
    }

    private static ScheduledEvent WaitThenResume() => new()
    {
        Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 30, Then = EventThenType.Resume,
    };

    [Theory]
    [InlineData("loop")]
    [InlineData("lair")]
    public void AnEventWaitingAtTheSwitch_IsEnded_AndDoesNotGoBackToTheOldRun(string kind)
    {
        using EventEngineHarness h = EventEngineHarness.Create(Path.Combine(_root, kind), TestLoopSet);
        MovementController controller = ControllerOver(h);
        DateTimeOffset now = new(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        if (kind == "loop") h.Runner.Start(new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        else
        {
            h.AutoLair.Mark(new RoomKey(1, 2));
            h.AutoLair.Mark(new RoomKey(1, 3));
            Assert.True(h.AutoLair.Start());
        }
        h.Events.Fire(WaitThenResume());
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.False(h.AutoLair.IsActive);
        Assert.Equal("hold", h.Events.RunningEventName);
        int sent = h.Sent.Count;

        // The set changes: the stop, then the marks drop, in the app's order.
        string? ended = controller.StopForGameDataChange("alpha", "beta");
        h.AutoLair.DropMarksOfOtherGameData("beta");

        Assert.Equal("Event 'hold' stopped", ended);
        Assert.Null(h.Events.RunningEventName);
        Assert.Contains(h.EventLog, m => m.Contains($"abandoned", StringComparison.Ordinal) && m.Contains(Reason, StringComparison.Ordinal));

        now = now.AddSeconds(35);
        h.Events.Tick();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.False(h.AutoLair.IsActive);
        Assert.Equal(sent, h.Sent.Count);
    }

    // The user's Stop still says so in the event log.
    [Fact]
    public void TheUsersStop_StillEndsAWaitingEventAsStoppedByTheUser()
    {
        using EventEngineHarness h = EventEngineHarness.Create(Path.Combine(_root, "user"), TestLoopSet);
        MovementController controller = ControllerOver(h);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        h.Events.Fire(WaitThenResume());

        controller.Stop();

        Assert.Null(h.Events.RunningEventName);
        Assert.Contains(h.EventLog, m => m.Contains("stopped by the user", StringComparison.Ordinal));
    }

    // ----- a party pick-up waiting for its member -----------------------------------

    private const string Strip = """
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
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed record Pickup(
        PartyComebackManager Comeback, RemoteCommandManager Engine, MessageRouter Router, PartyState Party,
        RoomTracker Tracker, LoopRunner Runner, MovementController Controller, List<string> Sent,
        ManualTimers ComebackTimers);

    // The engines and a pick-up manager over them, the controller wired to it as
    // AppServices wires it. cancelWired false leaves that wiring out.
    private Pickup NewPickup(bool cancelWired)
    {
        string root = Path.Combine(_root, "pickup-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), Strip);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState partyState = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, partyState, players);
        GameDataCache cache = new(root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        List<string> sent = new();
        void Send(byte[] b) => sent.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r'));
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(Send);
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs, walker: walker, postToUi: a => a());
        runner.SetWireSender(Send);
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);
        RoomEntityClassifier classifier = new(router, new MonsterMessageStore(), players, tracker);
        PartyManager party = new(router, partyState);
        ManualTimers comebackTimers = new();
        PartyComebackManager comeback = new(engine, party, tracker, classifier, walker, runner, lair, router, bfs,
            schedule: comebackTimers.Arm);
        comeback.SetWireSender(_ => { });
        MovementController controller = new(walker, runner, lair, coord);
        if (cancelWired)
            controller.EndingForGameDataChange += (reason, stopped) =>
            {
                if (comeback.RecoveringMember is not null) stopped.Add("the party pick-up");
                comeback.Cancel(reason);
            };
        _dispose.AddRange(new IDisposable[] { controller, comeback, lair, timers, party, engine });
        return new Pickup(comeback, engine, router, partyState, tracker, runner, controller, sent, comebackTimers);
    }

    // Standing in the member's room, re-invited and waiting for them to follow, with
    // the loop it stopped to go back held to resume.
    private static void WaitForTheMember(Pickup p)
    {
        p.Party.Members.Add(new PartyMember { Name = "Tank" });
        p.Party.IsInParty = true;
        p.Tracker.SetLocated(new RoomKey(1, 1));
        p.Runner.Start(new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        ChatLines.Hear(p.Router, ChatChannel.TelepathIncoming, "Tank", "@comeback 1/1");
        p.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Equal("Tank", p.Comeback.RecoveringMember);
        Assert.Equal("re-invited, waiting for them to follow", p.Comeback.RecoveryPhase);
        Assert.Equal(LoopState.Idle, p.Runner.State);
    }

    [Fact]
    public void APickupWaitingForItsMember_IsEnded_AndDoesNotPutTheLoopBack()
    {
        Pickup p = NewPickup(cancelWired: true);
        WaitForTheMember(p);
        int sent = p.Sent.Count;

        string? ended = p.Controller.StopForGameDataChange("alpha", "beta");

        Assert.Equal("The party pick-up stopped", ended);
        Assert.Null(p.Comeback.RecoveringMember);

        // The member never follows: the wait running out is what put the loop back.
        p.ComebackTimers.RunAll();

        Assert.Equal(LoopState.Idle, p.Runner.State);
        Assert.Equal(sent, p.Sent.Count);
    }

    // Without the cancel the same wait puts the loop back: what the test above guards.
    [Fact]
    public void WithoutThatCancel_TheWaitRunningOutPutsTheLoopBack()
    {
        Pickup p = NewPickup(cancelWired: false);
        WaitForTheMember(p);

        p.Controller.StopForGameDataChange("alpha", "beta");
        p.ComebackTimers.RunAll();

        Assert.NotEqual(LoopState.Idle, p.Runner.State);
    }
}
