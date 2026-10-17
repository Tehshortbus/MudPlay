using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Leader-side follower recovery. Three entry paths converge on the same walk-to-
// re-collect flow:
//   - Path A (@comeback): a stranded follower telepaths @comeback <map>/<room>
//     (e.g. @comeback 9/1012) or a bare @comeback. Used when the follower still
//     remembers us across the drop (a crash) and drives the pickup themselves.
//   - Path B (@where probe): the leader stayed in game and remembers the party.
//     When a dropped member re-enters the realm inside the grace window
//     (PartyManager raises MemberReturned), the leader telepaths @where, parses
//     the location reply, and recovers them — the fallback for a follower who
//     cleanly closed and lost their @comeback memory.
//   - Path C (left behind): a follower who couldn't move with us (held, knocked
//     down) falls off the party (PartyManager raises MemberLeftBehind); we
//     backtrack to where we left them.
//
// All paths run the same gates before committing: skip if our party is already
// full (we backfilled the slot while they were gone) and skip if they're farther
// than the return-distance setting, declining via @forget + a spoken reason so
// the member isn't left waiting. Otherwise we pause the running movement engine,
// walk to recover them, re-invite (Paradigm drops a left-behind member from the
// party; on Stock one an exit turned away is still on our list and is told to
// join, see AskBack), wait for the follow confirmation, then resume. The member
// following us again ends the recovery at whatever stage it has reached.
//
// Stop-and-restart, not gate-pause. Asserting a MovementCoordinator gate would
// block the recovery walk itself — AutoWalkManager.WalkTo parks in Paused while
// any gate is asserted. So we snapshot the running engine's resume state, Stop()
// it, run the recovery walk gate-clean, then re-Start the captured engine.
//   - Idle (no engine running) → reply "I can't I'm idle" and do nothing.
//   - Explicit room → walk straight there, re-invite, await follow, resume.
//   - No room → walk backwards along the path just taken (the
//     RoomTracker.GetHistory trail), room by room, up to ReturnDistanceRooms,
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
// no marshalling is needed. Single-flight: one member is fetched at a time, and
// the others left behind with them wait their turn (QueueRecovery).
//
// Two cases where nobody is gone back for at once, both the user's rulings of
// 2026-10-10:
//   - A train trip: a party train trip, or the leader's own train run with the
//     party in tow. Neither the game's drop line nor a member's @comeback stops
//     its walk; each member it leaves is kept with the room they were left in and
//     fetched when the training is done (TrainTripEnded), from however far. A
//     trip taken out of its own hands (the user's Stop, a run started over it)
//     fetches nobody: the pickup is the player's then.
//   - An exit that turned the member away. A loop whose own circuit goes through
//     it refuses their @comeback and carries on, Auto-Lair carries on and invites
//     them when a pass finds them, and a walk-to goes back once. The rule, and how
//     the leader tells an exit's doing from a hold, is at the head of "a member an
//     exit turned away".
public sealed class PartyComebackManager : IDisposable
{
    private const string LogCategory = "Comeback";

    // MajorMUD caps a party at 6 (leader + 5 followers). A returning member whose
    // slot we already refilled to this count can't be taken back.
    private const int MaxPartySize = 6;

    // How long to wait for the recovered follower's "X started to follow you."
    // confirmation before resuming the paused engine anyway, so a follower who
    // never re-follows can't hang the leader indefinitely. Mirrors Settings → Party
    // "If leading, wait only" like every other leader-side wait; zero waits until
    // they follow.
    public TimeSpan FollowWaitWindow { get; set; } = TimeSpan.FromSeconds(90);

    // How long a path-B @where probe stays pending before we stop watching for its
    // reply — a member who never answers shouldn't leave a stale probe that
    // false-matches an unrelated telepath containing a "map N, room M" phrase.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    // On a member's return we first send a bare CR to re-observe our room (they
    // re-enter where they left — our room if we haven't moved) and let the
    // "Also here:" re-display drive AutoPartyManager's invite-on-seen. Only if they
    // haven't rejoined by the time this elapses did they re-enter elsewhere — then
    // we fall back to the @where probe. Long enough to cover the CR round-trip +
    // the invite/follow-confirm handshake (report stock-20260730-193610).
    private static readonly TimeSpan CrRecoverFallback = TimeSpan.FromSeconds(3);

    private readonly RemoteCommandManager _engine;
    private readonly PartyManager _party;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly BfsMapper _bfs;
    private readonly LogService? _log;
    // The wait for a re-invited member to follow: a tick every _followInterval
    // until the recovery resumes.
    private IDisposable? _followTimer;
    private TimeSpan _followInterval;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    // Fires the @where fallback if a returned member hasn't rejoined after the CR
    // re-observe window. _crPendingName is the member we're giving the CR path a
    // chance to recover (null when none pending).
    private IDisposable? _crFallbackTimer;
    private string? _crPendingName;
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
    // The room our current recovery walk is headed for — a walker event for any
    // other destination belongs to someone else's walk.
    private RoomKey? _walkTarget;
    private int _backtrackIndex;
    // The recovery in flight is for a follower our move left behind (path C), and
    // whether their @ok landed just before that move (so it can't be trusted).
    private bool _leftBehind;
    private bool _okPremature;
    // The recovery in flight is a train trip's fetch (BeginRecovery says what that
    // changes), and its room is one we guessed and they haven't named.
    private bool _tripFetch;
    private bool _roomUnconfirmed;

    // The engine a recovery stopped, kept when that recovery gave up (backtracked
    // and never found them). We're idle only because we went looking, so their own
    // @comeback afterwards — typically once they're back online and can tell us
    // where they are — still recovers them and then resumes it, for ComebackWindow.
    private (string Given, ResumeTarget Resume, DateTimeOffset At)? _parkedResume;

    // For the bug report: whom we gave up on and what we'd resume for them.
    public string? ParkedResumeSummary => _parkedResume is { } p
        ? $"{p.Given} → {p.Resume.Kind}, {(_now() - p.At).TotalSeconds:F0}s ago" : null;
    // "If leading, accept @comeback for (min)" (Settings → Party).
    public TimeSpan ComebackWindow { get; set; } = TimeSpan.FromMinutes(2);

    // A member left behind this soon after their own @ok wasn't really free to move.
    private static readonly TimeSpan PrematureOkWindow = TimeSpan.FromSeconds(5);

    // Raised with the given name (and whether their @ok was premature) when a
    // left-behind follower re-follows, before the engine resumes — AppServices
    // treats them as held (chip + wait for @ok, or the full window if premature).
    public Action<string, bool>? LeftBehindRejoined { get; set; }

    // Whether a member sent @ok within a window of now (PartyEssentialHandlers.OkedWithin).
    public Func<string, TimeSpan, bool>? OkedWithin { get; set; }

    // Consecutive recovery walks that couldn't REACH a member, keyed by given name.
    // A follower stranded past a gate the leader can't cross (an item / key / toll
    // gate the return route runs through) fails the same walk every cycle, and each
    // fresh @comeback restarts the doomed walk — hijacking the leader's own
    // navigation over and over (report paradigm-20260827-154819: a manual walk-to
    // was repeatedly yanked toward a reconnected-but-unreachable follower). After
    // MaxFailedRecoveries failures we @forget the member so their next @comeback is
    // declined outright instead of restarting the chase. Reset on a follow-confirm
    // (they actually rejoined), which also un-strands a member who later returns by
    // a reachable route. UI-thread only, like the rest of this manager.
    private readonly Dictionary<string, int> _failedRecoveries =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailedRecoveries = 2;

    // Leader-side recovery reach (Settings → Party "Return distance", clamped 1..500
    // by the caller). A member who names a room farther than this many BFS hops away
    // is declined instead of chased; a bare @comeback backtracks at most this many
    // rooms along our own path (which RoomTracker keeps 50 rooms of).
    public int ReturnDistanceRooms { get; set; } = 30;

    private readonly Func<DateTimeOffset> _now;

    // Given name of the member we're actively walking to recover, or null when
    // idle. Surfaced in the bug report's Party section so a "leader never came
    // back for me" report shows whether a recovery was in flight.
    public string? RecoveringMember => _busy ? _senderGiven : null;

    // Whether that recovery is for a follower our own move left behind (path C).
    public bool RecoveringLeftBehind => _busy && _leftBehind;

    // How far that recovery has got, for the bug report.
    public string RecoveryPhase => _phase switch
    {
        ComebackPhase.WalkingToRoom => "walking to their room",
        ComebackPhase.WalkingBacktrack => "backtracking along our path",
        ComebackPhase.AwaitingFollow => "re-invited, waiting for them to follow",
        _ => "idle",
    };

    // Raised when a recovery starts or ends (RecoveringMember goes set / null).
    public event Action? RecoveringChanged;

    // Members we've hit the failed-recovery cap on and are now declining outright
    // (name → failure count). Surfaced in the bug report so a "leader keeps
    // abandoning me" report shows we gave up because their return route was
    // un-crossable, not by mistake. Empty in the common case.
    public IReadOnlyDictionary<string, int> GivenUpMembers =>
        _failedRecoveries
            .Where(kv => kv.Value >= MaxFailedRecoveries)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    // Invoked with a leader's given name when a @forget teardown involves someone
    // we were following, so the follower-side rejoin memory
    // (PartyRejoinCoordinator.ForgetRememberedLeader) clears too. AppServices wires
    // it.
    public Action<string>? ForgetLeaderCallback { get; set; }

    // Invoked with a member's given name and why, once we commit to walking to them
    // or decline them: whatever still holds our movement for that member (their
    // reconnect hold, a pending @wait) would park the pickup walk, and the member
    // is waiting on that walk. AppServices wires it.
    public Action<string, string>? ReleaseHolds { get; set; }

    // True on a Stock realm. Stock ends the follow of a member whose follow move
    // an exit refused and leaves them on our member list: nothing is printed to
    // us, and a `join` of theirs works with no fresh invite (GAME_MECHANICS "A
    // follower who can't move is left behind"). Paradigm drops them and tells us.
    // Unset reads as Paradigm, where nothing here differs from what it always did.
    public Func<bool>? OnStock { get; set; }

