using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Cash;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// What an event run has to live through without being lost or left stuck: its
// engine failing from inside its own teardown, a detour or a reconnect stopping
// and restarting the engine under it, and a dropped connection with events
// waiting. Each of these either stranded the queue or dropped it.
public sealed class EventRunSurvivalTests : IDisposable
{
    private const string TestLoopSet = "test-event-survival";

    private readonly string _root;

    public EventRunSurvivalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-event-survival-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(TestLoopSet), recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly RoomKey A = new(1, 1);
    private static readonly RoomKey B = new(1, 2);
    private static readonly RoomKey C = new(1, 3);

    private Harness NewHarness(bool deferPosts = false) => Harness.Create(_root, TestLoopSet, deferPosts: deferPosts);

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

    // "Loop 3 laps, then walk to C": the user's stop-rule loop event.
    private static ScheduledEvent FarmThenWalk(Harness h, int laps = 3)
    {
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        return Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", StopAfterLaps = laps,
            Then = EventThenType.WalkTo, ThenWalkTo = new RoomRef(C.Map, C.Room),
        });
    }

    private static void Lap(Harness h) => h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "farm"));

    private static void Arrive(Harness h, RoomKey room) =>
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "arrived", room));

    // ----- A failure raised from inside the engine's own teardown ------------

    // The walker raises Failed and only then resets itself. The waiting event's
    // walk, started from inside that raise, was wiped by the reset: its run stood
    // with an idle walker and nothing left to end it, holding the queue for good.
    [Fact]
    public void AWalkFailingMidRoute_TheWaitingEventsWalkSurvives()
    {
        using Harness h = NewHarness(deferPosts: true);
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("three", C, EventThenType.Nothing)));

        h.Walker.AbortFromRecoveryFailure("lost in a grid");
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);      // not acted on inside the raise
        h.Pump();

        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(B, h.Walker.Destination);
        Assert.Contains("'three'", h.Events.QueueSummary);
    }

    // The lone-event shape: its own Then walk-to was wiped the same way, and with
    // the run left standing the event's next firing was skipped as "still going".
    [Fact]
    public void ALoneEventsWalkFailingMidRoute_ItsThenWalkSurvives()
    {
        using Harness h = NewHarness(deferPosts: true);
        h.Tracker.SetLocated(B);
        ScheduledEvent e = Add(h, WalkTo("one", C, EventThenType.WalkTo));
        e.ThenWalkTo = new RoomRef(A.Map, A.Room);
        h.Events.Fire(e);

        h.Walker.AbortFromRecoveryFailure("lost in a grid");
        h.Pump();

        Assert.Contains("'one' Then walk-to", h.Events.RunSummary);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(A, h.Walker.Destination);
    }

    // ----- A detour stopping and restarting the engine -----------------------

    // A bank or sell trip stops the loop and starts it again when it is done. That
    // stop read as the user's: the event was abandoned, the waiting event dropped,
    // and the loop then resumed with no stop rule left to end it.
    [Fact]
    public void ADetourStoppingAStopRuleLoop_KeepsTheEventItsLapsAndTheQueue()
    {
        using Harness h = NewHarness();
        h.Events.Fire(FarmThenWalk(h));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));
        Lap(h);

        DetourResume detour = DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true);
        detour.Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");

        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Contains("suspended", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);

        detour.Resume(h.Walker, h.Runner, h.AutoLair);

        Assert.DoesNotContain("suspended", h.Events.RunSummary);
        Assert.Contains("1 lap(s)", h.Events.RunSummary);            // the lap before the detour still counts
        Lap(h);
        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Lap(h);

        Assert.Contains("'farm' Then walk-to", h.Events.RunSummary);  // the rule still ended it, at 3
        Assert.Contains("'boss'", h.Events.QueueSummary);
    }

    // A stop rule met while the loop is away (its minutes ran out during the
    // detour) waits for the loop to come back: a Then started under the detour
    // would be run over by its resume.
    [Fact]
    public void AStopRuleMetDuringADetour_IsAppliedWhenTheLoopIsBack()
    {
        using Harness h = NewHarness(deferPosts: true);
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        ScheduledEvent farm = FarmThenWalk(h);
        farm.StopAfterLaps = null;
        farm.StopAfterMinutes = 10;
        h.Events.Fire(farm);
        DetourResume detour = DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true);
        detour.Stop(h.Walker, h.Runner, h.AutoLair, "auto-deposit reroute");
        h.Walker.WalkTo(C);                                           // the detour's own walk

        now += TimeSpan.FromMinutes(11);
        h.Events.Tick();
        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Equal(C, h.Walker.Destination);                        // the detour is left alone

        h.Walker.Stop("the detour's walk is done");
        detour.Resume(h.Walker, h.Runner, h.AutoLair);
        Assert.Equal(LoopState.Running, h.Runner.State);              // not stopped inside its own start
        h.Pump();

        Assert.Contains("'farm' Then walk-to", h.Events.RunSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);
    }

    // The user's own stop still ends the event and the queue.
    [Fact]
    public void TheUsersStopOfTheLoop_StillEndsTheEventAndTheQueue()
    {
        using Harness h = NewHarness();
        h.Events.Fire(FarmThenWalk(h));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));

        h.Runner.Stop("user stop");

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // Stop pressed during the detour: no engine of the event's is running to say so.
    [Fact]
    public void StopDuringTheDetour_EndsTheSuspendedEvent()
    {
        using Harness h = NewHarness();
        using MovementController toolbar = new(h.Walker, h.Runner, h.AutoLair, h.Coordinator);
        toolbar.Stopping += h.Events.NoteUserStop;
        h.Events.Fire(FarmThenWalk(h));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");

        toolbar.Stop();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // A detour that never brings the loop back must not hold the queue for good:
    // after five minutes with nothing moving the event is given up as a user Stop
    // ends it. Something called the detour off, so nothing sets off by itself now:
    // no Then walk, no waiting event, one terminal notice.
    // With the master switch off every engine stands idle by design, so the idle
    // limit must not run then, or every suspended event would be given up five
    // minutes after the switch went off. It counts time the switch was on.
    [Fact]
    public void ASuspendedEvent_IsNotGivenUpForTimeTheMasterSwitchWasOff()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        bool off = false;
        h.Events.IsMasterSwitchOff = () => off;
        h.Events.Fire(FarmThenWalk(h));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");
        h.Events.Tick();
        now += TimeSpan.FromMinutes(4);
        h.Events.Tick();

        off = true;
        h.Events.NoteMasterSwitchChanged();
        now += TimeSpan.FromHours(3);
        h.Events.Tick();
        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Empty(h.Notices);

        off = false;
        h.Events.NoteMasterSwitchChanged();
        h.Events.Tick();
        Assert.Contains("'farm' Loop", h.Events.RunSummary);   // four of its five minutes used

        now += TimeSpan.FromMinutes(2);
        h.Events.Tick();
        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("[Event 'farm' given up:", Assert.Single(h.Notices));
    }

    [Fact]
    public void ADetourThatNeverComesBack_IsGivenUpAsAStopIs_NothingSetsOff()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Events.Fire(FarmThenWalk(h));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");

        h.Events.Tick();
        now += TimeSpan.FromMinutes(4);
        h.Events.Tick();
        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Empty(h.Notices);

        int sent = h.Sent.Count;
        now += TimeSpan.FromMinutes(2);
        h.Events.Tick();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal(sent, h.Sent.Count);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("[Event 'farm' given up:", notice);
        Assert.Contains("never brought it back", notice);
        Assert.Contains("1 waiting event(s) were dropped", notice);
        Assert.Contains(h.EventLog, l => l.Contains("never brought it back"));
        Assert.DoesNotContain(h.EventLog, l => l.Contains("didn't get done") || l.Contains("done; then"));
    }

    // The user's walk ends the detour for good, and they stand where it ends. The
    // event it left suspended, and the one waiting behind it, must not set off
    // minutes later.
    [Fact]
    public void AUserWalkAfterADetour_ThenStandingStill_StartsNothingFiveMinutesLater()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(A);
        h.Runner.Start(new Loop("ab", new[] { A, B }), userStarted: true);
        h.Events.Fire(Add(h, WalkTo("boss one", C)));                 // then go back to the loop
        h.Events.Fire(Add(h, WalkTo("boss two", B, EventThenType.Nothing)));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");
        h.Walker.WalkTo(B);                                           // the detour's walk to its shop

        h.Walker.Stop("superseded by new user walk-to");              // the user's own walk-to
        h.Tracker.SetLocated(B);
        h.Walker.WalkTo(A, preferTeleportFree: true);
        h.Tracker.SetLocated(A);
        h.Walker.Stop("user stop from Navigation");                   // they arrive; nothing moves
        int sent = h.Sent.Count;
        h.Events.Tick();
        now += TimeSpan.FromMinutes(6);
        h.Events.Tick();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal(sent, h.Sent.Count);
        Assert.Single(h.Notices);
    }

    // The same for a stop-rule loop whose bank reroute a plain stop ended: its Then
    // walk-to must not set off five minutes later.
    [Fact]
    public void AStopRuleLoopWhoseRerouteWasStopped_DoesNotWalkItsThenLater()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Events.Fire(FarmThenWalk(h));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "auto-deposit reroute");
        h.Walker.WalkTo(B);                                           // the bank walk
        h.Walker.Stop("remote stop");                                 // a plain stop ends the reroute
        h.Events.Tick();
        now += TimeSpan.FromMinutes(6);
        h.Events.Tick();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Single(h.Notices);
    }

    // Settings → Events: how long a paused event may stand still. The character's
    // own value, kept inside 1-120 minutes; the default is five.
    [Fact]
    public void ThePausedEventLimit_IsReadFromTheCharactersSettings()
    {
        using Harness h = NewHarness();
        Assert.Equal(TimeSpan.FromMinutes(5), h.Events.SuspendedIdleLimit);
        h.Profile.Current!.EventSuspendedIdleMinutes = 500;
        Assert.Equal(TimeSpan.FromMinutes(120), h.Events.SuspendedIdleLimit);
        h.Profile.Current.EventSuspendedIdleMinutes = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), h.Events.SuspendedIdleLimit);
    }

    [Fact]
    public void AOneMinutePausedEventSetting_GivesUpAfterOneMinuteStandingStill()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Profile.Current!.EventSuspendedIdleMinutes = 1;
        h.Events.Fire(FarmThenWalk(h));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");

        h.Events.Tick();
        now += TimeSpan.FromSeconds(50);
        h.Events.Tick();
        Assert.Contains("'farm' Loop", h.Events.RunSummary);

        now += TimeSpan.FromSeconds(15);
        h.Events.Tick();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("in 1 minute(s)", Assert.Single(h.Notices));
    }

    // Time spent walking, however long, is not standing still.
    [Fact]
    public void ADetourStillWalking_IsNeverGivenUp()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Events.Fire(FarmThenWalk(h));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");
        h.Walker.WalkTo(C);                                           // the detour's long walk

        for (int i = 0; i < 20; i++)
        {
            now += TimeSpan.FromMinutes(1);
            h.Events.Tick();
        }

        Assert.Contains("suspended", h.Events.RunSummary);
        Assert.Empty(h.Notices);
    }

    // A walk the user starts while an event is paused behind a detour takes over at
    // once (RouteChoicePrompt tells the manager as the walk commits): the event and
    // the queue end, and the user's walk is left to go on.
    [Fact]
    public void AUserWalkStartedWhileAnEventIsSuspended_EndsTheEventAndTheQueueAtOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("boss one", C)));
        h.Events.Fire(Add(h, WalkTo("boss two", B, EventThenType.Nothing)));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");
        Assert.Contains("suspended", h.Events.RunSummary);

        h.Walker.WalkTo(B, preferTeleportFree: true);                 // the user's walk, committed
        h.Events.NoteUserStop();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(B, h.Walker.Destination);
        Assert.Contains(h.EventLog, l => l.Contains("'boss one' abandoned") && l.Contains("stopped by the user"));
    }

    // And a loop the user starts meanwhile.
    [Fact]
    public void AUserLoopStartedWhileAnEventIsSuspended_EndsTheEventAndTheQueueAtOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("boss one", C)));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));
        DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true)
            .Stop(h.Walker, h.Runner, h.AutoLair, "sell detour");
        Assert.Contains("suspended", h.Events.RunSummary);

        Assert.True(h.Runner.Start(new Loop("mine", new[] { A, B }), userStarted: true));
        h.Events.NoteUserStop();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal("mine", h.Runner.CurrentLoop?.Name);
    }

    // A walk-to event's walk taken away for a sell trip or a flee: the detour's own
    // walks aren't the event's, and the walk to the event's room starting again is.
    [Fact]
    public void ADetourTakingAnEventsWalk_TheEventGoesOnWhenItsWalkIsBack()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        DetourResume detour = DetourResume.Snapshot(h.Walker, h.Runner, h.AutoLair, includeWalk: true);
        Assert.Equal(DetourResumeKind.Walk, detour.Kind);

        detour.Stop(h.Walker, h.Runner, h.AutoLair, "PvP flee");
        h.Walker.WalkTo(B);                                           // the detour's walk
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", B));

        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("suspended", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        detour.Resume(h.Walker, h.Runner, h.AutoLair);
        Assert.DoesNotContain("suspended", h.Events.RunSummary);

        Arrive(h, C);
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
    }

    // ----- The watch on what an event leaves running --------------------------

    // A loop event started while standing on the loop has arrived already. The
    // watch for "did it get going" stayed armed, and the loop failing of its own
    // accord laps later was blamed on the event in the terminal.
    [Fact]
    public void ALoopStartedOnTheSpot_ALaterFailureIsNotBlamedOnTheEvent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        }));
        Assert.Equal(LoopState.Running, h.Runner.State);

        Lap(h);
        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.Failed, "tier3 recovery failed: lost"));

        Assert.Empty(h.Notices);
        Assert.Contains("under way", h.Events.LastThenSummary);
    }

    [Fact]
    public void AGoBackToALoopWeAreStandingOn_ALaterFailureIsNotBlamedOnTheEvent()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(A);
        h.Runner.Start(new Loop("ab", new[] { A, B }), userStarted: true);
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 5, Then = EventThenType.Resume,
        }));
        now += TimeSpan.FromSeconds(6);
        h.Events.Tick();

        now += TimeSpan.FromHours(3);
        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.Failed, "tier3 recovery failed: lost"));

        Assert.Empty(h.Notices);
    }

    // ----- The queue -----------------------------------------------------------

    // An open-ended loop event with another waiting behind it was started and
    // stopped in the same breath: a Started, a first-waypoint (which resets the
    // session statistics and tells the party @reset) and a move on the wire.
    [Fact]
    public void AnOpenEndedLoopWithAnEventWaiting_IsHandedOn_NotStartedToBeStopped()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        int starts = 0, firstWaypoints = 0;
        h.Runner.Event += l =>
        {
            if (l.Kind == LoopEventKind.Started) starts++;
            if (l.Kind == LoopEventKind.ReachedFirstWaypoint) firstWaypoints++;
        };
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 0, Then = EventThenType.Nothing,
        }));
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        }));
        h.Events.Fire(Add(h, WalkTo("boss", C)));
        h.Sent.Clear();

        h.Events.Tick();

        Assert.Contains("'boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'farm'", h.Events.RunSummary);
        Assert.Equal(0, starts);
        Assert.Equal(0, firstWaypoints);
        Assert.Equal("n", Assert.Single(h.Sent));                     // the boss walk's first step only

        Arrive(h, C);
        Assert.Equal(1, starts);                                      // started once, after it
        Assert.Equal("farm", h.Runner.CurrentLoop?.Name);
    }

    // A's Then fires B while B is also waiting: B ran as the chain and then again
    // from the queue.
    [Fact]
    public void AChainedEventThatIsAlsoWaiting_RunsOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent b = Add(h, WalkTo("B", B, EventThenType.Nothing));
        ScheduledEvent a = Add(h, WalkTo("A", C, EventThenType.Event));
        a.ThenEventName = "B";
        h.Events.Fire(a);
        h.Events.Fire(b);

        Arrive(h, C);
        Assert.Contains("'B' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);

        Arrive(h, B);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // An "every minute" event behind a full queue wrote a terminal notice every
    // minute. Once per full spell; again only after the queue has had room.
    [Fact]
    public void AFullQueue_SaysSoOncePerEvent_UntilItHasHadRoom()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Profile.Current!.EventQueueLimit = 2;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("w0", B, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("w1", A, EventThenType.Nothing)));
        ScheduledEvent every = Add(h, WalkTo("every-minute", B, EventThenType.Nothing));

        for (int i = 0; i < 20; i++)
        {
            now += TimeSpan.FromSeconds(30);
            h.Events.Fire(every);
        }
        Assert.Single(h.Notices);

        Arrive(h, C);                                                 // w0 starts: the queue has room
        h.Events.Fire(every);                                         // takes the free place
        h.Events.Fire(Add(h, WalkTo("another", C, EventThenType.Nothing)));
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("'another' dropped", h.Notices[1]);
    }

    // ----- Stop -------------------------------------------------------------------

    // The toolbar's Stop with an event waiting out its seconds: no engine was
    // running to report the stop, so the event and its queue carried on.
    [Fact]
    public void StopDuringAWait_EndsTheEventAndTheQueue()
    {
        using Harness h = NewHarness();
        using MovementController toolbar = new(h.Walker, h.Runner, h.AutoLair, h.Coordinator);
        toolbar.Stopping += h.Events.NoteUserStop;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Resume,
        }));
        h.Events.Fire(Add(h, WalkTo("two", C)));

        toolbar.Stop();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'hold' abandoned at Wait — stopped by the user"));
    }

    // A Logoff bank trip jumping a running bank-trip event stops the engines for
    // its own start. The old trip reports Stopped from inside that, by when the run
    // is the new one, which aborted itself and emptied the queue.
    [Fact]
    public void ALogoffBankTripOverARunningBankTrip_IsNotAbortedByTheOldTripsStop()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.SetBankTripStarter(() => h.Walker.WalkTo(C));
        // The trip's own reaction to its walk being stopped, as AutoDepositManager's.
        h.Walker.Event += w =>
        {
            if (w.Kind == WalkEventKind.Stopped) h.Events.NoteBankTripEnded(AutoDepositManager.EventTripOutcome.Stopped);
        };
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "bank", TriggerType = EventTriggerType.Every, ActionType = EventActionType.BankTrip,
            Then = EventThenType.Nothing,
        }));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));

        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "bank before cleanup", TriggerType = EventTriggerType.Logoff, ActionType = EventActionType.BankTrip,
            Then = EventThenType.Nothing,
        }));

        Assert.Contains("'bank before cleanup' BankTrip", h.Events.RunSummary);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Contains("'two'", h.Events.QueueSummary);
    }

    // ----- A dropped connection --------------------------------------------------

    // The waiting events are still waiting after a reconnect (user, 2026-10-10).
    // Nothing is finished or started while the link is down: what ended then is
    // finished at the first prompt back in the game, and the next event starts.
    [Fact]
    public void ADroppedConnection_KeepsTheQueue_AndItPicksUpBackInTheGame()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));

        h.Events.NoteDisconnected();
        Arrive(h, C);                                                 // reported during the outage

        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        h.Events.NoteEnteredGame();

        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // The link can drop between an engine's raise and the dispatcher's turn on the
    // completion it posted: that turn finishes nothing and starts nothing while the
    // link is down. The completion is done on the way back in.
    [Fact]
    public void ADropBetweenACompletionsRaiseAndItsTurn_StartsNothingWhileTheLinkIsDown()
    {
        using Harness h = NewHarness(deferPosts: true);
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        Arrive(h, C);                                                 // posted, not yet run
        h.Events.NoteDisconnected();
        h.Sent.Clear();

        h.Pump();

        Assert.Empty(h.Sent);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        h.Events.NoteEnteredGame();

        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // The wait clocks run through the outage: one past its limit is dropped, with
    // its notice, at the first check after the reconnect and not before.
    [Fact]
    public void AnEventPastItsWaitLimitDuringAnOutage_IsDroppedOnTheWayBackIn()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
        h.EventsNow = () => now;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("old", B, EventThenType.Nothing)));
        now += TimeSpan.FromMinutes(20);
        h.Events.Fire(Add(h, WalkTo("recent", A, EventThenType.Nothing)));

        h.Events.NoteDisconnected();
        now += TimeSpan.FromMinutes(15);
        h.Events.Tick();
        Assert.Empty(h.Notices);
        Assert.Contains("'old'", h.Events.QueueSummary);

        h.Events.NoteEnteredGame();

        Assert.Contains("[Event 'old' dropped: it waited 30 minutes", Assert.Single(h.Notices));
        Assert.Contains("'recent'", h.Events.QueueSummary);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);         // its walk is the walker's to finish
    }

    // A loop restarts itself after a reconnect: the event running it goes with it,
    // laps and stop rule intact, and still holds the queue.
    [Fact]
    public void AStopRuleLoopAcrossAReconnect_GoesOnWithItsLapsAndTheQueue()
    {
        using Harness h = NewHarness();
        h.Events.Fire(FarmThenWalk(h));
        h.Events.Fire(Add(h, WalkTo("boss", B, EventThenType.Nothing)));
        Lap(h);
        Loop farm = h.Runner.CurrentLoop!;

        h.Events.NoteDisconnected();
        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);

        h.Events.NoteEnteredGame();
        h.Runner.Start(farm);                                         // the runner's own restart on the first prompt

        Assert.DoesNotContain("suspended", h.Events.RunSummary);
        Assert.Contains("1 lap(s)", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);
        Lap(h);
        Lap(h);
        Assert.Contains("'farm' Then walk-to", h.Events.RunSummary);
    }

    // An engine stopped during the outage is the drop's doing, not the user's: the
    // run ends without its Then, the waiting events are kept, and an event that
    // fires meanwhile takes its place behind them.
    [Fact]
    public void ARunWhoseEngineIsStoppedDuringAnOutage_Ends_AndTheQueueIsKept()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));

        h.Events.NoteDisconnected();
        h.Walker.Stop("connection lost");

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        h.Events.Fire(Add(h, WalkTo("three", A, EventThenType.Nothing)));
        Assert.Equal("(none)", h.Events.RunSummary);                  // nothing starts while the link is down
        Assert.StartsWith("'two'", h.Events.QueueSummary);
        Assert.Contains("'three'", h.Events.QueueSummary);

        h.Events.NoteEnteredGame();
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Contains("'three'", h.Events.QueueSummary);
    }

    // The user's Stop during an outage is still the user's.
    [Fact]
    public void StopDuringAnOutage_StillEmptiesTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("one", C)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        h.Events.NoteDisconnected();

        h.Events.NoteUserStop();
        h.Events.NoteEnteredGame();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }
}
