using Avalonia.Threading;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Leader-side @comeback party-pickup flow. A stranded follower whose lead engine
// walked off and left them behind sends @comeback <map>/<room> (e.g.
// @comeback 9/1012) or a bare @comeback; this manager pauses the running movement
// engine, walks to recover them, re-invites (left-behind members are dropped from
// the party server-side), waits for the follow confirmation, then resumes
// whatever was running.
//
// Stop-and-restart, not gate-pause. Asserting a MovementCoordinator gate would
// block the recovery walk itself — AutoWalkManager.WalkTo parks in Paused while
// any gate is asserted. So we snapshot the running engine's resume state, Stop()
// it, run the recovery walk gate-clean, then re-Start the captured engine.
//   - Idle (no engine running) → reply "I can't I'm idle" and do nothing.
//   - Explicit room → walk straight there, re-invite, await follow, resume.
//   - No room → walk backwards along the path just taken (the
//     RoomTracker.GetHistory trail), room by room, up to MaxBacktrackRooms,
//     checking for the follower at each arrival; recover on sight, else go idle
//     and let the player handle it.
//
// Everything runs on the UI thread — AutoWalkManager.Event,
// PartyManager.MemberFollowConfirmed, and the follow timeout's DispatcherTimer
// all fire there, so no marshalling is needed. Single-flight: a second @comeback
// while one is in progress replies busy and is ignored.
public sealed class PartyComebackManager : IDisposable
{
    private const string LogCategory = "Comeback";

    // How long to wait for the recovered follower's "X started to follow you."
    // confirmation before resuming the paused engine anyway, so a follower who
    // never re-follows can't hang the leader indefinitely.
    private static readonly TimeSpan FollowTimeout = TimeSpan.FromSeconds(20);

    private readonly RemoteCommandManager _engine;
    private readonly PartyManager _party;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly LogService? _log;
    private readonly DispatcherTimer _followTimer;

    private bool _disposed;
    private bool _busy;
    private ComebackPhase _phase = ComebackPhase.Idle;
    private ResumeTarget _resume;
    private string _senderGiven = string.Empty;
    private Action<string> _reply = static _ => { };
    private readonly List<RoomKey> _backtrack = new();
    private int _backtrackIndex;

    // Backtrack budget — how many rooms back along the just-walked path the leader
    // will search for a stranded follower before giving up and going idle. Mirrors
    // OtherSettings.MaxComebackBacktrackRooms; clamped to 1..50 on use.
    public int MaxBacktrackRooms { get; set; } = 10;

