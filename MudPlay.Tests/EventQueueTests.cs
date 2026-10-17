using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// Event runs are handled start to finish: an event that fires while another's run
// is under way waits for it (report paradigm-20261009-185106), and what the last
// of them goes back to is what the first interrupted. Also what a finished event
// says when the way back is refused (report paradigm-20261009-220128).
public sealed class EventQueueTests : IDisposable
{
    private const string TestLoopSet = "test-event-queue";

    private readonly string _root;

    public EventQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-event-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(TestLoopSet), recursive: true); }
        catch { /* best-effort */ }
    }

    private Harness NewHarness() => Harness.Create(_root, TestLoopSet);

    private static readonly RoomKey A = new(1, 1);
    private static readonly RoomKey B = new(1, 2);
    private static readonly RoomKey C = new(1, 3);

    // A saved event, as the scheduler fires it: only an event still in the list
    // leaves the queue.
    private static ScheduledEvent Add(Harness h, ScheduledEvent e)
    {
        h.Events.Events.Add(e);
        return e;
    }

    private static ScheduledEvent WalkTo(string name, RoomKey room, EventThenType then = EventThenType.Resume) => new()
    {
        Name = name,
        TriggerType = EventTriggerType.Every,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(room.Map, room.Room),
        Then = then,
    };

    // The reports' events: fire at a boss's guaranteed spawn, walk to its room, then
    // start the farming loop again.
    private static ScheduledEvent BossWalk(string name, string boss, RoomKey room) => new()
    {
        Name = name,
        TriggerType = EventTriggerType.Boss,
        BossName = boss,
        BossMoment = EventBossMoment.Guaranteed,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(room.Map, room.Room),
        Then = EventThenType.Loop,
        ThenLoopName = "Farm",
    };

    private static Loop RunUserLoop(Harness h, string name = "ab")
    {
        h.Tracker.SetLocated(A);
        Loop loop = new(name, new[] { A, B });
        h.Runner.Start(loop, userStarted: true);
        return loop;
    }

    private static void Arrive(Harness h, RoomKey room) =>
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "arrived", room));

    // ----- The collision (paradigm-20261009-185106) ------------------------

    // The report: a boss event's walk was nine rooms from its boss when a second
    // boss's timer came up. The second event took the walker and went the other
    // way; the first boss was never reached. Now the second waits, the first walk
    // arrives, and the loop both end in is started once, after the second.
    [Fact]
    public void ASecondBossEventMidWalk_WaitsForTheFirstToArrive()
    {
        using Harness h = NewHarness();
        h.Loops.Save(new Loop("Farm", new[] { A, B }));
        Loop study = h.Loops.Loops.Single(l => l.Name == "Farm");
        h.Tracker.SetLocated(A);
        h.Runner.Start(study, userStarted: true);
        ScheduledEvent first = Add(h, BossWalk("First boss", "first boss", C));
        ScheduledEvent second = Add(h, BossWalk("Second boss", "second boss", B));

        h.Events.Fire(first);
        h.Events.Fire(second);

        Assert.Equal(C, h.Walker.Destination);                       // the first walk goes on
        Assert.Contains("'First boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("'Second boss'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'Second boss' queued behind 'First boss' (WalkTo)"));

        Arrive(h, C);

        Assert.Equal(B, h.Walker.Destination);                       // now the second
        Assert.Contains("'Second boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'Farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);                 // not started to be stopped again

        Arrive(h, B);

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Same(study, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // Three in a row run in the order they fired.
    [Fact]
    public void WaitingEvents_RunInTheOrderTheyFired()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent one = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent two = Add(h, WalkTo("two", B, EventThenType.Nothing));
        ScheduledEvent three = Add(h, WalkTo("three", A, EventThenType.Nothing));

        h.Events.Fire(one);
        h.Events.Fire(two);
        h.Events.Fire(three);
        Assert.Contains("'one'", h.Events.RunSummary);

        Arrive(h, C);
        Assert.Contains("'two'", h.Events.RunSummary);
        Assert.Contains("'three'", h.Events.QueueSummary);

        Arrive(h, B);
        Assert.Contains("'three'", h.Events.RunSummary);
    }

    // An "every 5 minutes" event whose run takes 7 runs once, not twice in a row.
    [Fact]
    public void TheSameEventFiringMidRun_IsSkipped()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent e = Add(h, WalkTo("bank", C, EventThenType.Nothing));

        h.Events.Fire(e);
        h.Events.Fire(e);

        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'bank' fired again while its own run is still going"));

        Arrive(h, C);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    [Fact]
    public void TheSameEventFiringWhileItWaits_StaysQueuedOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent running = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent waiting = Add(h, WalkTo("two", B, EventThenType.Nothing));

        h.Events.Fire(running);
        h.Events.Fire(waiting);
        h.Events.Fire(waiting);

        Assert.Equal(1, h.Events.QueueSummary.Split("'two'").Length - 1);
        Assert.Contains(h.EventLog, l => l.Contains("'two' fired again while it is already waiting"));
    }

    // A walk that can't be finished still runs its Then, as it always has, and the
    // waiting event starts.
    [Fact]
    public void AFailedWalk_EndsTheRun_AndTheNextStarts()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        ScheduledEvent first = Add(h, WalkTo("one", C));
        ScheduledEvent second = Add(h, WalkTo("two", B));

        h.Events.Fire(first);
        h.Events.Fire(second);
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", C));

        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'ab'", h.Events.RunSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'one': its WalkTo didn't get done"));

        Arrive(h, B);
        Assert.Same(loop, h.Runner.CurrentLoop);
    }

    // The user stopping the walk is a stop: the waiting events don't start.
    [Fact]
    public void AUserStop_AbandonsTheRun_AndEmptiesTheQueue()
    {
        using Harness h = NewHarness();
        RunUserLoop(h);
        ScheduledEvent first = Add(h, WalkTo("one", C));
        ScheduledEvent second = Add(h, WalkTo("two", B));
        h.Events.Fire(first);
        h.Events.Fire(second);

        h.Walker.Stop("user stop from toolbar");

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(LoopState.Idle, h.Runner.State);                 // no hand-back either
        Assert.Contains(h.EventLog, l => l.Contains("'one' abandoned at WalkTo — its walk was stopped"));
        Assert.Contains(h.EventLog, l => l.Contains("1 waiting event(s) dropped ('two')"));
    }

    // "Loop 3 times then go to the bank" finishes before anything else fires off
    // (user, 2026-10-10): an event that fires mid-loop waits for the laps and for
    // the Then walk after them.
    [Fact]
    public void ALoopEventWithAStopRule_HoldsTheQueue_UntilItsThenIsDone()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", StopAfterLaps = 2,
            Then = EventThenType.WalkTo, ThenWalkTo = new RoomRef(C.Map, C.Room),
        });
        ScheduledEvent walk = Add(h, WalkTo("boss", B, EventThenType.Nothing));

        h.Events.Fire(farm);
        h.Events.Fire(walk);

        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);              // the loop goes on

        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "farm"));
        Assert.Contains("'boss'", h.Events.QueueSummary);
        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "farm"));

        Assert.Equal(LoopState.Idle, h.Runner.State);                 // the laps are done
        Assert.Contains("'farm' Then walk-to", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);             // still waiting: the bank walk

        Arrive(h, C);
        Assert.Contains("'boss' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // A loop event with no Stop after rule can never finish, so it is done once
    // the loop has started: the loop is then what the character is doing, and a
    // later event takes over from it and goes back to it.
    [Fact]
    public void ALoopEventWithNoStopRule_IsDoneOnceTheLoopStarts()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        });
        ScheduledEvent walk = Add(h, WalkTo("boss", C));

        h.Events.Fire(farm);

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("farm", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.EventLog, l => l.Contains("'farm' finished; started loop 'farm'") && l.Contains("no Stop after rule"));

        h.Events.Fire(walk);

        Assert.Contains("'boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);

        Arrive(h, C);
        Assert.Equal("farm", h.Runner.CurrentLoop?.Name);
    }

    // The same event waiting behind another: it starts in its turn, is done at
    // once, and the event behind it takes its loop as what to go back to.
    [Fact]
    public void AnEventQueuedBehindALoopEvent_StartsOnceTheLoopHas()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent first = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        });
        ScheduledEvent last = Add(h, WalkTo("last", B, EventThenType.Nothing));

        h.Events.Fire(first);
        h.Events.Fire(farm);
        h.Events.Fire(last);
        Arrive(h, C);

        Assert.Contains("'last' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // A Then walk-to is the event's last leg: the next event waits for it too.
    [Fact]
    public void AThenWalkTo_HoldsTheQueueUntilItArrives()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent first = Add(h, WalkTo("one", C, EventThenType.WalkTo));
        first.ThenWalkTo = new RoomRef(B.Map, B.Room);
        ScheduledEvent second = Add(h, WalkTo("two", A, EventThenType.Nothing));

        h.Events.Fire(first);
        Arrive(h, C);
        Assert.Contains("'one' Then walk-to", h.Events.RunSummary);

        h.Events.Fire(second);
        Assert.Contains("'two'", h.Events.QueueSummary);
        Assert.Equal(B, h.Walker.Destination);

        Arrive(h, B);
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
    }

    // A plain command isn't a run: it is sent at once and nothing waits or moves.
    [Fact]
    public void ACommandEventMidRun_IsSentAtOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        List<string> sent = new();
        h.Events.SetWireSender(b => sent.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
        ScheduledEvent walk = Add(h, WalkTo("one", C, EventThenType.Nothing));

        h.Events.Fire(walk);
        h.Events.Fire(new ScheduledEvent { Name = "stat", ActionType = EventActionType.Command, CommandText = "stat" });

        Assert.Equal("stat", Assert.Single(sent));
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
    }

    // ----- Bounds ------------------------------------------------------------

    [Fact]
    public void TheQueue_HoldsNoMoreThanItsCap()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));

        Assert.Equal(EventManager.DefaultMaxQueued, h.Events.MaxQueued);
        for (int i = 0; i < EventManager.DefaultMaxQueued + 1; i++)
            h.Events.Fire(Add(h, WalkTo($"w{i}", B, EventThenType.Nothing)));

        Assert.Contains($"'w{EventManager.DefaultMaxQueued - 1}'", h.Events.QueueSummary);
        Assert.DoesNotContain($"'w{EventManager.DefaultMaxQueued}'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains(
            $"'w{EventManager.DefaultMaxQueued}' dropped — {EventManager.DefaultMaxQueued} events are already waiting"));
        Assert.Contains($"[Event 'w{EventManager.DefaultMaxQueued}' dropped:", Assert.Single(h.Notices));
    }

    // Both limits are the character's own settings (Settings → Events).
    [Fact]
    public void TheQueueLimits_ComeFromTheCharactersSettings()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Profile.Current!.EventQueueLimit = 1;
        h.Profile.Current!.EventQueueWaitMinutes = 5;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));

        h.Events.Fire(Add(h, WalkTo("kept", B, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("one too many", A, EventThenType.Nothing)));

        Assert.Contains("'kept'", h.Events.QueueSummary);
        Assert.DoesNotContain("'one too many'", h.Events.QueueSummary);

        now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        h.Events.Tick();

        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'kept' dropped — it waited 5 minutes behind 'running' (WalkTo)"));
        Assert.Equal(2, h.Notices.Count);
    }

    // ----- The master switch ----------------------------------------------

    // A waiting event starts through the queue, not through Fire, so the switch
    // is asked there too: with it off no waiting event starts, by the run before
    // it ending or any other way. Each keeps its place, and they start in their
    // order once the switch is back on. One that fires meanwhile is skipped, not
    // queued.
    [Fact]
    public void WaitingEvents_MasterSwitchOff_DoNotStart_AndStartInOrderOnceItIsBackOn()
    {
        using Harness h = NewHarness();
        bool off = false;
        h.Events.IsMasterSwitchOff = () => off;
        h.Events.BlockedByMasterSwitch = _ => off;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("first", B, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("second", A, EventThenType.Nothing)));

        off = true;
        h.Events.NoteMasterSwitchChanged();
        h.Events.Fire(Add(h, WalkTo("third", B, EventThenType.Nothing)));
        Arrive(h, C);                       // the run ahead of them ends while it is off
        h.Events.Tick();

        Assert.Contains("'running'", h.Events.RunSummary);
        Assert.Contains("'first'", h.Events.QueueSummary);
        Assert.Contains("'second'", h.Events.QueueSummary);
        Assert.DoesNotContain("'third'", h.Events.QueueSummary);

        off = false;
        h.Events.NoteMasterSwitchChanged();

        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
        Assert.Contains("'second'", h.Events.QueueSummary);
        Arrive(h, B);
        Assert.Contains("'second' WalkTo", h.Events.RunSummary);
    }

    // With nothing running (the queue kept across a reconnect), the way back in
    // would start the next waiting event: not with the switch off.
    [Fact]
    public void WaitingEvent_NotStartedOnTheWayBackIn_WhileTheMasterSwitchIsOff()
    {
        using Harness h = NewHarness();
        bool off = false;
        h.Events.IsMasterSwitchOff = () => off;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("waiting", B, EventThenType.Nothing)));
        h.Events.NoteDisconnected();
        Arrive(h, C);

        off = true;
        h.Events.NoteMasterSwitchChanged();
        h.Events.NoteEnteredGame();

        Assert.Contains("'waiting'", h.Events.QueueSummary);
        Assert.DoesNotContain("'waiting' WalkTo", h.Events.RunSummary);

        off = false;
        h.Events.NoteMasterSwitchChanged();
        Assert.Contains("'waiting' WalkTo", h.Events.RunSummary);
    }

    // An event's walk arrived with the switch off, so its Then was kept. The link
    // then dropped, and the switch came back on with it down: the Then's loop is
    // not started against a dead connection (its moves would land at the board's
    // login prompts on the redial). It starts on the way back into the game.
    [Fact]
    public void ThenKeptWhileOff_IsNotRunBySwitchOn_WhileTheConnectionIsDown()
    {
        using Harness h = NewHarness();
        bool off = false;
        h.Events.IsMasterSwitchOff = () => off;
        h.Loops.Save(new Loop("Farm", new[] { A, B }));
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, BossWalk("boss", "boss", C)));

        off = true;
        h.Events.NoteMasterSwitchChanged();
        Arrive(h, C);
        h.Events.NoteDisconnected();
        off = false;
        h.Events.NoteMasterSwitchChanged();

        Assert.Equal(LoopState.Idle, h.Runner.State);

        h.Events.NoteEnteredGame();

        Assert.Equal("Farm", h.Runner.CurrentLoop?.Name);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // The wait limit counts time the switch was on: an event is not dropped for
    // the hours the switch spent off.
    [Fact]
    public void WaitLimit_DoesNotRunWhileTheMasterSwitchIsOff()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        bool off = false;
        h.Events.IsMasterSwitchOff = () => off;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("stuck", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("kept", B, EventThenType.Nothing)));
        now += h.Events.MaxQueueWait - TimeSpan.FromSeconds(1);
        h.Events.Tick();

        off = true;
        h.Events.NoteMasterSwitchChanged();
        now += TimeSpan.FromMinutes(4);     // short enough that switch-on asks nothing
        h.Events.Tick();
        Assert.Contains("'kept'", h.Events.QueueSummary);

        off = false;
        h.Events.NoteMasterSwitchChanged();
        h.Events.Tick();
        Assert.Contains("'kept'", h.Events.QueueSummary);

        now += TimeSpan.FromSeconds(2);     // the second it had left, and one more
        h.Events.Tick();
        Assert.DoesNotContain("'kept'", h.Events.QueueSummary);
    }

    // ----- The choice after a long spell with the master switch off ---------
    // "if we swap back on and have had events in queue for longer than 5 minutes
    // because of this, it should pop up with a menu for the user to pick which
    // ones to execute and which ones to drop" (user, 2026-10-10).

    private sealed class HeldQueue
    {
        public required Harness H { get; init; }
        public DateTimeOffset Now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        public bool Off;
        public int Asked;
        public int Withdrawn;

        public void SwitchOff()
        {
            Off = true;
            H.Events.NoteMasterSwitchChanged();
        }

        public void SwitchOn()
        {
            Off = false;
            H.Events.NoteMasterSwitchChanged();
        }
    }

    // A run under way with `waiting` events behind it, on a clock the test moves.
    private HeldQueue WithWaiting(Harness h, params string[] waiting)
    {
        HeldQueue q = new() { H = h };
        h.EventsNow = () => q.Now;
        h.Events.IsMasterSwitchOff = () => q.Off;
        h.Events.BlockedByMasterSwitch = _ => q.Off;
        h.Events.QueueChoiceNeeded += () => q.Asked++;
        h.Events.QueueChoiceWithdrawn += () => q.Withdrawn++;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));
        foreach (string name in waiting)
            h.Events.Fire(Add(h, WalkTo(name, B, EventThenType.Nothing)));
        return q;
    }

    private static ScheduledEvent Named(Harness h, string name) => h.Events.Events.Single(e => e.Name == name);

    [Fact]
    public void HeldFiveMinutesOrLess_TheWaitingEventsCarryOn_WithNoPrompt()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");

        q.SwitchOff();
        q.Now += EventManager.HeldQueuePromptAfter;
        Arrive(h, C);
        q.SwitchOn();

        Assert.Equal(0, q.Asked);
        Assert.DoesNotContain("waiting for your choice", h.Events.QueueSummary);
        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
        Assert.Contains("'second'", h.Events.QueueSummary);
    }

    [Fact]
    public void HeldLongerThanFiveMinutes_NothingWaitingStartsUntilTheUserAnswers()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.Now += TimeSpan.FromMinutes(2);

        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(6);
        Arrive(h, C);                           // the run ahead of them ends while it is off
        q.SwitchOn();

        Assert.Equal(1, q.Asked);
        Assert.Contains("waiting for your choice", h.Events.QueueSummary);
        Assert.Equal("(none)", h.Events.RunSummary);     // the kept completion ran; nothing took its place
        Assert.Contains("'first'", h.Events.QueueSummary);
        Assert.Contains("waiting for your choice (held 6 min by the master switch)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("asking which to run"));

        // Not by a tick, and not for the wait limit either, however long the answer takes.
        q.Now += h.Events.MaxQueueWait + TimeSpan.FromHours(1);
        h.Events.Tick();
        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("'second'", h.Events.QueueSummary);

        IReadOnlyList<EventManager.HeldQueueEntry> offered = h.Events.OfferQueueChoice();
        Assert.Equal(new[] { "first", "second" }, offered.Select(o => o.Event.Name));
        Assert.Equal(TimeSpan.FromMinutes(6), offered[0].HeldBySwitch);
        Assert.Equal(TimeSpan.FromMinutes(8) + h.Events.MaxQueueWait + TimeSpan.FromHours(1), offered[0].Waiting);
    }

    [Fact]
    public void Answered_TheTickedEventsRunInTheirOrder_AndTheRestAreDropped()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second", "third");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(30);
        Arrive(h, C);
        q.SwitchOn();
        h.Events.OfferQueueChoice();
        q.Now += h.Events.MaxQueueWait + TimeSpan.FromMinutes(1);   // the user took their time

        h.Events.ResolveQueueChoice(new[] { Named(h, "third"), Named(h, "first") });

        Assert.DoesNotContain("waiting for your choice", h.Events.QueueSummary);
        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
        Assert.Contains("'third'", h.Events.QueueSummary);
        Assert.DoesNotContain("'second'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'second' dropped by the user's choice"));
        Assert.Contains(h.EventLog, l => l.Contains("'first' kept by the user's choice"));
        Assert.Equal("[1 waiting event(s) dropped by your choice; 2 will run]", Assert.Single(h.Notices));

        // The time spent on the question is not held against the one still waiting.
        h.Events.Tick();
        Arrive(h, B);
        Assert.Contains("'third' WalkTo", h.Events.RunSummary);
    }

    [Fact]
    public void AnsweredWithNoneTicked_AllAreDropped_WithOneNotice()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        q.SwitchOn();
        h.Events.OfferQueueChoice();

        h.Events.ResolveQueueChoice(Array.Empty<ScheduledEvent>());

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(2, h.EventLog.Count(l => l.Contains("dropped by the user's choice")));
        Assert.Equal("[2 waiting event(s) dropped by your choice; none will run]", Assert.Single(h.Notices));
        Assert.Equal(0, q.Withdrawn);           // answered, not taken back
    }

    // The switch goes off again with the prompt up: the prompt is taken back, the
    // events stay waiting, and the question is put again at the next switch-on.
    [Fact]
    public void SwitchOffAgainWhileTheChoiceIsAwaited_TakesThePromptBack_AndAsksAgainNextTime()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        q.SwitchOn();
        h.Events.OfferQueueChoice();

        q.SwitchOff();
        Assert.Equal(1, q.Withdrawn);
        Assert.Contains("'first'", h.Events.QueueSummary);

        q.Now += TimeSpan.FromSeconds(30);
        q.SwitchOn();
        Assert.Equal(2, q.Asked);
        Assert.Equal("(none)", h.Events.RunSummary);

        h.Events.OfferQueueChoice();
        h.Events.ResolveQueueChoice(new[] { Named(h, "first") });
        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
    }

    // The prompt closed by its X: "drop all of them" (user, 2026-10-10). Every
    // listed event goes, with a log line each and one notice, and no choice is
    // left standing: an event that fires afterwards runs.
    [Fact]
    public void PromptClosedByItsX_DropsEveryListedEvent_AndTheNextEventToFireRuns()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        q.SwitchOn();
        h.Events.OfferQueueChoice();

        h.Events.DropQueueChoice(EventManager.PromptClosedUnanswered);

        Assert.DoesNotContain("waiting for your choice", h.Events.QueueSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(2, h.EventLog.Count(l => l.Contains("the Waiting Events window was closed without an answer")));
        Assert.Equal("[2 waiting event(s) dropped: the Waiting Events window was closed without an answer]",
            Assert.Single(h.Notices));
        Assert.Equal(0, q.Withdrawn);

        h.Events.Fire(Add(h, WalkTo("later", B, EventThenType.Nothing)));
        Assert.Contains("'later' WalkTo", h.Events.RunSummary);
    }

    // An event that queued after the prompt opened was not on its list, so the X
    // does not drop it: it runs.
    [Fact]
    public void PromptClosedByItsX_AnEventThatQueuedAfterItOpened_IsKeptAndRuns()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        q.SwitchOn();                           // 'running' still walks
        h.Events.OfferQueueChoice();
        h.Events.Fire(Add(h, WalkTo("late", B, EventThenType.Nothing)));

        h.Events.DropQueueChoice(EventManager.PromptClosedUnanswered);
        Arrive(h, C);

        Assert.Contains("'late' WalkTo", h.Events.RunSummary);
        Assert.DoesNotContain("'first'", h.Events.QueueSummary);
    }

    // "if it was triggered remotely the default should be to skip all the events,
    // if it was auto all off for a long period of time" (user, 2026-10-10): a
    // switch-on nobody is at the keyboard for asks nothing and drops what the
    // prompt would have listed. The queue then runs on.
    [Fact]
    public void RemoteSwitchOnAfterALongHold_DropsTheWaitingEventsWithoutAsking_AndTheNextEventRuns()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        q.Off = false;
        h.Events.NoteMasterSwitchChanged(remote: true);

        Assert.Equal(0, q.Asked);
        Assert.DoesNotContain("waiting for your choice", h.Events.QueueSummary);
        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(2, h.EventLog.Count(l => l.Contains("switched back on remotely after more than 5 minutes off")));
        Assert.Equal("[2 waiting event(s) dropped: Auto-All was switched back on remotely after more than 5 minutes off]",
            Assert.Single(h.Notices));

        h.Events.Fire(Add(h, WalkTo("later", B, EventThenType.Nothing)));
        Assert.Contains("'later' WalkTo", h.Events.RunSummary);
    }

    [Fact]
    public void RemoteSwitchOnAfterAShortHold_TheWaitingEventsCarryOn()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.SwitchOff();
        q.Now += EventManager.HeldQueuePromptAfter;
        Arrive(h, C);
        q.Off = false;
        h.Events.NoteMasterSwitchChanged(remote: true);

        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
        Assert.Contains("'second'", h.Events.QueueSummary);
        Assert.Empty(h.Notices);
    }

    // The unattended client that could not come back: a long hold, a switch-on
    // nobody answers, then hours of events and a reconnect. Before, the choice
    // stood for good, nothing started and the queue filled to its cap. Either
    // way the switch-on is settled now, every event that fires afterwards has
    // its run, and the queue never builds.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterAnUnansweredSwitchOn_EventsOverTheNextHoursStillRun_AndTheQueueNeverFills(bool remote)
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first", "second");
        q.SwitchOff();
        q.Now += TimeSpan.FromHours(1);
        Arrive(h, C);
        q.Off = false;
        h.Events.NoteMasterSwitchChanged(remote);
        if (!remote)
        {
            h.Events.OfferQueueChoice();
            h.Events.DropQueueChoice(EventManager.PromptClosedUnanswered);
        }

        for (int i = 0; i < 14; i++)
        {
            q.Now += TimeSpan.FromMinutes(30);
            if (i == 7)
            {
                h.Events.NoteDisconnected();
                h.Events.NoteEnteredGame();
            }
            RoomKey to = i % 2 == 0 ? B : C;
            h.Events.Fire(Add(h, WalkTo($"e{i}", to, EventThenType.Nothing)));
            h.Events.Tick();

            Assert.Contains($"'e{i}' WalkTo", h.Events.RunSummary);
            Assert.Equal("(empty)", h.Events.QueueSummary);
            Arrive(h, to);
        }

        Assert.Equal(14, h.EventLog.Count(l => l.Contains("started: WalkTo") && l.Contains("Event 'e")));
        Assert.DoesNotContain(h.Notices, n => n.Contains("the most Settings → Events lets wait"));
    }

    // The link is down at switch-on: the question can be asked and answered, but
    // nothing starts until the character is back in the game.
    [Fact]
    public void ConnectionDownAtSwitchOn_TheChoiceIsAsked_ButNothingStartsUntilBackInTheGame()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        h.Events.NoteDisconnected();
        q.SwitchOn();

        Assert.Equal(1, q.Asked);
        h.Events.OfferQueueChoice();
        h.Events.ResolveQueueChoice(new[] { Named(h, "first") });
        Assert.DoesNotContain("'first' WalkTo", h.Events.RunSummary);

        h.Events.NoteEnteredGame();
        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
    }

    // An event that fires with the prompt up queues behind the others as usual.
    // It was not on the list, so the answer neither drops it nor runs it early.
    [Fact]
    public void EventFiringWhileTheChoiceIsAwaited_QueuesBehind_AndIsNotPartOfTheAnswer()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        Arrive(h, C);
        q.SwitchOn();
        IReadOnlyList<EventManager.HeldQueueEntry> offered = h.Events.OfferQueueChoice();

        h.Events.Fire(Add(h, WalkTo("late", C, EventThenType.Nothing)));

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("'late'", h.Events.QueueSummary);
        Assert.Single(offered);

        h.Events.ResolveQueueChoice(Array.Empty<ScheduledEvent>());

        Assert.Contains("'late' WalkTo", h.Events.RunSummary);
        Assert.Contains("[1 waiting event(s) dropped by your choice; none will run]", h.Notices);
    }

    // A Stop empties the queue as it always does; the prompt about it goes too.
    [Fact]
    public void QueueEmptiedWhileTheChoiceIsAwaited_TakesThePromptBack()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "first");
        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        q.SwitchOn();                           // 'running' still walks; 'first' awaits the choice
        Assert.Contains("waiting for your choice", h.Events.QueueSummary);

        h.Walker.Stop("user stop from toolbar");

        Assert.DoesNotContain("waiting for your choice", h.Events.QueueSummary);
        Assert.Equal(1, q.Withdrawn);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // The running event's Then names an event that is itself waiting. Normally
    // it runs then, as the chain, and leaves the queue. Not while the user is
    // being asked about it: it would start before the answer, and a "drop" could
    // no longer reach it.
    [Fact]
    public void ThenEventThatIsWaitingOnTheChoice_IsLeftToTheChoice_AndRunsOnce()
    {
        using Harness h = NewHarness();
        HeldQueue q = new() { H = h };
        h.EventsNow = () => q.Now;
        h.Events.IsMasterSwitchOff = () => q.Off;
        h.Events.QueueChoiceNeeded += () => q.Asked++;
        h.Tracker.SetLocated(A);
        ScheduledEvent running = WalkTo("running", C, EventThenType.Event);
        running.ThenEventName = "first";
        h.Events.Fire(Add(h, running));
        h.Events.Fire(Add(h, WalkTo("first", B, EventThenType.Nothing)));

        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        q.SwitchOn();
        Assert.Equal(1, q.Asked);
        h.Events.OfferQueueChoice();

        Arrive(h, C);                           // 'running' finishes with the choice still open

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("'first'", h.Events.QueueSummary);
        Assert.Contains("waiting for your choice", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("its Then event 'first' is waiting for the user's choice"));

        h.Events.ResolveQueueChoice(new[] { Named(h, "first") });

        Assert.Contains("'first' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(1, h.EventLog.Count(l => l.Contains("Event 'first' started")));
    }

    // The wait limit picks up where it stood once the choice is made: an event
    // with a second of its limit left when the switch went off is still there
    // after a long hold and a long think, and is dropped two seconds after the
    // answer that kept it.
    [Fact]
    public void WaitLimit_ResumesAfterTheAnswer_WithWhatWasLeftBeforeTheHold()
    {
        using Harness h = NewHarness();
        HeldQueue q = WithWaiting(h, "kept");
        q.Now += h.Events.MaxQueueWait - TimeSpan.FromSeconds(1);
        h.Events.Tick();

        q.SwitchOff();
        q.Now += TimeSpan.FromHours(7);
        q.SwitchOn();                           // 'running' still walks; 'kept' awaits the choice
        h.Events.OfferQueueChoice();
        q.Now += TimeSpan.FromMinutes(40);      // the time spent on the question
        h.Events.Tick();
        h.Events.ResolveQueueChoice(new[] { Named(h, "kept") });

        h.Events.Tick();
        Assert.Contains("'kept'", h.Events.QueueSummary);

        q.Now += TimeSpan.FromSeconds(2);
        h.Events.Tick();
        Assert.DoesNotContain("'kept'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'kept' dropped — it waited"));
    }

    // A Logoff event already waiting when the switch went off is listed with the
    // rest. (One that fires while the switch is off is skipped, never queued.)
    [Fact]
    public void ALogoffEventAlreadyWaiting_IsListedLikeTheOthers()
    {
        using Harness h = NewHarness();
        HeldQueue q = new() { H = h };
        h.EventsNow = () => q.Now;
        h.Events.IsMasterSwitchOff = () => q.Off;
        h.Events.QueueChoiceNeeded += () => q.Asked++;
        h.Tracker.SetLocated(A);
        ScheduledEvent ahead = WalkTo("logoff walk", C, EventThenType.Nothing);
        ahead.TriggerType = EventTriggerType.Logoff;
        ScheduledEvent behind = WalkTo("logoff bank", B, EventThenType.Nothing);
        behind.TriggerType = EventTriggerType.Logoff;
        h.Events.Fire(Add(h, ahead));
        h.Events.Fire(Add(h, behind));          // waits behind another Logoff run

        q.SwitchOff();
        q.Now += TimeSpan.FromMinutes(10);
        q.SwitchOn();

        Assert.Equal(1, q.Asked);
        Assert.Equal("logoff bank", Assert.Single(h.Events.OfferQueueChoice()).Event.Name);
    }

    // A run that doesn't end (a walk left paused) must not set off hours-old events
    // when it finally does.
    [Fact]
    public void AnEventThatWaitedTooLong_IsDropped()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("stuck", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("stale", B, EventThenType.Nothing)));

        now += h.Events.MaxQueueWait - TimeSpan.FromSeconds(1);
        h.Events.Tick();
        Assert.Contains("'stale'", h.Events.QueueSummary);

        h.Events.Fire(Add(h, WalkTo("fresh", A, EventThenType.Nothing)));
        now += TimeSpan.FromSeconds(2);
        h.Events.Tick();

        Assert.DoesNotContain("'stale'", h.Events.QueueSummary);
        Assert.Contains("'fresh'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'stale' dropped — it waited 30 minutes behind 'stuck' (WalkTo)"));

        Arrive(h, C);
        Assert.Contains("'fresh' WalkTo", h.Events.RunSummary);
    }

    // Removed (or edited: an edit replaces the instance) while it waited.
    [Fact]
    public void AnEventRemovedWhileItWaited_DoesNotRun()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent second = Add(h, WalkTo("two", B, EventThenType.Nothing));
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(second);

        h.Events.Remove(second);
        Arrive(h, C);

        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // ----- What ends a run early -----------------------------------------------

    // Logoff events run in the minutes before a cleanup: one starts at once. The
    // run it interrupts is abandoned; what was waiting keeps its place behind it.
    [Fact]
    public void ALogoffEvent_JumpsTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent logoff = Add(h, WalkTo("to the inn", B, EventThenType.Nothing));
        logoff.TriggerType = EventTriggerType.Logoff;
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", A, EventThenType.Nothing)));

        h.Events.Fire(logoff);

        Assert.Contains("'to the inn' WalkTo", h.Events.RunSummary);
        Assert.Equal(B, h.Walker.Destination);
        Assert.Contains("'two'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'to the inn' jumps the queue: 'one' is abandoned at WalkTo"));

        Arrive(h, B);
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
    }

    // A second Logoff event doesn't interrupt the first: it waits, ahead of the
    // ordinary events. A boss event is an ordinary event.
    [Fact]
    public void ASecondLogoffEvent_WaitsAtTheHeadOfTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent first = Add(h, WalkTo("bank", C, EventThenType.Nothing));
        first.TriggerType = EventTriggerType.Logoff;
        ScheduledEvent boss = Add(h, BossWalk("Boss", "boss", B));
        ScheduledEvent second = Add(h, WalkTo("to the inn", B, EventThenType.Nothing));
        second.TriggerType = EventTriggerType.Logoff;

        h.Events.Fire(first);
        h.Events.Fire(boss);
        h.Events.Fire(second);

        Assert.Contains("'bank' WalkTo", h.Events.RunSummary);
        Assert.StartsWith("'to the inn'", h.Events.QueueSummary);
        Assert.Contains("'Boss'", h.Events.QueueSummary);

        Arrive(h, C);
        Assert.Contains("'to the inn' WalkTo", h.Events.RunSummary);
    }

    // Only the Logoff trigger makes an event logoff-type: a boss event, and an event
    // whose command logs off, are ordinary events.
    [Fact]
    public void OnlyTheLogoffTrigger_IsLogoffType()
    {
        Assert.True(EventManager.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Logoff, ActionType = EventActionType.BankTrip,
        }));
        Assert.False(EventManager.IsLogoffType(BossWalk("Boss", "boss", B)));
        Assert.False(EventManager.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.AtTime, ActionType = EventActionType.BankTrip,
        }));
        Assert.False(EventManager.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Every, ActionType = EventActionType.Command, CommandText = ";o",
            Then = EventThenType.Resume,
        }));
    }

    // A relog command with something after it is a run like any other: it waits its
    // turn, and nothing is sent until the run before it ends.
    [Fact]
    public void ALogOffCommandEvent_WaitsItsTurn()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        List<string> sent = new();
        h.Events.SetWireSender(b => sent.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));

        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "relog", TriggerType = EventTriggerType.Every, ActionType = EventActionType.Command,
            CommandText = ";o", Then = EventThenType.Resume,
        }));

        Assert.Empty(sent);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Equal("'two'", h.Events.QueueSummary.Split(" (")[0]);
        Assert.Contains("'relog'", h.Events.QueueSummary);
        Assert.DoesNotContain(h.EventLog, l => l.Contains("'relog' jumps the queue"));
    }

    // The engines halt in the graveyard; a wait their stop doesn't reach, and the
    // event behind it, must not carry on from there.
    [Fact]
    public void ADeath_AbandonsTheRun_AndEmptiesTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Resume,
        }));
        h.Events.Fire(Add(h, WalkTo("two", C)));

        h.Events.NoteDeath();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // ----- Going back ----------------------------------------------------------

    // The user's own walk or loop: an event that fires over it takes over at once
    // and goes back to it. Nothing here changed.
    [Fact]
    public void AnEventOverTheUsersOwnWalk_TakesOverAtOnce_AndGoesBack()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Walker.WalkTo(C, preferTeleportFree: true);
        ScheduledEvent e = Add(h, WalkTo("errand", B));

        h.Events.Fire(e);

        Assert.Equal(B, h.Walker.Destination);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains("resume target walk to 1/3", h.Events.RunSummary);

        Arrive(h, B);
        Assert.Equal(C, h.Walker.Destination);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // Two "go back" events in a row go back once, after the second, to what the
    // first one interrupted.
    [Fact]
    public void TwoGoBackEvents_GoBackOnce_AfterTheLast()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        int starts = 0;
        h.Runner.Event += l => { if (l.Kind == LoopEventKind.Started) starts++; };
        h.Events.Fire(Add(h, WalkTo("one", C)));
        h.Events.Fire(Add(h, WalkTo("two", B)));

        Arrive(h, C);
        Assert.Equal(0, starts);
        Arrive(h, B);

        Assert.Equal(1, starts);
        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.Contains(h.EventLog, l => l.Contains("'two' finished; going back to loop 'ab'"));
        Assert.Contains("'two' → loop 'ab'", h.Events.LastThenSummary);
    }

    // An event that ends in a loop of its own doesn't go back to the one it
    // interrupted.
    [Fact]
    public void AnEventEndingInItsOwnLoop_DoesNotGoBackToTheInterruptedOne()
    {
        using Harness h = NewHarness();
        RunUserLoop(h);
        h.Loops.Save(new Loop("other", new[] { B, C }));
        ScheduledEvent e = Add(h, WalkTo("move camp", C, EventThenType.Loop));
        e.ThenLoopName = "other";

        h.Events.Fire(e);
        h.Tracker.SetLocated(C);
        Arrive(h, C);

        Assert.Equal("other", h.Runner.CurrentLoop?.Name);
    }

    // The user's pause is the shared user gate, which no engine stop lifts: the
    // event's walk holds on it, and so does the loop it goes back to.
    [Fact]
    public void AUserPause_HoldsTheEventsWalk_AndTheLoopItGoesBackTo()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        h.Coordinator.AssertGate(MovementCoordinator.UserGate, "test");
        h.Sent.Clear();

        h.Events.Fire(Add(h, WalkTo("errand", C)));
        Assert.Empty(h.Sent);                                         // nothing moves
        Assert.Contains(MovementCoordinator.UserGate, h.Coordinator.AssertedGates);

        Arrive(h, C);

        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.Empty(h.Sent);
        Assert.Contains(MovementCoordinator.UserGate, h.Coordinator.AssertedGates);
    }

    // The master switch off freezes every engine on its own gate. An event's walk
    // caught by it is held, not abandoned: the run stays the running one, sends
    // nothing, the event behind it waits (and is dropped at the wait limit like any
    // other), and the walk goes on when the switch is back.
    [Fact]
    public void AutoAllOff_HoldsTheRunAndTheQueue_UntilItIsBackOn()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(A);
        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test");

        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        for (int i = 0; i < 5; i++)
        {
            now += TimeSpan.FromSeconds(1);
            h.Events.Tick();
        }

        Assert.Empty(h.Sent);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test");

        Assert.Equal("n", Assert.Single(h.Sent));
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);
    }

    // ----- The way back refused (paradigm-20261009-220128) ---------------------

    // The report's shape: the loop's rooms can be left on foot but only entered by a
    // room command, which is a teleport to the route search.
    //
    //   7/131 Study --N-- 7/132 Dusty Stair --E (one way)--> 1/21 City Entrance
    //   1/21 --W-- 1/10 Library --SW (CMD 100 teleport)--> 7/131
    private const string StudyGraphJson = """
        [
          { "Map Number": 7, "Room Number": 131, "Name": "Study",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "7/132", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 132, "Name": "Dusty Stair",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "7/131", "E": "1/21", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "City Entrance",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/10",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Library",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "0", "S": "0", "E": "1/21", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)",
            "U": "0", "D": "0" }
        ]
        """;

    private const string StudyTbinfoJson = """[ { "Number": 100, "Action": "pull book:teleport 131 7\n" } ]""";

    private static readonly RoomKey Study = new(7, 131);
    private static readonly RoomKey Stair = new(7, 132);
    private static readonly RoomKey City = new(1, 21);
    private static readonly RoomKey Library = new(1, 10);

    // The user starts the loop in the study; the boss event walks out to the city.
    private Harness StudyHarness(params (RoomKey From, RoomKey To)[] allowedTeleports)
    {
        Harness h = Harness.Create(_root, TestLoopSet, StudyGraphJson, StudyTbinfoJson, loopWalksItself: true);
        h.Walker.SetTeleportResolver((_, _) => "pull book");
        HashSet<(RoomKey From, RoomKey To)> allowed = new(allowedTeleports);
        h.Walker.SetAutomaticWalkTeleports(() => allowed);
        h.Loops.Save(new Loop("Farm", new[] { Study, Stair }));
        h.Tracker.SetLocated(Study);
        h.Runner.Start(h.Loops.Loops.Single(l => l.Name == "Farm"), userStarted: true);
        h.Events.Fire(Add(h, BossWalk("Second boss", "second boss", City)));
        h.Tracker.SetLocated(City);
        return h;
    }

    // What the report's client did, and all it said was in the program log: the walk
    // back to the loop is the client's own, the teleport on the only way back isn't
    // one Settings → Teleports allows such walks, so the loop ended where the event
    // did. That rule stands; the terminal now says so.
    [Fact]
    public void TheLoopBehindATeleportAutomaticWalksMayNotUse_SaysWhyItDidNotGoBack()
    {
        using Harness h = StudyHarness();

        Arrive(h, City);

        Assert.Equal(LoopState.Idle, h.Runner.State);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("Event 'Second boss' finished, but loop 'Farm' didn't get going", notice);
        Assert.Contains("this is an automatic walk", notice);
        Assert.Contains("Tick \"Library (1/10) → Study (7/131)\" on Settings → Teleports", notice);
        Assert.Contains("failed", h.Events.LastThenSummary);
        Assert.Contains(h.EventLog, l => l.Contains("didn't get going"));
    }

    // The same refusal arriving a moment after the loop started (the walker waited on
    // something before planning) is said too.
    [Fact]
    public void ARefusalAfterTheLoopStarted_IsSaidToo()
    {
        using Harness h = StudyHarness((Library, Study));
        Arrive(h, City);
        Assert.Empty(h.Notices);

        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.Failed, "approach failed: no route"));

        Assert.Contains("loop 'Farm' didn't get going: approach failed: no route", Assert.Single(h.Notices));
    }

    // With the teleport ticked, the loop walks back.
    [Fact]
    public void WithTheTeleportAllowed_TheLoopWalksBack()
    {
        using Harness h = StudyHarness((Library, Study));

        Arrive(h, City);

        Assert.Empty(h.Notices);
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal("Farm", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.EventLog, l => l.Contains("'Second boss' finished; starting loop 'Farm'"));
    }
}
