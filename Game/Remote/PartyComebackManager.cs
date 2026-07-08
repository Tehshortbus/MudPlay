using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

// Leader-side reconnect recovery. Two entry paths converge on the same walk-to-
// re-collect flow:
//   - Path A (@comeback): a stranded follower telepaths @comeback <map>/<room>
//     (e.g. @comeback 9/1012) or a bare @comeback. Used when the follower still
//     remembers us across the drop (a crash) and drives the pickup themselves.
//   - Path B (@where probe): the leader stayed in game and remembers the party.
//     When a dropped member re-enters the realm inside the grace window
//     (PartyManager raises MemberReturned), the leader telepaths @where, parses
//     the location reply, and recovers them — the fallback for a follower who
//     cleanly closed and lost their @comeback memory.
//
// Both paths run the same gates before committing: skip if our party is already
// full (we backfilled the slot while they were gone) and skip if they're farther
// than the return-distance setting, declining via @forget + a spoken reason so
// the member isn't left waiting. Otherwise we pause the running movement engine,
// walk to recover them, re-invite (left-behind members are dropped from the party
// server-side), wait for the follow confirmation, then resume.
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
// @forget is the counterpart teardown, bidirectional: a follower telepaths it to
// call off their own pickup, or the leader telepaths it to decline recovery. Both
// sides drop the other from the roster + grace window and clear any crash-rejoin
// memory, so neither keeps chasing the other.
//
// Everything runs on the UI thread — AutoWalkManager.Event,
// PartyManager.MemberFollowConfirmed, PartyManager.MemberReturned, the router
// telepath fan-out, and the follow timeout's DispatcherTimer all fire there, so
// no marshalling is needed. Single-flight: a second recovery request while one is
// in progress replies busy and is ignored.
public sealed class PartyComebackManager : IDisposable
{
    private const string LogCategory = "Comeback";

    // MajorMUD caps a party at 6 (leader + 5 followers). A returning member whose
    // slot we already refilled to this count can't be taken back.
    private const int MaxPartySize = 6;

    // How long to wait for the recovered follower's "X started to follow you."
    // confirmation before resuming the paused engine anyway, so a follower who
    // never re-follows can't hang the leader indefinitely.
    private static readonly TimeSpan FollowTimeout = TimeSpan.FromSeconds(20);

    // How long a path-B @where probe stays pending before we stop watching for its
    // reply — a member who never answers shouldn't leave a stale probe that
    // false-matches an unrelated telepath containing a "map N, room M" phrase.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    // Pulls the map/room numbers out of a @where reply
    // ("{Throne Room (map 9, room 1012); exits: ...}"). PartyEssentialHandlers
    // fixes that wording, so this parse tracks it.
    private static readonly Regex WhereReply =
        new(@"map (\d+), room (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RemoteCommandManager _engine;
    private readonly PartyManager _party;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly BfsMapper _bfs;
    private readonly LogService? _log;
    private readonly DispatcherTimer _followTimer;
    private readonly WireSender _wire = new();
    private readonly List<IDisposable> _subs = new();

    // Given names we've telepathed @where to and are awaiting a location reply
    // from, stamped with the probe time so PruneProbes can expire stragglers.
    private readonly Dictionary<string, DateTimeOffset> _pendingProbes =
        new(StringComparer.OrdinalIgnoreCase);

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

    // Leader-side recovery reach in BFS room-hops. A returning member farther than
    // this is declined instead of chased. Mirrors PartySettings.ReturnDistanceRooms
    // (Settings → Party); clamped 1..500 by the caller.
    public int ReturnDistanceRooms { get; set; } = 30;

    // Clock seam for probe-expiry bookkeeping; overridable in tests.
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    // Given name of the member we're actively walking to recover, or null when
    // idle. Surfaced in the bug report's Party section so a "leader never came
    // back for me" report shows whether a recovery was in flight.
    public string? RecoveringMember => _busy ? _senderGiven : null;

    // Invoked with a leader's given name when a @forget teardown involves someone
    // we were following, so the follower-side rejoin memory
    // (PartyRejoinCoordinator.ForgetRememberedLeader) clears too. AppServices wires
    // it; null in tests.
    public Action<string>? ForgetLeaderCallback { get; set; }

    // Test seam — every outbound wire buffer, in order.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public PartyComebackManager(
        RemoteCommandManager engine,
        PartyManager party,
        RoomTracker tracker,
        RoomEntityClassifier classifier,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        MessageRouter router,
        BfsMapper bfs,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(bfs);
        _engine = engine;
        _party = party;
        _tracker = tracker;
        _classifier = classifier;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _bfs = bfs;
        _log = log;

        _followTimer = new DispatcherTimer { Interval = FollowTimeout };
        _followTimer.Tick += OnFollowTimeout;

        _walker.Event += OnWalkEvent;
        _party.MemberFollowConfirmed += OnMemberFollowConfirmed;
        // Path B trigger: PartyManager owns reconnect detection and raises this
        // for a dropped member re-entering inside the grace window while we lead —
        // fired before it clears its own grace entry, so there's no race.
        _party.MemberReturned += OnMemberReturned;
        // Path B reply: the probed member's @where answer arrives as a telepath.
        _subs.Add(router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIn));