    public PartyComebackManager(
        RemoteCommandManager engine,
        PartyManager party,
        RoomTracker tracker,
        RoomEntityClassifier classifier,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        _engine = engine;
        _party = party;
        _tracker = tracker;
        _classifier = classifier;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _log = log;

        _followTimer = new DispatcherTimer { Interval = FollowTimeout };
        _followTimer.Tick += OnFollowTimeout;

        _walker.Event += OnWalkEvent;
        _party.MemberFollowConfirmed += OnMemberFollowConfirmed;

        if (!RemoteCommandCatalog.TryGetCategory("@comeback", out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@comeback'.");
        _engine.RegisterHandler("@comeback", category, OnComeback);

        // @forget is the follower's "stop coming back for me" — the
        // counterpart to @comeback. It lives here because only this
        // manager holds the in-flight recovery state it has to abandon.
        if (!RemoteCommandCatalog.TryGetCategory("@forget", out Models.GameData.PlayerRemoteControls forgetCategory))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@forget'.");
        _engine.RegisterHandler("@forget", forgetCategory, OnForget);

        // A left-behind follower is dropped from the party server-side, so
        // the engine's party-whitelist gate (IsActivePartyMember) can't
        // authorise their @comeback. Bridge the leader-side grace-window
        // eligibility (recently departed, NOT uninvited by us) into the
        // engine so the request is honoured for genuine strandings only.
        _engine.ComebackEligibility = _party.WasRecentlyPartied;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@comeback");
        _engine.UnregisterHandler("@forget");
        if (_engine.ComebackEligibility == _party.WasRecentlyPartied)
            _engine.ComebackEligibility = null;
        _walker.Event -= OnWalkEvent;
        _party.MemberFollowConfirmed -= OnMemberFollowConfirmed;
        _followTimer.Stop();
        _followTimer.Tick -= OnFollowTimeout;
    }

    private void OnComeback(RemoteCommandContext ctx)
    {
        if (_busy)
        {
            ctx.Reply("comeback already in progress");
            return;
        }

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first.
        ResumeTarget resume = SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None)
        {
            ctx.Reply("I can't I'm idle");
            return;
        }

        RoomKey? target = null;
        if (ctx.Args.Count > 0 && RoomKey.TryParseWire(ctx.Args[0], out RoomKey parsed))
            target = parsed;

        _busy = true;
        _phase = ComebackPhase.Idle;
        _resume = resume;
        _senderGiven = GivenName(ctx.Sender);
        _reply = ctx.Reply;
        _log?.Info(LogCategory,
            $"@comeback from {ctx.Sender} target={(target is { } t ? $"{t.Map}/{t.Room}" : "backtrack")} resume={resume.Kind}");

        // Stop the running engine(s) so the recovery walk runs without a
        // competing command stream or an asserted pause gate.
        StopRunningEngines("comeback recovery");

        if (target is { } room)
        {
            ctx.Reply($"coming back to {room.Map}/{room.Room}");
            BeginWalk(room, ComebackPhase.WalkingToRoom);
            return;
        }

        BuildBacktrack();
        if (_backtrack.Count == 0)
        {
            ctx.Reply("no path history to backtrack — going idle");
            GoIdle();
            return;
        }
        ctx.Reply($"backtracking up to {_backtrack.Count} room(s) to find you");
        StepBacktrack();
    }

    private void OnForget(RemoteCommandContext ctx)
    {
        // Only the member we're actively recovering can call off their own
        // pickup; a @forget from anyone else (or when nothing is running)
        // has nothing to abandon.
        if (!_busy || !string.Equals(GivenName(ctx.Sender), _senderGiven, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Reply("nothing to forget");
            return;
        }

        _log?.Info(LogCategory, $"@forget from {ctx.Sender} — uninviting and resuming");
        _party.Uninvite(_senderGiven);
        ctx.Reply($"forgetting {_senderGiven} — resuming");
        Resume();
    }

    // ----- engine snapshot / stop / resume ---------------------------

    private ResumeTarget SnapshotRunningEngine()
    {
        // Priority Lair -> Loop -> Walker: the upper engines drive the
        // lower ones (AutoLair drives the walker; a loop drives the
        // walker during its approach leg), so the topmost active engine
        // is the real activity to resume.
        if (_autoLair.IsActive)
            return new ResumeTarget(ResumeKind.Lair, null, null);
        if (_loopRunner.State is not LoopState.Idle && _loopRunner.CurrentLoop is { } loop)
            return new ResumeTarget(ResumeKind.Loop, null, loop);
        if (_walker.State is not WalkState.Idle && _walker.Destination is { } dest)
            return new ResumeTarget(ResumeKind.Walker, dest, null);
        return new ResumeTarget(ResumeKind.None, null, null);
    }

    private void StopRunningEngines(string reason)
    {
        // AutoLair.Stop() is gate-clean (clears its UserGate when paused
        // and stops the walker); LoopRunner / Walker Stop() return them
        // to Idle. Stop all three so the recovery walk owns the wire.
        if (_autoLair.IsActive) _autoLair.Stop(reason);
        if (_loopRunner.State is not LoopState.Idle) _loopRunner.Stop(reason);
        if (_walker.State is not WalkState.Idle) _walker.Stop(reason);
    }

    private void Resume()
    {
        ResumeTarget r = _resume;
        GoIdle();
        switch (r.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                if (r.Loop is { } loop) _loopRunner.Start(loop);
                break;
            case ResumeKind.Walker:
                if (r.WalkerDest is { } dest) _walker.WalkTo(dest);
                break;
        }
    }

    private void GoIdle()
    {
        _busy = false;
        _phase = ComebackPhase.Idle;
        _followTimer.Stop();
        _backtrack.Clear();
        _backtrackIndex = 0;
    }

    // ----- walk driving ----------------------------------------------

    private void BeginWalk(RoomKey room, ComebackPhase phase)
    {
        _phase = phase;
        // WalkTo can synchronously raise Finished ("already at
        // destination") which re-enters OnWalkEvent before this returns;
        // _phase is set above so that re-entry is handled correctly.
        if (!_walker.WalkTo(room))
        {
            _reply($"can't reach {room.Map}/{room.Room} — resuming");
            Resume();
        }
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!_busy) return;
        switch (_phase)
        {
            case ComebackPhase.WalkingToRoom:
                if (e.Kind == WalkEventKind.Finished) ReInviteAndAwait();
                else if (e.Kind == WalkEventKind.Failed) { _reply("path failed — resuming"); Resume(); }
                break;
            case ComebackPhase.WalkingBacktrack:
                if (e.Kind == WalkEventKind.Finished) OnBacktrackArrival();
                else if (e.Kind == WalkEventKind.Failed) { _reply("backtrack path failed — going idle"); GoIdle(); }
                break;
        }
    }

    private void BuildBacktrack()
    {
        _backtrack.Clear();
        _backtrackIndex = 0;
        // GetHistory() is newest-first: [0] is the current room, [1] the
        // previous, etc. Skip [0] and walk the trail backwards.
        IReadOnlyList<RoomKey> history = _tracker.GetHistory();
        int budget = Math.Clamp(MaxBacktrackRooms, 1, 50);
        for (int i = 1; i < history.Count && _backtrack.Count < budget; i++)
            _backtrack.Add(history[i]);
    }

    private void StepBacktrack()
    {
        if (_backtrackIndex >= _backtrack.Count)
        {
            _reply("couldn't find you after backtracking — going idle");
            GoIdle();
            return;
        }
        RoomKey next = _backtrack[_backtrackIndex++];
        BeginWalk(next, ComebackPhase.WalkingBacktrack);
    }

    private void OnBacktrackArrival()
    {
        // The classifier re-fires its "Also here" observation on room
        // arrival, so Current reflects the room we just stepped into by
        // the time the walker raises Finished for it.
        if (FollowerHere())
        {
            ReInviteAndAwait();
            return;
        }
        StepBacktrack();
    }

    private bool FollowerHere()
    {
        if (_classifier.Current is not { } obs) return false;
        foreach (RoomEntity entity in obs.Entities)
        {
            if (entity.Kind == EntityKind.Player
                && string.Equals(entity.ResolvedName, _senderGiven, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ----- re-invite + follow await ----------------------------------

    private void ReInviteAndAwait()
    {
        _phase = ComebackPhase.AwaitingFollow;
        _reply($"found you — re-inviting {_senderGiven}");
        _party.Invite(_senderGiven);
        _followTimer.Stop();
        _followTimer.Start();
    }

    private void OnMemberFollowConfirmed(string name)
    {
        if (!_busy || _phase != ComebackPhase.AwaitingFollow) return;
        if (!string.Equals(GivenName(name), _senderGiven, StringComparison.OrdinalIgnoreCase)) return;
        _reply("got you — resuming");
        Resume();
    }

    private void OnFollowTimeout(object? sender, EventArgs e)
    {
        if (!_busy || _phase != ComebackPhase.AwaitingFollow) return;
        _reply("follow timed out — resuming anyway");
        Resume();
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space < 0 ? name.Trim() : name[..space].Trim();
    }

    private enum ComebackPhase
    {
        Idle,
        WalkingToRoom,
        WalkingBacktrack,
        AwaitingFollow,
    }

    private enum ResumeKind
    {
        None,
        Walker,
        Loop,
        Lair,
    }

    private readonly record struct ResumeTarget(ResumeKind Kind, RoomKey? WalkerDest, Loop? Loop);
}
