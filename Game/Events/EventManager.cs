using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Events;

// In-memory store + dispatcher for the loaded character's ScheduledEvent
// entries. Owns the merge / save path against CharacterProfile.Events, exposes
// CRUD for the settings editor, reconciles saved-target references against
// LoopManager + LairManager, and runs fired events.
//
// A fired event is a RUN: its action goes until it's done, then its Then step
// (ScheduledEvent.ResolvedThen) decides what's next — nothing, go back to what
// was running, start a loop / auto-lair, walk somewhere, or fire another event.
// When an action is done depends on the action: a walk arrives, a command is
// sent, a wait / rest-up / Roomba sweep / bank trip ends, and a loop or
// auto-lair meets one of its stop-after rules (laps, minutes, a boss killed,
// conditions holding).
//
// One run at a time, start to finish, in the order the events fired (user,
// 2026-10-09: "if multiple events fire near same time, it handles them start to
// finish before the next one kicks in"; 2026-10-10: "if we set an event to loop
// 3 times then goto bank, that event should finish before any other events fire
// off ... then do the ones that triggered mid event, in order"). An event that
// fires while another's run is under way waits in a first-in first-out queue and
// starts when that run is over, its Then walk and any chain of Then → event
// included. Two things are not runs:
//   * A command event with nothing after it: it's sent and leaves whatever run
//     is going alone.
//   * A loop or auto-lair event with no stop-after rule. Nothing ends it, so
//     the event is finished the moment the loop has started, and the loop is
//     then simply what the character is doing: an event that fires later takes
//     over from it and goes back to it like any loop the user started.
//
// Only an event with the Logoff trigger jumps the queue (user, 2026-10-10: "an
// event that its 'when' is logoff, should fire immediately, all other events
// queue"; IsLogoffType). It starts at once, the run it interrupts is abandoned,
// and the events already waiting keep their places behind it; the connection it
// is racing would not wait for the run to end.
//
// A queued event starts as if it had fired the moment the run before it ended.
// When that run's Then would only leave an engine running (go back, start a loop
// or auto-lair), the engine isn't started to be stopped again in the same breath:
// the queued event takes it as its own Resume target, so "go back to what was
// running" happens once, after the last of them.
//
// A run the user takes over — they stop its walk / loop / auto-lair, press Stop
// while it waits or rests, or start something of their own meanwhile — ends
// without its Then and empties the queue: the user said stop. So do a death and
// Reset States. A stop by something that brings the engine back itself (a bank or
// sell trip, a flee, a trainer or comeback detour, the loop's own restart after a
// reconnect: LoopEvent.WillResume and its walker / auto-lair twins) is not the
// user's: the run is suspended, keeps its lap count and stop rule, and goes on
// when the engine is back.
//
// A dropped connection ends nothing by itself (user, 2026-10-10: the waiting
// events "should still be waiting after a reconnect"). The queue is kept, its
// wait clocks running; nothing is checked, finished or started while the link is
// down, and it all picks up at the first prompt back in the game. The run that
// was under way lives or dies with its engine: a loop restarts itself and the run
// with it, a walk the drop didn't stop goes on, and a run whose engine is stopped
// during the outage ends without its Then, the next waiting event starting once
// back in. A walk frozen on a movement gate (the user's Pause, Auto-All off)
// holds its run the same way, and the wait limit is what keeps a long freeze or
// outage from piling events up.
//
// Engines raise their Failed and Finished from the middle of their own teardown
// (the walker raises Failed and only then resets). What ends a run is therefore
// posted (Later), never done inside the raise: a walk the next event started there
// would be wiped by the reset that follows.
//
// Saved-target reconciliation: subscribes to LoopManager.LoopsChanged +
// LairManager.SetupsChanged. On either, walks every event whose ActionType is
// Loop or AutoLair and auto-disables any whose referenced name is no longer in
// the corresponding manager's collection. Auto-disable is sticky: re-creating a
// same-named target later doesn't auto-restore — the user re-enables manually
// after confirming the new target matches their intent.
//
// Threading: profile lifecycle, manager-change and engine events fire on the UI
// thread (the producers marshal upstream), as do the trigger sources that call
// Fire and the run ticker (a DispatcherTimer), so no internal locking is needed.
public sealed class EventManager : IDisposable
{
    private readonly ProfileService _profile;
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly AutoWalkManager _walker;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    // Runs an action once the engine raise it was asked from has unwound (see the
    // header).
    private readonly Action<Action> _post;

    // Tracks which events the reconciler auto-disabled (vs the user manually
    // flipping Disabled). The Settings.Events row badge renders "↻ target missing"
    // only for keys in this set. Cleared on profile load.
    private readonly HashSet<ScheduledEvent> _autoDisabled = new();

    // The loaded character's events — empty when no profile is active.
    public ObservableCollection<ScheduledEvent> Events { get; } = new();

    // Returns true when the reconciler — not the user — flipped this event's
    // Disabled flag on. Drives the row badge.
    public bool IsAutoDisabled(ScheduledEvent e) => _autoDisabled.Contains(e);

    // Fires after the reconciler auto-disables one or more events. The row-badge
    // refresh listens for this; here so the engine surface stays observable.
    public event Action? AutoDisabledChanged;

    // An event passed its gates and is about to run.
    public event Action<ScheduledEvent>? Fired;

    public EventManager(
        ProfileService profile,
        LoopManager loops,
        LairManager lairs,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        AutoWalkManager walker,
        LogService? log = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(walker);
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _walker = walker;
        _log = log;
        _post = post ?? (act => Avalonia.Threading.Dispatcher.UIThread.Post(act));

        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        loops.LoopsChanged += ReconcileTargets;
        lairs.SetupsChanged += ReconcileTargets;
        walker.Event += OnWalkEvent;
        loopRunner.Event += OnLoopEvent;
        autoLair.ActiveChanged += OnAutoLairActiveChanged;
        if (profile.Current is { } current) LoadFrom(current);
    }

