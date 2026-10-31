using MudPlay.Services;

namespace MudPlay.Game.Map;

// A run started on its own at a game entry: a loop a dropped link cut off, restarted
// after the reconnect, or the character's Default task. Each owner sets its run
// aside (SetAside, NoteDisconnected) and hands this the prompts it reads (OnPrompt).
//
// The prompt is read off the wire ahead of the lines that came with it, the room
// display among them, so the start is posted past them: planned at the prompt, the
// first step would go out from the room known before the drop, wherever the
// reconnect put us. It starts once the tracker is sure of the room. Unsure, it
// doesn't wait for good (user, 2026-10-17): once the tracker can't say where we are
// (unsure after a display was read, or still waiting on one at the next prompt) the
// client's own one-shot locate is asked (EngineRecoveryGate.TryResyncOnce: `rm`,
// else `sys st`), and the run starts from the room it names. With nothing to ask,
// no answer, or no room known within RoomWaitMax, the run starts from the room the
// tracker believes, as it did before it waited, and the recovery gate it attaches
// to puts it right; only with no room believed at all is it not started (or, with
// WaitForAnyRoom, kept until the map has one). Either way the terminal says so.
//
// With the master switch off it waits for the switch (ResumeAfterMasterSwitch):
// started then, the engine would raise its start events with the user's autos off;
// nor is the game asked where we are while it is off.
// A death found out at the login drops it: it would walk a stripped character out
// of the temple.
//
// A leader's party reform fires off the first room display. The start runs after
// the lines that came with its prompt, so a reform whose display was among them has
// already put its own holds up. One still waiting for its room (a room too dark to
// display) would see the run walk the leader out on the followers (report
// paradigm-20260923-092317), so the run is started behind a hold of its own, dropped
// once the reform has had its look. Started and held, rather than left waiting to
// start, the run is a paused one like any other: Stop stops it, and a member's
// @comeback finds an engine to resume.
public sealed class ReconnectRestart<TRun> where TRun : class
{
    private readonly string _owner;
    private readonly string _occasion;
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator? _coordinator;
    private readonly EngineRecoveryGate? _recovery;
    private readonly LogService? _log;
    private readonly Action<Action> _postToUi;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<TRun, string> _describe;
    private readonly Func<string?> _runningNow;
    private readonly Func<TRun, bool> _start;
    private IDisposable? _reformHoldTimer;
    private IDisposable? _roomWaitTimer;
    private Func<bool>? _reformAwaitsRoom;
    private int _unsureChecks;
    private bool _locateTried;
    private int _waitId;
    private TRun? _failed;
    private DateTimeOffset _failedAfterSending;