        if (!RemoteCommandCatalog.TryGetCategory("@comeback", out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@comeback'.");
        _engine.RegisterHandler("@comeback", category, OnComeback);

        // @forget is the recovery teardown — the counterpart to @comeback. It
        // lives here because only this manager holds the in-flight recovery state
        // it has to abandon.
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

    // Bind the outbound wire — the gate-wrapped engine sender the other party
    // engines use. MainWindowViewModel supplies it after telnet connects. Drives
    // the path-B @where probe and the decline @forget.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

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
        _party.MemberReturned -= OnMemberReturned;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        _followTimer.Stop();
        _followTimer.Tick -= OnFollowTimeout;
    }

    // ----- @comeback (path A) ----------------------------------------

    private void OnComeback(RemoteCommandContext ctx)
    {
        RoomKey? target = null;
        if (ctx.Args.Count > 0 && RoomKey.TryParseWire(ctx.Args[0], out RoomKey parsed))
            target = parsed;
        BeginRecovery(GivenName(ctx.Sender), target, ctx.Reply);
    }

    // ----- @where probe (path B) -------------------------------------

    private void OnMemberReturned(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (_busy) return;                 // a recovery is already running
        if (MemberInParty(given)) return;  // already back with us
        if (!_wire.IsBound) return;        // can't probe without a wire
        PruneProbes();
        _pendingProbes[given] = NowProvider();
        _wire.Send($"/{given} @where");
        _log?.Info(LogCategory, $"{given} re-entered the realm — probing @where for recovery.");
    }

    private void OnTelepathIn(MatchResult result)
    {
        if (_pendingProbes.Count == 0) return;
        if (result.Groups.Count < 2) return;
        PruneProbes();
        string sender = GivenName(result.Groups[0]);
        if (!_pendingProbes.ContainsKey(sender)) return;
        // Not a location reply — leave the probe pending; they may answer with a
        // later line, and PruneProbes expires it if they never do.
        if (!TryParseWhere(result.Groups[1], out RoomKey room)) return;
        _pendingProbes.Remove(sender);
        if (MemberInParty(sender)) return; // bare invite already collected them
        _log?.Info(LogCategory, $"{sender} answered @where at {room.Map}/{room.Room} — evaluating recovery.");
        BeginRecovery(sender, room, TelepathReply(sender));
    }

    private void PruneProbes()
    {
        DateTimeOffset now = NowProvider();
        List<string>? stale = null;
        foreach (KeyValuePair<string, DateTimeOffset> kv in _pendingProbes)
            if (now - kv.Value > ProbeTimeout) (stale ??= new()).Add(kv.Key);
        if (stale is null) return;
        foreach (string key in stale) _pendingProbes.Remove(key);
    }

    private static bool TryParseWhere(string message, out RoomKey room)
    {
        room = default;
        if (string.IsNullOrEmpty(message)) return false;
        Match m = WhereReply.Match(message);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out int map) || map <= 0) return false;
        if (!int.TryParse(m.Groups[2].Value, out int r) || r <= 0) return false;
        room = new RoomKey(map, r);
        return true;
    }

    // ----- shared recovery decision + drive --------------------------

    // Single funnel for both entry paths: gate on party-full + return-distance,
    // decline via @forget if we can't come, else snapshot / stop / walk.
    private void BeginRecovery(string senderGiven, RoomKey? target, Action<string> reply)
    {
        if (string.IsNullOrEmpty(senderGiven)) return;
        if (_busy)
        {
            reply("comeback already in progress");
            return;
        }

        // Party-full gate — we backfilled the empty slot while they were gone, so
        // there's no seat to recover them into.
        if (_party.State.Members.Count >= MaxPartySize)
        {
            reply("my party is full — can't take you back");
            DeclineAndForget(senderGiven);
            return;
        }

        // Return-distance gate — only measurable when both endpoints are known. A
        // member beyond the reach setting (or with no path at all) is declined
        // rather than chased across the map.
        if (target is { } dest && _tracker.State.CurrentRoom is { } current)
        {
            int? hops = _bfs.DistanceBetween(current.Key, dest);
            if (hops is null)
            {
                reply("can't find a path to you — you're on your own");
                DeclineAndForget(senderGiven);
                return;
            }
            if (hops > ReturnDistanceRooms)
            {
                reply($"you're {hops} rooms off (limit {ReturnDistanceRooms}) — can't come, forget me");
                DeclineAndForget(senderGiven);
                return;
            }
        }

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first.
        ResumeTarget resume = SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None)
        {
            reply("I can't I'm idle");
            return;
        }

        _busy = true;
        _phase = ComebackPhase.Idle;
        _resume = resume;
        _senderGiven = senderGiven;
        _reply = reply;
        _log?.Info(LogCategory,
            $"recovering {senderGiven} target={(target is { } t ? $"{t.Map}/{t.Room}" : "backtrack")} resume={resume.Kind}");

        // Stop the running engine(s) so the recovery walk runs without a
        // competing command stream or an asserted pause gate.
        StopRunningEngines("comeback recovery");

        if (target is { } room)
        {
            reply("coming to your location for pickup");
            BeginWalk(room, ComebackPhase.WalkingToRoom);
            return;
        }

        BuildBacktrack();
        if (_backtrack.Count == 0)
        {
            reply("no path history to backtrack — going idle");
            GoIdle();
            return;
        }
        reply($"backtracking up to {_backtrack.Count} room(s) to find you");
        StepBacktrack();
    }

    // Tell the returning member to stop waiting on us, and drop them from our own
    // roster + grace window so we don't keep auto-inviting them on re-entry.
    private void DeclineAndForget(string given)
    {
        _wire.Send($"/{given} @forget");
        _party.ForgetReconnectMember(given);
        _log?.Info(LogCategory, $"declined recovery of {given} — sent @forget and dropped them.");
    }

    // ----- @forget teardown (bidirectional) --------------------------

    private void OnForget(RemoteCommandContext ctx)
    {
        string given = GivenName(ctx.Sender);
        if (string.IsNullOrEmpty(given))
        {
            ctx.Reply("nothing to forget");
            return;
        }

        // If we're mid-recovery for exactly this player, abandon the walk and
        // resume whatever we paused before tearing the relationship down.
        bool wasRecovering = _busy
            && string.Equals(given, _senderGiven, StringComparison.OrdinalIgnoreCase);

        _log?.Info(LogCategory, $"@forget from {ctx.Sender} — dropping them from the party.");
        _party.ForgetReconnectMember(given);
        // If they were our leader, clear the crash-rejoin memory so a later
        // reconnect doesn't keep telepathing @comeback at them.
        ForgetLeaderCallback?.Invoke(given);
        _pendingProbes.Remove(given);

        if (wasRecovering)
        {
            ctx.Reply($"forgetting {given} — resuming");
            Resume();
        }
        else
        {
            ctx.Reply($"forgetting {given}");
        }
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

    // Wraps a telepath reply to the probed member in the same { } meta-braces the
    // engine's SendReply uses, so path-B narration reads like any @-command answer.
    private Action<string> TelepathReply(string given) => msg => _wire.Send($"/{given} {{{msg}}}");

    private bool MemberInParty(string given)
    {
        foreach (PartyMember m in _party.State.Members)
            if (GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