    // Stock, and that member has a row on our roster: by the game's rule they are
    // still on our list, whether or not they follow. The game tells a Stock
    // leader nothing when a follow ends, so the row is all there is to go on; a
    // member it did take off the list (held, stunned or too heavy when we moved)
    // keeps a row here until a `par` drops it, which is why everything built on
    // this falls back to an invite.
    private bool StillOnOurList(string given) => OnStock?.Invoke() == true && MemberInParty(given);

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
        LogService? log = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? DispatcherTicks.Once;
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

        _walker.Event += OnWalkEvent;
        _party.MemberFollowConfirmed += OnMemberFollowConfirmed;
        // Path B trigger: PartyManager owns reconnect detection and raises this
        // for a dropped member re-entering inside the grace window while we lead —
        // fired before it clears its own grace entry, so there's no race.
        _party.MemberReturned += OnMemberReturned;
        // Path C trigger: a follower who couldn't move with us fell off the party.
        _party.MemberLeftBehind += OnMemberLeftBehind;
        // What the loop's own steps cross, and who a `par` still lists as following:
        // how a loop tells a member an exit of its circuit turned away.
        _tracker.StateChanged += OnRoomChanged;
        _party.ParReplyRead += OnParReplyRead;
        _loopRunner.Event += OnLoopEvent;
        _autoLair.ActiveChanged += OnLairActiveChanged;
        _classifier.EntitiesObserved += OnEntitiesObserved;
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
        _party.MemberLeftBehind -= OnMemberLeftBehind;
        _tracker.StateChanged -= OnRoomChanged;
        _party.ParReplyRead -= OnParReplyRead;
        _loopRunner.Event -= OnLoopEvent;
        _autoLair.ActiveChanged -= OnLairActiveChanged;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        StopFollowTimer();
        StopCrFallback();
        StopJoinFallback();
    }

    // ----- @comeback (path A) ----------------------------------------

    private void OnComeback(RemoteCommandContext ctx)
    {
        RoomKey? target = null;
        if (ctx.Args.Count > 0 && RoomKey.TryParseWire(ctx.Args[0], out RoomKey parsed))
            target = parsed;
        string given = GivenName(ctx.Sender);
        if (TrainTripOn)
        {
            // The trip's own walk is not stopped for anyone (user, 2026-10-10:
            // "train trips should be excluded"); they are fetched when the training
            // is done, from the room they name. Said once, and only when denials
            // are (user, 2026-10-10: it "should match whether the refusals are
            // verbose or silent").
            bool firstWord = _answeredOnTrip.Add(given);
            NoteLeftOnTrip(given, target, named: true);
            _log?.Info(LogCategory,
                $"{given} asked to be fetched{(target is { } t ? $" from {t.Map}/{t.Room}" : "")} during the train trip — kept until the training is done");
            if (firstWord) ctx.Refuse(TrainTripReply);
            return;
        }
        // Someone a train trip of ours came back without, saying where they are.
        // The trip was ours, so this is still its fetch: whatever we are doing
        // now, and from however far.
        if (TakeAwaitedAfterTrip(given))
        {
            _log?.Info(LogCategory,
                $"{given}, whom the train trip left, asks to be fetched{(target is { } w ? $" from {w.Map}/{w.Room}" : "")} — going back for them.");
            BeginRecovery(given, target, ctx.Reply, tripFetch: true);
            return;
        }
        (GateVerdict verdict, RoomKey? from, GateStep? step) = GateVerdictForRequest(given, target);
        switch (verdict)
        {
            case GateVerdict.LoopRefuses:
                // Going back would only leave them at the same exit on the next lap
                // (user, 2026-10-10: on a loop the leader "should not accept a comback
                // for them, it should reject them"). A denial, so it is said only when
                // denials are, though the follower's client reads it to give the
                // follow up at once (user, 2026-10-10: it "should match whether the
                // refusals are verbose or silent"). Unanswered, the follower gives
                // up when its own window runs out.
                _log?.Info(LogCategory,
                    $"{given} asked to be fetched{(from is { } f ? $" from {f.Map}/{f.Room}" : "")}, where our loop goes through an exit they can't pass — refused, the loop carries on");
                ctx.Refuse(LoopGateRefusal);
                return;
            case GateVerdict.LairInvitesOnSight:
                // Auto-Lair doesn't break off for them. Said once, and only when
                // denials are.
                LeaveToTheNextPass(given, from);
                if (_lairAnswered.Add(given)) ctx.Refuse(LairGateReply);
                return;
            case GateVerdict.WalkEnds when step is { } gate:
                // Stock: the request is the first we hear of the second drop. The
                // walk ends, and they are answered by where that leaves us.
                EndWalkAt(given, gate);
                break;
        }
        BeginRecovery(given, target, ctx.Reply);
    }

    // The answer to a member our loop left at an exit that doesn't let them through.
    public const string LoopGateRefusal = "I can't, my loop goes through an exit you can't pass";

    // The answer to a member Auto-Lair left at an exit that turned them away. "I
    // can't yet" is what a follower's client reads as "wait", not as a refusal.
    public const string LairGateReply = NotYet + ", an exit on my way turned you away. I'll invite you when my next pass finds you";

    // The answer to a member who asks to be fetched in the middle of a train trip.
    public const string TrainTripReply = NotYet + ", I'm on a train trip. I'll come for you when the training is done";

    // How each answer that says we are coming, now or later, begins. The follower's
    // client waits longer on one of these and on nothing else we telepath it
    // (ComebackRequester.OnTelepathIn), so the answers below are built from these
    // and the two sides can't drift apart. The words must stay as they are: a
    // follower on another version reads them too.
    public const string ComingForPickup = "coming to your location for pickup";
    public const string Backtracking = "backtracking up to";
    public const string AlreadyInProgress = "comeback already in progress";
    public const string NotYet = "I can't yet";
    public const string DidNotFindYou = "didn't find you";
    public const string FoundYou = "found you";
    public const string GotYou = "got you";

    private static readonly string[] ComingOrLaterOpenings =
        { ComingForPickup, Backtracking, AlreadyInProgress, NotYet, DidNotFindYou, FoundYou, GotYou };

    // True when a leader's answer says it is coming for us, now or later.
    public static bool SaysComingOrLater(string answer)
    {
        foreach (string opening in ComingOrLaterOpenings)
            if (answer.StartsWith(opening, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ----- a train trip (nobody is fetched until it is over) ----------------

    // A train trip is under way: a party train trip (PartyTrainCoordinator.
    // TripRunning), or a train or spell-shop run of the leader's own that took the
    // engine's place with the party in tow (TrainerWalkManager.OwnRunActive).
    public Func<bool>? TrainTripRunning { get; set; }

    private bool TrainTripOn => TrainTripRunning?.Invoke() == true;

    // One line for the terminal.
    public Action<string>? Notice { get; set; }

    // Who the trip in hand left on the way, oldest first, with the room each was
    // left in: the room our step left when the game told us, or (RoomNamed) the
    // room they named when they asked.
    private readonly List<(string Given, RoomKey? Room, bool RoomNamed)> _leftOnTrip = new();
    // Who has been told, this trip, that they will be fetched after training.
    private readonly HashSet<string> _answeredOnTrip = new(StringComparer.OrdinalIgnoreCase);

    // Members a finished trip came back without and whose room we don't have, by
    // when the trip ended: the game tells a Stock leader nothing of a follower it
    // drops, and a room of our own guessing can turn out empty. Their @comeback
    // names it, and is fetched as the trip's own for as long as a @comeback is
    // taken from a member who has left (ComebackWindow).
    private readonly Dictionary<string, DateTimeOffset> _awaitedAfterTrip = new(StringComparer.OrdinalIgnoreCase);

    // A trip that ended with the master switch off: whom it left, and when it
    // ended. Fetched when the switch is back on (SettleAfterMasterSwitch), if that
    // is soon enough.
    private (List<(string Given, RoomKey? Room, bool RoomNamed)> Left, DateTimeOffset At)? _owedTripFetch;

    // For the bug report: the members a train trip has left on the way so far, the
    // ones a finished trip is waiting to hear from, and a fetch the master switch
    // is holding.
    public IReadOnlyList<string> LeftOnTrainTrip => _leftOnTrip.Select(l => Placed(l.Given, l.Room)).ToList();
    public IReadOnlyList<string> AwaitedAfterTrainTrip => _awaitedAfterTrip.Keys.ToList();
    public IReadOnlyList<string> TripFetchOwed =>
        _owedTripFetch is { } owed ? owed.Left.Select(l => Placed(l.Given, l.Room)).ToList() : [];

    private static string Placed(string given, RoomKey? room) =>
        room is { } r ? $"{given} at {r.Map}/{r.Room}" : $"{given} (room not known)";

    private void NoteLeftOnTrip(string given, RoomKey? room, bool named)
    {
        int at = _leftOnTrip.FindIndex(l => SameName(l.Given, given));
        if (at < 0) _leftOnTrip.Add((given, room, named && room is not null));
        // The room they name stands over the one we guessed, never the other way.
        else if (room is not null && (named || !_leftOnTrip[at].RoomNamed)) _leftOnTrip[at] = (given, room, named);
    }

    // Everyone who set out on the last trip that ended by itself, and when it
    // ended. A Stock leader's own list may still show a follower the game dropped
    // without a word, so the roll is all that covers them.
    private (HashSet<string> Names, DateTimeOffset At)? _lastTripRoll;

    private bool TakeAwaitedAfterTrip(string given)
    {
        DateTimeOffset now = _now();
        bool awaited = _awaitedAfterTrip.Remove(given, out DateTimeOffset since) && now - since <= ComebackWindow;
        bool onTheRoll = _lastTripRoll is { } roll && roll.Names.Remove(given) && now - roll.At <= ComebackWindow;
        return awaited || onTheRoll;
    }

    // The trip is over and the engine it paused is back.
    //
    // byItself: it finished, or failed on its own. Everyone it left on the way is
    // fetched now, in the order they were left, through the same pickup as any
    // member left behind, whatever client they are on and however long ago it was
    // (user, 2026-10-10: "the leader should realize that and pick them up when the
    // training is done"). The fetch runs with no walk or loop to go back to as
    // well, and from however far: the trip was ours, so the fetch is.
    //
    // Not by itself: the user's second Stop, a run started over it, Reset States,
    // its walk stopped under it. The player has taken over, and as with a pickup
    // they took over (2026-10-08) nothing walks back on its own: who was left is
    // logged and named once in the terminal, and their @comeback is still a
    // member's, answered by whatever we are doing when it comes.
    //
    // setOut: who set out, where the trip kept a roll.
    public void TrainTripEnded(bool byItself, IReadOnlyList<string> setOut)
    {
        ArgumentNullException.ThrowIfNull(setOut);
        _answeredOnTrip.Clear();
        List<(string Given, RoomKey? Room, bool RoomNamed)> left = new(_leftOnTrip);
        _leftOnTrip.Clear();
        List<string> roll = setOut.Select(GivenName)
            .Where(n => n.Length > 0 && !left.Exists(l => SameName(l.Given, n)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastTripRoll = byItself && roll.Count > 0
            ? (new HashSet<string>(roll, StringComparer.OrdinalIgnoreCase), _now())
            : null;
        // Set out, not following by our list, and not one the game told us it
        // dropped. Someone standing with us is only waiting for the re-invite a
        // leader's own train makes necessary.
        List<string> unplaced = roll.Where(n => !MemberFollowing(n) && !InOurRoom(n)).ToList();
        if (left.Count == 0 && unplaced.Count == 0) return;

        // However long ago the trip left them, their own request is a member's.
        foreach ((string given, _, _) in left) _party.ExpectComebackFrom(given);
        foreach (string given in unplaced) _party.ExpectComebackFrom(given);

        if (!byItself)
        {
            string names = string.Join(", ", left.Select(l => Placed(l.Given, l.Room)).Concat(unplaced.Select(n => Placed(n, null))));
            _log?.Info(LogCategory,
                $"the train trip was taken out of its hands; it left {names} — not going back on our own, the pickup is yours. Their @comeback is still taken as a member's.");
            Notice?.Invoke($"[Train trip ended early - left behind: {names}. Not going back for them on its own]");
            return;
        }

        DateTimeOffset now = _now();
        foreach (string given in unplaced)
        {
            _awaitedAfterTrip[given] = now;
            _log?.Info(LogCategory,
                $"the train trip came back without {given} and the game didn't say where it left them — their @comeback is gone back for, wherever we are then.");
        }
        if (left.Count == 0) return;
        if (MasterOff)
        {
            _owedTripFetch = (left, now);
            _log?.Info(LogCategory,
                $"the train trip left {string.Join(", ", left.Select(l => Placed(l.Given, l.Room)))}, but the master switch is off — going back for them when it is back on, if that is within {ComebackWindow.TotalMinutes:0.#} min.");
            return;
        }
        FetchLeftOnTrip(left);
    }

    private void FetchLeftOnTrip(List<(string Given, RoomKey? Room, bool RoomNamed)> left)
    {
        foreach ((string given, RoomKey? room, bool named) in left)
        {
            _log?.Info(LogCategory,
                $"the train trip left {given} {(room is { } r ? $"at {r.Map}/{r.Room}" : "on the way")} — going back for them now the training is done.");
            BeginRecovery(given, room, TelepathReply(given), tripFetch: true, roomUnconfirmed: room is not null && !named);
        }
    }

    // We walked to the room our own step left when the game dropped them, and they
    // aren't in it. Inviting into an empty room would hold the engine for the whole
    // follow wait, so the search ends here and they say where they are: that
    // request is the trip's fetch still.
    private void NotWhereTheTripLeftThem()
    {
        _log?.Info(LogCategory,
            $"{_senderGiven} isn't in the room the trip left them in — waiting for their @comeback to name a room.");
        _awaitedAfterTrip[_senderGiven] = _now();
        _party.ExpectComebackFrom(_senderGiven);
        _reply($"{DidNotFindYou} where the trip left you - @comeback with your room and I'll come");
        Resume();
    }

    // ----- a member an exit turned away ----------------------------------------
    //
    // An exit that lets through only some (an item, a toll, a level, class, race or
    // alignment window) can drop a follower behind an engine step of ours. What the
    // engine then does is the user's ruling for each (2026-10-10):
    //   - A loop would leave them at the same exit every lap: it carries on and
    //     refuses their @comeback ("it should reject them").
    //   - A walk-to, or an errand's walk, goes back and re-invites once ("on a walk
    //     to, it absolutely should comeback for them"). Turned away at the same exit
    //     again once the walk is under way again, the walk ends there with a notice
    //     naming them and the exit, and no second pickup is started ("option A").
    //     Another gated exit further on gets its own one pickup.
    //   - Auto-Lair doesn't break off. It carries on and invites them when a pass
    //     next finds them in a room it enters ("auto-lair will just pick them up
    //     again on its next pass in the room").
    //
    // When the exit is what turned this member away:
    //   - For a loop, only an exit its own circuit takes, crossed by a step of the
    //     loop. A gate on the walk to the circuit, on a train trip's walk or crossed
    //     by hand is not the loop's, and the member is gone back for.
    //   - Not when they told us they are held or waiting (SignalledHold): that is
    //     what stopped them, and they are gone back for.
    //   - Not, for a loop or Auto-Lair, when the exit is known to admit them
    //     (MemberCanPass true: level, class, race, purse and item all known and
    //     met). Where the leader can't tell, the gated step counts as the cause.
    //   - What the game does beats what we hold of them: fetched from an exit once
    //     and dropped at that same exit again, they are gated there, whatever the
    //     judge says, until the run is over or they are seen through it.

    // The member has a hold or a wait out with us (@wait, @held): whatever stopped
    // them, it wasn't the exit.
    public Func<string, bool>? MemberSignalledHold { get; set; }

    // Whether the exit would have let this member through, from what we hold of
    // them (MemberGateJudge): true, false, or null when it can't be told.
    public Func<string, RoomExit, bool?>? MemberCanPass { get; set; }

    // An @ok a moment before being left behind reads as a hold too, though on its
    // face it is a release: they said they were free and then didn't move with us,
    // so whatever held them still did (the premature-@ok rule, PrematureOkWindow).
    private bool SignalledHold(string given) =>
        MemberSignalledHold?.Invoke(given) == true || OkedWithin?.Invoke(given, PrematureOkWindow) == true;

    // An engine step of ours through an exit that lets through only some.
    private readonly record struct GateStep(RoomKey From, Direction Direction, RoomExit Exit, DateTimeOffset At, bool OnLoopCircuit);

    // A pickup made from such an exit: whom for, under which engine, and when.
    // Proven once the same exit dropped them again after it.
    private sealed class GateFetch(string given, GateStep gate, ResumeKind engine, DateTimeOffset at)
    {
        public string Given { get; } = given;
        public RoomKey From { get; } = gate.From;
        public RoomKey To { get; } = gate.Exit.Target;
        public ResumeKind Engine { get; } = engine;
        public DateTimeOffset At { get; set; } = at;
        public bool Proven { get; set; }
    }

    private enum GateVerdict { GoBack, LoopRefuses, LairInvitesOnSight, WalkEnds }

    // Followers the game told us it dropped right behind a loop step through an
    // exit that turned them away: who, and the room it leads out of.
    private readonly Dictionary<string, (RoomKey From, DateTimeOffset At)> _gatedOnLoop =
        new(StringComparer.OrdinalIgnoreCase);

    // Members the game told us it dropped behind a move of ours, until they follow
    // again. For them the judgement was made on that line and stands.
    private readonly HashSet<string> _dropLineSeen = new(StringComparer.OrdinalIgnoreCase);

    // The latest engine step through each gated exit, by the room it leaves, and
    // the one our latest step took if it took one.
    private readonly Dictionary<RoomKey, GateStep> _gateCrossed = new();
    private GateStep? _lastStepGate;

    private readonly List<GateFetch> _gateFetches = new();

    // When each member was last known to be following: their join, or a `par`
    // that listed them. Where the game tells a leader nothing of a drop, a gate
    // crossed since then is the one that could have turned them away.
    private readonly Dictionary<string, DateTimeOffset> _seenFollowingAt = new(StringComparer.OrdinalIgnoreCase);

    // Members Auto-Lair left at an exit that turned them away, to be invited when
    // a pass finds them; when each was last invited that way; and who has had the
    // one answer to their @comeback.
    private readonly HashSet<string> _inviteOnSight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _invitedOnSightAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lairAnswered = new(StringComparer.OrdinalIgnoreCase);
    // A room is shown again on a look, a fight's end and a re-display: one invite
    // per visit is enough.
    private static readonly TimeSpan OnSightInviteCooldown = TimeSpan.FromSeconds(60);

    // For the bug report: the members our loop left at an exit they can't pass,
    // the ones Auto-Lair invites on sight, and the exits a pickup was made from.
    public IReadOnlyList<string> LeftAtLoopGates =>
        _gatedOnLoop.Select(kv => $"{kv.Key} at {kv.Value.From.Map}/{kv.Value.From.Room}").ToList();
    public IReadOnlyList<string> InviteOnSight => _inviteOnSight.ToList();
    public IReadOnlyList<string> FetchedAtGates => _gateFetches
        .Select(f => $"{f.Given} from {f.From.Map}/{f.From.Room} ({f.Engine}{(f.Proven ? ", turned away again" : "")})").ToList();

    // Whether we went back for this member from this exit on the run under way: the
    // pickup JudgeGateDrop noted, which stands until they are seen through the exit
    // or the run is over. PartyTollGate asks it before paying a member's toll a
    // second time at the exit that turned them away.
    public bool WentBackFor(string given, RoomKey from, RoomKey to) =>
        _gateFetches.Exists(f => SameName(f.Given, given) && f.From == from && f.To == to);

    // The members this manager knows are not with us: being gone back for or
    // waiting their turn, told by the game to have dropped behind a move, left by
    // a train trip, left at a loop's gate, or waiting for Auto-Lair's next pass.
    // PartyTollGate leaves them out of a toll's count: they aren't following
    // through it. A member merely unseen in the room is not one of these.
    public IReadOnlyCollection<string> KnownLeftBehind()
    {
        HashSet<string> left = new(StringComparer.OrdinalIgnoreCase);
        if (_busy && !string.IsNullOrEmpty(_senderGiven)) left.Add(_senderGiven);
        foreach (var queued in _queued) left.Add(queued.Given);
        foreach (var onTrip in _leftOnTrip) left.Add(onTrip.Given);
        left.UnionWith(_dropLineSeen);
        left.UnionWith(_gatedOnLoop.Keys);
        left.UnionWith(_inviteOnSight);
        return left;
    }

    private ResumeKind EngineInCharge => _busy ? _resume.Kind : SnapshotRunningEngine().Kind;

    // Each room change: was it an engine's step through an exit that lets through
    // only some? A step of our own pickup walk, one made by hand and one with no
    // engine up cross nothing on an engine's behalf.
    private void OnRoomChanged(RoomTransition transition)
    {
        if (transition.NewRoom is not { } now || transition.PreviousRoom is not { } before || before.Key == now.Key)
            return;
        _lastStepGate = null;
        if (_busy || _tracker.LastMoveWasManual || SnapshotRunningEngine().Kind == ResumeKind.None) return;
        foreach ((Direction direction, RoomExit exit) in before.Exits)
        {
            if (!exit.AdmitsOnlySome || (exit.Target != now.Key && exit.Landing != now.Key)) continue;
            bool onCircuit = _loopRunner.State is not LoopState.Idle
                && _loopRunner.CircuitExits().Any(c => c.From == before.Key && c.Exit.Target == exit.Target);
            GateStep step = new(before.Key, direction, exit, _now(), onCircuit);
            _lastStepGate = step;
            _gateCrossed[before.Key] = step;
            return;
        }
    }

    private void OnParReplyRead()
    {
        DateTimeOffset now = _now();
        foreach (PartyMember m in _party.State.Members)
        {
            if (m.IsSelf || m.IsInvited) continue;
            string given = GivenName(m.Name);
            _seenFollowingAt[given] = now;
            // Listed as following after an engine step through the exit we once
            // fetched them from: they are through it, and the next drop there is
            // judged afresh.
            _gateFetches.RemoveAll(f => SameName(f.Given, given)
                && _gateCrossed.TryGetValue(f.From, out GateStep crossed) && crossed.Exit.Target == f.To && crossed.At > f.At);
        }
    }

    // A loop run is over (stopped for good, or failed): whom it was judged to
    // leave, and what it fetched from where, belong to that run.
    private void OnLoopEvent(LoopEvent e)
    {
        if (e.Kind != LoopEventKind.Failed && !(e.Kind == LoopEventKind.Stopped && !e.WillResume)) return;
        _gatedOnLoop.Clear();
        _gateFetches.RemoveAll(f => f.Engine == ResumeKind.Loop);
        ForgetGateSteps();
    }

    // The steps an engine made through gated exits are that run's.
    private void ForgetGateSteps()
    {
        _gateCrossed.Clear();
        _lastStepGate = null;
    }

    // Auto-Lair stopped for good: the next pass they were promised isn't coming.
    private void OnLairActiveChanged(bool active)
    {
        if (active || _autoLair.StoppedForResume) return;
        _inviteOnSight.Clear();
        _invitedOnSightAt.Clear();
        _lairAnswered.Clear();
        _gateFetches.RemoveAll(f => f.Engine == ResumeKind.Lair);
        ForgetGateSteps();
    }

    // What the engine in charge does about a member dropped behind this gated step
    // (the rule is at the head of this section).
    private GateVerdict JudgeGateDrop(string given, GateStep gate, ResumeKind engine, bool signalledHold)
    {
        if (signalledHold) return GateVerdict.GoBack;
        GateFetch? before = _gateFetches.Find(f => SameName(f.Given, given) && f.From == gate.From && f.To == gate.Exit.Target);
        // Dropped here again, by a step later than the pickup we made from it.
        bool again = before is not null && (before.Proven || gate.At > before.At);
        bool admitted = MemberCanPass?.Invoke(given, gate.Exit) == true;
        GateVerdict verdict = engine switch
        {
            ResumeKind.Loop when again || (gate.OnLoopCircuit && !admitted) => GateVerdict.LoopRefuses,
            ResumeKind.Lair when again || !admitted => GateVerdict.LairInvitesOnSight,
            ResumeKind.Walker when again => GateVerdict.WalkEnds,
            _ => GateVerdict.GoBack,
        };
        if (verdict == GateVerdict.GoBack)
        {
            // We go back for them from this exit: a second drop at it settles it.
            if (before is null) _gateFetches.Add(new GateFetch(given, gate, engine, _now()));
            else before.At = _now();
        }
        else if (before is { Proven: false })
        {
            before.Proven = true;
            before.At = gate.At;
            _log?.Info(LogCategory,
                $"{given} was dropped at the exit out of {gate.From.Map}/{gate.From.Room} again after we went back for them: it doesn't let them through, whatever we hold of them.");
        }
        return verdict;
    }

    // What a member's request is answered with when an exit may be why they were
    // left: the verdict, and the room and step it is about. Where the game told us
    // of the drop, what was judged on that line stands. Where it tells a leader
    // nothing (Stock), the engine's own gated steps since the member was last seen
    // following answer: the room they name, or with no room named the latest one.
    private (GateVerdict Verdict, RoomKey? From, GateStep? Step) GateVerdictForRequest(string given, RoomKey? target)
    {
        (GateVerdict, RoomKey?, GateStep?) goBack = (GateVerdict.GoBack, null, null);
        ResumeKind engine = EngineInCharge;
        if (engine == ResumeKind.None) return goBack;
        // Already being gone back for, or waiting their turn.
        if (_busy && SameName(given, _senderGiven)) return goBack;
        if (_queued.Exists(q => SameName(q.Given, given))) return goBack;
        if (engine == ResumeKind.Loop && _gatedOnLoop.TryGetValue(given, out var noted)
            && (target is null || target == noted.From)
            && (_now() - noted.At <= ComebackWindow
                || _gateFetches.Exists(f => f.Proven && SameName(f.Given, given) && f.From == noted.From)))
            return (GateVerdict.LoopRefuses, noted.From, null);
        if (engine == ResumeKind.Lair && _inviteOnSight.Contains(given))
            return (GateVerdict.LairInvitesOnSight, null, null);
        if (_dropLineSeen.Contains(given)) return goBack;

        DateTimeOffset since = _seenFollowingAt.GetValueOrDefault(given, DateTimeOffset.MinValue);
        GateStep? crossed = null;
        if (target is { } named)
        {
            if (_gateCrossed.TryGetValue(named, out GateStep there)) crossed = there;
        }
        else
        {
            foreach (GateStep step in _gateCrossed.Values)
                if (crossed is not { } latest || step.At > latest.At) crossed = step;
        }
        if (crossed is not { } gate || gate.At <= since) return goBack;
        return (JudgeGateDrop(given, gate, engine, SignalledHold(given)), gate.From, gate);
    }

    private static string Named(GateStep gate) =>
        $"{gate.Direction.ToLongName()} out of {gate.From.Map}/{gate.From.Room}";

    // The walk went back for them once and the same exit has turned them away
    // again: it would every time. The walk ends where it stands, the player is
    // told whom and where, and no second pickup is started (user, 2026-10-10).
    private void EndWalkAt(string given, GateStep gate)
    {
        _log?.Info(LogCategory,
            $"{given} was turned away again at the exit {Named(gate)}, after we went back for them once — ending the walk here; no second pickup.");
        Notice?.Invoke($"[Walk ended - {given} can't pass the exit {Named(gate)}; went back for them once already]");
        if (_walker.State is not WalkState.Idle) _walker.Stop($"{given} can't pass the exit {Named(gate)}");
    }

    // Auto-Lair carries on without them, and invites them when a pass next finds
    // them in a room it enters.
    private void LeaveToTheNextPass(string given, RoomKey? from)
    {
        if (!_inviteOnSight.Add(given)) return;
        _log?.Info(LogCategory,
            $"{given} was turned away at an exit{(from is { } r ? $" out of {r.Map}/{r.Room}" : "")} — Auto-Lair carries on, and invites them when a pass next finds them.");
    }

    // A room's occupants were read. One of them may be a member Auto-Lair is to
    // invite on sight. A `look` into the next room shows who stands there, not here.
    private void OnEntitiesObserved(RoomEntitiesObservation observation)
    {
        if (_inviteOnSight.Count == 0 || _busy || MasterOff) return;
        if (!_autoLair.IsActive || _tracker.IsPeekSuppressed()) return;
        DateTimeOffset now = _now();
        foreach (RoomEntity entity in observation.Entities)
        {
            if (entity.Kind != EntityKind.Player) continue;
            string given = GivenName(entity.ResolvedName);
            if (!_inviteOnSight.Contains(given)) continue;
            // A row on our roster means they are back with us, on Paradigm. On
            // Stock the row never left when the exit turned them away, so it says
            // nothing: they are owed this until they follow, which is what takes
            // them off _inviteOnSight.
            if (MemberInParty(given) && !StillOnOurList(given)) continue;
            if (now - _invitedOnSightAt.GetValueOrDefault(given, DateTimeOffset.MinValue) < OnSightInviteCooldown) continue;
            _invitedOnSightAt[given] = now;
            _log?.Info(LogCategory, $"{given} is here — bringing them back, as Auto-Lair's pass found them.");
            AskBack(given, removedByAHold: false, inviteAfter: JoinFirstWait, onSight: true);
        }
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ----- @where probe (path B) -------------------------------------

    // The master switch (true = off): off, nobody is probed for or gone back to
    // on our own. The @comeback and @forget a member sends are remote commands
    // and are stopped at the dispatcher (which keeps a @comeback to hand over
    // when the switch is back on). A recovery under way when the switch goes off
    // is held: see SettleAfterMasterSwitch.
    public Func<bool>? MasterSwitchOff { get; set; }

    private bool MasterOff => MasterSwitchOff?.Invoke() == true;

    private void OnMemberReturned(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (MasterOff) return;
        if (_busy) return;                 // a recovery is already running
        if (MemberInParty(given)) return;  // already back with us
        if (!_wire.IsBound) return;        // can't probe without a wire

        // Re-observe our room before spending a @where round-trip: a member who
        // re-enters the realm lands back where they left, so if we haven't moved
        // they're standing in our room. A bare CR re-displays it, and the
        // "Also here: <member>" line drives AutoPartyManager's invite-on-seen —
        // recovering them with no @where (report stock-20260730-193610). If they
        // haven't rejoined when CrRecoverFallback elapses, they re-entered
        // elsewhere and FireCrFallback sends the @where. One CR recovery in flight
        // at a time; a second concurrent return goes straight to the probe.
        if (_crPendingName is null)
        {
            _crPendingName = given;
            _wire.Send("");   // bare CR — re-display the current room
            StopCrFallback();
            _crFallbackTimer = _schedule(CrRecoverFallback, FireCrFallback);
            _log?.Info(LogCategory,
                $"{given} re-entered the realm — CR to re-observe our room before probing @where.");
            return;
        }
        ProbeWhere(given);
    }

    // CR re-observe window elapsed. If the member rejoined via invite-on-seen they
    // were in our room — nothing to do; otherwise they re-entered elsewhere, so
    // fall back to the @where probe.
    private void FireCrFallback()
    {
        _crFallbackTimer = null;
        string? given = _crPendingName;
        _crPendingName = null;
        if (given is null) return;
        if (MemberInParty(given))
        {
            _log?.Info(LogCategory, $"{given} rejoined after the CR — no @where needed.");
            return;
        }
        if (_busy || !_wire.IsBound || MasterOff) return;
        ProbeWhere(given);
    }

    private void StopCrFallback()
    {
        _crFallbackTimer?.Dispose();
        _crFallbackTimer = null;
    }

    private void ProbeWhere(string given)
    {
        PruneProbes();
        _pendingProbes[given] = _now();
        _wire.Send($"/{given} @where");
        _log?.Info(LogCategory, $"{given} not recovered by CR — probing @where for recovery.");
    }

    private void OnTelepathIn(MatchResult result)
    {
        if (_pendingProbes.Count == 0) return;
        if (MasterOff) return;
        if (result.Groups.Count < 2) return;
        PruneProbes();
        string sender = GivenName(result.Groups[0]);
        if (!_pendingProbes.ContainsKey(sender)) return;
        // Not a location reply — leave the probe pending; they may answer with a
        // later line, and PruneProbes expires it if they never do.
        if (!WhereReplyParser.TryParseRoom(result.Groups[1], out RoomKey room)) return;
        _pendingProbes.Remove(sender);
        if (MemberInParty(sender)) return; // bare invite already collected them
        _log?.Info(LogCategory, $"{sender} answered @where at {room.Map}/{room.Room} — evaluating recovery.");
        BeginRecovery(sender, room, TelepathReply(sender));
    }

    private void PruneProbes()
    {
        DateTimeOffset now = _now();
        List<string>? stale = null;
        foreach (KeyValuePair<string, DateTimeOffset> kv in _pendingProbes)
            if (now - kv.Value > ProbeTimeout) (stale ??= new()).Add(kv.Key);
        if (stale is null) return;
        foreach (string key in stale) _pendingProbes.Remove(key);
    }

    // ----- left behind (path C) -------------------------------------

    // The follower couldn't move when we did (held / knocked down) and is standing
    // in a room we just walked out of. Only a running engine walked away from them —
    // a manual move leaves the pickup to the player — so an idle leader stays silent
    // rather than telling the member "I'm idle". Recovery backtracks along the path
    // just taken, since that's where they were left.
    // Our own teleport: a token used (TokenTracker.TokenUsed), or a party-splitting
    // exit crossed while leading (Darkwood's `go vortex`, a chime). A teleport drops
    // everyone following us — they weren't held, the teleport split us — so for a
    // short while afterwards a "no longer following you" isn't a left-behind member
    // to go back for; the re-invite on landing regroups the party. A route-card token sends the party across first; a hand-used one leaves
    // the regroup to the player.
    public void NoteOwnTeleport() => _ownTeleportAt = _now();
    private DateTimeOffset? _ownTeleportAt;
    private static readonly TimeSpan OwnTeleportWindow = TimeSpan.FromSeconds(15);
    // How long after a move of ours a "no longer following you" still counts as
    // that move leaving the member behind. The game prints it with the move's own
    // room display, so this only has to cover lag.
    private static readonly TimeSpan OwnMoveWindow = TimeSpan.FromSeconds(5);

    // Lets go of the @wait a member has out with us (PartyEssentialHandlers.
    // ReleaseWait).
    public Action<string>? ReleaseMemberWait { get; set; }

    private void OnMemberLeftBehind(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        // Their @wait is let go whatever is decided, since it would park a walk back
        // to them. What it says about why they were left is read first, here, so
        // the reading doesn't hang on which handler of the event runs before which.
        bool signalledHold = SignalledHold(given);
        DecideForLeftBehind(given, signalledHold);
        ReleaseMemberWait?.Invoke(given);
    }

    private void DecideForLeftBehind(string given, bool signalledHold)
    {
        if (MasterOff) return;
        if (_ownTeleportAt is { } at && _now() - at <= OwnTeleportWindow)
        {
            _log?.Info(LogCategory, $"{given} was dropped by our own teleport — not going back for them.");
            return;
        }
        // The game prints the same line for a follower who walks off by themselves
        // (sent on with a relayed command, or moved by hand): they left us, we
        // didn't leave them, and there is nobody back down our path to fetch. Only a
        // member dropped right behind a move of ours was left behind (report
        // paradigm-20261007-183903: a follower sent through a gate with `@do se`
        // while the leader rested was gone back for).
        if (_tracker.LastMoveSentAt is not { } moved || _now() - moved > OwnMoveWindow)
        {
            _log?.Info(LogCategory, $"{given} stopped following, but not behind a move of ours — not going back for them.");
            return;
        }
        // On a party train trip nobody is gone back for, and the trip's walk is not
        // stopped: the room our step left is kept, and they are fetched when the
        // training is done.
        if (TrainTripOn)
        {
            IReadOnlyList<RoomKey> path = _tracker.GetHistory();
            RoomKey? leftAt = path.Count >= 2 ? path[1] : null;
            NoteLeftOnTrip(given, leftAt, named: false);
            _log?.Info(LogCategory,
                $"{given} was left behind {(leftAt is { } r ? $"at {r.Map}/{r.Room} " : "")}on the train trip — fetching them when the training is done.");
            return;
        }
        _dropLineSeen.Add(given);
        // Dropped behind an engine step through an exit that lets through only
        // some: what the engine does is its own (the rule is at the head of "a
        // member an exit turned away"). Held or too heavy on that same step, they
        // are gone back for like anyone.
        ResumeKind engine = SnapshotRunningEngine().Kind;
        if (_lastStepGate is { } gate && engine != ResumeKind.None)
        {
            switch (JudgeGateDrop(given, gate, engine, signalledHold))
            {
                case GateVerdict.LoopRefuses:
                    _gatedOnLoop[given] = (gate.From, _now());
                    _log?.Info(LogCategory,
                        $"{given} was turned away at the exit our loop took out of {gate.From.Map}/{gate.From.Room} — not going back; the loop would leave them there again.");
                    return;
                case GateVerdict.LairInvitesOnSight:
                    LeaveToTheNextPass(given, gate.From);
                    return;
                case GateVerdict.WalkEnds:
                    EndWalkAt(given, gate);
                    return;
            }
        }
        // One step can leave several behind (an exit that turns away more than one
        // follower prints the line for each): the others wait their turn.
        if (_busy)
        {
            if (!string.Equals(given, _senderGiven, StringComparison.OrdinalIgnoreCase) && !_tracker.LastMoveWasManual)
                QueueRecovery(given, null, TelepathReply(given), tripFetch: false, roomUnconfirmed: false);
            return;
        }
        if (SnapshotRunningEngine().Kind == ResumeKind.None)
        {
            _log?.Info(LogCategory, $"{given} was left behind, but no engine is running — leaving the pickup to you.");
            return;
        }
        // An engine can be up without having made the move: the one that left the
        // member behind was a manual one (a keystroke, a macro, a relayed command),
        // made over a walk the user had paused, or pausing it there and then, as
        // every manual move does. A recovery started for it is a walk born paused,
        // which wakes on Resume long after the player has fetched the member
        // themselves (report paradigm-20260929-221642: six rooms walked back to
        // where they had stood). Judged on the move, not on the pause: the same
        // pause gate is raised by a remote stop, an errand or the route picker, and
        // a member an engine step left behind under one of those is still ours to
        // go back for.
        if (_tracker.LastMoveWasManual)
        {
            _log?.Info(LogCategory, $"{given} was left behind by a move of your own, not an engine's — leaving the pickup to you.");
            return;
        }
        _log?.Info(LogCategory, $"{given} was left behind by our move — going back for them.");
        bool premature = OkedWithin?.Invoke(given, PrematureOkWindow) == true;
        BeginRecovery(given, null, TelepathReply(given));
        _leftBehind = _busy && string.Equals(_senderGiven, given, StringComparison.OrdinalIgnoreCase);
        _okPremature = _leftBehind && premature;
    }

    // ----- shared recovery decision + drive --------------------------

    // Single funnel for both entry paths: gate on party-full + return-distance,
    // decline via @forget if we can't come, else snapshot / stop / walk.
    // tripFetch: the fetch of a member a train trip of ours left. It goes although
    // no walk, loop or Auto-Lair is running to be put back afterwards, and from
    // however far: the return-distance limit is for a request out of the blue, and
    // this one is for a member our own trip walked away from (user, 2026-10-10).
    // roomUnconfirmed: the room is our own guess (the one our step left when the
    // game dropped them), to be checked for them on arrival.
    //
    // Every answer given through `reply`, here and for the rest of the recovery,
    // is protocol and goes whatever the Warn setting says: the follower's client
    // (ComebackRequester) reads each one to decide whether to give the follow up
    // at once ("I can't", "my party is full", "can't find a path", "can't come",
    // "forget me", "going idle"), to give up after a pickup that failed ("can't
    // reach", "path failed", "follow timed out"), or to wait for us for longer
    // (the answers SaysComingOrLater names). Silenced, a follower would wait out
    // its whole window for a leader who had already said no. The words it matches
    // on are not to be reworded. (The three answers OnComeback gives through
    // Refuse are not among these: they follow the Warn setting.)
    private void BeginRecovery(string senderGiven, RoomKey? target, Action<string> reply,
        ResumeTarget? carried = null, bool tripFetch = false, bool roomUnconfirmed = false)
    {
        if (string.IsNullOrEmpty(senderGiven)) return;
        if (_busy)
        {
            bool same = string.Equals(senderGiven, _senderGiven, StringComparison.OrdinalIgnoreCase);
            // On the way to the room we guessed, and they name the same one.
            if (same && target == _walkTarget && _phase == ComebackPhase.WalkingToRoom) _roomUnconfirmed = false;
            // The member we're looking for now says where they are — go there.
            bool guessedElsewhere = _phase == ComebackPhase.WalkingToRoom && _roomUnconfirmed && target != _walkTarget;
            if (target is { } there && same && (_phase == ComebackPhase.WalkingBacktrack || guessedElsewhere))
            {
                _log?.Info(LogCategory,
                    $"{senderGiven} is at {there.Map}/{there.Room} — {(guessedElsewhere ? "not where we left them" : "backtrack dropped")}, walking there");
                _roomUnconfirmed = false;
                _reply = reply;
                _backtrack.Clear();
                _backtrackIndex = 0;
                // Our own stop — idle the phase so OnWalkEvent doesn't read it as
                // the user taking over; BeginWalk sets the new phase.
                _phase = ComebackPhase.Idle;
                _walker.Stop("comeback: member gave their room");
                reply(ComingForPickup);
                BeginWalk(there, ComebackPhase.WalkingToRoom);
                return;
            }
            // Somebody else is being fetched: this one is next, with the room they
            // name. Without the queue the second of two followers an exit turned
            // away was answered and then never gone back for.
            if (!same)
            {
                QueueRecovery(senderGiven, target, reply, tripFetch, roomUnconfirmed);
                reply($"{AlreadyInProgress} - fetching {_senderGiven} first, then you");
                return;
            }
            reply(AlreadyInProgress);
            return;
        }

        // Repeated-failure backoff — a member we've failed to reach
        // MaxFailedRecoveries times running is stranded past a gate we can't cross;
        // stop chasing them so the doomed recovery walk can't keep hijacking our own
        // navigation (report paradigm-20260827-154819). @forget them so they stop
        // waiting; the count is left at the cap so a fresh @comeback keeps declining
        // until they actually rejoin (which clears it in OnMemberFollowConfirmed).
        if (_failedRecoveries.GetValueOrDefault(senderGiven) >= MaxFailedRecoveries)
        {
            reply($"tried reaching you {MaxFailedRecoveries}x and couldn't - forget me");
            DeclineAndForget(senderGiven);
            return;
        }

        // Party-full gate — we backfilled the empty slot while they were gone, so
        // there's no seat to recover them into. On Stock a member an exit turned
        // away is still on our list (StillOnOurList), so the row we hold for them is
        // their own seat and not one we refilled.
        int seatsTaken = _party.State.Members.Count;
        if (seatsTaken >= MaxPartySize && StillOnOurList(senderGiven))
        {
            seatsTaken--;
            _log?.Info(LogCategory,
                $"{senderGiven} asks to be fetched and still has a row on our list (Stock keeps a member an exit turned away): their own seat isn't counted against a full party.");
        }
        if (seatsTaken >= MaxPartySize)
        {
            reply("my party is full - can't take you back");
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
                reply("can't find a path to you - you're on your own");
                DeclineAndForget(senderGiven);
                return;
            }
            if (!tripFetch && hops > ReturnDistanceRooms)
            {
                reply($"you're {hops} rooms off (limit {ReturnDistanceRooms}) - can't come, forget me");
                DeclineAndForget(senderGiven);
                return;
            }
        }

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first. A recovery taken
        // off the queue carries the engine the one before it stopped.
        ResumeTarget resume = carried ?? SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None && TakeParkedResume(senderGiven) is { } parked)
            resume = parked;
        if (resume.Kind == ResumeKind.None && !tripFetch)
        {
            reply("I can't I'm idle");
            return;
        }
        _parkedResume = null;

        _busy = true;
        _phase = ComebackPhase.Idle;
        _resume = resume;
        _tripFetch = tripFetch;
        _roomUnconfirmed = roomUnconfirmed && target is not null;
        RecoveringChanged?.Invoke();
        _senderGiven = senderGiven;
        _reply = reply;
        // Read before the hold is released below.
        _heldWhenAsked = SignalledHold(senderGiven);
        _log?.Info(LogCategory,
            $"recovering {senderGiven} target={(target is { } t ? $"{t.Map}/{t.Room}" : "backtrack")} resume={resume.Kind}");

        // Stop the running engine(s) so the recovery walk runs without a
        // competing command stream or an asserted pause gate.
        StopRunningEngines("comeback recovery");
        // After the stop, so clearing the hold can't let the stopped engine step off.
        ReleaseHolds?.Invoke(senderGiven, "walking to pick them up");

        if (target is { } room)
        {
            reply(ComingForPickup);
            BeginWalk(room, ComebackPhase.WalkingToRoom);
            return;
        }

        BuildBacktrack();
        if (_backtrack.Count == 0)
        {
            reply("no path history to backtrack - going idle");
            ParkAndGoIdle();
            return;
        }
        reply($"{Backtracking} {_backtrack.Count} room(s) to find you");
        StepBacktrack();
    }

    // Tell the returning member to stop waiting on us, and drop them from our own
    // roster + grace window so we don't keep auto-inviting them on re-entry.
    //
    // @forget (and @waiting, further down) go by telepath whatever channel the
    // @comeback came in on. They are commands, not answers: a client acts on one
    // from whoever it hears it from. On gangpath or say every member's client
    // would hear it, a say directed at one player included, and each follower that
    // heard its leader's @forget would drop that leader. Telepath is the one
    // channel that reaches only the member it is meant for. (The answers in
    // braces, which do nothing to a client that isn't waiting on them, go on the
    // channel the request arrived on.)
    private void DeclineAndForget(string given)
    {
        _wire.Send($"/{given} @forget");
        _party.ForgetReconnectMember(given);
        ReleaseHolds?.Invoke(given, "declined their pickup");
        _log?.Info(LogCategory, $"declined recovery of {given} — sent @forget and dropped them.");
    }

    // ----- @forget teardown (bidirectional) --------------------------

    private void OnForget(RemoteCommandContext ctx)
    {
        string given = GivenName(ctx.Sender);
        if (string.IsNullOrEmpty(given))
        {
            ctx.Refuse("nothing to forget");
            return;
        }

        // If we're mid-recovery for exactly this player, abandon the walk and
        // resume whatever we paused before tearing the relationship down.
        bool wasRecovering = _busy
            && string.Equals(given, _senderGiven, StringComparison.OrdinalIgnoreCase);

        _log?.Info(LogCategory, $"@forget from {ctx.Sender} — dropping them from the party.");
        _party.ForgetReconnectMember(given);
        ReleaseHolds?.Invoke(given, "@forget");
        // If they were our leader, clear the crash-rejoin memory so a later
        // reconnect doesn't keep telepathing @comeback at them.
        ForgetLeaderCallback?.Invoke(given);
        _pendingProbes.Remove(given);
        // Waiting their turn, kept for the end of a train trip, awaited after one,
        // or to be invited on sight: none of it any more.
        _queued.RemoveAll(q => SameName(q.Given, given));
        ForgetTripMember(given);
        _gatedOnLoop.Remove(given);
        _dropLineSeen.Remove(given);
        _gateFetches.RemoveAll(f => SameName(f.Given, given));
        ForgetOnSight(given);

        if (wasRecovering)
        {
            ctx.Reply($"forgetting {given} - resuming");
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
        if (_walker.State is not WalkState.Idle && _walker.Journey is { } journey)
            return new ResumeTarget(ResumeKind.Walker, journey, null);
        return new ResumeTarget(ResumeKind.None, null, null);
    }

    private void StopRunningEngines(string reason)
    {
        // AutoLair.Stop() is gate-clean (clears its UserGate when paused
        // and stops the walker); LoopRunner / Walker Stop() return them
        // to Idle. Stop all three so the recovery walk owns the wire.
        if (_autoLair.IsActive) _autoLair.Stop(reason, willResume: true);
        if (_loopRunner.State is not LoopState.Idle) _loopRunner.Stop(reason, willResume: true);
        if (_walker.State is not WalkState.Idle) _walker.Stop(reason, willResume: true);
    }

    // Members waiting for the recovery in flight to end, oldest first: who, the
    // room they named (none: backtrack) and how to answer them.
    private readonly List<(string Given, RoomKey? Target, Action<string> Reply, bool TripFetch, bool RoomUnconfirmed)> _queued = new();

    // For the bug report: who is waiting behind the recovery in flight.
    public IReadOnlyList<string> QueuedRecoveries => _queued.Select(q => q.Given).ToList();

    private void QueueRecovery(string given, RoomKey? target, Action<string> reply, bool tripFetch, bool roomUnconfirmed)
    {
        int at = _queued.FindIndex(q => string.Equals(q.Given, given, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            // Asked again, perhaps with a room this time: the newer word stands, and
            // a room they name is no longer our guess.
            var was = _queued[at];
            _queued[at] = (given, target ?? was.Target, reply, tripFetch || was.TripFetch,
                target is null ? was.RoomUnconfirmed : roomUnconfirmed);
            return;
        }
        _queued.Add((given, target, reply, tripFetch, roomUnconfirmed && target is not null));
        _log?.Info(LogCategory, $"{given} is left behind too — fetching them after {_senderGiven}");
    }

    // The next member waiting is fetched before the stopped engine is put back,
    // and inherits it. False when nobody is waiting or none of them can be fetched.
    private bool StartNextQueued(ResumeTarget resume)
    {
        while (_queued.Count > 0)
        {
            (string given, RoomKey? target, Action<string> reply, bool tripFetch, bool roomUnconfirmed) = _queued[0];
            _queued.RemoveAt(0);
            BeginRecovery(given, target, reply, resume, tripFetch, roomUnconfirmed);
            if (_busy) return true;
        }
        return false;
    }

    private void ForgetTripMember(string given)
    {
        _leftOnTrip.RemoveAll(l => SameName(l.Given, given));
        _awaitedAfterTrip.Remove(given);
        _lastTripRoll?.Names.Remove(given);
        _owedTripFetch?.Left.RemoveAll(l => SameName(l.Given, given));
    }

    private void ForgetOnSight(string given)
    {
        _inviteOnSight.Remove(given);
        _invitedOnSightAt.Remove(given);
        _lairAnswered.Remove(given);
    }

    private void Resume()
    {
        ResumeTarget r = _resume;
        GoIdle();
        if (StartNextQueued(r)) return;
        switch (r.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                // ResumeAfterDetour, not Start: picking a member back up continues the
                // same hunting session, so the loop-start session-stats reset and
                // party @reset must not fire again.
                if (r.Loop is { } loop) _loopRunner.ResumeAfterDetour(loop);
                break;
            case ResumeKind.Walker:
                if (r.Journey is { } journey) _walker.ResumeJourney(journey);
                break;
        }
    }

    private void ParkAndGoIdle()
    {
        ResumeTarget r = _resume;
        string gaveUpOn = _senderGiven;
        GoIdle();
        // Someone else is waiting: the search goes on for them, and the engine is
        // theirs to put back.
        if (StartNextQueued(r)) return;
        if (r.Kind != ResumeKind.None && !string.IsNullOrEmpty(gaveUpOn))
        {
            _parkedResume = (gaveUpOn, r, _now());
            _log?.Info(LogCategory,
                $"gave up on {gaveUpOn}; keeping {r.Kind} to resume if they @comeback within {ComebackWindow.TotalMinutes:0} min");
        }
    }

    private ResumeTarget? TakeParkedResume(string given)
    {
        if (_parkedResume is not { } p) return null;
        if (!string.Equals(p.Given, given, StringComparison.OrdinalIgnoreCase)) return null;
        if (_now() - p.At > ComebackWindow) { _parkedResume = null; return null; }
        _log?.Info(LogCategory, $"{given} came back after we gave up — recovering, then resuming {p.Resume.Kind}");
        return p.Resume;
    }

    // Reset States: drop every in-flight recovery, pending probe and parked resume
    // so nothing re-issues a walk later. Doesn't stop the walker — the caller does.
    public void Cancel(string reason)
    {
        bool had = _busy || _parkedResume is not null || _crPendingName is not null || _pendingProbes.Count > 0;
        _parkedResume = null;
        _crPendingName = null;
        StopCrFallback();
        _pendingProbes.Clear();
        _queued.Clear();
        _leftOnTrip.Clear();
        _answeredOnTrip.Clear();
        _awaitedAfterTrip.Clear();
        _lastTripRoll = null;
        _owedTripFetch = null;
        _gatedOnLoop.Clear();
        _dropLineSeen.Clear();
        _gateCrossed.Clear();
        _gateFetches.Clear();
        _lastStepGate = null;
        _inviteOnSight.Clear();
        _invitedOnSightAt.Clear();
        _lairAnswered.Clear();
        _askedToJoin.Clear();
        StopJoinFallback();
        GoIdle();
        if (had) _log?.Info(LogCategory, $"recovery state cleared ({reason})");
    }

    private void GoIdle()
    {
        // The pickup is over: a member asked to join who never followed isn't
        // invited from wherever we have walked on to.
        if (_busy) ForgetJoinAsked(_senderGiven);
        _busy = false;
        RecoveringChanged?.Invoke();
        _walkTarget = null;
        _leftBehind = false;
        _okPremature = false;
        _heldWhenAsked = false;
        _tripFetch = false;
        _roomUnconfirmed = false;
        _phase = ComebackPhase.Idle;
        StopFollowTimer();
        _backtrack.Clear();
        _backtrackIndex = 0;
        _rejoinedWhileOff = null;
    }

    // ----- walk driving ----------------------------------------------

    private void BeginWalk(RoomKey room, ComebackPhase phase)
    {
        _phase = phase;
        _walkTarget = room;
        // WalkTo can synchronously raise Finished ("already at
        // destination") which re-enters OnWalkEvent before this returns;
        // _phase is set above so that re-entry is handled correctly.
        if (!_walker.WalkTo(room))
        {
            if (phase == ComebackPhase.WalkingToRoom) NoteRecoveryFailedToReach();
            _reply($"can't reach {room.Map}/{room.Room} - resuming");
            Resume();
        }
    }

    // Count a recovery walk that couldn't reach the member (plan-time no-path or a
    // mid-route gate refusal). Drives the MaxFailedRecoveries backoff in
    // BeginRecovery. A follow-confirm clears the count; only failures to REACH them
    // count — a reach-but-no-follow (FollowWaitWindow) is a different problem and stays
    // neutral.
    private void NoteRecoveryFailedToReach()
    {
        if (string.IsNullOrEmpty(_senderGiven)) return;
        int n = _failedRecoveries.GetValueOrDefault(_senderGiven) + 1;
        _failedRecoveries[_senderGiven] = n;
        _log?.Info(LogCategory,
            $"recovery of {_senderGiven} failed to reach them ({n}/{MaxFailedRecoveries})");
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!_busy)
        {
            // A walk that is over (arrived, failed, or stopped for good): the one
            // pickup it made from an exit was that walk's.
            // A leg a loop or Auto-Lair walked is not a walk of its own.
            bool walkOver = e.Kind is WalkEventKind.Finished or WalkEventKind.Failed
                || (e.Kind == WalkEventKind.Stopped && !e.WillResume);
            if (walkOver && !_autoLair.IsActive && _loopRunner.State is LoopState.Idle)
            {
                _gateFetches.RemoveAll(f => f.Engine == ResumeKind.Walker);
                ForgetGateSteps();
            }
            return;
        }
        // Our own recovery walks are only ever started on an idle walker, so a
        // Stopped mid-recovery — or a Finished for some other room — means the user
        // (or another engine) took the wheel with a walk of their own. Stand down and
        // drop the resume: carrying on would re-issue the next backtrack room after
        // every later walk, and resuming would drag them back onto the walk they
        // just replaced (report: a token mid-walk split the party, and every walk the
        // player set afterwards ended by heading back toward the token room).
        if (_phase is ComebackPhase.WalkingToRoom or ComebackPhase.WalkingBacktrack
            && (e.Kind == WalkEventKind.Stopped
                || (e.Kind == WalkEventKind.Finished && !Equals(e.Destination, _walkTarget))))
        {
            _log?.Info(LogCategory,
                $"recovery of {_senderGiven} called off — our walk was replaced ({e.Kind}: {e.Detail})");
            _parkedResume = null;
            _queued.Clear();
            GoIdle();
            return;
        }
        switch (_phase)
        {
            case ComebackPhase.WalkingToRoom:
                if (e.Kind == WalkEventKind.Finished)
                {
                    if (_roomUnconfirmed && !FollowerHere()) NotWhereTheTripLeftThem();
                    else ReInviteAndAwait();
                }
                else if (e.Kind == WalkEventKind.Failed) { NoteRecoveryFailedToReach(); _reply("path failed - resuming"); Resume(); }
                break;
            case ComebackPhase.WalkingBacktrack:
                if (e.Kind == WalkEventKind.Finished) OnBacktrackArrival();
                else if (e.Kind == WalkEventKind.Failed) { _reply("backtrack path failed - going idle"); ParkAndGoIdle(); }
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
        // A train trip's fetch looks down the whole trail: the return distance
        // doesn't bound it.
        int budget = _tripFetch ? history.Count : Math.Max(1, ReturnDistanceRooms);
        for (int i = 1; i < history.Count && _backtrack.Count < budget; i++)
            _backtrack.Add(history[i]);
    }

    private void StepBacktrack()
    {
        if (_backtrackIndex >= _backtrack.Count)
        {
            _reply("couldn't find you after backtracking - going idle");
            ParkAndGoIdle();
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

    private bool FollowerHere() => InOurRoom(_senderGiven);

    // The room's occupants as last read list that player.
    private bool InOurRoom(string given)
    {
        if (_classifier.Current is not { } obs) return false;
        foreach (RoomEntity entity in obs.Entities)
        {
            if (entity.Kind == EntityKind.Player
                && string.Equals(entity.ResolvedName, given, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ----- re-invite + follow await ----------------------------------

    private void ReInviteAndAwait()
    {
        _phase = ComebackPhase.AwaitingFollow;
        _reply($"{FoundYou} - re-inviting {_senderGiven}");
        AskBack(_senderGiven, removedByAHold: _heldWhenAsked, inviteAfter: InviteDelayWithin(FollowWaitWindow));
        StopFollowTimer();
        // "If leading, wait only" at 0 means no limit, which suits a wait on a
        // member who is with us. One who never answers the re-invite is not (on
        // Stock a member still on our list is told nothing by it), and the walk or
        // loop this recovery stopped must not wait on them for good.
        _followInterval = FollowWaitWindow > TimeSpan.Zero ? FollowWaitWindow : UnlimitedFollowWait;
        _followTimer = _schedule(_followInterval, OnFollowTick);
    }

    // What stands in for "no limit" on the wait for a re-invited member to follow.
    private static readonly TimeSpan UnlimitedFollowWait = TimeSpan.FromSeconds(90);

    // ----- asking a member back: an invite, or on Stock a `join` of theirs -------

    // Stock: how long a member asked to `join` is given to follow before they are
    // invited after all.
    public TimeSpan JoinFirstWait { get; set; } = TimeSpan.FromSeconds(5);

    // Mirrors PartySettings.SendJoinToInvited, as AutoPartyManager.JoinNagEnabled does.
    public bool SendJoinToInvited { get; set; } = true;

    // How long a pickup that waits `followWait` for the follow gives the `join`
    // before the invite. The invite has to be out while the pickup still waits: a
    // member the game took off our list can only come back by it, and a pickup
    // that timed out first left them with nothing. So a wait shorter than two
    // JoinFirstWaits gives the join half of it. At 0 ("If leading, wait only" set
    // to no wait) the invite goes with the @join, as it always went on its own.
    private TimeSpan InviteDelayWithin(TimeSpan followWait)
    {
        if (followWait <= TimeSpan.Zero) return TimeSpan.Zero;
        return followWait < JoinFirstWait + JoinFirstWait ? followWait / 2 : JoinFirstWait;
    }

    // Stock: the members asked to `join` in an invite's place, when each is invited
    // after all if they haven't followed, and whether Auto-Lair's pass asked (no
    // pickup stands with them then, so that invite is only good while they are
    // still in the room with us).
    private readonly Dictionary<string, (DateTimeOffset InviteAt, bool OnSight)> _askedToJoin =
        new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _joinFallbackTimer;

    // The member a recovery is for had a @wait out with us when they asked.
    private bool _heldWhenAsked;

    // For the bug report: who has been asked to join and isn't following yet.
    public IReadOnlyCollection<string> AskedToJoin => _askedToJoin.Keys.ToList();

    // Bring a member back into the party once we stand with them.
    //
    // Paradigm drops a member it leaves behind, so it takes an invite. On Stock a
    // member an exit turned away is still on our list: an invite prints them
    // nothing (the game tells only someone it newly adds), and a `join` of theirs
    // works without one. So their client is told to join, by the @join it already
    // takes from a player it grants "Request invite"; a follower's own client
    // also joins by itself on our "found you" (ComebackRequester), which needs no
    // grant.
    //
    // That a still-listed member's `join` works is read from the Stock DLL and has
    // not been seen in play, and a member the game did take off our list (held,
    // stunned or too heavy when we moved; their @wait says so) is answered "You
    // must be invited first!", which only they see. So the invite is the
    // fallback: at once for a member who signalled a hold, and otherwise when
    // inviteAfter has passed with no follow.
    //
    // The @join is one of the nags Settings → Party "Send @join nags to invited
    // members" governs. Switched off, none is sent here either: the member is
    // invited as before, and on Stock comes back by their own client's join.
    private void AskBack(string given, bool removedByAHold, TimeSpan inviteAfter, bool onSight = false)
    {
        if (!StillOnOurList(given) || removedByAHold)
        {
            _party.Invite(given);
            return;
        }
        if (!SendJoinToInvited)
        {
            _log?.Info(LogCategory,
                $"{given} is still on our list (Stock), and \"Send @join nags to invited members\" is off: no @join is sent, they are invited as before.");
            _party.Invite(given);
            return;
        }
        _wire.Send($"/{given} @join");
        if (inviteAfter <= TimeSpan.Zero)
        {
            _log?.Info(LogCategory,
                $"{given} is still on our list (Stock): told to join by @join, and invited with it since nothing is waited for.");
            _party.Invite(given);
            return;
        }
        _askedToJoin[given] = (_now() + inviteAfter, onSight);
        _log?.Info(LogCategory,
            $"{given} is still on our list (Stock): told to join by @join, no invite sent. If they aren't following in {inviteAfter.TotalSeconds:0.#}s they are invited, as a member off our list would be.");
        ArmJoinFallback();
    }

    // One shot for the earliest invite owed.
    private void ArmJoinFallback()
    {
        StopJoinFallback();
        if (_askedToJoin.Count == 0) return;
        TimeSpan until = _askedToJoin.Values.Min(asked => asked.InviteAt) - _now();
        _joinFallbackTimer = _schedule(until > ShortestTick ? until : ShortestTick, OnJoinFallbackTick);
    }

    private void StopJoinFallback()
    {
        _joinFallbackTimer?.Dispose();
        _joinFallbackTimer = null;
    }

    private static readonly TimeSpan ShortestTick = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MasterOffJoinRecheck = TimeSpan.FromSeconds(1);

    private void ForgetJoinAsked(string given)
    {
        if (_askedToJoin.Remove(given)) ArmJoinFallback();
    }

    // Whoever was asked to join and has not followed in time is invited, as before.
    private void OnJoinFallbackTick()
    {
        _joinFallbackTimer = null;
        if (_askedToJoin.Count == 0) return;
        // Held with everything else: looked at again shortly, until the switch is back.
        if (MasterOff)
        {
            _joinFallbackTimer = _schedule(MasterOffJoinRecheck, OnJoinFallbackTick);
            return;
        }
        DateTimeOffset now = _now();
        foreach (string given in _askedToJoin.Keys.ToArray())
        {
            (DateTimeOffset inviteAt, bool onSight) = _askedToJoin[given];
            if (now < inviteAt) continue;
            _askedToJoin.Remove(given);
            // Auto-Lair walked on meanwhile, or was stopped: an invite from here
            // reaches nobody we stand with. The pass that next finds them asks
            // again, with no cooldown held against it.
            if (onSight && (!_autoLair.IsActive || !InOurRoom(given)))
            {
                _invitedOnSightAt.Remove(given);
                _log?.Info(LogCategory,
                    $"{given} didn't follow after the @join, and {(_autoLair.IsActive ? "is no longer in the room with us" : "Auto-Lair has stopped")}: no invite is sent from here. A pass that finds them asks again.");
                continue;
            }
            _log?.Info(LogCategory,
                $"{given} didn't follow after the @join: inviting them. Either the game told them \"You must be invited first!\" (they were off our list after all), or their client doesn't take our @join.");
            _party.Invite(given);
        }
        ArmJoinFallback();
    }

    private void OnMemberFollowConfirmed(string name)
    {
        // Any successful (re)follow clears the member's failed-recovery count — they
        // rejoined, so the backoff has served its purpose and a future strand starts
        // fresh. Runs even outside an active recovery (e.g. a manual re-invite that
        // succeeds), un-stranding a member we'd previously given up on.
        _failedRecoveries.Remove(GivenName(name));
        // Back with us before their turn came: nobody left to fetch.
        _queued.RemoveAll(q => string.Equals(q.Given, GivenName(name), StringComparison.OrdinalIgnoreCase));
        _gatedOnLoop.Remove(GivenName(name));
        _dropLineSeen.Remove(GivenName(name));
        ForgetTripMember(GivenName(name));
        ForgetOnSight(GivenName(name));
        ForgetJoinAsked(GivenName(name));
        _seenFollowingAt[GivenName(name)] = _now();

        if (!_busy || _phase == ComebackPhase.Idle) return;
        if (!string.Equals(GivenName(name), _senderGiven, StringComparison.OrdinalIgnoreCase)) return;
        // Everything below telepaths or restarts the run. With the master switch
        // off the rejoin is only noted, and settled when it is back on.
        if (MasterOff)
        {
            _rejoinedWhileOff = name;
            return;
        }
        bool midWalk = _phase is ComebackPhase.WalkingToRoom or ComebackPhase.WalkingBacktrack;
        if (midWalk)
        {
            // They are following us again before the walk reached them: they caught
            // up, or were invited by hand. Nobody is left to fetch, and a walk kept
            // going heads back to a room they have since left (reports
            // paradigm-20261007-134824, paradigm-20260929-221642).
            _log?.Info(LogCategory, $"{_senderGiven} is back in the party — recovery walk called off");
            // Our own stop — idle the phase so OnWalkEvent doesn't read it as the
            // user taking over.
            _phase = ComebackPhase.Idle;
            if (_walker.State is not WalkState.Idle) _walker.Stop("comeback: member is back in the party");
        }
        if (_leftBehind)
        {
            // Whatever held them may still hold them: wait the full window for their
            // @ok, and tell them so — a hold that cleared while they were out of the
            // party never sent one.
            // An @ok sent just before they were left behind is distrusted because
            // they then failed to move with us. One who has since come to us while
            // we were still on the way back has moved, so theirs is taken as usual.
            bool distrustOk = _okPremature && !midWalk;
            LeftBehindRejoined?.Invoke(_senderGiven, distrustOk);
            if (distrustOk)
            {
                // Their @ok came a moment before they were left behind, so another
                // one proves nothing — sit out the whole window instead.
                _reply($"{GotYou} - your last @ok was too early, waiting the full {FollowWaitWindow.TotalSeconds:0}s");
            }
            else
            {
                // By telepath: see DeclineAndForget.
                _wire.Send($"/{_senderGiven} @waiting");
                _reply($"{GotYou} - waiting for your @ok");
            }
        }
        else
        {
            _reply($"{GotYou} - resuming");
        }
        Resume();
    }

    // The member a recovery was for, seen following again while the master switch
    // was off.
    private string? _rejoinedWhileOff;

    // The master switch is back on. A recovery under way when it went off was
    // held, not dropped: its walk froze with every other, and its wait for the
    // follow did not time out. Its walk resumes with the rest; this settles a
    // rejoin that was seen meanwhile.
    //
    // A train trip that ended meanwhile fetches whom it left now, by the rule a
    // @comeback kept for the switch is replayed by: only while it is still fresh
    // (ComebackWindow), since past that the party has long moved on.
    public void SettleAfterMasterSwitch()
    {
        if (_rejoinedWhileOff is { } name)
        {
            _rejoinedWhileOff = null;
            OnMemberFollowConfirmed(name);
        }
        if (_owedTripFetch is not { } owed) return;
        _owedTripFetch = null;
        if (owed.Left.Count == 0) return;
        TimeSpan age = _now() - owed.At;
        if (age > ComebackWindow)
        {
            _log?.Info(LogCategory,
                $"master switch back on — the train trip that left {string.Join(", ", owed.Left.Select(l => l.Given))} ended {age.TotalMinutes:0.#} min ago, past the {ComebackWindow.TotalMinutes:0.#} min @comeback window: not going back on our own.");
            return;
        }
        FetchLeftOnTrip(owed.Left);
    }

    private void StopFollowTimer()
    {
        _followTimer?.Dispose();
        _followTimer = null;
    }

    private void OnFollowTick()
    {
        _followTimer = _schedule(_followInterval, OnFollowTick);
        OnFollowTimeout();
    }

    private void OnFollowTimeout()
    {
        if (!_busy || _phase != ComebackPhase.AwaitingFollow) return;
        // The timer keeps ticking, so the wait runs out at its first tick after
        // the master switch is back on.
        if (MasterOff) return;
        _reply("follow timed out - resuming anyway");
        Resume();
    }

    // Wraps a telepath reply to the probed member in the same { } meta-braces the
    // engine's SendReply uses, so path-B narration reads like any @-command answer.
    private Action<string> TelepathReply(string given) => msg => _wire.Send($"/{given} {{{WireText.Ascii(msg)}}}");

    // On our list as following us (an [Invited] row is on the list and isn't).
    private bool MemberFollowing(string given)
    {
        foreach (PartyMember m in _party.State.Members)
            if (!m.IsSelf && !m.IsInvited && GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

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

    // A walk is kept as its journey, so the walk picked back up is planned by the
    // rules the interrupted one had (the route its card showed).
    private readonly record struct ResumeTarget(ResumeKind Kind, WalkJourney? Journey, Loop? Loop);
}