    // owner: the engine, as its log lines and gate holds name it. occasion: what the
    // start follows, as a notice reads it ("reconnect", "login"). describe: the run
    // as a log line names it ("loop 'Ring'"). runningNow: the run the engine has
    // going, as describe names it, or null when it is idle. start: starts the run;
    // false when it didn't. schedule runs the time limits (DispatcherTicks.Once live).
    // recovery: the gate whose one-shot locate is asked for a room the tracker can't
    // place.
    public ReconnectRestart(string owner, string occasion, RoomTracker tracker, MovementCoordinator? coordinator,
        EngineRecoveryGate? recovery, LogService? log,
        Action<Action> postToUi, Func<TimeSpan, Action, IDisposable> schedule,
        Func<TRun, string> describe, Func<string?> runningNow, Func<TRun, bool> start)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(runningNow);
        ArgumentNullException.ThrowIfNull(start);
        _owner = owner;
        _occasion = occasion;
        _tracker = tracker;
        _coordinator = coordinator;
        _recovery = recovery;
        _log = log;
        _postToUi = postToUi;
        _schedule = schedule;
        _describe = describe;
        _runningNow = runningNow;
        _start = start;
        _tracker.StateChanged += OnTrackerStateChanged;
        _tracker.PlayerDeathInferred += Clear;
    }

    // The owner is being disposed: the tracker outlives it.
    public void Detach()
    {
        _tracker.StateChanged -= OnTrackerStateChanged;
        _tracker.PlayerDeathInferred -= Clear;
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = null;
        ForgetRoomWait();
    }

    // The run waiting to start, or null.
    public TRun? Pending { get; private set; }

    // True while the master switch (Auto-All) is off. Nothing is started, and the game
    // is never asked where we are, while it is.
    public Func<bool>? MasterSwitchOff { get; set; }

    // With the switch off, keep the start for when it is back on (a restart) rather
    // than let it go (the Default task: by then the user is at the keys).
    public bool KeepForMasterSwitch { get; set; } = true;

    // With no room believed at all, wait for one with no time limit rather than let
    // the start go (the Default task at a login, as it always waited).
    public bool WaitForAnyRoom { get; set; }

    // A walk the client started by itself that is going, as a notice names it, or
    // null: the start waits for it to end. Unset: none looked at.
    public Func<string?>? WaitFor { get; set; }

    // The walk the start is waiting behind, or null.
    public string? WaitingFor { get; private set; }

    // A terminal notice, for a start made from a room the map isn't sure of, one not
    // made at all, one another run took the place of, and one waiting behind a walk.
    public Action<string>? Notice { get; set; }

    // The first game prompt came with the switch off: the start is owed, and
    // ResumeAfterMasterSwitch runs it.
    public bool HeldForMasterSwitch { get; private set; }

    // Back in the game, but the tracker isn't sure of the room yet.
    public bool AwaitsRoom { get; private set; }

    // No room believed at all, and the start waits for one (WaitForAnyRoom).
    public bool AwaitsAnyRoom { get; private set; }

    // The game was asked where we are; its answer starts the run.
    public bool Locating { get; private set; }

    // The started run is held until the party reform has seen the room.
    public bool HeldForReform { get; private set; }

    // Whether a leader's party reform is still waiting for the first room display
    // after a reconnect (PartyReformCoordinator.PendingReform). Unset: no reform.
    public void SetReformProbe(Func<bool> reformAwaitsRoom)
    {
        ArgumentNullException.ThrowIfNull(reformAwaitsRoom);
        _reformAwaitsRoom = reformAwaitsRoom;
    }

    // When the client last sent the game a command, and last read anything from the
    // server, on the connection now up (the one just lost, at a drop). Null, or
    // unset: no connection to judge by, and a failed run is never put down to it.
    public Func<DateTimeOffset?>? LastCommandSent { get; set; }
    public Func<DateTimeOffset?>? LastDataRead { get; set; }

    // The run ended on its own, failed rather than stopped. A link going dead under
    // it shows first as a move with no answer, and the run gives up on that long
    // before the connection is counted as lost, so at the drop there's nothing going
    // to set aside. Kept with when the last command went out, for NoteDisconnected
    // to tell that failure from the game's own refusals.
    public void NoteFailed(TRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (LastCommandSent?.Invoke() is not { } sent) return;
        _failed = run;
        _failedAfterSending = sent;
    }

    // A run to start on the next game prompt. For a restart, one an errand stopped (a
    // bank run's light buy) when the connection dropped, with the guards a run going
    // at the drop has.
    public void SetAside(TRun run, string why)
    {
        ArgumentNullException.ThrowIfNull(run);
        Pending = run;
        _log?.Info(_owner, $"{_occasion}: {_describe(run)} will start on the first in-game prompt ({why})");
    }

    // The link dropped. running: the run the engine had going, set aside here before
    // stop tears it down; null when nothing was running. A start held for the master
    // switch or the room goes back to waiting for the next stay's first prompt, which
    // asks both again.
    //
    // With nothing running, a run that failed on a link already dead is set aside
    // the same way (ConnectionLoss.LeftUnanswered). One the game failed stays failed.
    public void NoteDisconnected(TRun? running, Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        HeldForMasterSwitch = false;
        ForgetRoomWait();
        if (running is not null)
        {
            Pending = running;
            stop();
        }
        else if (_failed is { } failed)
        {
            DateTimeOffset? read = LastDataRead?.Invoke();
            if (read is { } last && ConnectionLoss.LeftUnanswered(last, _failedAfterSending))
            {
                Pending = failed;
                _log?.Info(_owner,
                    $"{_occasion}: {_describe(failed)} failed with the server already silent (nothing read after "
                    + $"{_failedAfterSending.ToLocalTime():HH:mm:ss.fff}); it will start on the first in-game prompt");
            }
            else
                _log?.Info(_owner, $"{_occasion}: {_describe(failed)} failed and is not restarted: "
                    + (read is null ? "the link was closed on purpose" : "the server answered after its last command"));
        }
        _failed = null;
        // A reform hold belongs to the connection that just ended. Dropped after the
        // stop, which drops it too: lifting it first would let the held run step.
        EndReformHold("disconnected");
    }

    // Reset States, a profile swap, a change of game data, and a death found out at
    // the login: nothing starts on the next prompt.
    public void Clear()
    {
        Pending = null;
        _failed = null;
        HeldForMasterSwitch = false;
        AwaitsAnyRoom = false;
        WaitingFor = null;
        ForgetRoomWait();
    }

    // A start already back in the game, kept waiting on the master switch or on the
    // room, is the user's to call off: a Stop, or another character loaded. One still
    // waiting for the first prompt is left as it always was.
    public void DropHeld(string why)
    {
        if (!HeldForMasterSwitch && !AwaitsRoom && !AwaitsAnyRoom && WaitingFor is null) return;
        _log?.Info(_owner, $"{_occasion}: {(Pending is { } p ? _describe(p) : "(nothing)")} will not be started ({why})");
        Clear();
    }

    // Another run started while the start is owed: a loop or an Auto-Lair run,
    // whoever started it, or a walk the user started (MovementController says which).
    // That run is the one going now; started after it, this one would take its place.
    public void SupersededBy(string by)
    {
        _failed = null;
        if (Pending is not { } run) return;
        _log?.Info(_owner, $"{_occasion}: {_describe(run)} will not be started: {by} was started");
        Notice?.Invoke($"[{Capitalised(_describe(run))} not restarted after the {_occasion}: {by} was started]");
        Clear();
    }

    // The master switch is back on, in the game: start the run, once the room is known.
    public void ResumeAfterMasterSwitch()
    {
        if (!HeldForMasterSwitch) return;
        HeldForMasterSwitch = false;
        TryStart(fromBelief: null);
    }

    // A game prompt was read. True when a start is owed: it is posted past the
    // prompt's lines, and the owner leaves the prompt at that.
    public bool OnPrompt()
    {
        if (Pending is not null)
        {
            _postToUi(() => TryStart(fromBelief: null));
            return true;
        }
        ReleaseHoldOnceReformSawRoom(linesStillToCome: true);
        return false;
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        // Posted, so the rest of the display (and the party reform reading it) is
        // handled before the run plans its first step.
        if (AwaitsRoom && t.NewConfidence == RoomConfidence.Confirmed) _postToUi(() => TryStart(fromBelief: null));
        else if (AwaitsAnyRoom && t.NewRoom is not null) _postToUi(() => TryStart(fromBelief: "no room was known when it was due"));
    }

    // fromBelief: why the run is started from the room the tracker believes, unsure
    // of it; null to start only from a room it is sure of.
    private void TryStart(string? fromBelief)
    {
        if (Pending is not { } run) return;
        // A run started meanwhile (the user's, while this one was still owed) is the
        // run now: started, the old one would supersede it.
        if (_runningNow() is { } running)
        {
            _log?.Info(_owner, $"{_occasion}: {_describe(run)} is not started; {running} was started meanwhile");
            Clear();
            return;
        }
        // A walk the client started by itself (an errand, a flee) goes on; the start
        // waits for it, and the next prompt asks again.
        if (WaitFor?.Invoke() is { } walk)
        {
            if (WaitingFor != walk)
            {
                _log?.Info(_owner, $"{_occasion}: {_describe(run)} waits for {walk} to end");
                Notice?.Invoke($"[{Capitalised(_describe(run))} restarts after {walk} ends]");
            }
            WaitingFor = walk;
            return;
        }
        WaitingFor = null;
        if (MasterSwitchOff?.Invoke() == true)
        {
            if (!KeepForMasterSwitch)
            {
                _log?.Info(_owner, $"{_occasion}: {_describe(run)} is not started: the master switch is off");
                Clear();
                return;
            }
            if (!HeldForMasterSwitch)
                _log?.Info(_owner,
                    $"{_occasion}: {_describe(run)} is not started with the master switch off; it starts when the switch is back on");
            HeldForMasterSwitch = true;
            ForgetRoomWait();
            return;
        }
        bool sure = _tracker.State.Confidence == RoomConfidence.Confirmed && _tracker.State.CurrentRoom is not null;
        if (!sure && fromBelief is null)
        {
            WaitForRoom(run);
            return;
        }
        if (_tracker.State.CurrentRoom is not { } room)
        {
            if (WaitForAnyRoom)
            {
                if (!AwaitsAnyRoom)
                    _log?.Info(_owner, $"{_occasion}: {_describe(run)} waits for the map to know a room ({fromBelief})");
                ForgetRoomWait();
                AwaitsAnyRoom = true;
                return;
            }
            _log?.Warn(_owner, $"{_occasion}: {_describe(run)} is not started: the map has no room to start it from ({fromBelief})");
            Notice?.Invoke($"[{Capitalised(_describe(run))} not started after the {_occasion}: the map has no room to start it from]");
            Clear();
            return;
        }
        Clear();
        if (sure)
            _log?.Info(_owner, $"{_occasion}: starting {_describe(run)} from {room.Key}");
        else
        {
            _log?.Warn(_owner,
                $"{_occasion}: starting {_describe(run)} from {room.Key}, the room the map believes ({_tracker.State.Confidence}): {fromBelief}");
            Notice?.Invoke($"[{Capitalised(_describe(run))} started from {room.Key} after the {_occasion}, though the map isn't sure "
                + $"of the room ({fromBelief}); it is put right on the way]");
        }
        BeginReformHoldIfReformAwaits();
        if (!_start(run)) EndReformHold("the run didn't start");
    }

    private void WaitForRoom(TRun run)
    {
        if (Locating) return;   // its answer starts the run
        if (!AwaitsRoom)
        {
            _log?.Info(_owner,
                $"{_occasion}: {_describe(run)} waits for the room to be known (tracker {_tracker.State.Confidence} at "
                + $"{_tracker.State.CurrentRoom?.Key.ToString() ?? "(none)"})");
            AwaitsRoom = true;
            int id = _waitId;
            _roomWaitTimer = _schedule(ReconnectRestartLimits.RoomWaitMax, () => OnRoomWaitElapsed(id));
        }
        // Pending is a display still to come (one the drop left owed); anything else
        // unsure is a display read that didn't place us.
        if (_tracker.State.Confidence != RoomConfidence.Pending || ++_unsureChecks > 1) Locate(run);
    }

    private void Locate(TRun run)
    {
        if (_locateTried)
        {
            FallBack("the game named a room the map doesn't have");
            return;
        }
        _locateTried = true;
        int id = _waitId;
        string reason = $"{_describe(run)} to start after the {_occasion}, tracker {_tracker.State.Confidence}";
        if (_recovery?.TryResyncOnce?.Invoke(reason, _ => OnLocateAnswered(id), () => OnLocateFailed(id)) == true)
        {
            Locating = true;
            _log?.Info(_owner, $"{_occasion}: {_describe(run)}: the room can't be told; asking the game where we are");
            return;
        }
        FallBack("nothing to ask the game where we are with");
    }

    // An answer to a wait that has since ended (a drop, a Stop, a switch-off) is
    // nobody's: _waitId moved on with it.
    private void OnLocateAnswered(int id)
    {
        if (id != _waitId || !Locating) return;
        Locating = false;
        // The locator has set the tracker by now; the start goes as any other does.
        _postToUi(() => TryStart(fromBelief: null));
    }

    private void OnLocateFailed(int id)
    {
        if (id != _waitId || !Locating) return;
        Locating = false;
        FallBack("the game didn't say where we are");
    }

    private void OnRoomWaitElapsed(int id)
    {
        if (id != _waitId || !AwaitsRoom) return;
        _roomWaitTimer = null;
        FallBack($"no room known within {ReconnectRestartLimits.RoomWaitMax.TotalSeconds:0}s");
    }

    private void FallBack(string why) => TryStart(fromBelief: why);

    // A locate or a time limit still out is left to answer nobody.
    private void ForgetRoomWait()
    {
        AwaitsRoom = false;
        Locating = false;
        _unsureChecks = 0;
        _locateTried = false;
        _waitId++;
        _roomWaitTimer?.Dispose();
        _roomWaitTimer = null;
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // Also for a run the user starts over the started one while it is held: it
    // waits for the reform just the same.
    public void BeginReformHoldIfReformAwaits()
    {
        if (HeldForReform || _reformAwaitsRoom?.Invoke() != true) return;
        HeldForReform = true;
        _log?.Info(_owner, $"{_occasion}: holding movement until the party reform has seen the room");
        _coordinator?.AssertGate(MovementCoordinator.ReconnectReformGate, _owner,
            "the party reform has yet to see the room");
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = _schedule(ReconnectRestartLimits.ReformHoldMax, OnReformHoldElapsed);
    }

    // linesStillToCome: called from a prompt, whose lines (the room display the
    // reform is waiting for among them) haven't been handled yet, so the hold is
    // looked at again once they have.
    private void ReleaseHoldOnceReformSawRoom(bool linesStillToCome)
    {
        if (!HeldForReform) return;
        if (_reformAwaitsRoom?.Invoke() != true)
        {
            EndReformHold("the party reform has seen the room");
            return;
        }
        if (linesStillToCome) _postToUi(() => ReleaseHoldOnceReformSawRoom(linesStillToCome: false));
    }

    private void OnReformHoldElapsed()
    {
        _reformHoldTimer = null;
        EndReformHold($"no room display for the party reform within {ReconnectRestartLimits.ReformHoldMax.TotalSeconds:0}s");
    }

    public void EndReformHold(string why)
    {
        if (!HeldForReform) return;
        HeldForReform = false;
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = null;
        _log?.Info(_owner, $"{_occasion}: movement hold for the party reform dropped ({why})");
        _coordinator?.ClearGate(MovementCoordinator.ReconnectReformGate, _owner, why);
    }
}