    // Symmetric tear-down for the five ProfileService / LoopManager / LairManager
    // subscriptions wired in the engine-bound ctor. Today AppServices keeps
    // EventManager alive for the app's lifetime so this is mostly hygiene — but it
    // keeps the rule explicit in case a future refactor scopes EventManager
    // per-character. Idempotent.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTicker();
        _walker.Event -= OnWalkEvent;
        _loopRunner.Event -= OnLoopEvent;
        _autoLair.ActiveChanged -= OnAutoLairActiveChanged;
        _profile.ProfileLoaded -= LoadFrom;
        _profile.ProfileClosed -= Clear;
        _profile.ProfileSaving -= SnapshotForSave;
        _loops.LoopsChanged -= ReconcileTargets;
        _lairs.SetupsChanged -= ReconcileTargets;
    }
    private bool _disposed;

    // Starts a Roomba sweep for EventActionType.Roomba; returns null on success or
    // the sweep's refusal reason. Bound by AppServices to GhSweepManager, which is
    // built after this manager.
    private Func<EventRoombaMode, string?>? _startRoomba;
    public void SetRoombaStarter(Func<EventRoombaMode, string?> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _startRoomba = start;
    }

    // Bind the wire sender for EventActionType.Command dispatch. Same shape as the
    // other automation engines — the main window VM supplies the gate-wrapped
    // SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // ----- CRUD ------------------------------------------------------

    // Append a new event and persist. No duplicate check.
    public void Add(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Events.Add(e);
        ReconcileTargets();
        _profile.Save();
    }

    // Replace an existing event by reference. Persists. No-op when original isn't
    // in the list.
    public bool Replace(ScheduledEvent original, ScheduledEvent updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int idx = Events.IndexOf(original);
        if (idx < 0) return false;
        Events[idx] = updated;
        _autoDisabled.Remove(original);
        // An edit saved with the switch still as the remote command left it keeps
        // the note of who made it; one that changes the state is the user's own.
        if (_lastSwitch.Remove(original, out string? note) && updated.Disabled == original.Disabled)
            _lastSwitch[updated] = note;
        ReconcileTargets();
        _profile.Save();
        return true;
    }

    // Remove an event by reference. Persists. No-op when not in the list.
    public bool Remove(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Events.Remove(e)) return false;
        _autoDisabled.Remove(e);
        _lastSwitch.Remove(e);
        _profile.Save();
        return true;
    }

    // ----- One event switched on or off, without opening it for edit ------------
    //
    // What the remote @event command does. The state is the same Disabled flag the
    // edit window's "Disabled" box sets, saved with the character at once.

    public enum SwitchResult { SwitchedOn, SwitchedOff, AlreadyOn, AlreadyOff, TargetMissing }

    // "Disable all events" (Settings → Events) is ticked: nothing fires, whatever
    // each event's own state says.
    public bool AllDisabled => _profile.Current?.EventsGloballyDisabled == true;

    // That event is the one whose run is under way.
    public bool IsRunning(ScheduledEvent e) => _run is { } run && ReferenceEquals(run.Event, e);

    // For the bug report: who last switched that event by remote command, which
    // way and when, this session. Null when nobody has.
    public string? LastSwitch(ScheduledEvent e) => _lastSwitch.GetValueOrDefault(e);

    private readonly Dictionary<ScheduledEvent, string> _lastSwitch = new();

    // The events a typed name stands for, by the rule the remote @loop commands
    // use for a loop's name (LoopManager.FindByName): a name that equals it wins
    // outright; otherwise every event whose name holds every typed word, in any
    // order; and once more without apostrophes, which typed chat drops. One result
    // is a match, several an ambiguity for the caller to report, none a miss. An
    // event with no name can't be named.
    //
    // The three steps are written out here and not shared with LoopManager: loops
    // are keyed by name, one to a name, and events are a list that may hold two of
    // a name, so the exact step differs. The word match itself is the shared one.
    public IReadOnlyList<ScheduledEvent> FindByName(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ScheduledEvent>();
        string typed = query.Trim();
        ScheduledEvent[] found = Events
            .Where(e => string.Equals(e.Name?.Trim(), typed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length > 0) return found;
        found = Events.Where(e => RoomSearchService.NameMatchesTokens(e.Name, typed)).ToArray();
        if (found.Length > 0) return found;
        string bare = typed.Replace("'", "");
        return Events.Where(e => RoomSearchService.NameMatchesTokens(e.Name?.Replace("'", ""), bare)).ToArray();
    }

    // Switch one event on or off for `by` (who asked, for the log and the bug
    // report). Takes effect at once: the list raises a Replace carrying the same
    // event, which is what starts or stops its own countdown in the scheduler,
    // redraws an open Events tab and moves the Disabled box of an edit window
    // open on it.
    //
    // Switched off, the event fires no more and a firing of it still waiting in
    // the queue is dropped when its turn comes; a run of it already under way goes
    // on to its end, as it does when the event is edited mid-run. An event whose
    // saved loop or Auto-Lair setup is gone is not switched on: it could not run,
    // and the reconciler would only switch it off again.
    public SwitchResult SetEnabled(ScheduledEvent e, bool enabled, string by)
    {
        ArgumentNullException.ThrowIfNull(e);
        int index = Events.IndexOf(e);
        if (index < 0) throw new ArgumentException("The event is not one of this character's.", nameof(e));
        if (enabled != e.Disabled) return enabled ? SwitchResult.AlreadyOn : SwitchResult.AlreadyOff;
        if (enabled && MissingTarget(e) is { } missing)
        {
            _log?.Info("Events", $"Event '{Label(e)}' not switched on for {by}: {missing}.");
            return SwitchResult.TargetMissing;
        }

        e.Disabled = !enabled;
        _autoDisabled.Remove(e);
        _lastSwitch[e] = $"switched {(enabled ? "on" : "off")} by {by} at {_now().ToLocalTime():HH:mm:ss}";
        Events[index] = e;
        _profile.Save();
        _log?.Info("Events",
            $"Event '{Label(e)}' switched {(enabled ? "on" : "off")} by {by}"
            + (!enabled && IsRunning(e) ? "; its run under way goes on to its end" : "")
            + (enabled && AllDisabled ? "; \"Disable all events\" is on, so it won't fire yet" : "")
            + ".");
        return enabled ? SwitchResult.SwitchedOn : SwitchResult.SwitchedOff;
    }

    // Why that event can't run as it is saved: the loop or Auto-Lair setup its
    // action names is no longer in the catalogue. Null when nothing is missing.
    public string? MissingTarget(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.ActionType switch
        {
            EventActionType.Loop when !string.IsNullOrEmpty(e.LoopName) && FindLoop(e.LoopName) is null
                => $"referenced loop '{e.LoopName}' was deleted or renamed",
            EventActionType.AutoLair when !string.IsNullOrEmpty(e.AutoLairSetupName) && FindSetup(e.AutoLairSetupName) is null
                => $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed",
            _ => null,
        };
    }

    // ----- Run hooks ---------------------------------------------------

    // A bank / stash trip for EventActionType.BankTrip; false when it couldn't
    // start. Bound by AppServices to AutoDepositManager.StartEventTrip, which
    // reports the end through NoteBankTripEnded.
    private Func<bool>? _startBankTrip;
    public void SetBankTripStarter(Func<bool> start) => _startBankTrip = start;

    // A stash → bank transfer for EventActionType.StashTransfer: (stash, bank room)
    // → null when it started, else why not. Bound by AppServices to
    // StartStashTransfer; the runner reports the end through NoteStashTransferEnded.
    // `stop` ends one that another event takes over from.
    private Func<RoomKey, RoomKey, string?>? _startStashTransfer;
    private Action? _stopStashTransfer;
    public void SetStashTransferHooks(Func<RoomKey, RoomKey, string?> start, Action stop)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);
        _startStashTransfer = start;
        _stopStashTransfer = stop;
    }

    // Whether the character is resting (the health manager's rest gate is up or a
    // rest is in flight), and a nudge to re-check now — a RestUp run holds
    // RestUpRequested (which counts as a "rest up here" room) until resting stops.
    private Func<bool>? _isResting;
    private Action? _evaluateRest;
    public void SetRestHooks(Func<bool> isResting, Action evaluate)
    {
        _isResting = isResting;
        _evaluateRest = evaluate;
    }

    // Whether a stop-after boss-timer moment has come (EventBossWatcher.StopReached).
    private Func<ScheduledEvent, bool>? _bossStopReached;
    public void SetBossStopCheck(Func<ScheduledEvent, bool> reached) => _bossStopReached = reached;

    // Live money / encumbrance / exp / level readings for a stop-after rule's
    // conditions.
    private Func<EventConditionEvaluator.Readings>? _readStats;
    public void SetStatsReader(Func<EventConditionEvaluator.Readings> read) => _readStats = read;

    // Where a line the user has to see goes: the terminal. A Then that can't get
    // going leaves the character standing where the event ended, and the reason
    // was only in the program log (report paradigm-20261009-220128). Bound by
    // AppServices; the sink posts to the UI thread.
    private Action<string>? _notice;
    public void SetNotice(Action<string> notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _notice = notice;
    }

    // True while a RestUp event is resting: the health manager treats the room as
    // a "rest up here" room, resting to rest max.
    public bool RestUpRequested => _run is { Action: EventActionType.RestUp };

    private readonly Func<DateTimeOffset> _now;

    // ----- Dispatch --------------------------------------------------

    // A chain of Then → event hand-offs longer than this is taken to be a cycle.
    private const int MaxChainDepth = 10;
    private static readonly TimeSpan RunTickInterval = TimeSpan.FromSeconds(1);
    // A rest-up is done once resting stops, but the rest gate only rises on the
    // next prompt; give it this long before reading "not resting" as done.
    private static readonly TimeSpan RestStartGrace = TimeSpan.FromSeconds(3);

    // The queue's bounds, set on Settings → Events (user, 2026-10-10: "make these
    // limits user definable ... and use these as the default settings"). An event
    // waits at most once, so the cap only bites on a character with more events
    // than it all firing behind one run. The wait limit is for a run that doesn't
    // end soon (a long loop event, a walk left paused): without it the events of
    // hours ago would all set off when it does. Time with the master switch off
    // doesn't count toward it (HoldsForMasterSwitch).
    public const int DefaultMaxQueued = 10;
    public const int DefaultMaxQueueWaitMinutes = 30;
    public const int MaxQueuedCeiling = 100;
    public const int MaxQueueWaitMinutesCeiling = 1440;

    // A suspended run whose engine nothing brought back, with every engine idle
    // this long, is given up rather than left holding the queue for good (Settings →
    // Events, user, 2026-10-10).
    public const int DefaultSuspendedIdleMinutes = 5;
    public const int SuspendedIdleMinutesCeiling = 120;

    public int MaxQueued =>
        Math.Clamp(_profile.Current?.EventQueueLimit ?? DefaultMaxQueued, 1, MaxQueuedCeiling);
    public TimeSpan MaxQueueWait => TimeSpan.FromMinutes(
        Math.Clamp(_profile.Current?.EventQueueWaitMinutes ?? DefaultMaxQueueWaitMinutes, 1, MaxQueueWaitMinutesCeiling));
    public TimeSpan SuspendedIdleLimit => TimeSpan.FromMinutes(
        Math.Clamp(_profile.Current?.EventSuspendedIdleMinutes ?? DefaultSuspendedIdleMinutes, 1, SuspendedIdleMinutesCeiling));

    private sealed class EventRun(
        ScheduledEvent e, ScheduledEvent origin, EventResumePlan? resume, int depth, DateTimeOffset startedAt)
    {
        public ScheduledEvent Event { get; } = e;
        // The event whose firing began this chain of Then → event hand-offs.
        public ScheduledEvent Origin { get; } = origin;
        public EventResumePlan? Resume { get; } = resume;
        public int Depth { get; } = depth;
        // Moved forward by any time the run spent held by the master switch, so
        // what is counted from it is time the event was actually running.
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public int Laps;
        public RoomKey? WalkTarget;
        // The action is done and the run is on its Then walk-to.
        public bool OnThenWalk;
        // Its engine was stopped by something that restarts it itself (a detour, a
        // reconnect): the run waits for it, lap count and stop rule intact.
        public bool Suspended;
        // Since when every engine has stood idle under a suspended run.
        public DateTimeOffset? IdleSince;
        // A stop rule met while suspended, applied when the engine is back.
        public string? StopWhenBack;

        // The action still under way; null once the run is on its Then walk.
        public EventActionType? Action => OnThenWalk ? null : Event.ActionType;
        public string Step => OnThenWalk ? "Then walk-to" : Event.ActionType.ToString();
    }

    // At: when it took its place, moved forward by any time its clock stood still
    // (the master switch off, a choice awaited), so the wait limit counts only time
    // it could have run. FiredAt stays the real moment. HeldBySwitch: how much of
    // its wait the master switch was off; HeldAnswered: how much of that the user
    // has already been asked about (see the queue choice below).
    private readonly record struct QueuedEvent(ScheduledEvent Event, DateTimeOffset At)
    {
        public DateTimeOffset FiredAt { get; } = At;
        public TimeSpan HeldBySwitch { get; init; }
        public TimeSpan HeldAnswered { get; init; }
    }

    // ----- Master switch -------------------------------------------------
    // With the master switch (Auto-All) off nothing here acts. An event that
    // fires is skipped, not queued (Fire asks BlockedByMasterSwitch with the
    // event's name; true = off, and AutoModeController.Blocks counts the skip).
    // What is already under way is held, not dropped: the running or suspended
    // event does not move on, an event waiting in the queue does not start and
    // keeps its place, and a completion that lands meanwhile is kept for later.
    // Every clock stops with it (HoldsForMasterSwitch): a wait's seconds, a stop
    // rule's minutes, the queue's wait limit and a suspended run's idle limit all
    // count time the switch was on, so nothing ends or is given up for the hours
    // the switch spent off.
    public Func<string, bool>? BlockedByMasterSwitch { get; set; }
    public Func<bool>? IsMasterSwitchOff { get; set; }

    // Since when the master switch has held everything; null while it is on.
    private DateTimeOffset? _heldSince;
    // Completions that landed while it was off, run when it is back on.
    private readonly List<Action> _whenSwitchBackOn = new();

    // True while the master switch is off. The first read after it comes back on
    // moves every clock forward by the time it was off.
    private bool HoldsForMasterSwitch()
    {
        if (IsMasterSwitchOff?.Invoke() == true)
        {
            _heldSince ??= _now();
            return true;
        }
        if (_heldSince is not { } since) return false;
        _heldSince = null;
        TimeSpan held = _now() - since;
        if (_run is { } run)
        {
            run.StartedAt += held;
            if (run.IdleSince is { } idle) run.IdleSince = idle + held;
        }
        for (int i = 0; i < _queue.Count; i++)
            _queue[i] = _queue[i] with { At = _queue[i].At + held, HeldBySwitch = _queue[i].HeldBySwitch + held };
        // A choice already being awaited stood still with everything else.
        if (_choiceSince is { } asked) _choiceSince = asked + held;
        // Decided here, at the moment the hold ends, so nothing waiting can be
        // taken between the switch coming on and the user being asked.
        else if (_queue.Any(q => q.HeldBySwitch - q.HeldAnswered > HeldQueuePromptAfter)) _choiceSince = _now();
        return false;
    }

    // The master switch was switched (AppServices tells us on both edges, so the
    // clocks stop the moment it goes off and not at the next tick). Coming back
    // on, what landed meanwhile is finished and, with nothing running, the next
    // waiting event starts, in its order: unless the switch held the waiting
    // events long enough that the user is asked first (the queue choice below).
    // remote: the switch was not switched by the user's own press (a party
    // member's `@auto-all on`, the local API), so nobody is there to ask.
    public void NoteMasterSwitchChanged(bool remote = false)
    {
        if (HoldsForMasterSwitch())
        {
            // The prompt asks about a switch-on that is no longer true. The
            // events stay waiting and the question is put again next time.
            if (_choiceSince is not null) QueueChoiceWithdrawn?.Invoke();
            return;
        }
        Action[] landed = _whenSwitchBackOn.ToArray();
        _whenSwitchBackOn.Clear();
        // A completion starts an engine. With the connection down it waits for
        // the way back into the game, like one that landed during the outage:
        // started now, the engine's moves would reach the board's login prompts.
        if (_offline) _whenBackInGame.AddRange(landed);
        else foreach (Action act in landed) act();
        if (_choiceSince is not null)
        {
            string held = $"Master switch back on with {_queue.Count} event(s) waiting, held by it for more than "
                + $"{HeldQueuePromptAfter.TotalMinutes:0} minutes ({string.Join(", ", _queue.Select(q => $"'{Label(q.Event)}' {q.HeldBySwitch.TotalMinutes:0} min"))})";
            if (remote)
            {
                // "if it was triggered remotely the default should be to skip all
                // the events, if it was auto all off for a long period of time"
                // (user, 2026-10-10): a prompt on an unattended client would be
                // answered by nobody, and the queue would stand behind it.
                _log?.Info("Events", held + ": switched on remotely, so they are dropped without asking.");
                _offered = _queue.Select(q => q.Event).ToList();
                SettleQueueChoice(Array.Empty<ScheduledEvent>(),
                    unanswered: $"Auto-All was switched back on remotely after more than {HeldQueuePromptAfter.TotalMinutes:0} minutes off");
                return;
            }
            _log?.Info("Events", held + ": asking which to run; none of them starts until that is answered.");
            QueueChoiceNeeded?.Invoke();
        }
        StartNextQueued();
    }

    // ----- Queue choice after a long spell with the master switch off ------
    // Events waiting when the switch goes off are kept, with their clocks
    // stopped. Back on within HeldQueuePromptAfter they carry on by themselves.
    // After longer, up to a queue-full of stale events would set off one after
    // another, so the user is asked which to run: "if we swap back on and have
    // had events in queue for longer than 5 minutes because of this, it should
    // pop up with a menu for the user to pick which ones to execute and which
    // ones to drop" (user, 2026-10-10). Until the answer nothing waiting starts
    // and nothing is dropped for the wait limit; the running or suspended event
    // carries on, and events that fire queue behind as usual.
    //
    // The choice is never left standing, since every later event would wait
    // behind it for good: closing the prompt drops the listed events
    // (DropQueueChoice), and a switch-on nobody is at the keyboard for drops them
    // without asking (NoteMasterSwitchChanged, remote). Only the switch going off
    // again keeps it, and then the switch is what holds the queue.
    public static readonly TimeSpan HeldQueuePromptAfter = TimeSpan.FromMinutes(5);

    // One waiting event as the prompt lists it. Waiting: since it fired.
    // HeldBySwitch: how much of that the master switch was off.
    public readonly record struct HeldQueueEntry(ScheduledEvent Event, TimeSpan Waiting, TimeSpan HeldBySwitch);

    // Since when the user's choice has been awaited; null when none is.
    private DateTimeOffset? _choiceSince;
    // The events the open prompt lists: the answer is about these only, so one
    // that queued after the prompt opened is neither run early nor dropped by it.
    private List<ScheduledEvent>? _offered;

    // Open the prompt, or bring the open one forward.
    public event Action? QueueChoiceNeeded;
    // Close the prompt with no answer: the switch went off again, or the queue
    // it asked about is gone (another profile, a Stop).
    public event Action? QueueChoiceWithdrawn;

    // The waiting events as they stand, for the prompt to list. Empty when no
    // choice is awaited.
    public IReadOnlyList<HeldQueueEntry> OfferQueueChoice()
    {
        if (_choiceSince is null) return Array.Empty<HeldQueueEntry>();
        DateTimeOffset now = _now();
        _offered = _queue.Select(q => q.Event).ToList();
        return _queue.Select(q => new HeldQueueEntry(q.Event, now - q.FiredAt, q.HeldBySwitch)).ToList();
    }

    // The user's answer: of the events the prompt listed, the ones in `run` keep
    // their places and the rest are dropped. The queue then moves as usual
    // (nothing starts while the connection is down).
    public void ResolveQueueChoice(IReadOnlyCollection<ScheduledEvent> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        SettleQueueChoice(run, unanswered: null);
    }

    // No answer is coming: the user closed the prompt by its X ("drop all of
    // them"; user, 2026-10-10), or it could not be shown. Every event it listed
    // is dropped, and the queue runs on for whatever fires afterwards. A choice
    // left standing would hold every later event behind it for good.
    public void DropQueueChoice(string why)
    {
        ArgumentException.ThrowIfNullOrEmpty(why);
        SettleQueueChoice(Array.Empty<ScheduledEvent>(), unanswered: why);
    }

    // The wording for an X-close.
    public const string PromptClosedUnanswered = "the Waiting Events window was closed without an answer";

    // unanswered: why nobody chose (every listed event is then dropped, with that
    // as the reason given); null when `run` is the user's own answer.
    private void SettleQueueChoice(IReadOnlyCollection<ScheduledEvent> run, string? unanswered)
    {
        if (_choiceSince is not { } since) return;
        List<ScheduledEvent> offered = _offered ?? new List<ScheduledEvent>();
        DateTimeOffset now = _now();
        _choiceSince = null;
        _offered = null;
        int dropped = 0, kept = 0;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            QueuedEvent q = _queue[i];
            bool asked = offered.Any(e => ReferenceEquals(e, q.Event));
            if (asked && !run.Any(e => ReferenceEquals(e, q.Event)))
            {
                RemoveQueuedAt(i);
                dropped++;
                string waited = $"after waiting {(now - q.FiredAt).TotalMinutes:0} min ({q.HeldBySwitch.TotalMinutes:0} min of it with the master switch off)";
                _log?.Info("Events", unanswered is null
                    ? $"Event '{Label(q.Event)}' dropped by the user's choice {waited}."
                    : $"Event '{Label(q.Event)}' dropped {waited}: {unanswered}.");
                continue;
            }
            if (asked)
            {
                kept++;
                _log?.Info("Events", $"Event '{Label(q.Event)}' kept by the user's choice: it runs in its turn.");
            }
            // The wait limit does not count the time spent on the question.
            DateTimeOffset stoodSince = q.At > since ? q.At : since;
            _queue[i] = q with { At = q.At + (now - stoodSince), HeldAnswered = q.HeldBySwitch };
        }
        if (dropped > 0)
        {
            _notice?.Invoke(unanswered is not null
                ? $"[{dropped} waiting event(s) dropped: {unanswered}]"
                : kept > 0
                    ? $"[{dropped} waiting event(s) dropped by your choice; {kept} will run]"
                    : $"[{dropped} waiting event(s) dropped by your choice; none will run]");
        }
        StartNextQueued();
    }

    // The queue the choice was about is gone.
    private void EndQueueChoice()
    {
        if (_choiceSince is null) return;
        _choiceSince = null;
        _offered = null;
        QueueChoiceWithdrawn?.Invoke();
    }

    private EventRun? _run;
    private readonly List<QueuedEvent> _queue = new();
    // Events already told, in the terminal, that the queue was full. Emptied
    // whenever the queue gives up a place, so each is told once per full spell.
    private readonly HashSet<ScheduledEvent> _toldFull = new();
    // Between a dropped connection and the first prompt back in the game.
    private bool _offline;
    // What ended a run while the link was down, done on the way back in.
    private readonly List<Action> _whenBackInGame = new();
    private Avalonia.Threading.DispatcherTimer? _ticker;
    // Set while this manager stops or starts an engine, so the Stopped / Started
    // that raises isn't read as the user taking over.
    private bool _driving;
    // Why an engine this manager was starting refused, said by the engine while
    // _driving hid the event from the handlers.
    private string? _drivenFailure;

    // The loop the running event goes back to, or null. It is held here, apart from
    // the loop runner, from the moment the event takes over until it hands back, so
    // a change to the character's settings for that loop has to be brought to it.
    public Loop? ResumeLoop => (_run?.Resume as EventResumePlan.Loop)?.SavedLoop;

    // One line for the bug report: the running event, how far along it is, and
    // what it goes back to.
    public string RunSummary => _run is not { } r
        ? "(none)"
        : $"'{Label(r.Event)}' {r.Step} for {(_now() - r.StartedAt).TotalSeconds:0}s"
          + (r.Laps > 0 ? $", {r.Laps} lap(s)" : "")
          + (r.OnThenWalk ? "" : $"; then {r.Event.ResolvedThen}")
          + (r.Resume is { } plan ? $"; resume target {plan.Describe()}" : "")
          + (r.Depth > 0 ? $"; chain depth {r.Depth}" : "")
          + (r.Suspended ? "; suspended (its engine was stopped by something that brings it back)" : "")
          + (_offline ? "; connection down" : "")
          + HeldNote;

    // Said on the run and on the queue while the master switch holds them, so a
    // report of "my event never finished" shows why.
    private string HeldNote => _heldSince is { } since
        ? $"; held by the master switch for {(_now() - since).TotalSeconds:0}s, clocks stopped"
          + (_whenSwitchBackOn.Count > 0 ? $", {_whenSwitchBackOn.Count} completion(s) kept for when it is back on" : "")
        : "";

    // For the bug report: the events waiting behind the running one, oldest first.
    public string QueueSummary => _queue.Count == 0
        ? "(empty)"
        : string.Join(", ", _queue.Select(q => $"'{Label(q.Event)}' (waiting {(_now() - q.At).TotalSeconds:0}s)")) + HeldNote
          + (_choiceSince is null
              ? ""
              : $"; waiting for your choice (held {_queue.Max(q => q.HeldBySwitch).TotalMinutes:0} min by the master switch)");

    // For the bug report: what the engine the last finished event left running
    // (its Then, or an open-ended loop action) came to.
    public string LastThenSummary { get; private set; } = "(none)";

    // The events that jump the queue: the Logoff trigger (fired by the cleanup
    // warning). What an event's action does, a command that sends a log-off
    // included, doesn't make it one.
    internal static bool IsLogoffType(ScheduledEvent e) => e.TriggerType == EventTriggerType.Logoff;

    // Run the event (see the header), or queue it behind the run under way. Skips
    // when Disabled is true, when "Disable all events" is on, or when the fire-time
    // safety net finds a missing saved target (Loop / AutoLair name no longer in the
    // manager's collection) — the safety net mirrors what ReconcileTargets does on
    // LoopsChanged / SetupsChanged, defense in depth for races and direct-disk
    // profile edits. True when the firing was taken: run now, or given a place in
    // the queue.
    public bool Fire(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Disabled) return false;
        // Master switch — Settings → Events "Disable all events" gate.
        // Per-character; checked here so the scheduler doesn't have to
        // worry about it. With no profile loaded, events keep firing.
        if (_profile.Current?.EventsGloballyDisabled == true) return false;
        // Events are the user's own automation: with the master switch off this
        // firing is skipped, not put off and not queued (user, 2026-10-09). Every
        // trigger kind comes through here, a Logoff event included, so one check
        // covers logon, timed, state and boss events alike.
        if (BlockedByMasterSwitch?.Invoke($"event '{Label(e)}'") == true) return false;

        if (e.ActionType == EventActionType.Command && e.ResolvedThen == EventThenType.Nothing)
        {
            Fired?.Invoke(e);
            ExecuteCommand(e);
            return true;
        }
        bool jumps = IsLogoffType(e);
        if (_run is { } holder)
        {
            // A Logoff event runs in the minutes before a cleanup; one that waited
            // behind a long trip would miss the connection. Behind another Logoff
            // run it waits, at the head of the queue.
            if (!jumps || IsLogoffType(holder.Origin) || IsLogoffType(holder.Event))
            {
                if (!Enqueue(e, holder, jumps)) return false;
                Fired?.Invoke(e);
                return true;
            }
            _log?.Info("Events",
                $"Logoff event '{Label(e)}' jumps the queue: '{Label(holder.Event)}' is abandoned at {holder.Step}"
                + (_queue.Count > 0 ? $"; {_queue.Count} waiting event(s) keep their places." : "."));
        }
        else if (!jumps && _queue.Count > 0)
        {
            // Nothing is running but events are waiting (kept across a dropped
            // connection): this one takes its place behind them.
            bool queued = Enqueue(e, holder: null, jumps: false);
            if (queued) Fired?.Invoke(e);
            StartNextQueued();
            return queued;
        }
        Fired?.Invoke(e);
        StartRun(e, _run is { } current ? current.Resume : SnapshotCurrentActivity(), depth: 0);
        return true;
    }

    // ----- Queue -------------------------------------------------------

    // False when this firing is dropped instead: the event is already running or
    // waiting (an "every 5 minutes" event whose run takes 7 runs once, not twice in
    // a row), or the queue is full. jumps: a Logoff event goes ahead of the
    // ordinary ones (behind any Logoff event already waiting) and is never
    // turned away by the cap.
    private bool Enqueue(ScheduledEvent e, EventRun? holder, bool jumps)
    {
        DropExpired();
        string behind = holder is null ? "the events already waiting" : $"'{Label(holder.Event)}' ({holder.Step})";
        if (holder is not null && (ReferenceEquals(holder.Event, e) || ReferenceEquals(holder.Origin, e)))
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' fired again while its own run is still going ({holder.Step}) — this firing is skipped.");
            return false;
        }
        if (_queue.Any(q => ReferenceEquals(q.Event, e)))
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' fired again while it is already waiting — it stays queued once.");
            return false;
        }
        if (jumps)
        {
            int at = 0;
            while (at < _queue.Count && IsLogoffType(_queue[at].Event)) at++;
            _queue.Insert(at, new QueuedEvent(e, _now()));
            _log?.Info("Events",
                $"Logoff event '{Label(e)}' queued ahead of the others, behind {behind}; {_queue.Count} waiting.");
            return true;
        }
        if (_queue.Count >= MaxQueued)
        {
            // An "every minute" event behind a long run would otherwise write the
            // same line to the terminal every minute.
            if (_toldFull.Add(e))
                Dropped(e, $"{_queue.Count} events are already waiting behind {behind}, "
                    + "the most Settings → Events lets wait");
            else
                _log?.Debug("Events", $"Event '{Label(e)}' dropped again — the queue is still full.");
            return false;
        }
        _queue.Add(new QueuedEvent(e, _now()));
        _log?.Info("Events", $"Event '{Label(e)}' queued behind {behind}; {_queue.Count} waiting.");
        return true;
    }

    private void RemoveQueuedAt(int index)
    {
        _queue.RemoveAt(index);
        _toldFull.Clear();
        if (_queue.Count == 0) EndQueueChoice();
    }

    // An event turned away at one of the queue's limits: the log and the terminal
    // both say so, since nothing else will show that it never ran.
    private void Dropped(ScheduledEvent e, string why)
    {
        _log?.Warn("Events", $"Event '{Label(e)}' dropped — {why}.");
        _notice?.Invoke($"[Event '{Label(e)}' dropped: {why}]");
    }

    private void DropExpired()
    {
        // Asked first: it also settles the clocks after a spell with the master
        // switch off, so nothing is dropped for time spent waiting on the switch.
        if (HoldsForMasterSwitch()) return;
        // Nor while the user is being asked which of them to run.
        if (_choiceSince is not null) return;
        DateTimeOffset now = _now();
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            if (now - _queue[i].At <= MaxQueueWait) continue;
            ScheduledEvent stale = _queue[i].Event;
            RemoveQueuedAt(i);
            Dropped(stale, $"it waited {MaxQueueWait.TotalMinutes:0} minutes"
                + (_run is { } holder ? $" behind '{Label(holder.Event)}' ({holder.Step})" : "")
                + ", the longest Settings → Events lets one wait");
        }
    }

    private void ClearQueue(string why)
    {
        if (_queue.Count == 0) return;
        _log?.Info("Events",
            $"{_queue.Count} waiting event(s) dropped ({string.Join(", ", _queue.Select(q => $"'{Label(q.Event)}'"))}) — {why}.");
        _queue.Clear();
        _toldFull.Clear();
        EndQueueChoice();
    }

    // The next waiting event that may still run, taken off the queue. One removed,
    // edited (an edit replaces the instance) or disabled while it waited is dropped.
    private ScheduledEvent? TakeNextQueued()
    {
        // No waiting event starts with the master switch off, whichever way it
        // would have been taken (the run before it ending, its Then handing on,
        // the way back in after a reconnect). It keeps its place.
        if (HoldsForMasterSwitch()) return null;
        // Nor while the user has yet to say which of them should run.
        if (_choiceSince is not null) return null;
        DropExpired();
        while (_queue.Count > 0)
        {
            QueuedEvent next = _queue[0];
            RemoveQueuedAt(0);
            if (!Events.Contains(next.Event))
                _log?.Info("Events", $"Event '{Label(next.Event)}' dropped from the queue — it was removed or edited while it waited.");
            else if (next.Event.Disabled || _profile.Current?.EventsGloballyDisabled == true)
                _log?.Info("Events", $"Event '{Label(next.Event)}' dropped from the queue — it was disabled while it waited.");
            else
            {
                _log?.Info("Events",
                    $"Event '{Label(next.Event)}' leaves the queue after {(_now() - next.At).TotalSeconds:0}s; {_queue.Count} still waiting.");
                return next.Event;
            }
        }
        return null;
    }

    // The run that held the queue is over: start the next waiting event as if it
    // had fired now. Not while the connection is down; NoteEnteredGame does it then.
    private void StartNextQueued()
    {
        if (_run is not null || _offline) return;
        if (TakeNextQueued() is { } next) StartRun(next, SnapshotCurrentActivity(), depth: 0);
    }

    // Runs `act` once the engine raise it was asked from has unwound, or on the way
    // back into the game when the connection is down (see the header).
    private void Later(Action act)
    {
        if (_offline) _whenBackInGame.Add(act);
        else _post(() =>
        {
            // The link can drop between the engine's raise and the dispatcher's turn;
            // nothing is finished or started while it is down.
            if (_offline) _whenBackInGame.Add(act);
            else FinishUnlessHeld(act);
        });
    }

    // A completion runs the event's Then, which starts an engine: with the master
    // switch off it is kept and run when the switch is back on.
    private void FinishUnlessHeld(Action act)
    {
        if (HoldsForMasterSwitch()) _whenSwitchBackOn.Add(act);
        else act();
    }

    // Synchronously fires every EventTriggerType.Logoff event in the list. Called
    // by EventScheduler when the BBS warns of its cleanup shutdown, while the
    // connection is still up. Returns the number of events actually dispatched:
    // one Fire skipped (the master switch off, "Disable all events") is not
    // counted.
    public int FireLogoffEvents()
    {
        // Snapshot to a list so an event action that adds / removes
        // events mid-fire doesn't invalidate the iterator. Fairly
        // contrived but cheap insurance.
        List<ScheduledEvent> snapshot = Events
            .Where(e => e.TriggerType == EventTriggerType.Logoff && !e.Disabled)
            .ToList();
        int taken = 0;
        foreach (ScheduledEvent e in snapshot)
            if (Fire(e)) taken++;
        return taken;
    }

    // origin: the event that began the chain this run belongs to; null for a run
    // that begins one.
    private void StartRun(ScheduledEvent e, EventResumePlan? resume, int depth, ScheduledEvent? origin = null)
    {
        if (_run is { } prior)
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' takes over from '{Label(prior.Event)}' ({prior.Step}; its Then is dropped).");
            EndRun();
            // A transfer is between walks while it searches or deposits, so stopping
            // the engines for the new action wouldn't always reach it.
            if (prior.Action == EventActionType.StashTransfer) _stopStashTransfer?.Invoke();
        }
        // Whatever the last event's Then was getting going is this run's to stop.
        _thenWatch = null;
        EventRun run = new(e, origin ?? e, resume, depth, _now());
        _run = run;
        _log?.Info("Events",
            $"Event '{Label(e)}' started: {e.ActionType}; then {e.ResolvedThen}"
            + (resume is { } plan ? $" (resume target {plan.Describe()})." : "."));

        bool openEnded = e.ActionType is EventActionType.Loop or EventActionType.AutoLair && !e.HasStopRule;
        if (openEnded && HandOpenEndedToQueued(run)) return;
        switch (StartAction(run))
        {
            case ActionStart.Running:
                if (openEnded) FinishOpenEnded(run);
                else StartTicker();
                break;
            case ActionStart.Done:
                Complete(run, finished: true);
                break;
            case ActionStart.Failed:
                Complete(run, finished: false);
                break;
        }
    }

    // A loop or auto-lair with no stop-after rule has nothing to wait for: the
    // event is finished now that it is running and its Then never comes. (With an
    // event already waiting it never gets here: HandOpenEndedToQueued.)
    private void FinishOpenEnded(EventRun run)
    {
        ScheduledEvent e = run.Event;
        EndRun();
        bool loop = e.ActionType == EventActionType.Loop;
        NoteThenStarted(Label(e), "started", loop ? $"loop '{e.LoopName}'" : $"auto-lair '{e.AutoLairSetupName}'",
            loop ? ThenWatchKind.Loop : ThenWatchKind.AutoLair, EngineStart.Started,
            note: " (no Stop after rule, so the event is done and its Then never runs)");
        StartNextQueued();
    }

    // The same event with another waiting behind it: the loop isn't started to be
    // stopped in the same breath (its start resets the session statistics, tells the
    // party @reset and puts a move on the wire). The waiting event starts instead,
    // with the loop as what it goes back to — as a Then loop is handed on.
    private bool HandOpenEndedToQueued(EventRun run)
    {
        ScheduledEvent e = run.Event;
        EventResumePlan? plan = null;
        string what;
        if (e.ActionType == EventActionType.Loop)
        {
            if (FindLoop(e.LoopName) is { } saved) plan = new EventResumePlan.Loop(saved);
            what = $"loop '{e.LoopName}'";
        }
        else
        {
            if (FindSetup(e.AutoLairSetupName) is { } setup) plan = PlanFor(setup);
            what = $"auto-lair '{e.AutoLairSetupName}'";
        }
        // A missing target is StartAction's to report and auto-disable.
        if (plan is null || _queue.Count == 0 || TakeNextQueued() is not { } next) return false;
        EndRun();
        _log?.Info("Events",
            $"Event '{Label(e)}' finished without starting {what}: '{Label(next)}' was waiting and starts now, with it as what it goes back to.");
        LastThenSummary = $"'{Label(e)}' → {what}: handed to '{Label(next)}' {Stamp()}";
        StartRun(next, plan, depth: 0);
        return true;
    }

    private static EventResumePlan.AutoLair PlanFor(LairSetup setup)
    {
        Dictionary<RoomKey, int?> markers = new();
        foreach (LairMarker m in setup.Markers) markers[new RoomKey(m.Map, m.Room)] = m.OverrideRespawnSeconds;
        return new EventResumePlan.AutoLair(markers);
    }

    private enum ActionStart { Running, Done, Failed }

    private ActionStart StartAction(EventRun run)
    {
        ScheduledEvent e = run.Event;
        switch (e.ActionType)
        {
            case EventActionType.WalkTo:
                if (e.WalkToTarget is not { } target)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' has no walk-to target.");
                    return ActionStart.Failed;
                }
                RoomKey key = new(target.Map, target.Room);
                run.WalkTarget = key;
                if (!StartWalk(key, "event walk-to", e.WalkToEntersBossRoom == true)) return ActionStart.Failed;
                // Already standing there: the walker finished inside WalkTo, while
                // _driving hid its Finished from us.
                return _walker.State == WalkState.Idle ? ActionStart.Done : ActionStart.Running;

            case EventActionType.Loop:
                if (FindLoop(e.LoopName) is null)
                {
                    if (!string.IsNullOrWhiteSpace(e.LoopName))
                        AutoDisable(e, $"referenced loop '{e.LoopName}' was deleted or renamed");
                    return ActionStart.Failed;
                }
                return StartLoop(e.LoopName, "event supersede") ? ActionStart.Running : ActionStart.Failed;

            case EventActionType.AutoLair:
                if (FindSetup(e.AutoLairSetupName) is null)
                {
                    if (!string.IsNullOrWhiteSpace(e.AutoLairSetupName))
                        AutoDisable(e, $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed");
                    return ActionStart.Failed;
                }
                return StartAutoLair(e.AutoLairSetupName, "event supersede") ? ActionStart.Running : ActionStart.Failed;

            case EventActionType.Command:
                ExecuteCommand(e);
                return ActionStart.Done;

            case EventActionType.Roomba:
                if (_startRoomba is null) return ActionStart.Failed;
                EventRoombaMode mode = e.RoombaMode ?? EventRoombaMode.Sort;
                // The sweep refuses to start over a running walk / loop / auto-lair.
                StopEngines("event Roomba");
                if (_startRoomba(mode) is { } refused)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' Roomba ({mode}) didn't start: {refused}");
                    return ActionStart.Failed;
                }
                return ActionStart.Running;

            case EventActionType.Wait:
                StopEngines("event wait");
                return ActionStart.Running;

            case EventActionType.RestUp:
                // Stand still to rest: a running engine would walk off mid-rest.
                StopEngines("event rest-up");
                _evaluateRest?.Invoke();
                return ActionStart.Running;

            case EventActionType.BankTrip:
                if (_startBankTrip is null) return ActionStart.Failed;
                StopEngines("event bank trip");
                if (_startBankTrip()) return ActionStart.Running;
                _log?.Warn("Events", $"Event '{Label(e)}' bank / stash trip didn't start (see the AutoDeposit log line).");
                return ActionStart.Failed;

            case EventActionType.StashTransfer:
                if (_startStashTransfer is null) return ActionStart.Failed;
                if (e.TransferStash is not { } stash || e.TransferBank is not { } bank)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' has no stash room or bank to transfer between.");
                    return ActionStart.Failed;
                }
                StopEngines("event stash transfer");
                if (_startStashTransfer(new RoomKey(stash.Map, stash.Room), new RoomKey(bank.Map, bank.Room)) is { } refusal)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' stash transfer didn't start: {refusal}");
                    return ActionStart.Failed;
                }
                return ActionStart.Running;
        }
        return ActionStart.Failed;
    }

    // The action is done (finished) or couldn't be done (not finished): either
    // way the Then step runs — "go back to the loop" should still happen when the
    // walk or trip fails.
    private void Complete(EventRun run, bool finished)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        _log?.Info("Events", finished
            ? $"Event '{Label(run.Event)}' done; then {run.Event.ResolvedThen}."
            : $"Event '{Label(run.Event)}': its {run.Event.ActionType} didn't get done; then {run.Event.ResolvedThen} anyway.");
        RunThen(run);
    }

    // The user took the run over (or the character died, or the states were
    // reset): end it without its Then. Nothing that waited behind it starts
    // either — a stop is a stop, not the next event's cue.
    //
    // byUser false is an engine reporting it was stopped. With the connection down
    // that is the drop's doing, not the user's: the run still ends without its
    // Then, but the waiting events are kept for the way back in.
    private void Abort(EventRun run, string why, bool byUser = false)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        if (_offline && !byUser)
        {
            _log?.Info("Events",
                $"Event '{Label(run.Event)}' ended at {run.Step} — {why} with the connection down; its Then is skipped"
                + (_queue.Count > 0 ? $" and {_queue.Count} waiting event(s) are kept for the way back in." : "."));
            return;
        }
        _log?.Info("Events", $"Event '{Label(run.Event)}' abandoned at {run.Step} — {why}; its Then is skipped.");
        ClearQueue(why);
    }

    private void EndRun()
    {
        _run = null;
        StopTicker();
    }

    private void EndByUser(string why)
    {
        if (_run is { } run) Abort(run, why, byUser: true);
        else ClearQueue(why);
        _whenBackInGame.Clear();
        _whenSwitchBackOn.Clear();
        _thenWatch = null;
    }

    // Reset States: end the running event without its Then.
    public void CancelRun() => EndByUser("states reset");

    // The character died (PlayerDeathHalt). The engines are stopped in the
    // graveyard; a run that was standing still (a wait, a rest) or a queued event
    // must not walk out of it.
    public void NoteDeath() => EndByUser("the character died");

    // The user pressed Stop (MovementController.Stopping, the Navigation window's
    // Stop). The engines say so themselves when one of them is running; this is for
    // a run with none to say it: a wait, a rest, or one suspended behind a detour.
    public void NoteUserStop() => NoteStop("stopped by the user");

    // The same stop with its own reason: a change of game data stops everything the
    // way the user's Stop does (MovementController.StopForGameDataChange).
    public void NoteStop(string why)
    {
        // Not a Stop this manager's own engine start set off.
        if (!_driving) EndByUser(why);
    }

    // The event running now, by name, or null: for the notice of what a change of
    // game data ended.
    public string? RunningEventName => _run is { } run ? Label(run.Event) : null;

    // The connection dropped (EventScheduler.NotifyDisconnected, ahead of the
    // engines' own handling of the drop). Everything is kept; see the header.
    public void NoteDisconnected()
    {
        if (_offline) return;
        _offline = true;
        if (_run is { } run)
            _log?.Info("Events",
                $"Connection dropped with event '{Label(run.Event)}' at {run.Step} and {_queue.Count} waiting: kept for the way back in.");
        else if (_queue.Count > 0)
            _log?.Info("Events", $"Connection dropped with {_queue.Count} event(s) waiting: kept for the way back in.");
    }

    // The first prompt back in the game (EventScheduler, ahead of the Logon events,
    // which then take their place behind what was already waiting). What ended
    // during the outage is finished now, an event past its wait limit is dropped,
    // and the next one starts if nothing is running.
    public void NoteEnteredGame()
    {
        if (!_offline) return;
        _offline = false;
        if (_run is { } run) run.IdleSince = null;
        if (_run is not null || _queue.Count > 0)
            _log?.Info("Events", $"Back in the game: running {RunSummary}; waiting {QueueSummary}.");
        Action[] ended = _whenBackInGame.ToArray();
        _whenBackInGame.Clear();
        foreach (Action act in ended) FinishUnlessHeld(act);
        DropExpired();
        StartNextQueued();
    }

    private void RunThen(EventRun run)
    {
        ScheduledEvent e = run.Event;
        string label = Label(e);
        switch (e.ResolvedThen)
        {
            case EventThenType.Resume:
                if (HandThenToQueued(run)) return;
                if (run.Resume is { } plan)
                    NoteThenStarted(label, "going back to", plan.Describe(), WatchFor(plan), ExecuteResume(plan),
                        (plan as EventResumePlan.Walker)?.Destination);
                else
                    _log?.Info("Events", $"Event '{label}': nothing was running to go back to.");
                return;
            case EventThenType.Loop:
                if (HandThenToQueued(run)) return;
                NoteThenStarted(label, "starting", $"loop '{e.ThenLoopName}'", ThenWatchKind.Loop,
                    StartLoop(e.ThenLoopName, "event then") ? EngineStart.Started : EngineStart.Refused);
                return;
            case EventThenType.AutoLair:
                if (HandThenToQueued(run)) return;
                NoteThenStarted(label, "starting", $"auto-lair '{e.ThenAutoLairSetupName}'", ThenWatchKind.AutoLair,
                    StartAutoLair(e.ThenAutoLairSetupName, "event then") ? EngineStart.Started : EngineStart.Refused);
                return;
            case EventThenType.WalkTo:
                if (StartThenWalk(run)) return;
                break;
            case EventThenType.Event:
                ScheduledEvent? next = FindEvent(e.ThenEventName);
                if (next is null)
                    _log?.Warn("Events", $"Event '{label}': Then event '{e.ThenEventName}' doesn't exist.");
                else if (next.Disabled || _profile.Current?.EventsGloballyDisabled == true)
                    _log?.Info("Events", $"Event '{label}': Then event '{Label(next)}' is disabled.");
                else if (run.Depth + 1 > MaxChainDepth)
                    _log?.Warn("Events",
                        $"Event '{label}': not firing '{Label(next)}' — {MaxChainDepth} events in a row, the chain looks like a cycle.");
                else
                {
                    // Run as the chain, it has had its turn.
                    int waiting = _queue.FindIndex(q => ReferenceEquals(q.Event, next));
                    if (waiting >= 0 && _choiceSince is not null)
                    {
                        // The user is being asked whether that event still runs.
                        // Taken out of the queue here it would start before the
                        // answer, and a "drop" could no longer reach it.
                        _log?.Info("Events",
                            $"Event '{label}': its Then event '{Label(next)}' is waiting for the user's choice over the held events, so it is left to that choice and not started now.");
                        break;
                    }
                    if (waiting >= 0)
                    {
                        RemoveQueuedAt(waiting);
                        _log?.Info("Events", $"Event '{Label(next)}' was also waiting: it runs now, as '{label}' fires it, and leaves the queue.");
                    }
                    StartRun(next, run.Resume, run.Depth + 1, run.Origin);
                    return;
                }
                break;
        }
        // Nothing follows this event (or what should have couldn't): the queue moves.
        StartNextQueued();
    }

    // The run's Then would leave an engine running, and an event is waiting: the
    // waiting event starts now with that engine as what it goes back to. Starting
    // the engine first would put a loop's opening move on the wire for the waiting
    // event's walk to trip over, and its "go back" would restart the same thing.
    private bool HandThenToQueued(EventRun run)
    {
        if (TakeNextQueued() is not { } next) return false;
        EventResumePlan? plan = ThenPlan(run);
        _log?.Info("Events",
            $"Event '{Label(run.Event)}' finished; '{Label(next)}' was waiting and starts now"
            + (plan is null ? "." : $", with {plan.Describe()} as what it goes back to."));
        StartRun(next, plan, depth: 0);
        return true;
    }

    // What the run's Then leaves running, as a Resume target for the event after it.
    private EventResumePlan? ThenPlan(EventRun run)
    {
        ScheduledEvent e = run.Event;
        switch (e.ResolvedThen)
        {
            case EventThenType.Resume:
                return run.Resume;
            case EventThenType.Loop:
                if (FindLoop(e.ThenLoopName) is { } saved) return new EventResumePlan.Loop(saved);
                _log?.Warn("Events", $"Event '{Label(e)}': Then loop '{e.ThenLoopName}' doesn't exist.");
                return null;
            case EventThenType.AutoLair:
                if (FindSetup(e.ThenAutoLairSetupName) is not { } setup)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}': Then auto-lair '{e.ThenAutoLairSetupName}' doesn't exist.");
                    return null;
                }
                return PlanFor(setup);
        }
        return null;
    }

    // A Then walk-to is the last leg of the event, so the run stays up (and holds
    // the queue) until it arrives, fails or is stopped. False when there is no walk
    // to wait for: no target, it wouldn't start, or the character is already there.
    private bool StartThenWalk(EventRun run)
    {
        ScheduledEvent e = run.Event;
        if (e.ThenWalkTo is not { } to)
        {
            _log?.Warn("Events", $"Event '{Label(e)}': Then walk-to has no room.");
            return false;
        }
        RoomKey key = new(to.Map, to.Room);
        if (!StartWalk(key, "event then", e.ThenWalkToEntersBossRoom == true))
        {
            ThenFailed(Label(e), $"the walk to {key.Map}/{key.Room}", _drivenFailure ?? "it wouldn't start");
            return false;
        }
        if (_walker.State == WalkState.Idle) return false;
        run.OnThenWalk = true;
        run.WalkTarget = key;
        _run = run;
        StartTicker();
        return true;
    }

    private void EndThenWalk(EventRun run, string? failure)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        string what = $"the walk to {run.WalkTarget?.Map}/{run.WalkTarget?.Room}";
        if (failure is null)
        {
            _log?.Info("Events", $"Event '{Label(run.Event)}' finished: {what} arrived.");
            LastThenSummary = $"'{Label(run.Event)}' → {what}: arrived {Stamp()}";
        }
        else ThenFailed(Label(run.Event), what, failure);
        StartNextQueued();
    }

    // ----- What a Then came to -----------------------------------------

    // An engine a Then started is on its own once it starts: the run is over. It is
    // still watched until it has plainly got going or given up, because the walk
    // back to a loop is an automatic walk and can be refused a second later (a
    // teleport on the only way back that Settings → Teleports doesn't allow), which
    // left the character standing in the event's room with nothing said in the
    // terminal (report paradigm-20261009-220128).
    //
    // Only a loop still walking to its start, or a walk still walking, is watched.
    // A loop started while standing on it has arrived already, and a watch left
    // armed blamed the event for the loop failing of its own accord laps later.
    // Auto-Lair reports no arrival at all and is not watched once it has started.
    private enum ThenWatchKind { Loop, Walk, AutoLair }
    private sealed record ThenWatch(string EventLabel, string What, ThenWatchKind Kind, RoomKey? WalkDestination);
    private ThenWatch? _thenWatch;

    private static ThenWatchKind WatchFor(EventResumePlan plan) => plan switch
    {
        EventResumePlan.Loop => ThenWatchKind.Loop,
        EventResumePlan.AutoLair => ThenWatchKind.AutoLair,
        _ => ThenWatchKind.Walk,
    };

    private void NoteThenStarted(
        string label, string verb, string what, ThenWatchKind kind, EngineStart outcome,
        RoomKey? walkDestination = null, string note = "")
    {
        if (outcome == EngineStart.Refused)
        {
            ThenFailed(label, what, _drivenFailure ?? "it wouldn't start");
            return;
        }
        if (outcome == EngineStart.StillRunning)
        {
            _log?.Info("Events", $"Event '{label}' finished; {what} never stopped.");
            LastThenSummary = $"'{label}' → {what}: still running {Stamp()}";
            return;
        }
        string step = kind == ThenWatchKind.Loop && _loopRunner is { State: not LoopState.Idle } runner
            ? $", step {runner.CurrentIndex + 1} of {runner.StepCount}"
              + (runner.State == LoopState.Approaching ? " once it has walked there" : "")
            : "";
        _log?.Info("Events", $"Event '{label}' finished; {verb} {what}{step}{note}.");
        bool stillArriving = kind switch
        {
            ThenWatchKind.Loop => _loopRunner.State == LoopState.Approaching,
            ThenWatchKind.Walk => _walker.State == WalkState.Walking,
            _ => false,
        };
        LastThenSummary = $"'{label}' → {what}: {(stillArriving ? "started" : "under way")} {Stamp()}";
        _thenWatch = stillArriving ? new ThenWatch(label, what, kind, walkDestination) : null;
    }

    private void ThenFailed(string label, string what, string why)
    {
        _thenWatch = null;
        _log?.Warn("Events", $"Event '{label}' finished, but {what} didn't get going: {why}");
        LastThenSummary = $"'{label}' → {what}: failed {Stamp()} — {why}";
        _notice?.Invoke($"[Event '{label}' finished, but {what} didn't get going: {why}]");
    }

    private void ThenArrived(ThenWatch watch)
    {
        _thenWatch = null;
        _log?.Info("Events", $"Event '{watch.EventLabel}': {watch.What} is under way.");
        LastThenSummary = $"'{watch.EventLabel}' → {watch.What}: under way {Stamp()}";
    }

    private string Stamp() => _now().ToLocalTime().ToString("HH:mm:ss");

    // ----- Engine starts ----------------------------------------------

    // enterBossRoom: the event's own choice for a room marked "stop before
    // entering" on the Bosses tab. False leaves the walker's usual stop one room
    // short in place.
    private bool StartWalk(RoomKey key, string reason, bool enterBossRoom)
    {
        _drivenFailure = null;
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Walker, reason);
            if (enterBossRoom) _walker.SetBossRoomRule(key, walkAround: null, haltBefore: null, enterDestination: true);
            if (_walker.WalkTo(key)) return true;
        }
        finally { _driving = false; }
        _log?.Warn("Events", $"Walk to {key.Map}/{key.Room} failed to start"
            + (_drivenFailure is { } why ? $": {why}" : "."));
        return false;
    }

    private bool StartLoop(string? name, string reason)
    {
        _drivenFailure = null;
        if (FindLoop(name) is not { } saved)
        {
            _drivenFailure = $"no saved loop is named '{name}'";
            return false;
        }
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Loop, reason);
            return StartedLoop(_loopRunner, saved);
        }
        finally { _driving = false; }
    }

    // The runner says true once it has asked the walker for the walk to the loop,
    // whatever the walker answered: a walk refused on the spot has already ended
    // the loop by the time Start returns.
    private static bool StartedLoop(LoopRunner runner, Loop saved) =>
        runner.Start(saved) && runner.State != LoopState.Idle;

    private bool StartAutoLair(string? name, string reason)
    {
        _drivenFailure = null;
        if (FindSetup(name) is not { } setup)
        {
            _drivenFailure = $"no saved auto-lair setup is named '{name}'";
            return false;
        }
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Lair, reason);
            _autoLair.Clear();
            foreach (LairMarker m in setup.Markers)
                _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
            return _autoLair.Start();
        }
        finally { _driving = false; }
    }

    private void StopEngines(string reason)
    {
        _driving = true;
        try { EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.None, reason); }
        finally { _driving = false; }
    }

    private void ExecuteCommand(ScheduledEvent e)
    {
        // Empty CommandText is a valid Command action — fires a bare
        // CR. Useful for MOTD pagination + similar single-Enter
        // prompts. WireSender.Send("") encodes to just "\r".
        if (string.IsNullOrEmpty(e.CommandText))
        {
            _wire.Send(string.Empty);
            return;
        }
        foreach (string chunk in SplitCommand(e.CommandText))
        {
            if (chunk.Length == 0) continue;
            _wire.Send(chunk);
        }
    }

    // ----- Completion signals ------------------------------------------

    internal void OnWalkEvent(WalkEvent w)
    {
        if (_driving)
        {
            if (w.Kind == WalkEventKind.Failed) _drivenFailure = w.Detail;
            return;
        }
        if (_thenWatch is { Kind: ThenWatchKind.Walk } watch)
        {
            if (w.Kind == WalkEventKind.Failed) ThenFailed(watch.EventLabel, watch.What, w.Detail);
            else if (w.Kind == WalkEventKind.Stopped) _thenWatch = null;
            else if (w.Kind == WalkEventKind.Finished
                     && (Equals(w.Destination, watch.WalkDestination) || Equals(w.Requested, watch.WalkDestination)))
                ThenArrived(watch);
        }
        if (_run is not { } run) return;
        // A boss room marked stop-before ends the walk one room short, and the
        // walker says so (Requested). Matching the boss room alone left the event
        // running for good, its Then never reached.
        bool arrived = w.Kind == WalkEventKind.Finished
                       && (Equals(w.Destination, run.WalkTarget) || Equals(w.Requested, run.WalkTarget));
        if (run.OnThenWalk || run.Action == EventActionType.WalkTo)
        {
            if (run.Suspended)
            {
                // A detour walks too (to its shop, away from a player), and its walk
                // is stopped when the run's own is taken up again. Only the walk to
                // this run's room starting is the run's; what the detour's walks
                // come to is the detour's business, and the user's Stop arrives by
                // NoteUserStop.
                if (w.Kind == WalkEventKind.Started && Equals(_walker.Journey?.Destination, run.WalkTarget)) Resumed(run);
            }
            else if (w.Kind == WalkEventKind.Stopped)
            {
                if (w.WillResume) Suspend(run, $"its walk was stopped by something that takes it up again ({w.Detail})");
                else Abort(run, "its walk was stopped");
            }
            else if (run.OnThenWalk)
            {
                if (arrived) Later(() => EndThenWalk(run, failure: null));
                else if (w.Kind == WalkEventKind.Failed) Later(() => EndThenWalk(run, w.Detail));
            }
            else if (arrived) Later(() => Complete(run, finished: true));
            else if (w.Kind == WalkEventKind.Failed) Later(() => Complete(run, finished: false));
            return;
        }
        switch (run.Action)
        {
            case EventActionType.Wait:
            case EventActionType.RestUp:
                if (w.Kind == WalkEventKind.Started) Abort(run, "a walk started");
                break;
        }
    }

    internal void OnLoopEvent(LoopEvent l)
    {
        if (_driving)
        {
            if (l.Kind == LoopEventKind.Failed) _drivenFailure = l.Detail;
            return;
        }
        if (_thenWatch is { Kind: ThenWatchKind.Loop } watch)
        {
            if (l.Kind == LoopEventKind.Failed) ThenFailed(watch.EventLabel, watch.What, l.Detail);
            else if (l.Kind == LoopEventKind.Stopped) _thenWatch = null;
            else if (l.Kind == LoopEventKind.ReachedFirstWaypoint) ThenArrived(watch);
        }
        if (_run is not { } run) return;
        switch (run.Action)
        {
            case EventActionType.Loop:
                if (l.Kind == LoopEventKind.RepeatStarted)
                {
                    run.Laps++;
                    if (run.Event.StopAfterLaps is { } laps && laps > 0 && run.Laps >= laps)
                        StopAndComplete(run, $"{run.Laps} lap(s)");
                }
                else if (l.Kind == LoopEventKind.Stopped)
                {
                    if (l.WillResume) Suspend(run, $"its loop was stopped by something that restarts it ({l.Detail})");
                    else Abort(run, "its loop was stopped");
                }
                else if (l.Kind == LoopEventKind.Started && run.Suspended)
                {
                    if (string.Equals(_loopRunner.CurrentLoop?.Name, run.Event.LoopName, StringComparison.OrdinalIgnoreCase))
                        Resumed(run);
                    else
                        Abort(run, "another loop was started while its own was away");
                }
                else if (l.Kind == LoopEventKind.Failed) Later(() => Complete(run, finished: false));
                break;
            case EventActionType.Wait:
            case EventActionType.RestUp:
                if (l.Kind == LoopEventKind.Started) Abort(run, "a loop started");
                break;
        }
    }

    private void OnAutoLairActiveChanged(bool active)
    {
        if (_driving || _run is not { } run) return;
        if (run.Action == EventActionType.AutoLair)
        {
            if (active) Resumed(run);
            else if (_autoLair.StoppedForResume) Suspend(run, "its auto-lair was stopped by something that brings it back");
            else Abort(run, "its auto-lair was stopped");
        }
        else if (run.Action is EventActionType.Wait or EventActionType.RestUp && active)
            Abort(run, "an auto-lair started");
    }

    // A tracked boss died (BossTimerStore.BossKilled).
    public void NoteBossKilled(string bossName)
    {
        if (_run is { Action: EventActionType.Loop or EventActionType.AutoLair } run
            && run.Event.StopBossMoment == EventBossMoment.Killed
            && string.Equals(run.Event.StopBossName, bossName, StringComparison.OrdinalIgnoreCase))
            StopAndComplete(run, $"{bossName} was killed");
    }

    // The three errand signals below are ignored while this manager is stopping or
    // starting engines: a Logoff bank trip that jumps a running bank-trip event
    // stops the engines for its own start, the old trip reports Stopped from inside
    // that, and the run by then is the new one — which aborted itself.

    // A Roomba sweep ended (GhSweepManager.SweepCompleted).
    // finished: false for one that ended before it was done, which didn't get its
    // job done.
    public void NoteRoombaFinished(bool finished)
    {
        if (_driving) return;
        if (_run is { Action: EventActionType.Roomba } run) Later(() => Complete(run, finished));
    }

    // A bank / stash trip ended (AutoDepositManager.EventTripEnded).
    public void NoteBankTripEnded(Game.Cash.AutoDepositManager.EventTripOutcome outcome)
    {
        if (_driving || _run is not { Action: EventActionType.BankTrip } run) return;
        switch (outcome)
        {
            case Game.Cash.AutoDepositManager.EventTripOutcome.Done: Later(() => Complete(run, finished: true)); break;
            case Game.Cash.AutoDepositManager.EventTripOutcome.Failed: Later(() => Complete(run, finished: false)); break;
            default: Abort(run, "its trip was stopped"); break;
        }
    }

    // A stash → bank transfer ended (StashTransferRunner.Ended). One started from the
    // map menu ends here too; it only counts while a transfer event is running.
    public void NoteStashTransferEnded(Game.Cash.StashTransferOutcome outcome)
    {
        if (_driving || _run is not { Action: EventActionType.StashTransfer } run) return;
        switch (outcome)
        {
            case Game.Cash.StashTransferOutcome.Done: Later(() => Complete(run, finished: true)); break;
            case Game.Cash.StashTransferOutcome.Failed: Later(() => Complete(run, finished: false)); break;
            default: Abort(run, "its transfer was stopped"); break;
        }
    }

    // ----- Suspended runs ------------------------------------------------

    private void Suspend(EventRun run, string why)
    {
        if (run.Suspended) return;
        run.Suspended = true;
        run.IdleSince = null;
        _log?.Info("Events",
            $"Event '{Label(run.Event)}' suspended at {run.Step} — {why}; it goes on when that is back"
            + (run.Laps > 0 ? $" ({run.Laps} lap(s) so far)." : "."));
    }

    // The engine a suspended run was waiting for has started again.
    private void Resumed(EventRun run)
    {
        if (!run.Suspended) return;
        run.Suspended = false;
        run.IdleSince = null;
        _log?.Info("Events", $"Event '{Label(run.Event)}' goes on: its {run.Step} is back.");
        if (run.StopWhenBack is not { } why) return;
        run.StopWhenBack = null;
        // Posted: this is the engine's own Started raise.
        Later(() => { if (ReferenceEquals(_run, run)) StopAndComplete(run, why); });
    }

    // A stop-after rule ended the loop / auto-lair: stop it ourselves, then Then.
    // Under a suspended run the engine is in a detour's hands, and a Then started
    // now would run into the detour and be run over by its resume: the rule is
    // applied when the engine is back.
    private void StopAndComplete(EventRun run, string why)
    {
        if (run.Suspended)
        {
            run.StopWhenBack ??= why;
            return;
        }
        _log?.Info("Events", $"Event '{Label(run.Event)}': stopping its {run.Event.ActionType} — {why}.");
        _driving = true;
        try
        {
            if (run.Event.ActionType == EventActionType.Loop) _loopRunner.Stop("event stop-after");
            else _autoLair.Stop("event stop-after");
        }
        finally { _driving = false; }
        // Posted: a lap rule is met inside the loop runner's own raise.
        Later(() => Complete(run, finished: true));
    }

    // The once-a-second check for what no engine event reports: a wait's time, a
    // rest-up ending, the minutes / conditions stop-after rules, and a queued event
    // that has waited too long.
    internal void Tick()
    {
        if (_offline || _run is not { } run) return;
        // An event already running when the master switch goes off is held, as
        // its walk or loop is: nothing here ends its action, so its Then (which
        // starts an engine) waits for the switch to come back on, and a suspended
        // run is not given up for standing idle meanwhile.
        if (HoldsForMasterSwitch()) return;
        DropExpired();
        if (run.Suspended && SuspendedTooLong(run)) return;
        ScheduledEvent e = run.Event;
        TimeSpan elapsed = _now() - run.StartedAt;
        switch (run.Action)
        {
            case EventActionType.Wait:
                if (elapsed.TotalSeconds >= Math.Max(0, e.WaitSeconds ?? 0)) Complete(run, finished: true);
                break;
            case EventActionType.RestUp:
                if (elapsed >= RestStartGrace && _isResting?.Invoke() != true) Complete(run, finished: true);
                break;
            case EventActionType.Loop:
            case EventActionType.AutoLair:
                if (e.StopAfterMinutes is { } minutes && minutes > 0 && elapsed.TotalMinutes >= minutes)
                    StopAndComplete(run, $"{minutes} minute(s) up");
                else if (e.StopConditions is { Count: > 0 } conditions && _readStats is { } read
                         && EventConditionEvaluator.AllHold(conditions, read()))
                    StopAndComplete(run, EventConditionEvaluator.Describe(conditions));
                else if (_bossStopReached?.Invoke(e) == true)
                    StopAndComplete(run, $"{e.StopBossName}'s {e.StopBossMoment} moment came");
                break;
        }
    }

    // A detour that never brought the engine back (it was cancelled, or gave up)
    // would leave the run suspended for good, holding the queue. With every engine
    // idle for SuspendedIdleLimit nothing is coming, and something called the detour
    // off (a plain stop, the user's own walk): the run ends as a user Stop ends it,
    // with no Then and the waiting events dropped, so nothing sets off by itself
    // long after.
    private bool SuspendedTooLong(EventRun run)
    {
        bool idle = _walker is not { State: not WalkState.Idle }
                    && _loopRunner is not { State: not LoopState.Idle }
                    && _autoLair is not { IsActive: true };
        if (!idle)
        {
            run.IdleSince = null;
            return false;
        }
        run.IdleSince ??= _now();
        if (_now() - run.IdleSince < SuspendedIdleLimit) return false;
        int dropped = _queue.Count;
        string minutes = SuspendedIdleLimit.TotalMinutes.ToString("0");
        _log?.Warn("Events",
            $"Event '{Label(run.Event)}': what stopped its {run.Step} never brought it back ({minutes} minute(s) with nothing moving); given up.");
        EndByUser("what paused it never brought it back");
        _notice?.Invoke(
            $"[Event '{Label(run.Event)}' given up: what paused its {run.Step} never brought it back in {minutes} minute(s)"
            + (dropped > 0 ? $"; its Then was skipped and the {dropped} waiting event(s) were dropped]" : "; its Then was skipped]"));
        return true;
    }

    private void StartTicker()
    {
        if (_ticker is not null) return;
        // No UI dispatcher: nothing to pump, callers tick by hand.
        if (Avalonia.Application.Current is null) return;
        _ticker = new Avalonia.Threading.DispatcherTimer { Interval = RunTickInterval };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private void StopTicker()
    {
        _ticker?.Stop();
        _ticker = null;
    }

    // A Command action's text split into the lines it sends, by the same rule macros,
    // triggers and aliases use.
    internal static IEnumerable<string> SplitCommand(string text) =>
        MacroStore.SplitCommandSteps(text);

    // ----- Reconciliation -------------------------------------------

    // Walk every Loop / AutoLair-action event and auto-disable any whose
    // referenced saved name is no longer in the manager's collection. Idempotent —
    // already-disabled events are left alone. Re-runs on profile load + every
    // LoopsChanged / SetupsChanged event.
    private void ReconcileTargets()
    {
        bool anyChanged = false;
        foreach (ScheduledEvent e in Events)
        {
            if (e.Disabled || MissingTarget(e) is not { } missing) continue;
            AutoDisableInternal(e, missing);
            anyChanged = true;
        }
        if (anyChanged)
        {
            _profile.Save();
            AutoDisabledChanged?.Invoke();
        }
    }

    // Fire-time auto-disable. Differs from AutoDisableInternal in that it persists
    // + notifies immediately (since the caller isn't part of a bulk
    // reconciliation pass).
    private void AutoDisable(ScheduledEvent e, string reason)
    {
        AutoDisableInternal(e, reason);
        _profile.Save();
        AutoDisabledChanged?.Invoke();
    }

    private void AutoDisableInternal(ScheduledEvent e, string reason)
    {
        e.Disabled = true;
        _autoDisabled.Add(e);
        _log?.Warn("Events", $"Event '{Label(e)}' auto-disabled — {reason}.");
    }

    // ----- Lookups ---------------------------------------------------

    private Loop? FindLoop(string? name) =>
        string.IsNullOrEmpty(name) || _loops is null
            ? null
            : _loops.Loops.FirstOrDefault(l =>
                string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private LairSetup? FindSetup(string? name) =>
        string.IsNullOrEmpty(name) || _lairs is null
            ? null
            : _lairs.Setups.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private ScheduledEvent? FindEvent(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Events.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Label(ScheduledEvent e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name;

    // ----- Lifecycle -------------------------------------------------

    private void LoadFrom(CharacterProfile p)
    {
        EndRun();
        _queue.Clear();
        EndQueueChoice();
        _toldFull.Clear();
        _whenBackInGame.Clear();
        _whenSwitchBackOn.Clear();
        _heldSince = null;
        _thenWatch = null;
        LastThenSummary = "(none)";
        Events.Clear();
        _autoDisabled.Clear();
        _lastSwitch.Clear();
        bool converted = p.Events is not null && ConvertToThen(p.Events);
        if (p.Events is not null)
            foreach (ScheduledEvent e in p.Events) Events.Add(e);
        if (converted)
        {
            _log?.Info("Events", "Converted this character's events to the Then format (a walk-to goes back, anything else stops).");
            _profile.Save();
        }
        ReconcileTargets();
    }

    // Events saved before they had a Then get the behavior they always had written
    // in (a walk-to went back, anything else stopped), so the editor shows it and
    // the profile saves in the current shape. True when any changed.
    internal static bool ConvertToThen(IEnumerable<ScheduledEvent> events)
    {
        bool converted = false;
        foreach (ScheduledEvent e in events)
        {
            if (e.Then is not null) continue;
            e.Then = e.ResolvedThen;
            converted = true;
        }
        return converted;
    }

    private void Clear()
    {
        EndRun();
        _queue.Clear();
        EndQueueChoice();
        _toldFull.Clear();
        _whenBackInGame.Clear();
        _whenSwitchBackOn.Clear();
        _heldSince = null;
        _thenWatch = null;
        LastThenSummary = "(none)";
        Events.Clear();
        _autoDisabled.Clear();
        _lastSwitch.Clear();
    }

    private void SnapshotForSave(CharacterProfile p)
    {
        p.Events = Events.Count == 0 ? null : Events.ToList();
    }

    // ----- Resume ------------------------------------------------------

    // Snapshot the engine that was actively driving movement before an event
    // takes over. Precedence — AutoLair beats LoopRunner beats the one-shot
    // walker, because the higher engines drive the lower ones (a Loop's approach
    // walk is "really" the loop). Returns null when nothing was running, in which
    // case a Then of Resume has nothing to go back to.
    internal EventResumePlan? SnapshotCurrentActivity()
    {
        if (_autoLair is { IsActive: true } al && al.Marked.Count > 0)
        {
            Dictionary<RoomKey, int?> snap = new();
            foreach (RoomKey k in al.Marked)
                snap[k] = al.Overrides.TryGetValue(k, out int? v) ? v : null;
            return new EventResumePlan.AutoLair(snap, OffARun: true, MarkedOn: al.MarksGameData);
        }
        if (_loopRunner is not null
            && _loopRunner.State is not LoopState.Idle
            && _loopRunner.CurrentLoop is { } loop)
        {
            return new EventResumePlan.Loop(loop);
        }
        // The journey, not the room the walker is heading for: caught on a side trip
        // (to a giver, a shop) that room is the side trip's, and a walk resumed from
        // a bare room has lost the route it was on.
        if (_walker is { State: WalkState.Walking, Journey: { } journey })
        {
            return new EventResumePlan.Walker(journey);
        }
        return null;
    }

    // How starting a Then's engine went. StillRunning: what it was to go back to
    // never stopped (a command or chained event left it alone).
    internal enum EngineStart { Started, StillRunning, Refused }

    // Go back to what was running before the event took over — unless it's still
    // running. The walk back to a loop or between lairs is the client's own, so it
    // keeps to Settings → Teleports like any automatic walk; a refusal there comes
    // back as Refused, or a moment later through the engine's Failed event.
    internal EngineStart ExecuteResume(EventResumePlan plan)
    {
        _drivenFailure = null;
        _driving = true;
        try
        {
            switch (plan)
            {
                case EventResumePlan.Loop l:
                    // By name: the catalogue's copy of a loop the run holds is a new object
                    // once another client has edited the file.
                    if (_loopRunner.State is not LoopState.Idle
                        && string.Equals(_loopRunner.CurrentLoop?.Name, l.SavedLoop.Name, StringComparison.OrdinalIgnoreCase))
                        return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Loop, "event resume");
                    return StartedLoop(_loopRunner, l.SavedLoop) ? EngineStart.Started : EngineStart.Refused;
                case EventResumePlan.AutoLair al:
                    if (_autoLair.IsActive) return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Lair, "event resume");
                    if (al.OffARun)
                    {
                        // Marked on another game data than the active one: not started.
                        if (!_autoLair.RestoreMarks(al.Markers, al.MarkedOn)) return EngineStart.Refused;
                    }
                    else
                    {
                        _autoLair.Clear();
                        foreach (KeyValuePair<RoomKey, int?> kv in al.Markers)
                            _autoLair.Mark(kv.Key, kv.Value);
                    }
                    return _autoLair.Start() ? EngineStart.Started : EngineStart.Refused;
                case EventResumePlan.Walker w:
                    if (_walker.State == WalkState.Walking && Equals(_walker.Journey?.Destination, w.Destination))
                        return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Walker, "event resume");
                    return _walker.ResumeJourney(w.Journey) ? EngineStart.Started : EngineStart.Refused;
            }
            return EngineStart.Refused;
        }
        finally { _driving = false; }
    }

    // What was running when an event took over — the Resume target. Discriminated
    // across the three engine types SnapshotCurrentActivity distinguishes.
    internal abstract record EventResumePlan
    {
        public abstract string Describe();

        public sealed record Loop(Game.Map.Loop SavedLoop) : EventResumePlan
        {
            public override string Describe() => $"loop '{SavedLoop.Name}'";
        }
        // OffARun: the markers are what a run had when the event took over from it,
        // with the game data they were marked on (MarkedOn). They go back only while
        // that set is the active one. Markers read from a saved setup are the active
        // set's.
        public sealed record AutoLair(Dictionary<RoomKey, int?> Markers, bool OffARun = false, string? MarkedOn = null) : EventResumePlan
        {
            public override string Describe() => $"auto-lair ({Markers.Count} markers)";
        }
        public sealed record Walker(WalkJourney Journey) : EventResumePlan
        {
            public RoomKey Destination => Journey.Destination;
            public override string Describe() => $"walk to {Destination.Map}/{Destination.Room}";
        }
    }
}
