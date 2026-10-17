using System.Collections.Concurrent;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Trust-by-default room-tracking FSM. Maintains RoomState from the four signals
// every player session produces: outgoing move commands, observed room
// displays, observed move refusals, and the user's manual "I am here" override.
//
// State semantics:
//   - Unknown — fresh tracker, no observation yet.
//   - Confirmed — current room is trusted.
//   - Pending — one or more moves sent, awaiting confirmation.
//   - Suspect — observation didn't line up; current room preserved as best
//     guess; counter incremented. Internal-only, no UI churn.
//   - Lost — replay-from-last-Confirmed failed; user must manually pick or wait
//     for a confirming observation.
//
// Recovery: a single tier — replay the persisted CharacterProfile.RecentSteps
// from the last Confirmed room through the graph; if the endpoint matches the
// current observation, we Confirm there. No fuzzy footprint matching.
//
// Persistence: CharacterProfile.LastKnownRoom is written ONLY on strict-1-of-1
// Confirmed transitions (FindCandidates(name, exits).Count == 1) and on the
// user's manual locate. Predicted-neighbour / null-name-learned /
// replay-recovery Confirmed transitions update in-memory state but don't
// overwrite the on-disk anchor — the existing anchor is a stronger trust signal
// than any deduction. This is the contract the engine-level recovery gate
// relies on for tier-3 backtrack: the persisted anchor is always something we
// KNEW, not something we deduced. Every NoteMoveSent appends a step. The profile
// flushes to disk on the normal save cycle (app close, settings Apply, explicit
// save).
public sealed class RoomTracker
{
    // Maximum back-to-back moves we'll track in flight. Anything beyond this is
    // dropped to keep the queue bounded — a sustained observation drought past
    // 15 moves is a parser problem, not a tracking one.
    private const int PendingQueueCap = 15;

    // Confirmed-position rolling history retained for debugging / recovery.
    // Capped to keep memory bounded.
    private const int HistoryCap = 50;

    // Strike count at which the next mismatch triggers replay-recovery instead
    // of incrementing further.
    private const int SuspectStrikeLimit = 3;

    // Depth ceiling for the passive grid footprint. Generous — the passive
    // re-localiser keeps narrowing as long as the player keeps moving; a unique
    // convergence, not the ceiling, is what ends it.
    private const int PassiveGridDepthCeiling = 128;

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;
    private readonly ConcurrentQueue<PendingMove> _pending = new();

    // True while one or more move commands are on the wire awaiting their room
    // confirm — the player/engine hasn't settled in the current room yet. AutoSearch
    // reads this so a queued burst (n;e;n) doesn't try to `sea` transit rooms it has
    // already passed; the search fires only in the room movement actually settles in.
    public bool HasQueuedMoves => !_pending.IsEmpty;
    private readonly LinkedList<HistoryEntry> _history = new();
    private readonly List<DirectionDto> _recentSteps = new();
    // The room the buffered replay trail is anchored at — where we stood when the FIRST
    // still-buffered step was sent. TryReplayRecover projects _recentSteps from here, not
    // from _history.First (the newest CONFIRMED room), which drifts several rooms ahead of
    // the trail during a run of ambiguous non-strict confirms and makes replay abort.
    private RoomKey? _stepsAnchor;

    // Passive, engine-independent grid re-localiser. When we're stuck Lost /
    // ambiguous-Suspect in a block of identically-named rooms (the Soldiers'
    // Quarters grid) and NO navigation engine is attached, EngineRecoveryGate's
    // tier-2 forward localiser — which would normally narrow "which identical room
    // am I in?" — never runs (it early-returns with no engine). This is the same
    // FootprintMatcher SLAM accumulator, driven passively off the moves the player
    // is already making by hand (or being dragged through): each (move, observation)
    // pair drops candidates until one remains, then we re-anchor there WITHOUT
    // sending a single command. Gated OFF while an engine drives (_isEngineAttached)
    // so only one localiser re-anchors; it refuses to guess — an ambiguous set that
    // never narrows to one just stays Lost.
    private readonly FootprintMatcher _passiveGrid;
    private bool _passiveGridActive;
    // The last cardinal move not yet folded into _passiveGrid — set on every cardinal
    // step, consumed once when the next observation lands. Cleared on seed (the seed
    // already reflects where we stand). Text-exit (cardinal-less) moves don't advance
    // the passive footprint.
    private Direction? _passivePendingMove;
    // Reports whether a navigation engine is currently attached to the recovery gate.
    // Null before wiring → treated as "not attached", so the passive path
    // runs. Wired by AppServices to EngineRecoveryGate.HasAttachedEngine.
    private Func<bool>? _isEngineAttached;

    // Reports whether we're afraid. Fear runs us through the room's obvious exits
    // with no command echo, so while it's on an unexpected display is read as a
    // move to a neighbour (TryFearMove). Null before wiring → never.
    private Func<bool>? _isFeared;

    // Profile the tracker is currently writing into. Set by Hydrate; cleared by
    // OnProfileClosed. When null, persistence operations are no-ops — the
    // tracker still runs in memory but doesn't touch any profile.
    private CharacterProfile? _profile;

    // Optional live-inventory snapshot provider. When set, NoteDeath records the
    // deathpile contents (worn + carried items) on the death record. Bound by
    // AppServices once the InventoryManager exists; null before
    // wiring, in which case death records simply carry no item lists.
    private Func<InventorySnapshot>? _inventorySnapshot;

    // Look-direction suppression deadline. While the wall clock is at or before
    // this timestamp, the next NoteRoomObserved call is treated as a peek (room
    // preview from a look <dir> command) and discarded. The 3-second window
    // auto-clears even if no peek display arrives, so the suppression can't eat
    // a future genuine observation.
    private DateTimeOffset? _suppressObservationUntil;
    private const int LookSuppressWindowMs = 3000;
    // When that look went out, so a move sent before it can be told from one sent after.
    private DateTimeOffset _lookSentAt;

    // Wall-clock time the most recent move command was enqueued, or null before
    // the first move this session. Lets observers tell a post-move room display
    // ("the room we just walked into") apart from a pre-move stale observation —
    // the RoomEntityClassifier uses it to avoid wiping the new room's
    // freshly-parsed occupants on the move-confirming transition (the occupants
    // line arrives before the exits line that confirms the move).
    public DateTimeOffset? LastMoveSentAt { get; private set; }

    // Whether that move was a manual one: it reached us without a movement engine
    // announcing it and wasn't a party leader's drag. That is a keystroke, and
    // equally a macro, a trigger or a relayed command, which arrive the same way
    // and pause navigation the same way (ManualMoveObserved). A follower left
    // behind by one is the player's to fetch, however many holds the engines
    // happen to be under at the time.
    public bool LastMoveWasManual { get; private set; }

    // A move this client sent, typed or an engine's, has had no answer yet. A
    // party leader's drag waits in the same queue but nobody here asked for it, so
    // a refusal that arrives with only drags queued answers none of ours.
    public bool OwnMoveInFlight
    {
        get
        {
            foreach (PendingMove move in _pending)
                if (!move.IsFollowDrag) return true;
            return false;
        }
    }

    // Diagnostics: the most recent server move-echo the tracker recorded (the
    // command + when), or null if none this session. The echo gate confirms a
    // move's landing only once its command has been echoed, so a bug report of a
    // "walker stuck Pending in a same-named room" shows here whether that gating
    // echo actually arrived — the tell for a missed-echo stall.
    public (string Command, DateTimeOffset At)? LastInboundMoveEcho => _lastInboundEcho;

    // Whether the statline has been seen to echo a command this session: the echo
    // then decides a move's arrival, and timing only when it hasn't.
    public bool MoveEchoSeen => _everReceivedEcho;

    // The display held as possibly a re-look of the room being left, with no echo
    // to say otherwise, and the move it was held for (TryTakeHeldArrival).
    public string? HeldDisplay => _heldDisplay is { } held && _pending.TryPeek(out PendingMove head) && head == held.Head
        ? $"'{held.Display.Name}' for move {DescribeMove(held.Head)} sent {held.Head.SentAt.ToLocalTime():HH:mm:ss}"
        : null;

    // True while we're standing in a room too dark to display its name or exits
    // ("The room is very dark..." / "The room is pitch black..."). Set by
    // NoteDarkRoomEntered on the dark line, cleared the moment a normal room
    // display parses (NoteRoomObserved), the graph reloads, or we die. Every
    // writer runs on the marshalled MessageRouter / observer thread (the UI
    // thread in prod), so a plain auto-prop needs no volatile / lock. Consumers
    // (DarkRoomCombatWatcher) read it to know a mob revealed only by its
    // dark-cyan attack line — with no "Also here:" to list it — should be
    // engaged.
    public bool IsInDarkRoom { get; private set; }

    // True while we stand in a room entered blind: the move went through and the
    // game printed only "You are blind.", so nothing of the room was displayed. Set
    // by NoteBlindMove's advance and cleared with IsInDarkRoom (a normal display, a
    // graph reload, a death). With IsInDarkRoom it lets a consumer that reads the
    // room's display on arrival (death recovery's floor read) tell "nothing was
    // shown" from "shown, and empty".
    public bool EnteredBlind { get; private set; }

    // The last observation we fully processed (name + exit set) and when. Used
    // to tell a passive redisplay of the same room — an Enter, a cash-on-ground
    // notice, a party arrival echo — apart from a genuine failed-move mismatch,
    // so a stationary player can't accrue Suspect strikes while standing still.
    private RoomObservation? _lastObservation;
    private DateTimeOffset _lastObservationAt;

    // The state class itself — bound by the UI, mutated only by this tracker.
    public RoomState State { get; } = new();

    // Fires after every transition that changes RoomState.CurrentRoom or
    // RoomState.Confidence. Carries the full state snapshot so handlers can
    // branch on both fields without racing.
    public event Action<RoomTransition>? StateChanged;

    // Fired the first time we learn a real name for a room that shipped without
    // one (typical of map-15 ganghouse rooms in the 1.x MDB exports).
    // Subscribers — the main window in particular — ask the user whether to keep
    // the learned name for later sessions (LearnedRoomNameStore).
    public event Action<NameLearnedEvent>? NameLearned;

    // Fired from NoteDeath for BOTH death phrasings ("You now have N lives
    // remaining." and the miracle-save "You have N lives left."). This is the one
    // universal local-death signal — DeathLineWatcher's PlayerDied only fires on
    // the "slain by" line, which a miracle-save death never prints, so movement /
    // combat quiescence that hangs off "slain by" alone silently misses every
    // miracle death (the loop kept rerouting out of the graveyard; the combat
    // engine re-attacked a stale hostile). Consumers that must react to ANY death
    // subscribe here. Raised synchronously inside NoteDeath, before the respawn
    // room confirms, so a loop-stop lands ahead of the graveyard's recovery-reroute.
    public event Action? PlayerDeathObserved;

    // The room the last witnessed death happened in, for PlayerDeathObserved's
    // listeners: by the time it is raised the tracker holds no room. Null when the
    // room wasn't known.
    public RoomKey? LastDeathRoom { get; private set; }

    // Whether the map was sure of that room when the death came. With a move in
    // flight, or a position in doubt, the room it held is only where the
    // character was last known to be.
    public bool LastDeathRoomConfirmed { get; private set; }

    // Whether the last death recorded was an arena death, which takes no item,
    // coin or key: a witnessed one (NoteDeath's tookNothing) or one worked out
    // after the fact (UnwitnessedDeath.NothingLost). For the listeners of the
    // death events: what they do about a pile, a purse or a life spent doesn't
    // apply to one.
    public bool LastDeathTookNothing { get; private set; }

    // The game-data set whose rooms the tracker places the character in, the set a
    // death recorded now is stamped with (DeathRecord.GameData).
    public string? RoomsGameData => _graph.ActiveSet;

    // The exit the one move in flight is crossing: what a refusal of that move is a
    // refusal of. Null with no move in flight, with more than one queued (the one
    // refused isn't then the one out of this room), or when it maps to no exit.
    public (RoomKey From, RoomExit Exit)? ExitInFlight()
    {
        if (State.Confidence != RoomConfidence.Pending || State.CurrentRoom is not { } source) return null;
        if (_pending.Count != 1 || !_pending.TryPeek(out PendingMove head)) return null;
        return TryResolvePendingExit(source, head, out RoomExit exit) ? (source.Key, exit) : null;
    }

    // Fired from NoteUnwitnessedDeath: a death found out on the way back into the
    // game, long after it happened. Its own event because most of what
    // PlayerDeathObserved sets off is about the moment of dying (the room just
    // left, the conditions and party of that moment, the respawn display still to
    // come), and here that moment is past: the character already stands in the
    // room the game put it in, and what the client holds about that room, its
    // conditions and its pack was read after the death. Subscribers are the few
    // things a death still owes then: stopping the movement engines, the self
    // buffs it wiped, the recovery list.
    //
    // Raised only for a death worked out at the login, before anything has run
    // (UnwitnessedDeath.AtEntry). One worked out later finds a character that has
    // played on since, and gets its record and no more.
    public event Action? PlayerDeathInferred;

    // Fired from NoteUnwitnessedDeath for every record it makes, at the login or
    // later: the death history has a new row, and a life was spent.
    public event Action? UnwitnessedDeathRecorded;

    // Fires when a MANUAL movement step is observed — a cardinal or text-exit command
    // the user typed that did NOT match a walker/loop echo claim (so the engine didn't
    // send it). Consumers pause navigation so the automation never fights a hand-driven
    // step. The engines' own moves are echo-claimed and never fire this. Carries the
    // command as typed, so the pause can say what caused it.
    public event Action<string>? ManualMoveObserved;

    // The server refused the move just sent ("There is no exit in that direction!" and
    // the like) — raised after the tracker has un-counted it.
    public event Action? MoveBlocked;

    // A sent or dragged move landed in the room it predicted, shown by that room's
    // own display. Proof the character can move, which a consumer holding a stale
    // "held" condition uses to drop it.
    public event Action? MoveConfirmed;

    // A room script refused the typed exit command in flight — raised BEFORE the
    // revert, so a consumer that retries the command on its own (the walker's greet
    // re-ask) can stand down before the revert's transition reaches it.
    public event Action? CommandMoveRefused;

    // Optional authoritative-position resync hook (Paradigm `rm`). Invoked when the
    // tracker drops to Suspect on an AMBIGUOUS observation it can't self-resolve —
    // e.g. a forced, non-directional transport (a boat disembark, a teleport-trap
    // drag) into a duplicated-name room. The engine recovery gate already asks for a
    // resync on a mid-walk mismatch, but it no-ops when no engine is attached — which
    // is exactly the case during a manual boat ride, leaving the map stranded until
    // the user hand-types `rm` (report paradigm-20260827-081044). AppServices wires
    // this so it only fires in that engine-less gap; returns true when a resync was
    // requested (false on stock realms / when throttled), and is otherwise a no-op.
    public Func<string, bool>? RequestAuthoritativeResync { get; set; }

    private readonly Func<DateTimeOffset> _now;

    public RoomTracker(RoomGraphManager graph) : this(graph, log: null) { }

    public RoomTracker(RoomGraphManager graph, LogService? log, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _passiveGrid = new FootprintMatcher(
            probeHop: (from, dir) => GraphFootprintProbes.Hop(_graph, from, dir),
            matchesObservation: (key, obs) => GraphFootprintProbes.Matches(_graph, key, obs),
            log: log,
            depthCeiling: PassiveGridDepthCeiling);
        State.LastUpdatedAt = _now();
    }

    // Wired by AppServices so the passive grid re-localiser stands down while an
    // engine drives (EngineRecoveryGate.HasAttachedEngine). Left unset in tests /
    // headless → the passive path is treated as un-gated (no engine present).
    public void SetEngineAttachedProbe(Func<bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _isEngineAttached = probe;
    }

    // Wired by AppServices to ConditionTracker.IsFeared.
    public void SetFearProbe(Func<bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _isFeared = probe;
    }

    // Reports whether we have an item — carried, worn or on the key ring — or null
    // while the inventory hasn't been read. Settles which landing an item-gated
    // cast-on-walk teleport takes (InferCastLanding). Unset before
    // wiring → unknown.
    private Func<int, bool?>? _holdsItem;

    public void SetItemHeldProbe(Func<int, bool?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _holdsItem = probe;
    }

    // Whether we have an item, by that probe: null while the inventory is unread.
    public bool? HoldsItem(int itemId) => _holdsItem?.Invoke(itemId);

    // Diagnostics: the last step through an exit whose spell teleported us on —
    // the room it left, the room we were put in, and what that choice went by.
    // Null until one is crossed this session.
    public (RoomKey From, RoomKey Landing, string Basis, DateTimeOffset At)? LastCastLanding { get; private set; }

    // Snapshot of the rolling confirmed-position history, newest-first. [0] is
    // the most recently confirmed room (typically the current room); [1] the one
    // before it, and so on, capped at the internal history window — enough to
    // walk back along the path just taken.
    public IReadOnlyList<RoomKey> GetHistory()
    {
        List<RoomKey> snapshot = new(_history.Count);
        foreach (HistoryEntry entry in _history)
            snapshot.Add(entry.Room);
        return snapshot;
    }

    // Server reported "There is no exit in that direction!" for the last
    // attempted move. Strong signal that the tracker's model of the current room
    // may be wrong — if we were Confirmed, demote to Suspect so the next room
    // observation runs through candidate search and re-anchors us if a unique
    // match exists.
    //
    // The server doesn't tell us WHICH direction failed in the message itself,
    // only that the most recent attempt did. We don't have a reliable "what
    // direction did the user just type" hook (the OutboundMovementObserver
    // announces cardinal sends but the failed direction may be one of several
    // pending in the queue). So we conservatively demote on any direction-failed
    // reply while Confirmed — the next observation resolves cheaply via
    // candidate search.
    public void NoteDirectionFailed(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (State.Confidence != RoomConfidence.Confirmed) return;
        if (State.CurrentRoom is null) return;
        // Drop the oldest in-flight cardinal — server rejected it. If
        // there are no pending moves, leave the queue alone.
        _pending.TryDequeue(out _);
        EnterSuspect(when, "direction failed reply observed");
    }

    // ----- profile hydrate / save -------------------------------------

    // Adopt the supplied profile as our persistence target and seed state from
    // its CharacterProfile.LastKnownRoom + CharacterProfile.RecentSteps. Called
    // by AppServices on ProfileService.ProfileLoaded. Idempotent — calling twice
    // with the same profile reuses the seed.
    //
    // Seeding strategy: if LastKnownRoom resolves to a room in the active graph,
    // we land Confirmed there and prime the _recentSteps list with the persisted
    // entries (so a subsequent failed observation triggers replay). If the room
    // can't be resolved (stale profile, different game-data set, graph not yet
    // loaded), the tracker stays Unknown and the next observation lands
    // normally.
    public void Hydrate(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;

        if (profile.LastKnownRoom is not { } persisted) return;
        Room? room = _graph.GetRoom(new RoomKey(persisted.Map, persisted.Room));
        if (room is null)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Hydrate: LastKnownRoom {persisted.Map}/{persisted.Room} not in active graph; staying Unknown.");
            return;
        }

        _recentSteps.Clear();
        if (profile.RecentSteps is { } steps) _recentSteps.AddRange(steps);
        // The persisted steps project from the room we're hydrating as Confirmed.
        _stepsAnchor = room.Key;

        // Set Confirmed without writing back to the profile (we just
        // read from it). Bypass the SetRoom persistence path so we
        // don't immediately wipe RecentSteps before they're useful.
        Room? prev = State.CurrentRoom;
        RoomConfidence prevConf = State.Confidence;
        State.CurrentRoom = room;
        State.Confidence = RoomConfidence.Confirmed;
        State.SuspectStrikes = 0;
        State.LastUpdatedAt = _now();
        PushHistory(room.Key, State.LastUpdatedAt);
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Hydrate: Confirmed at {room.Name} {room.Key} with {_recentSteps.Count} pending replay steps.");
        RaiseStateChanged(prevConf, RoomConfidence.Confirmed, prev, room);
    }

    // Flush the CURRENT confirmed room as the on-disk anchor at profile-save time
    // (wired to ProfileService.ProfileSaving, so it covers the save-on-close). The
    // mid-session SetRoom path deliberately keeps a trusted strict anchor over a
    // predicted-neighbour deduction while playing — a shaky prediction must not
    // poison the recovery anchor. But at save/close we want the NEXT session to
    // hydrate where the player actually is: without this, a run of predicted moves
    // through same-named rooms (Paradigm's Graveyard has ~100 identical "Graveyard"
    // rooms, so every move confirms by prediction, never a 1-of-1 match) leaves the
    // anchor frozen at the last `rm` and reload lands there instead of the real
    // position. Writes the live position as the anchor and clears the replay tail
    // (the anchor now IS the current room — nothing left to replay). No-op unless
    // the position is Confirmed; a Suspect/Unknown view leaves the existing anchor
    // untouched so we never overwrite a good anchor with a guess.
    public void PersistCurrentRoomForSave()
    {
        if (_profile is null) return;
        if (State.Confidence != RoomConfidence.Confirmed) return;
        if (State.CurrentRoom is not { } room) return;
        PersistConfirmedAnchor(room, State.LastUpdatedAt);
    }

    // Called when the profile unloads. Detaches the persistence target.
    public void OnProfileClosed()
    {
        _profile = null;
        ClearPassiveGrid();
    }

    // Bind the live inventory snapshot provider so NoteDeath can capture the
    // deathpile contents onto the death record. Set by AppServices once the
    // InventoryManager is built; optional — unbound, death records carry no item
    // lists.
    public void AttachInventorySnapshot(Func<InventorySnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inventorySnapshot = provider;
    }

    // ----- inputs -----------------------------------------------------

    // The walker (or any other engine caller that just sent a move) reports the
    // direction. The tracker enqueues a pending move and prepares to validate
    // against the next observation. Because the engine's bytes echo back through
    // SendUserInput → OutboundMovementObserver → NoteMoveSentByObserver, this
    // direct call also arms a consume-once echo claim so that echo is dropped
    // rather than enqueued as a phantom second move.
    public void NoteMoveSent(Direction direction, DateTimeOffset? whenUtc = null)
        => NoteMoveSentCore(direction, isEngineAnnouncement: true, isFollowDrag: false,
                            whenUtc ?? _now());

    // A party follower dragged one room in the leader's direction (FollowMoveObserver,
    // off the " -- Following your Party leader <dir> -- " line). Predicts like a
    // cardinal move, but the pending move is flagged IsFollowDrag so its arrival is
    // never mistaken for a passive re-look (see ReconcileFromPending): the game drags
    // a follower with no byte round-trip, so the confirming room display lands almost
    // instantly — the very timing the re-look guard reads as "too fast to be a real
    // move." No echo claim: a follower sends no movement bytes, so there is nothing to
    // consume back.
    public void NoteFollowMove(Direction direction, DateTimeOffset? whenUtc = null)
        => NoteMoveSentCore(direction, isEngineAnnouncement: false, isFollowDrag: true,
                            whenUtc ?? _now());

    private void NoteMoveSentCore(Direction direction, bool isEngineAnnouncement, bool isFollowDrag,
                                  DateTimeOffset when)
    {
        if (isEngineAnnouncement)
        {
            _cardinalEchoClaim = (direction, when + EchoClaimExpiry);
        }
        LastMoveWasManual = !isEngineAnnouncement && !isFollowDrag;
        SettleUnshownCastDrag(when);
        EnqueuePending(isFollowDrag
            ? PendingMove.FromFollowDrag(direction, when)
            : PendingMove.FromDirection(direction, when));
        AppendStep(new DirectionDto(direction));
        _passivePendingMove = direction;

        if (State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending)
        {
            SetConfidence(RoomConfidence.Pending, when, $"move {direction} sent");
        }
        ArmCastDisplayWait();
        // From Unknown / Suspect / Lost we still enqueue and persist
        // the step — replay needs the full step record — but we don't
        // flip to Pending because we don't have a confirmed anchor to
        // hang the prediction on.
    }

    // Echo-aware overload for OutboundMovementObserver. The engine's own bytes
    // echo back here after the walker / loop-runner already called NoteMoveSent
    // directly. Consume the matching echo claim (armed by that direct call) and
    // drop this second announcement. The claim is consumed exactly once and is
    // matched on direction alone, NOT on a wall-clock window: a synchronous map
    // re-root on entering a new area can stall the byte round-trip well past any
    // fixed millisecond budget, and the old time-window let that late echo slip
    // through as a phantom move that wedged the pending queue and stalled the
    // walk. A generous expiry still bounds an orphaned claim (a refused move
    // whose echo never comes) so it can't later swallow an unrelated manual step.
    // Manual cardinal typing (no engine call, so no claim) lands as a real move.
    public void NoteMoveSentByObserver(Direction direction, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (_cardinalEchoClaim is { Dir: var claimDir, ExpiresAt: var expiresAt }
            && claimDir == direction
            && when < expiresAt)
        {
            _cardinalEchoClaim = null;
            _log?.Log(LogSeverity.Debug, "RoomTracker",
                $"Consumed engine echo of move {direction}; not re-enqueued.");
            return;
        }
        NoteMoveSentCore(direction, isEngineAnnouncement: false, isFollowDrag: false, when);
        ManualMoveObserved?.Invoke(direction.ToToken());
    }

    // Echo-aware overload for OutboundMovementObserver's text-exit path. Mirrors
    // the cardinal claim above. SpecialExitDispatch (walker / loop-runner)
    // announces a text exit WITH its resolved cardinal, then pumps the bytes;
    // those bytes flow back through SendUserInput → the observer, which would
    // otherwise enqueue a SECOND, cardinal-less pending move for the same
    // physical step. That phantom keeps the pending queue non-empty after the
    // walker has already landed, holding the tracker in Pending and stalling the
    // walk until the next observation flushes it. Consume the claim armed by the
    // engine's direct call to drop the echo. Manual text typing (no engine call)
    // lands as a real announcement.
    public void NoteMoveSentByObserver(string command, DateTimeOffset? whenUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        DateTimeOffset when = whenUtc ?? _now();
        if (_textEchoClaim is { Command: var claimCmd, ExpiresAt: var expiresAt }
            && string.Equals(claimCmd, command, StringComparison.OrdinalIgnoreCase)
            && when < expiresAt)
        {
            _textEchoClaim = null;
            _log?.Log(LogSeverity.Debug, "RoomTracker",
                $"Consumed engine echo of move '{command}'; not re-enqueued.");
            return;
        }
        NoteMoveSentCore(command, cardinal: null, isEngineAnnouncement: false, when);
        if (_engineCommandClaim is { Command: var engineCmd, ExpiresAt: var engineExpiry }
            && string.Equals(engineCmd, command, StringComparison.OrdinalIgnoreCase)
            && when < engineExpiry)
        {
            _engineCommandClaim = null;
            LastMoveWasManual = false;
            _log?.Log(LogSeverity.Debug, "RoomTracker",
                $"Move '{command}' was an engine's own command; not a typed move.");
            return;
        }
        ManualMoveObserved?.Invoke(command);
    }

    private (Direction Dir, DateTimeOffset ExpiresAt)? _cardinalEchoClaim;
    private (string Command, DateTimeOffset ExpiresAt)? _textEchoClaim;
    private (string Command, DateTimeOffset ExpiresAt)? _engineCommandClaim;
    // A move's bytes normally echo back within a few milliseconds, but a
    // synchronous map re-root on entering a new area can stall the round-trip;
    // the expiry only has to outlast that worst-case pump, while staying short
    // enough that an orphaned claim (echo never arrived) can't shadow a later
    // manual move of the same direction.
    private static readonly TimeSpan EchoClaimExpiry = TimeSpan.FromSeconds(2);

    // A move the engine sends, by its command text, so a text exit (e.g. "go
    // path") that maps to no Direction is tracked like a cardinal.
    public void NoteMoveSent(string command, Direction? cardinal = null, DateTimeOffset? whenUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        NoteMoveSentCore(command, cardinal, isEngineAnnouncement: true, whenUtc ?? _now());
    }

    // An engine command that is NOT a move but whose wording collides with the
    // text-exit verbs — a multi-action prerequisite like `step tile`, `rub
    // bloodstone orb` or `pull lever`, sent before the cardinal that actually
    // crosses. Claims the echo so OutboundMovementObserver drops it, WITHOUT
    // enqueuing a pending move: the action doesn't relocate the player, the
    // following cardinal does, and a phantom pending move here both stalls the
    // tracker and (via ManualMoveObserved) makes navigation pause itself as though
    // the user had taken over — the walk sent `step tile`, read its own echo as a
    // hand-typed move, and parked mid-detour (report paradigm-20260911-100708).
    public void NoteAuxCommandSent(string command, DateTimeOffset? whenUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        DateTimeOffset when = whenUtc ?? _now();
        _textEchoClaim = (command, when + EchoClaimExpiry);
    }

    // A command an engine sends on the user's behalf that may or may not be a move —
    // a loop waypoint's command. If its wording reads as a text exit it is tracked as
    // a move like any other, but it isn't the user taking over, so it raises no
    // ManualMoveObserved (report paradigm-20261005-201859: a loop's own `go path`
    // paused the loop as typed).
    public void NoteEngineCommandSent(string command, DateTimeOffset? whenUtc = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        _engineCommandClaim = (command, (whenUtc ?? _now()) + EchoClaimExpiry);
    }

    private void NoteMoveSentCore(string command, Direction? cardinal, bool isEngineAnnouncement, DateTimeOffset when)
    {
        if (isEngineAnnouncement)
        {
            _textEchoClaim = (command, when + EchoClaimExpiry);
        }
        LastMoveWasManual = !isEngineAnnouncement;
        SettleUnshownCastDrag(when);
        EnqueuePending(new PendingMove(cardinal, command, when));
        AppendStep(new DirectionDto(cardinal, command));
        // Only a cardinal advances the passive footprint. A text-exit ("go path")
        // carries no direction to hop the candidate set through, so clear the pending
        // move (null) — the next observation then skips a passive step rather than
        // folding it under a stale, wrong direction.
        _passivePendingMove = cardinal;

        if (State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending)
        {
            SetConfidence(RoomConfidence.Pending, when, $"move '{command}' sent");
        }
        ArmCastDisplayWait();
    }

    // The user typed a peek command (look <dir> or equivalent). Arm the
    // suppression flag so the next room display is treated as a preview and
    // dropped instead of being parsed as a move outcome. The flag auto-clears
    // after LookSuppressWindowMs.
    public void NoteLookSent(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        _suppressObservationUntil = when.AddMilliseconds(LookSuppressWindowMs);
        _lookSentAt = when;
        _log?.Log(LogSeverity.Info, "RoomTracker",
            "Look-direction sent — next observation will be ignored as a peek.");
    }

    // The server starved the room display with a darkness line ("The room is
    // very dark..." / "The room is pitch black...") instead of a name + exits.
    // A dark room never fires NoteRoomObserved, so the usual name+exits move
    // confirmation can't run — but a dark line carries its own signal: the game
    // prints an explicit refusal for EVERY blocked move (see the bonk mechanic
    // in GAME_MECHANICS.md), so a sent move that yields a dark line instead of a
    // bonk means we traversed. Flag the darkness (so combat can still engage a
    // mob revealed only by its attack line), then, if a move is pending and its
    // graph edge resolves to a mapped neighbour, advance there — never as a
    // strict anchor, since this is a pure prediction with no name/exit
    // confirmation. An unmapped edge (dark corridor off the graph) just holds the
    // last anchor rather than fabricating a landing.
    public void NoteDarkRoomEntered(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();

        // A look-direction peek into an adjacent dark room renders the same
        // "can't see" line while we stand in a lit room. Drop it as a preview,
        // exactly like NoteRoomObserved drops a peeked room display. Consume the
        // window here — a peeked dark room prints no exits line to consume it
        // later — so the flag can't linger and eat a real move's outcome.
        if (_suppressObservationUntil is { } until)
        {
            _suppressObservationUntil = null;
            if (when <= until)
            {
                _log?.Log(LogSeverity.Info, "RoomTracker",
                    "Dropped peeked dark-room line (look-direction preview).");
                return;
            }
            // window expired — fall through and treat as our own dark room
        }

        // Log only the transition INTO darkness, not every dark redisplay (a
        // dark room can re-emit its "can't see" line each round). This one line
        // explains both why the map stops getting name/exit updates and why
        // combat now leans on attack-line detection, so it stays at Info.
        if (!IsInDarkRoom)
            _log?.Log(LogSeverity.Info, "RoomTracker",
                "Entered a dark room — no name/exits shown; position inferred from moves, combat from attack lines.");
        IsInDarkRoom = true;

        if (TakeCastLandingLine(when, "is too dark to show")) return;

        // Only a pending move can be confirmed by the dark line. A dark display
        // while Confirmed (a passive re-render, standing still) or with nothing
        // in flight carries nothing to advance on.
        if (State.Confidence != RoomConfidence.Pending) return;
        if (State.CurrentRoom is not { } source) return;
        if (!_pending.TryPeek(out PendingMove head)) return;
        if (!TryResolvePendingExit(source, head, out RoomExit exit))
        {
            // We KNOW we moved (no bonk), but the edge isn't on the graph — a
            // dark corridor off the map. Can't fabricate a landing, so hold the
            // anchor; this is exactly the "map stalled in the dark" signal a bug
            // report needs, so surface it at Info.
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Dark move '{DescribeMove(head)}' has no mapped exit from {source.Key} ({source.Name}) — " +
                "holding position; map may drift until a lit room re-anchors.");
            return;
        }
        if (PredictedLanding(exit) is not { } expected)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Dark move '{DescribeMove(head)}' targets {exit.Target}, absent from the graph — holding position.");
            return;
        }

        // Predicted neighbour is mapped — the move traversed. Dequeue the head
        // and land there. Non-strict (isStrictAnchor: false): a dark advance is
        // a deduction with no name/exit match, so it must not overwrite the
        // persisted LastKnownRoom, and it preserves RecentSteps so replay
        // recovery keeps the trail (same contract as Strategy 1's non-1-of-1
        // landing in ReconcileFromPending).
        _pending.TryDequeue(out _);
        State.SuspectStrikes = 0;
        if (exit.CastLandings is not null)
        {
            bool sentOn = expected.Key != exit.Target;
            bool asked = AskedForCastStep(head);
            if (sentOn) CastCrossingStarted?.Invoke(exit.Landing);
            EndCastCrossing();
            // This line stood for the room passed through; the landing's display
            // follows, lit or as another dark line.
            if (sentOn) BookCastLanding(expected, in exit, when, alreadyAsked: asked);
        }
        string moveLabel = DescribeMove(head);
        RoomConfidence target = _pending.IsEmpty
            ? RoomConfidence.Confirmed
            : RoomConfidence.Pending;
        SetRoom(expected, target, when, $"dark-room advance via {moveLabel}", isStrictAnchor: false);
        ArmCastDisplayWait();
    }

    // A move issued while blinded SUCCEEDS but starves the room display: the
    // server prints only "You are blind." (period) — no name, no "Obvious
    // exits:", no description (CONFIRMED capture, see GAME_MECHANICS.md).
    // Structurally this is the dark-room case (a traversed move with no
    // confirming render), so we dead-reckon the same way: if a move is pending
    // and its graph edge resolves to a mapped neighbour, advance there — never
    // as a strict anchor, since this is a pure prediction with no name/exit
    // match. Unlike NoteDarkRoomEntered this does NOT set IsInDarkRoom: it's the
    // player who can't see, not the room, so carried light can't help and the
    // dark-room attack-line combat path must stay off. The refusal form ("You
    // can't see well enough to move.") is a bonk handled elsewhere; only the
    // succeeded-move "You are blind." reaches here.
    public void NoteBlindMove(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();

        // A look-direction peek while blind can render this same line from the
        // lit room we're standing in. Drop it as a preview and consume the
        // window, exactly as NoteDarkRoomEntered / NoteRoomObserved do.
        if (_suppressObservationUntil is { } until)
        {
            _suppressObservationUntil = null;
            if (when <= until)
            {
                _log?.Log(LogSeverity.Info, "RoomTracker",
                    "Dropped peeked blind-move line (look-direction preview).");
                return;
            }
            // window expired — fall through and treat as our own move
        }

        if (TakeCastLandingLine(when, "is shown to a blind character")) return;

        // Only a pending move can be advanced by the blind line. A blind echo
        // while Confirmed (a passive re-render, standing still) or with nothing
        // in flight carries nothing to advance on. These guards used to bail
        // silently; a "You are blind." that fires here yet leaves the map frozen
        // is exactly the failure a bug report captures, so surface WHY at Info —
        // one line only fires per blind move (rare), so there's no spam risk, and
        // the log distinguishes "handler never ran" (no line at all) from "ran
        // but state wasn't right" (one of these lines) in the next capture.
        if (State.Confidence != RoomConfidence.Pending)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Blind-move line seen but confidence is {State.Confidence}, not Pending — " +
                "no in-flight move to dead-reckon; holding position.");
            return;
        }
        if (State.CurrentRoom is not { } source)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                "Blind-move line seen but no current room anchor — holding position.");
            return;
        }
        if (!_pending.TryPeek(out PendingMove head))
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Blind-move line seen at {source.Key} ({source.Name}) but the pending-move " +
                "queue is empty — the move never registered; holding position.");
            return;
        }
        if (!TryResolvePendingExit(source, head, out RoomExit exit))
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Blind move '{DescribeMove(head)}' has no mapped exit from {source.Key} ({source.Name}) — " +
                "holding position; map may drift until a lit room re-anchors.");
            return;
        }
        if (PredictedLanding(exit) is not { } expected)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Blind move '{DescribeMove(head)}' targets {exit.Target}, absent from the graph — holding position.");
            return;
        }

        // Predicted neighbour is mapped — the move traversed. Dequeue the head
        // and land there, non-strict (same contract as the dark-room advance:
        // a deduction must not overwrite the persisted LastKnownRoom and keeps
        // RecentSteps intact for replay recovery).
        _pending.TryDequeue(out _);
        State.SuspectStrikes = 0;
        if (exit.CastLandings is not null)
        {
            bool sentOn = expected.Key != exit.Target;
            bool asked = AskedForCastStep(head);
            if (sentOn) CastCrossingStarted?.Invoke(exit.Landing);
            EndCastCrossing();
            // As in the dark: this line stood for the room passed through, and the
            // landing's follows.
            if (sentOn) BookCastLanding(expected, in exit, when, alreadyAsked: asked);
        }
        string moveLabel = DescribeMove(head);
        RoomConfidence target = _pending.IsEmpty
            ? RoomConfidence.Confirmed
            : RoomConfidence.Pending;
        EnteredBlind = true;
        SetRoom(expected, target, when, $"blind-move advance via {moveLabel}", isStrictAnchor: false);
        ArmCastDisplayWait();
    }

    // Read-only peek check: true when the room display now arriving is not a room
    // we are standing in. Unlike NoteRoomObserved, this does NOT consume the flag —
    // the room-display consumers that fire BEFORE the exits line (cash pickup,
    // auto-get, the room-entity classifier that drives the combat gate, the party
    // invites) call this to skip acting on such a room, while the flag stays armed
    // until NoteRoomObserved fires on "Obvious exits:" and consumes it. Without
    // this, a `look <dir>` peek renders a full room display and the engines fire
    // get / equip / combat against a room the player never entered.
    //
    // Two kinds of display qualify:
    //   - a look-direction peek: a look <dir> was sent within the suppression
    //     window and its preview hasn't been consumed yet. A move sent before that
    //     look is answered first, so once the server has echoed that move the
    //     display arriving is the move's own and not the peek (`e` then `l e` typed
    //     quickly). A move it hasn't echoed says nothing either way — it may be
    //     one the game never answered — so the peek reading stands;
    //   - the room a cast-on-walk exit only passes through, the one room shown for
    //     a step that ends somewhere else (CastTransitIsNext).
    public bool IsPeekSuppressed(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (CastTransitIsNext(when)) return true;
        if (_suppressObservationUntil is not { } until) return false;
        if (when > until) return false;
        return !(_pending.TryPeek(out PendingMove head) && head.SentAt <= _lookSentAt && HeadMoveEchoed(head));
    }

    // ----- Crossing a cast-on-walk teleport ---------------------------
    // The game shows such a step as two rooms: the room the exit names, which it
    // puts us in and displays before casting the exit's spell, and then the landing
    // the spell teleports us to (GAME_MECHANICS "Jungle to the Lost City: the Vine
    // Bridge trap and the golden idol"). By the time the first display is read we
    // already stand at the landing, and what it lists belongs to a room we have
    // left. So the landing is booked off it at once, by what we hold, which lets an
    // engine step on without waiting; and the display that follows is the
    // landing's own, held against the booking and read for what is in the room.
    // Only when that display doesn't come is the room asked for (a bare Enter).

    // How long after such a step goes out the next display is believed to be the
    // room passed through, and how long the answer to an asking is waited for. A
    // step the game swallowed is never answered, and must not go on hiding every
    // later display of the room we are still in.
    private static readonly TimeSpan CastTransitWindow = TimeSpan.FromMilliseconds(LookSuppressWindowMs);

    // How long a step through such an exit may go with no room shown, and a
    // landing booked off the room passed through with no display of its own,
    // before the room is asked for. A follower dragged through may be shown none.
    private static readonly TimeSpan CastDisplayWait = TimeSpan.FromMilliseconds(1500);
    private IDisposable? _castDisplayTimer;
    private DateTimeOffset? _castDisplayWaitFor;
    private IDisposable? _castLandingTimer;

    // The pending move no room was shown for in time, and when the room was asked
    // for on its account.
    private (DateTimeOffset SentAt, DateTimeOffset At)? _castAsked;

    // A landing booked off the room passed through, its own display still to come:
    // the next display, dark line or blind line is the landing's
    // (VerifyCastLanding, TakeCastLandingLine).
    private (RoomKey Booked, RoomKey Transit, IReadOnlyList<(int ItemId, RoomKey Room)> Landings, DateTimeOffset At)? _castLandingUnseen;

    // The display being reconciled was of the room passed through, not of the room
    // it booked us into: its exits and doors aren't that room's.
    private bool _bookedOffAnotherRoomsDisplay;

    // A room display is being reconciled: a transition raised now was decided by
    // that display (RoomTransition.Displayed).
    private bool _reconcilingDisplay;

    private Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private Action? _requestRedisplay;

    // One-shot timer for the waits above. Wired by AppServices; unset, the
    // waits never run out.
    public void SetDelayScheduler(Func<TimeSpan, Action, IDisposable> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _scheduleDelay = schedule;
    }

    // Asks the game to show the room we stand in (a bare Enter). Bound with the
    // wire, once connected.
    public void SetRoomRedisplay(Action redisplay)
    {
        ArgumentNullException.ThrowIfNull(redisplay);
        _requestRedisplay = redisplay;
    }

    // Diagnostics: a cast-on-walk landing is booked and its own display hasn't
    // come yet.
    public bool CastLandingAwaitsDisplay => _castLandingUnseen is not null;

    // A step through an exit whose spell teleports everyone on by themselves is
    // seen to have happened. Carries the landing the step is expected to end in,
    // so an engine can tell its own step from someone else's. Raised once per
    // crossing, before the room change it leads to, and not at all for a step the
    // game refused or dropped (every raise goes with the step leaving the queue).
    // A leader's engine takes it as the moment its party is split.
    public event Action<RoomKey>? CastCrossingStarted;

    private void EndCastCrossing()
    {
        _castAsked = null;
        _castDisplayWaitFor = null;
        _castDisplayTimer?.Dispose();
        _castDisplayTimer = null;
    }

    private bool AskedForCastStep(PendingMove head) => _castAsked?.SentAt == head.SentAt;

    // The move in flight goes through a cast-on-walk exit that will send us on, and
    // the display now arriving is the room it passes through.
    private bool CastTransitIsNext(DateTimeOffset when) =>
        State.Confidence == RoomConfidence.Pending
        && State.CurrentRoom is { } source
        && _pending.TryPeek(out PendingMove head)
        && CastStepDisplayDue(head, when)
        && TryResolvePendingExit(source, head, out RoomExit exit)
        && exit.CastLandings is not null
        && InferCastLanding(exit, out _) is not null;

    // Whether the step's own display is still believed to be the next one shown.
    // For a moment after it goes out, so a step the game swallowed doesn't go on
    // hiding every later display of the room we are still in. Once a room had to be
    // asked for: a step of our own may only be late (the game queues a command
    // behind an action delay), and its display then comes ahead of the answer, so
    // the belief holds a moment longer; a drag's display comes with the line that
    // announced the drag or not at all, so what follows the asking is our room.
    private bool CastStepDisplayDue(PendingMove head, DateTimeOffset when)
    {
        if (_castAsked is { } asked && asked.SentAt == head.SentAt)
            return !head.IsFollowDrag && when - asked.At <= CastTransitWindow;
        return when - head.SentAt <= CastTransitWindow;
    }

    // A step through a cast-on-walk exit is now the move in flight: start the wait
    // for a room to be shown for it.
    private void ArmCastDisplayWait()
    {
        if (_scheduleDelay is null || State.Confidence != RoomConfidence.Pending) return;
        if (State.CurrentRoom is not { } source || !_pending.TryPeek(out PendingMove head)) return;
        if (_castDisplayWaitFor == head.SentAt) return;
        if (!TryResolvePendingExit(source, head, out RoomExit exit) || exit.CastLandings is null) return;
        _castDisplayTimer?.Dispose();
        _castDisplayWaitFor = head.SentAt;
        _castDisplayTimer = _scheduleDelay(CastDisplayWait, OnCastDisplayOverdue);
    }

    // No room has been shown for the step. A follower's drag can be shown none, and
    // then nothing else would ever say where the spell put us: ask for the room.
    // A step the game refused has left the queue by now. One it swallowed is
    // answered with the room we never left, and is taken off the queue on that
    // (ReconcileFromPending).
    private void OnCastDisplayOverdue()
    {
        _castDisplayTimer = null;
        if (_castDisplayWaitFor is not { } waitedFor) return;
        if (State.Confidence != RoomConfidence.Pending) return;
        if (!_pending.TryPeek(out PendingMove head) || head.SentAt != waitedFor) return;
        _castAsked = (waitedFor, _now());
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"No room shown {CastDisplayWait.TotalSeconds:0.#}s after move {DescribeMove(head)} through a teleporting exit — asking for the room.");
        _requestRedisplay?.Invoke();
    }

    // We stand in a landing booked off the room passed through. The game shows the
    // landing next; until it has, that display is owed, and when it doesn't come in
    // time the room is asked for instead. Not when the wait for a room had already
    // run out on this step (alreadyAsked): that asking's answer is still to come.
    private void BookCastLanding(Room landing, in RoomExit exit, DateTimeOffset when, bool alreadyAsked)
    {
        _castLandingUnseen = (landing.Key, exit.Target, exit.CastLandings!, when);
        _castLandingTimer?.Dispose();
        _castLandingTimer = alreadyAsked ? null : _scheduleDelay?.Invoke(CastDisplayWait, OnCastLandingOverdue);
    }

    private void EndCastLandingWait()
    {
        _castLandingUnseen = null;
        _castLandingTimer?.Dispose();
        _castLandingTimer = null;
    }

    // The landing's own display hasn't come. Standing there still, ask for the room
    // once, so its occupants and items are read and the booking is checked. Moved
    // on already (an engine steps as soon as the landing is booked), there is
    // nothing left to ask about, and the next display is the next step's.
    private void OnCastLandingOverdue()
    {
        _castLandingTimer = null;
        if (_castLandingUnseen is not { } unseen) return;
        if (!_pending.IsEmpty || State.CurrentRoom?.Key != unseen.Booked || _requestRedisplay is not { } ask)
        {
            _castLandingUnseen = null;
            return;
        }
        _castLandingUnseen = unseen with { At = _now() };
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"No display of {unseen.Booked} {CastDisplayWait.TotalSeconds:0.#}s after the exit's spell sent us there — asking for the room.");
        ask();
    }

    // A dark-room or blind line while a landing's display is owed is that display:
    // the landing can't be seen. Not a move arriving. An engine's next step went
    // out as soon as the landing was booked, and read as that step the line put the
    // tracker a room ahead of the character from there to the next room shown.
    private bool TakeCastLandingLine(DateTimeOffset when, string what)
    {
        if (_castLandingUnseen is not { } unseen) return false;
        EndCastLandingWait();
        if (when - unseen.At > CastTransitWindow) return false;
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"The landing of a cast-on-walk teleport ({unseen.Booked}) {what}.");
        return true;
    }

    // A move is announced behind a follower's drag through a cast-on-walk exit that
    // was shown no room. A drag's display comes with the line that announced it, so
    // none is coming now, and left at the head of the queue the drag had the next
    // room's display read against it: hidden from the consumers as the room passed
    // through, and, where that room reads like the one the drag left (the golden
    // idol's passage: both "Earthen Catacombs", north and east), thrown away as a
    // re-look, leaving the follower a room behind for good. So the drag is settled
    // where our pack says its spell put us, and the next display is the next move's.
    private void SettleUnshownCastDrag(DateTimeOffset when)
    {
        if (State.Confidence != RoomConfidence.Pending || State.CurrentRoom is not { } source) return;
        if (_pending.Count != 1 || !_pending.TryPeek(out PendingMove head) || !head.IsFollowDrag) return;
        if (!TryResolvePendingExit(source, head, out RoomExit exit) || exit.CastLandings is null) return;
        Room? onward = InferCastLanding(exit, out string basis);
        if ((onward ?? _graph.GetRoom(exit.Target)) is not { } landing) return;

        _pending.TryDequeue(out _);
        if (onward is not null) CastCrossingStarted?.Invoke(exit.Landing);
        EndCastCrossing();
        LastCastLanding = (source.Key, landing.Key, $"{basis}; dragged on before any room was shown", when);
        SetRoom(landing, RoomConfidence.Pending, when,
            $"drag {DescribeMove(head)} settled where its spell leaves us ({basis}): dragged on with no room shown for it");
    }

    // The first display after a landing was booked: the landing's own, as the game
    // shows it or as we asked for it. Fitting the booked room, it bears the booking
    // out. Fitting another of the exit's landings, or only the room passed through,
    // it says the booking was wrong (an inventory read gone stale, a spell that
    // sent us nowhere), and we are where it shows. An engine's next step may be in
    // flight by now; a display that fits the booked room is then the room that
    // step is leaving, and is read as that.
    private void VerifyCastLanding(RoomObservation observation, DateTimeOffset when)
    {
        if (_castLandingUnseen is not { } unseen) return;
        EndCastLandingWait();
        if (when - unseen.At > CastTransitWindow) return;
        if (State.CurrentRoom is not { } booked || booked.Key != unseen.Booked) return;

        List<Room> fitting = LandingsFitting(unseen.Landings, observation);
        if (MatchesPredicted(booked, observation))
        {
            // Borne out as far as the screen can say. Where several landings read
            // alike the pick still rests on what we carry: ask the realm, if it can
            // say which room this is.
            if (fitting.Count > 1)
                RequestAuthoritativeResync?.Invoke("a cast-on-walk teleport's landings look alike");
            return;
        }

        Room? shown = fitting.Count > 0 ? fitting[0]
            : _graph.GetRoom(unseen.Transit) is { } transit && MatchesPredicted(transit, observation) ? transit
            : null;
        if (shown is null) return;

        string why = fitting.Count > 0
            ? "the room shown after the crossing fits this landing, not the one booked"
            : "the room shown after the crossing is still the one the exit leads to: its spell sent us nowhere";
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Cast-on-walk teleport: booked {booked.Key}, but {why} — now at {shown.Key}.");
        if (LastCastLanding is { } last) LastCastLanding = (last.From, shown.Key, why, when);
        ClearPendingAndSteps();
        SetRoom(shown, RoomConfidence.Confirmed, when, $"cast-on-walk landing corrected: {why}", isStrictAnchor: false);
        if (fitting.Count > 1)
            RequestAuthoritativeResync?.Invoke("a cast-on-walk teleport's landings look alike");
    }

    // ----- A move the connection swallowed ----------------------------
    // A carrier loss usually shows as a move that is never answered: the loop sent a
    // step and the link died under it. That move stayed queued across the reconnect
    // and held the tracker in Pending for good. The login's room display read as a
    // passive re-look, a new walk deferred "until the in-flight move settles" (and
    // every Go re-armed that wait), and the next real move was reconciled against the
    // dead one. The server will never answer a move sent before the drop, so the
    // first display after the reconnect says where we stand instead.
    private bool _connectionLostMidMove;

    public void NoteConnectionLost()
    {
        if (!_pending.IsEmpty) _connectionLostMidMove = true;
    }

    // The first room display after a drop that had a move in flight. The move either
    // landed before the link died or it didn't: whichever of the two rooms the
    // display matches is where we are. When it matches both (an identically-named
    // corridor) or neither, the display can't say, and the ordinary unsure-position
    // handling takes it from the room we last knew.
    private void ReconcileAfterReconnect(RoomObservation observation, DateTimeOffset when)
    {
        Room? source = State.CurrentRoom;
        Room? target = null;
        if (_pending.Count == 1 && _pending.TryPeek(out PendingMove head) && source is not null
            && TryResolvePendingExit(source, head, out RoomExit exit))
            target = PredictedLanding(exit);
        while (_pending.TryDequeue(out _)) { /* drain */ }
        EndCastCrossing();
        _passivePendingMove = null;
        _cardinalEchoClaim = null;

        bool atSource = source is not null && MatchesPredicted(source, observation);
        bool atTarget = target is not null && MatchesPredicted(target, observation);
        if (atSource && !atTarget)
        {
            DropMostRecentStep();   // the step it logged was never taken
            SetRoom(source, RoomConfidence.Confirmed, when, "reconnected: the move sent before the drop never landed");
            return;
        }
        if (atTarget && !atSource)
        {
            SetRoom(target, RoomConfidence.Confirmed, when, "reconnected: the move sent before the drop had landed");
            return;
        }
        if (atSource)
        {
            EnterSuspect(when, "reconnected with a move in flight; the room it left and the room it led to read the same");
            return;
        }
        State.Confidence = RoomConfidence.Confirmed;
        ReconcileFromConfirmed(observation, when);
    }

    // The server-side observation parser reports the room it just saw — name +
    // the set of directions on the "Obvious exits:" line. The tracker reconciles
    // this against the expected outcome of any pending move.
    public void NoteRoomObserved(RoomObservation observation, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();

        if (_suppressObservationUntil is { } until)
        {
            if (when <= until)
            {
                // A look-<dir> peek is armed. The server answers commands IN ORDER,
                // so when a move is still pending its confirming display arrives
                // BEFORE the peek's adjacent-room display. Dropping that confirmation
                // here was the reported desync: a peek armed the instant after a move
                // ate the move's own landing display, and the peek's adjacent room
                // then mismatched the pending move and bumped Suspect (report
                // paradigm-20260813-201720, "move + l <dir> too fast"). So drop ONLY a
                // display that isn't the pending move's outcome; let a move-related
                // display through and keep the peek armed for the real peek that
                // follows it.
                if (!ObservationLooksLikePendingMoveOutcome(observation, when))
                {
                    _suppressObservationUntil = null;
                    _log?.Log(LogSeverity.Info, "RoomTracker",
                        $"Dropped peek observation: '{observation.Name}'.");
                    return;
                }
                // Falls through: this is the pending move's own display — confirm it
                // normally; the peek stays armed for the following adjacent-room peek.
            }
            else
            {
                _suppressObservationUntil = null;   // window expired — process normally
            }
        }

        // A normal room display parsed — the room is lit enough to print its
        // name + exits, so we're no longer in the dark. The peek early-return
        // above skips this, so peeking a lit neighbour while we stand in a dark
        // room doesn't wrongly clear the flag. Log only the true→false edge so a
        // triager sees where darkness began and ended without per-display noise.
        if (IsInDarkRoom)
            _log?.Log(LogSeverity.Info, "RoomTracker",
                "Room display visible again — no longer in the dark.");
        IsInDarkRoom = false;
        EnteredBlind = false;

        // Mirror open-door modifiers from the latest observation into
        // state so the walker can pre-check before kicking off the
        // door FSM ("open door south" → skip the bash/pick wait,
        // send the cardinal move directly).
        State.OpenDoorDirections = observation.OpenDoorDirections;
        State.ClosedDoorDirections = observation.ClosedDoorDirections;

        // From here on a room the tracker settles on is the one this display shows:
        // a booked landing it corrects, as much as the reconcile that follows.
        _reconcilingDisplay = true;
        try
        {
            VerifyCastLanding(observation, when);

            bool afterDrop = _connectionLostMidMove;
            _connectionLostMidMove = false;
            // Unsure of the room already: there is no in-flight move to settle, only
            // dead ones to forget before the display is judged.
            if (afterDrop && State.Confidence != RoomConfidence.Pending)
                while (_pending.TryDequeue(out _)) { /* drain */ }

            switch (State.Confidence)
            {
                case RoomConfidence.Unknown:
                    LandFromCandidateSearch(observation, when);
                    break;

                case RoomConfidence.Confirmed:
                    ReconcileFromConfirmed(observation, when);
                    break;

                case RoomConfidence.Pending:
                    if (afterDrop) ReconcileAfterReconnect(observation, when);
                    else ReconcileFromPending(observation, when);
                    break;

                case RoomConfidence.Suspect:
                    ReconcileFromSuspect(observation, when);
                    break;

                case RoomConfidence.Lost:
                case RoomConfidence.PendingRespawn:
                    // Lost / PendingRespawn → observation is authoritative;
                    // land via candidate search. PendingRespawn arrives via
                    // the same code path because the recovery semantics are
                    // identical: the next obs is wherever we are now.
                    LandFromCandidateSearch(observation, when);
                    break;
            }
        }
        finally
        {
            _reconcilingDisplay = false;
        }

        // The display was of a room we were only passed through, and we are booked
        // somewhere it didn't show: none of what it said holds for where we stand.
        if (_bookedOffAnotherRoomsDisplay)
        {
            _bookedOffAnotherRoomsDisplay = false;
            State.OpenDoorDirections = null;
            State.ClosedDoorDirections = null;
            _lastObservation = null;
            return;
        }

        // Mirror the full observed-exit set so the walker can skip a redundant
        // `sea <dir>` when a graph-hidden exit is already showing in the live
        // display. Set AFTER the switch: the reconcile paths call SetRoom, which
        // clears ObservedExitDirections (so a dead-reckoned / recovered room never
        // carries a stale set) — re-applying it here binds the set to the room we
        // actually just observed. A blind character still gets no exits here (blind
        // room displays carry no `Obvious exits:` line), so the walker will run the
        // real `sea` a hidden exit needs instead of skipping it on a stale value.
        State.ObservedExitDirections = observation.Exits;

        // Record what we just fully processed so the next observation can tell
        // a passive redisplay of the same room from a genuine move outcome. Set
        // after the switch so the reconcile paths above still see the previous
        // value; skipped on the peek-suppression early-return so a dropped peek
        // doesn't masquerade as the last real observation.
        _lastObservation = observation;
        _lastObservationAt = when;
    }

    // Would this observation be handled by the pending-move reconcile as this move's
    // outcome rather than a stray display? True when it matches the head pending
    // move's predicted target (its forward confirmation, incl. the null-name-learned
    // and name-re-learned cases) OR the source room (a passive re-look while the
    // move is in flight). Used
    // by the peek-suppression path to avoid dropping a move's confirming display as if
    // it were a look-<dir> peek: only a display that is NEITHER — the peeked adjacent
    // room — is the actual peek. Mirrors ReconcileFromPending's Strategy 1 / 1a / 1b
    // matches without mutating any state.
    private bool ObservationLooksLikePendingMoveOutcome(RoomObservation observation, DateTimeOffset when)
    {
        if (State.Confidence != RoomConfidence.Pending) return false;
        if (State.CurrentRoom is not { } source) return false;
        if (!_pending.TryPeek(out PendingMove head)) return false;

        // Passive re-look / self-confirm of the source room (Strategy 1b).
        if (MatchesPredicted(source, observation)) return true;

        // Forward confirmation: the predicted target room (Strategy 1 / 1a).
        if (!TryResolvePendingExit(source, head, out RoomExit exit)) return false;
        // A cast-on-walk teleport shows the exit's target; a room we had to ask
        // for after it shows a landing.
        if (exit.CastLandings is { } landings)
        {
            foreach ((int _, RoomKey landingKey) in landings)
                if (_graph.GetRoom(landingKey) is { } landing && MatchesPredicted(landing, observation))
                    return true;
        }
        if (_graph.GetRoom(exit.Target) is not { } expected) return false;
        return MatchesPredicted(expected, observation)
            || (ArrivesInRoomTheDataLeavesUnnamed(expected, observation)
                && ReadUnnamedArrival(source, head, expected, observation, when) == UnnamedArrival.Taken);
    }

    // The death-message detector saw a post-death lives readout — either "You now
    // have N lives remaining." (plain / suicide death) or "You have N lives left."
    // (miracle-save death). Capture a DeathRecord on the loaded profile (room
    // captured = where we were when the death message fired),
    // drain pending state, and transition to PendingRespawn so the next
    // observation lands as the new authoritative position without churning
    // Suspect strikes.
    //
    // tookNothing: an arena death (ArenaDeathRooms.DeathTookNothing, decided by the
    // caller once the death's own lines have been read). It is a death like any
    // other for where the character is, and goes in the history, but nothing was
    // dropped: the record holds no pile and is written Recovered, so nothing sets
    // out to recover one.
    public void NoteDeath(int livesRemaining, string? messageText = null, DateTimeOffset? whenUtc = null,
        bool tookNothing = false)
    {
        DateTimeOffset when = whenUtc ?? _now();
        Room? died = State.CurrentRoom;
        LastDeathRoom = died?.Key;
        LastDeathRoomConfirmed = died is not null && State.Confidence == RoomConfidence.Confirmed;
        LastDeathTookNothing = tookNothing;

        if (_profile is not null)
        {
            List<DeathItem>? equipped = null, lost = null;
            CurrencyHoldings? coins = null;
            if (!tookNothing && _inventorySnapshot is { } provider)
            {
                InventorySnapshot snapshot = provider();
                (equipped, lost) = DeathLootCapture.FromSnapshot(snapshot);
                coins = snapshot.Currency;
            }
            AppendDeathRecord(_profile, when,
                died is null ? null : new RoomRef(died.Key.Map, died.Key.Room), died?.Name, RoomsGameData,
                livesRemaining, messageText, equipped, lost, coins, nothingLost: tookNothing);
        }

        while (_pending.TryDequeue(out _)) { /* drain */ }
        _recentSteps.Clear();
        _stepsAnchor = null;
        ClearPassiveGrid();
        PersistSteps();
        IsInDarkRoom = false;
        EnteredBlind = false;
        SetRoom(room: null, RoomConfidence.PendingRespawn, when, "death recorded");

        // Broadcast the death AFTER the PendingRespawn transition but BEFORE the
        // graveyard's own room display confirms. A loop stopping here resets its
        // recovery state, so the later PendingRespawn → Confirmed(graveyard)
        // transition can't drive a recovery-reroute back out.
        PlayerDeathObserved?.Invoke();
    }

    // A death that was not seen: it happened after the link was gone, and was worked
    // out on the way back in (a hang-up while dropped, on a board that kills for it).
    // It gets the record a death line makes, from what was known when the character
    // was last in the game, so the history, Death Recovery and everything else that
    // reads the list treat it as any other death.
    //
    // Nothing about the position changes: the character is already in the room the
    // game put it in and the map has read that room, so there is no respawn display
    // to wait for. Hence PlayerDeathInferred, not PlayerDeathObserved.
    //
    // An arena death (UnwitnessedDeath.NothingLost) is recorded as NoteDeath
    // records one: no pile, written Recovered.
    // Returns the record made, or null with no profile to put it on.
    public DeathRecord? NoteUnwitnessedDeath(Recovery.UnwitnessedDeath death)
    {
        ArgumentNullException.ThrowIfNull(death);
        if (_profile is null) return null;
        LastDeathTookNothing = death.NothingLost;
        // The room's number is of the set the held list was written on: named from
        // this graph only when that is the set it holds (or the list didn't say).
        bool sameSet = death.GameData is null
            || string.Equals(death.GameData, RoomsGameData, StringComparison.OrdinalIgnoreCase);
        string? roomName = sameSet && death.Room is { } room ? _graph.GetRoom(new RoomKey(room.Map, room.Room))?.Name : null;
        DeathRecord record = AppendDeathRecord(_profile, death.At, death.Room, roomName, death.GameData,
            death.LivesRemaining, death.Message,
            death.NothingLost ? null : death.Equipped, death.NothingLost ? null : death.Lost,
            death.NothingLost ? null : death.Coins, nothingLost: death.NothingLost);
        UnwitnessedDeathRecorded?.Invoke();
        if (death.AtEntry) PlayerDeathInferred?.Invoke();
        return record;
    }

    // The one place a death becomes a DeathRecord on the profile.
    private DeathRecord AppendDeathRecord(
        CharacterProfile profile, DateTimeOffset when, RoomRef? room, string? roomName, string? gameData,
        int livesRemaining, string? messageText,
        List<DeathItem>? equipped, List<DeathItem>? lost, CurrencyHoldings? coins, bool nothingLost = false)
    {
        profile.DeathHistory ??= new List<DeathRecord>();
        DeathRecord record = new(when, room, livesRemaining, messageText)
        {
            RecordNumber = profile.DeathHistory.Count + 1,
            RoomName = roomName,
            GameData = gameData,
            // Recovered, not Active: every reader of the history takes Active and
            // Partial for a pile still out there.
            Status = nothingLost ? DeathRecoveryStatus.Recovered : DeathRecoveryStatus.Active,
            RecoveryMessage = nothingLost ? ArenaDeathNote : null,
            NothingLost = nothingLost,
            EquippedAtDeath = equipped,
            LostItems = lost,
            CoinsAtDeath = coins,
        };
        profile.DeathHistory.Add(record);
        string where = room is null ? "(unknown room)" : $"{room.Map}/{room.Room}";
        _log?.Log(LogSeverity.Info, "RoomTracker", nothingLost
            ? $"Arena death recorded at {where}; {livesRemaining} lives remaining; nothing was lost, so no deathpile and nothing to recover."
            : $"Death recorded at {where}; {livesRemaining} lives remaining; " +
              $"deathpile worn={equipped?.Count ?? 0}, lost (pack, lit light, keys)={lost?.Count ?? 0}.");
        return record;
    }

    // The note an arena death's record carries: the Deaths list shows it on the row.
    public const string ArenaDeathNote = "Arena death: nothing was lost.";

    // A movement-refusal line was seen (e.g. "You are too paralyzed to move." /
    // "You can't go that way."). Drains the most recently queued pending move
    // (since that's the one the server just refused) and reverts toward
    // Confirmed at the current room.
    public void NoteMoveBlocked(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();

        // Drop the most recent pending move + its persisted step — the
        // server refused, so it never happened.
        DropMostRecentPending();

        if (State.Confidence == RoomConfidence.Pending)
        {
            RoomConfidence target = _pending.IsEmpty
                ? RoomConfidence.Confirmed
                : RoomConfidence.Pending;
            SetConfidence(target, when, "move blocked");
        }
        MoveBlocked?.Invoke();
    }

    // The follow move a party leader's drag promised was refused: the game printed
    // the follow line, then the exit's own refusal in words the refusal detector
    // doesn't know, and the follow ended. Un-counts that drag, and only a drag: a
    // move of our own queued behind it has its own answer coming. False when the
    // newest queued move isn't a drag.
    public bool NoteFollowDragRefused(DateTimeOffset? whenUtc = null)
    {
        if (MostRecentPending() is not { IsFollowDrag: true }) return false;
        NoteMoveBlocked(whenUtc);
        return true;
    }

    // How recently the most-recent move must have been sent for a "command
    // ignored" drop to be attributed to it. A fast loop or Roomba sweep floods
    // commands far tighter than this; a move still Pending from longer ago is
    // more likely a slow landing than the command the limiter just dropped.
    private static readonly TimeSpan DroppedCommandWindow = TimeSpan.FromSeconds(3);

    // The game dropped the command we just sent to its typing-rate limiter
    // ("You are typing too quickly - command ignored") — the move never
    // executed, yet NoteMoveSent already counted it. Un-count the most-recent
    // pending move so the tracker doesn't run one room ahead of the character.
    // This is the net-miscount that turns a homogeneous grid into a Lost state:
    // a single counted-but-never-made move is invisible among
    // identically-named rooms and compounds every lap. Unlike a plain refusal
    // ("no exit"), "command ignored" doesn't name WHAT it dropped — it could be
    // a non-move command — so this only reverts when a move is actually in
    // flight AND was sent inside DroppedCommandWindow (i.e. plausibly the
    // contended command). A long-outstanding pending move is left alone.
    public void NoteCommandDropped(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (State.Confidence != RoomConfidence.Pending || _pending.IsEmpty) return;
        if (LastMoveSentAt is not { } sent || when - sent > DroppedCommandWindow) return;

        DropMostRecentPending();
        RoomConfidence target = _pending.IsEmpty ? RoomConfidence.Confirmed : RoomConfidence.Pending;
        SetConfidence(target, when, "command dropped (typing too quickly)");
        _log?.Log(LogSeverity.Info, "RoomTracker",
            "Command dropped by the game's typing-rate limiter while a move was in flight; " +
            "un-counting it so the tracker doesn't run a room ahead.");
    }

    // A fall line — "You fall to the ground with a thud, taking N damage!" (a failed
    // jump) or "You take N damage from the fall!" — usually means the move didn't
    // land where it was headed (user, 2026-09-29). Drop the prediction and go Suspect,
    // so the next room display is resolved from scratch rather than assumed to be the
    // planned landing (and Paradigm asks `rm` when no engine is driving). Where the
    // fall IS the plan (the map-12 pit jumps), the re-check just confirms the room.
    public void NoteFell(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (State.Confidence is not (RoomConfidence.Confirmed or RoomConfidence.Pending)) return;
        while (!_pending.IsEmpty) DropMostRecentPending();
        EnterSuspect(when, "fell — the move may not have landed where it was headed");
    }

    // A typed exit command (touch altar / enter portal / a teleport keyword) was
    // refused by a condition in its room script — monsters in the room, or a level /
    // alignment / item / price check — so it never moved us. Those refusal lines
    // are generic ("You do not see that here.") and also answer ordinary commands,
    // so revert only when the move in flight is a typed command sent within
    // DroppedCommandWindow; a pending cardinal is never touched. Returns whether it
    // reverted.
    public bool NoteCommandMoveRefused(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        if (State.Confidence != RoomConfidence.Pending) return false;
        if (MostRecentPending() is not { Command: not null } last) return false;
        if (when - last.SentAt > DroppedCommandWindow) return false;
        CommandMoveRefused?.Invoke();
        NoteMoveBlocked(when);
        return true;
    }

    // The server echoes the command you typed back on the prompt line
    // ("[HP=..]:e") as it processes it, immediately before rendering the move's
    // result room — whereas a spontaneous redisplay (an NPC walking through, a
    // regen-tick refresh, a post-combat redraw) carries NO echo because you typed
    // nothing to cause it. InboundMoveEchoScanner feeds the echoed command here;
    // ReconcileFromPending reads it (via HeadMoveEchoed) as the causal "this move
    // actually executed" signal that replaces the old 400ms timing heuristic: in
    // an identically-named grid a redisplay matching the predicted target is the
    // move's landing only once the move has been echoed, regardless of how fast or
    // slow it arrived. A single slot is enough — moves and their echoes are FIFO,
    // and a stale echo can't apply to a later move because its timestamp predates
    // that move's SentAt (see HeadMoveEchoed). Only works when the user's statline
    // is one the scanner can split; the re-look guard falls back to timing when no
    // echo is ever seen (see _everReceivedEcho and the guard in ReconcileFromPending).
    public void NoteInboundMoveEcho(string echoedCommand, DateTimeOffset? whenUtc = null,
        bool provesStatlineReadable = true)
    {
        if (string.IsNullOrWhiteSpace(echoedCommand)) return;
        string command = echoedCommand.Trim();
        // Any prompt-echo the scanner produces proves this user's statline is one the
        // echo path can read — latch it so the arrival gate trusts the echo instead of
        // the timing fallback. A custom statline the scanner can't split never reaches
        // here, so the latch stays false and the timing fallback keeps the walker from
        // freezing. Set before the head-match gate: even a non-move command echo (stat,
        // inventory) is proof the scanner is working.
        //
        // A detached echo (one the scanner recovered off its own line rather than the
        // prompt) must NOT latch this: it only lands when something displaced the echo
        // from the prompt, so it proves nothing about whether the ordinary prompt-glued
        // echo is readable. Latching on it would strand a user whose statline the
        // scanner can't split — the gate would switch to demanding echoes it can only
        // occasionally see, and hold every un-echoed move as a re-look forever.
        if (provesStatlineReadable)
            _everReceivedEcho = true;
        // Record only an echo that names the move currently in flight. A non-move
        // command echoed mid-move (stat, inventory), or the echo of an already-
        // confirmed move, must not overwrite the head move's echo and leave its
        // real landing looking un-echoed — which would hold the move as a re-look.
        if (!_pending.TryPeek(out PendingMove head) || !EchoMatchesHead(command, head)) return;
        _lastInboundEcho = (command, whenUtc ?? _now());
        _log?.Log(LogSeverity.Debug, "RoomTracker",
            $"Server echoed head move '{command}'; its next matching room display will confirm the landing.");
    }

    // A "The door is closed!" refusal was seen. Beyond the generic move-blocked
    // revert, this tells us the cached "door open" reading for the just-attempted
    // direction is stale — the door shut (typically mid-combat) since we last saw
    // the room. Clear that direction's open-door flag so the next attempt routes
    // through the door-open FSM (bash / pick / key) instead of trusting the stale
    // "already open" reading and bonking the shut door again. Reads the refused
    // direction off the pending tail BEFORE NoteMoveBlocked drops it.
    public void NoteDoorClosed(DateTimeOffset? whenUtc = null)
    {
        if (MostRecentPendingCardinal() is { } dir)
            ClearOpenDoorDirection(dir);
        NoteMoveBlocked(whenUtc);
    }

    // "The door to the <dir> just closed." — an ambient door-shut announcement
    // that names its own direction, unlike the bare "The door is closed!"
    // refusal (which carries none and reads the direction off the pending move).
    //   - Heading that way: the door shut in our path — clear its stale "open"
    //     flag and revert the pending move exactly like NoteDoorClosed, so the
    //     next attempt routes through the door-open FSM (bash / pick / key)
    //     instead of bonking the now-closed door.
    //   - Standing still: it's this room's exit that shut, so clear its flag now;
    //     the next move through it opens it first instead of bouncing off it
    //     (report paradigm-20260928-110224).
    //   - Moving another way: the line may belong to the room being left or the
    //     one being entered, so leave the flags alone.
    public void NoteNamedDoorClosed(Direction closedDir, DateTimeOffset? whenUtc = null)
    {
        if (_pending.IsEmpty)
        {
            ClearOpenDoorDirection(closedDir);
            return;
        }
        if (MostRecentPendingCardinal() != closedDir) return;
        ClearOpenDoorDirection(closedDir);
        NoteMoveBlocked(whenUtc);
    }

    // "The door to the <dir> just opened." / "You see <name> open the door to the
    // <dir>." — someone opened one of this room's doors, or our own door FSM just
    // did (the engines' OnDoorReply). Standing still, mark it
    // open so the next move through it doesn't try to open it again; mid-move the
    // line could be about either room, so leave it.
    public void NoteNamedDoorOpened(Direction dir)
    {
        if (!_pending.IsEmpty) return;
        if (State.ClosedDoorDirections is { } shut && shut.Contains(dir))
        {
            HashSet<Direction> stillShut = new(shut);
            stillShut.Remove(dir);
            State.ClosedDoorDirections = stillShut.Count > 0 ? stillShut : null;
        }
        HashSet<Direction> next = State.OpenDoorDirections is { } open ? new(open) : new();
        if (!next.Add(dir)) return;
        State.OpenDoorDirections = next;
        _log?.Log(LogSeverity.Info, "RoomTracker", $"Door {dir} reported opened — marked open.");
    }

    // Exits the latest display of the room we're confirmed in shows as passable —
    // listed, and not a shut door or gate. A lever-opened passage shows here once
    // it's open, so the engines can cross it without re-pulling the lever. Null
    // while unsure where we are.
    public IReadOnlySet<Direction>? ShownOpenExits()
    {
        if (State.Confidence != RoomConfidence.Confirmed || State.ObservedExitDirections is not { } seen) return null;
        if (State.ClosedDoorDirections is not { Count: > 0 } shut) return seen;
        HashSet<Direction> open = new(seen);
        open.ExceptWith(shut);
        return open;
    }

    // "The exit to the <dir> just opened!" / "…just closed!" — a timed or
    // action-opened exit (a lever's hidden passage) changed while we stand here.
    // Keep the room's shown exits in step, so the engines cross an opened one
    // without re-pulling its lever and don't count on a shut one. Mid-move the
    // line could be about either room, so leave it.
    public void NoteNamedExitChanged(Direction dir, bool opened)
    {
        if (!_pending.IsEmpty) return;
        HashSet<Direction> exits = State.ObservedExitDirections is { } seen ? new(seen) : new();
        bool changed = opened ? exits.Add(dir) : exits.Remove(dir);
        if (!opened) ClearOpenDoorDirection(dir);
        if (!changed) return;
        State.ObservedExitDirections = exits;
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Exit {dir} reported {(opened ? "opened" : "closed")} — {(opened ? "added to" : "removed from")} the room's shown exits.");
    }

    // Tier-3 manual override — the user pointed at a room on the map and said
    // "I'm here". Hard sets the current room and promotes to Confirmed.
    public void SetLocated(RoomKey key, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();

        Room? room = _graph.GetRoom(key);
        if (room is null)
        {
            _log?.Log(LogSeverity.Warn, "RoomTracker",
                $"Manual locate refused: room key {key} not present in active graph.");
            return;
        }

        ClearPendingAndSteps();
        SetRoom(room, RoomConfidence.Confirmed, when, "manual locate", isStrictAnchor: true);
    }

    // Subscribed by AppServices to RoomGraphManager.GraphReloaded. The active set
    // just rebuilt — drop any per-set state and start over.
    public void OnGraphReloaded(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? _now();
        ClearPendingAndSteps();
        _history.Clear();
        IsInDarkRoom = false;
        EnteredBlind = false;
        SetRoom(room: null, RoomConfidence.Unknown, when, "graph reloaded");
    }

    // ----- FSM internals ----------------------------------------------

    private void ReconcileFromConfirmed(RoomObservation observation, DateTimeOffset when)
    {
        Room? current = State.CurrentRoom;
        if (current is not null && MatchesPredicted(current, observation))
        {
            // Same room — refresh timestamp, no state churn. Subset
            // match tolerates exits the live display hides relative
            // to the graph (closed doors, hidden / searchable, gated).
            State.LastUpdatedAt = when;
            RaiseStateChanged(RoomConfidence.Confirmed, RoomConfidence.Confirmed, current, current);
            return;
        }

        // We thought we knew where we were but the observation
        // disagrees. Three possibilities: (a) a 1-of-1 candidate exists
        // in the graph and the user got teleported / dragged → land
        // Confirmed at the new room; (b) a current-room neighbour
        // shipped without a Name (typical of ganghouse rooms on map 15)
        // and its ExitMask covers the observation → adopt the neighbour
        // and learn the observed name; (c) ambiguous / zero → escalate
        // to Suspect at the current room.
        if (TryFearMove(current, observation, when)) return;

        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 silent desync", isStrictAnchor: true);
            return;
        }

        if (current is not null
            && TryMatchNullNameNeighbour(current, observation, out Room? learned))
        {
            ClearPendingAndSteps();
            SetRoom(learned, RoomConfidence.Confirmed, when, "null-name neighbour matched + learned");
            return;
        }

        // Same-named-area recovery — most commonly a client relaunch. Hydrate
        // seeds Confirmed at the persisted LastKnownRoom (the last KNOWN unique
        // room) and primes RecentSteps with the walk taken since. In an ambiguous
        // area (e.g. Darkwood Forest, ~8 identical "Main Road" rooms) that anchor
        // is stale — the player walked deeper before quitting — so the first login
        // redisplay disagrees with it, yet no unique candidate and no null-name
        // neighbour resolves it. A single mismatch only bumps a Suspect strike and
        // never reaches the strike limit that would otherwise trigger replay, so
        // the player sits stranded on the wrong room. Project the persisted trail
        // forward from the history anchor here: when its endpoint matches the
        // observation we land Confirmed at the true room. Harmless mid-walk — a
        // stale non-anchor start over-walks to a room that won't match, so replay
        // returns false and we fall through to Suspect exactly as before.
        if (TryReplayRecover(observation, when)) return;

        // A hand-typed hidden-exit move / CMD teleport / drag lands us in a room
        // whose live display hides exits the graph knows about (the hidden exit we
        // just crossed, a closed door), so the exact (Name, ExitMask) search above
        // misses even though the destination is unique by name. The door-tolerant
        // covering-exits re-anchor otherwise only runs after three Suspect strikes;
        // a uniquely-named destination should re-latch on the first display rather
        // than freezing the marker at the source until the strike limit trips.
        if (TryReanchorByName(observation, when, "covering-exits into name-unique room")) return;

        EnterSuspect(when, $"observation mismatched from Confirmed; candidates={candidates.Count}");
    }

    private void ReconcileFromPending(RoomObservation observation, DateTimeOffset when)
    {
        Room? source = State.CurrentRoom;
        bool sourceUnderAnotherName = false;

        // Try to match against the head pending move first — that's the
        // oldest move the server is most likely to be confirming. The head may
        // be a cardinal step or a text exit ("go path"); both resolve to a
        // single deterministic graph edge out of the source room.
        if (_pending.TryPeek(out PendingMove head)
            && source is not null
            && TryResolvePendingExit(source, head, out RoomExit exit))
        {
            if (TryTakeHeldArrival(head, exit, observation, when)) return;

            Room? expected = _graph.GetRoom(exit.Target);
            string moveLabel = DescribeMove(head);
            string confirmed = $"move {moveLabel} confirmed";
            string? castBasis = null;

            // A cast-on-walk teleport is shown as the exit's own target and then the
            // landing: we are put in the first, shown it, sent on, and shown where
            // we came out. The first display for the step that fits the exit's
            // target is that room, and where it leaves us is the landing we hold
            // the item for, booked here and now, even though the display may read
            // exactly like a landing (the golden idol's passage, where that room
            // and both landings are the same one-exit room): its occupants aren't
            // the landing's. The landing's own display then finds the step off the
            // queue and is read as the room we stand in.
            //
            // When no room was shown and we asked for one, what follows depends on
            // whose step it was. A drag's own display would have come with the line
            // that announced it, so the answer is the room we stand in and is read
            // for which landing that is. A step of our own may only be late, its
            // display then coming ahead of the answer, so it is read as ever; and
            // an answer showing the room the step was leaving says the game never
            // took the step.
            bool castAmbiguous = false;
            bool sentOn = false;
            bool bookedUnseen = false;
            bool castAsked = false;
            if (exit.CastLandings is not null)
            {
                castAsked = AskedForCastStep(head);
                bool standingRoom = castAsked && head.IsFollowDrag;
                bool showsTransit = expected is not null && MatchesPredicted(expected, observation);
                Room? onward = InferCastLanding(exit, out string held);
                Room? landing = PickCastLanding(exit, observation, out bool landingsAlike, out string basis);
                if (standingRoom && landing is not null)
                {
                    expected = landing;
                    castAmbiguous = landingsAlike;
                    castBasis = $"{basis}; no room was shown for the drag, so the room was asked for";
                    sentOn = true;
                    confirmed += $", sent on by the exit's spell ({castBasis})";
                }
                else if (showsTransit && onward is null)
                {
                    castBasis = $"left where the exit leads: {held}";
                    confirmed += $", and its spell leaves us here ({held})";
                }
                else if (showsTransit && standingRoom)
                {
                    castBasis = "still in the room the exit leads to when asked: its spell sent us nowhere";
                    confirmed += ", and its spell sent us nowhere (no room was shown for the drag; asked for, it is this one)";
                }
                else if (showsTransit)
                {
                    expected = onward;
                    castBasis = held;
                    sentOn = true;
                    bookedUnseen = true;
                    confirmed += $", shown the room it passes through and sent on by the exit's spell ({held})";
                }
                else if (landing is not null)
                {
                    expected = landing;
                    castAmbiguous = landingsAlike;
                    castBasis = basis;
                    sentOn = true;
                    confirmed += $", sent on by the exit's spell ({castBasis})";
                }
                else if (castAsked && !head.IsFollowDrag && _pending.Count == 1
                    && MatchesPredicted(source, observation)
                    && !(_everReceivedEcho && HeadMoveEchoed(head)))
                {
                    // Asked for, the room is the one the step was leaving, and the
                    // game never echoed the step: it was never taken. Off the queue
                    // with it, or the tracker waits on it for good.
                    EndCastCrossing();
                    DropMostRecentPending();
                    _log?.Log(LogSeverity.Info, "RoomTracker",
                        $"Move {moveLabel} through a teleporting exit was never answered, and the room asked for is still '{source.Name}' — un-counting the move.");
                    SetConfidence(RoomConfidence.Confirmed, when, $"move {moveLabel} never taken (the room asked for is the one it was leaving)");
                    return;
                }
            }

            // Strategy 1 — predicted neighbour matches.
            if (expected is not null && (bookedUnseen || MatchesPredicted(expected, observation)))
            {
                // Ambiguity guard: in an identically-named corridor the predicted
                // target and the source room match the SAME observation, so a passive
                // re-look redisplay of the source (an NPC walking through, a regen-tick
                // refresh, a post-combat redraw that landed while this move was still in
                // flight) satisfies this predicted-target match too. The causal tell is
                // the server's command ECHO: the game echoes the move you typed on the
                // prompt line ("[HP=..]:e") as it processes it, immediately before
                // rendering the destination room, whereas a spontaneous re-look carries
                // NO echo (you typed nothing to cause it). So when the observation also
                // matches the source AND the head move has NOT been echoed yet, it's that
                // stale re-look, not the arrival: fall through to the Strategy-1b passive-
                // redisplay path (stay Pending) so the move's real, echoed landing still
                // confirms here. This is timing-independent — the earlier heuristic
                // confirmed any matching redisplay that arrived after a 400ms floor, which
                // let a LATE spontaneous re-look phantom-advance the loop and fire the next
                // step from the source room (the "double move").
                // Exit-set discriminator: a passive re-look shows the SAME exits the
                // source last displayed. When a genuine move lands in a same-named
                // neighbour whose exits DIFFER (report paradigm-20260811-104042: a
                // `go path` from a "Darkwood Forest" {W,SW} into a "Darkwood Forest"
                // {N} — the source's CUMULATIVE graph mask still covers {N} from an
                // earlier visit, so the subset match above passes), the redisplay's
                // exits won't match the last live display, so it's the move's real
                // outcome, not a re-look. Only hold Pending when the exits are
                // unchanged (or there's no prior display to compare — the pure
                // identically-named-AND-exited corridor, where the echo is the only tell).
                //
                // A follow-drag is EXEMPT: the game drags a party follower with no typed
                // command, so there is no echo to wait for, and a follower never emits
                // stray re-looks — the game only redisplays a drag on a real arrival. So
                // its confirming display IS the move (report paradigm-20260811-122610: an
                // identical "Slum Street {N,S}" corridor where discarding the instant
                // drag-arrival stranded the anchor a room back and desynced). NoteFollowMove
                // flags the pending move exempt so it confirms immediately; self-typed
                // moves keep the echo gate.
                //
                // STATLINE DEPENDENCY + FALLBACK: the echo signal only works when the
                // user's statline is one InboundMoveEchoScanner can read (LineExtractor
                // splits the "[HP=..]:" prompt from its trailing command). A player can
                // set an arbitrary statline; if theirs isn't split, NO echo ever arrives,
                // and gating purely on !HeadMoveEchoed would hold every same-named-grid
                // move forever (a frozen walker). So we only trust the echo once we've
                // actually SEEN one this session (_everReceivedEcho); until then — i.e.
                // for a statline the scanner can't parse — fall back to the old 400ms
                // timing floor, which is no worse than the pre-echo behaviour. The common
                // default "[HP=..]:" prompts flip the latch on the first move.
                if (LooksLikeSourceRelook(source, head, observation, when))
                {
                    _log?.Log(LogSeverity.Debug, "RoomTracker",
                        $"Redisplay matches source '{source.Name}' AND predicted {moveLabel} target; " +
                        (_everReceivedEcho
                            ? "the move has not been echoed by the server yet"
                            : "no move-echo has been seen this session (statline not echo-readable), holding by timing") +
                        "; treating as a passive re-look and staying Pending.");
                    HoldDisplay(head, observation, when);
                    return;
                }

                _pending.TryDequeue(out _);
                State.SuspectStrikes = 0;
                if (castBasis is not null)
                {
                    LastCastLanding = (source.Key, expected.Key, castBasis, when);
                    if (sentOn) CastCrossingStarted?.Invoke(exit.Landing);
                    EndCastCrossing();
                    if (bookedUnseen)
                    {
                        _bookedOffAnotherRoomsDisplay = true;
                        BookCastLanding(expected, in exit, when, alreadyAsked: castAsked);
                    }
                }
                // Predicted-neighbour is a deduction — only strict (i.e.
                // worth persisting to LastKnownRoom) when the landing
                // room is also a 1-of-1 graph match for the observation.
                bool strict = !castAmbiguous && !bookedUnseen
                    && _graph.FindCandidates(observation.Name, observation.Exits).Count == 1;
                if (_pending.IsEmpty)
                    SetRoom(expected, RoomConfidence.Confirmed, when, confirmed, isStrictAnchor: strict);
                else
                {
                    // More moves still in flight — land Confirmed at
                    // the new room (we know where we are) but keep
                    // Pending posture if more confirmations are due.
                    SetRoom(expected, RoomConfidence.Pending, when, $"{confirmed}, queue not empty");
                    ArmCastDisplayWait();
                }
                MoveConfirmed?.Invoke();
                // The display fitted more than one of the spell's landings, so the
                // one we took rests on what we carry. Where the realm can say which
                // room this is, ask.
                if (castAmbiguous)
                    RequestAuthoritativeResync?.Invoke("a cast-on-walk teleport's landings look alike");
                return;
            }

            // Strategy 1a — predicted neighbour is null-name and the
            // observation's exits cover its ExitMask. Adopt the
            // neighbour, learn the observed name, fire NameLearned.
            // Without this branch a walk INTO a null-name ganghouse
            // room never resolves: MatchesPredicted fails on the null
            // name, candidate search returns 0, tracker bumps Suspect.
            //
            // The same holds for a neighbour the data leaves unnamed that bears a
            // name learned earlier, when the display has another (GAME_MECHANICS
            // "Rooms the data leaves unnamed": a gang's house can be renamed). The
            // name it bears is then no evidence against having arrived, and the
            // exits decide exactly as they do for a room with no name. Only here,
            // with a move in flight that predicts the room: the rooms of one house
            // can share a name and exits, and nothing else tells them apart.
            //
            // A display that fits isn't always the arrival: ReadUnnamedArrival
            // weighs the rooms nearby that could have shown it.
            UnnamedArrival arrival = expected is not null && ArrivesInRoomTheDataLeavesUnnamed(expected, observation)
                ? ReadUnnamedArrival(source, head, expected, observation, when)
                : UnnamedArrival.Declined;
            if (arrival == UnnamedArrival.NotYet)
            {
                _log?.Log(LogSeverity.Debug, "RoomTracker",
                    $"Display '{observation.Name}' fits {expected!.Key} ahead, but a room nearby could have shown it and move {moveLabel} " +
                    (_everReceivedEcho ? "has not been echoed yet" : "was sent too recently") + "; staying Pending.");
                HoldDisplay(head, observation, when);
                return;
            }
            if (arrival == UnnamedArrival.Taken)
            {
                bool renamed = !expected!.HasUnknownName;
                string bore = expected.Name;
                Room? learned = _graph.LearnRoomName(expected.Key, observation.Name);
                if (learned is not null)
                {
                    _log?.Log(LogSeverity.Info, "RoomTracker", renamed
                        ? $"Learned name '{observation.Name}' for {expected.Key} (pending target; it bore '{bore}', and the data gives it no name)."
                        : $"Learned name '{observation.Name}' for {expected.Key} (pending-target null-name match).");
                    NameLearned?.Invoke(new NameLearnedEvent(expected.Key, observation.Name));
                    _pending.TryDequeue(out _);
                    State.SuspectStrikes = 0;
                    RoomConfidence target = _pending.IsEmpty
                        ? RoomConfidence.Confirmed
                        : RoomConfidence.Pending;
                    SetRoom(learned, target, when,
                        $"move {moveLabel} confirmed ({(renamed ? "name re-learned" : "null-name learned")})");
                    ArmCastDisplayWait();
                    MoveConfirmed?.Invoke();
                    return;
                }
            }

            // Strategy 1b — passive redisplay of the source room while a
            // move is still pending. CONFIRMED game mechanic: a refused
            // ("bonked") move NEVER redisplays the room — every refusal
            // instead prints an explicit line (wording varies by bonk
            // type: "There is no exit in that direction!", "You can't go
            // that way.", "The door is closed.", etc.), which
            // MovementRefusalDetector catches and routes to
            // NoteMoveBlocked. So a redisplay that still matches the
            // source can only be a passive re-look (a combat-clear, an
            // arrival/departure notice, a bare re-glance) — never the
            // pending move's outcome. Treat it as noise: keep the pending
            // move intact and stay Pending so the move's real result (a
            // different room) can still confirm via Strategy 1. A genuine
            // self-loop exit that lands back in the source room is caught
            // earlier by Strategy 1 (its target IS the source), so it
            // never reaches here. Dequeuing + confirming here — the old
            // "move-refused redisplay" behaviour — silently invented a
            // refusal that never happened, stranding the loop at the
            // source while the player had actually moved on, and cascaded
            // into a bogus recovery that bonked a real exit at the true
            // room.
            if (MatchesPredicted(source, observation))
            {
                _log?.Log(LogSeverity.Debug, "RoomTracker",
                    $"Passive redisplay of source '{source.Name}' while move {moveLabel} pending; ignoring.");
                HoldDisplay(head, observation, when);
                return;
            }

            sourceUnderAnotherName = !head.IsFollowDrag
                && !(_everReceivedEcho && HeadMoveEchoed(head))
                && MayBeSourceUnderAnotherName(source, observation);
        }

        // Neither predicted nor refused — fall back to graph search.
        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 candidate after Pending miss", isStrictAnchor: true);
            return;
        }

        // Exact (name, exits) missed — but a dark-corridor drift usually surfaces
        // in a uniquely-named lit room whose observed exits are a SUBSET of the
        // graph's (a closed door or unsearched hidden exit drops a bit, so the
        // exact search returns 0 even for a name-unique landmark). Re-latch on
        // that room via the same door-tolerant lookup the Confirmed and Suspect
        // paths already use, instead of accruing Suspect strikes — this is what
        // snaps the map back the moment the player leaves a dark maze for a named
        // room, rather than staying frozen at the last dead-reckoned anchor.
        if (TryReanchorByName(observation, when, "name-unique after Pending miss")) return;

        // The room being left, shown under a name it doesn't bear yet (we came
        // back into the game in it, renamed while we were away): nothing says
        // where we are is wrong, and the move's own display is still to come. A
        // Suspect here lost the house until we walked out of it to a room the map
        // is sure of. Logged at Info: with no echo, a step that landed somewhere
        // unexpected waits here where it used to go Suspect and ask for a resync.
        if (sourceUnderAnotherName)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Display '{observation.Name}' fits the room being left, {source!.Key}, which has not been shown since we came to it and bears '{source.Name}'; staying Pending.");
            State.LastUpdatedAt = when;
            return;
        }

        EnterSuspect(when, $"Pending observation didn't match queue head; candidates={candidates.Count}");
    }

    private void ReconcileFromSuspect(RoomObservation observation, DateTimeOffset when)
    {
        // A stationary player in an ambiguous room (identical "Main Road" rooms,
        // "Darkwood Forest" {SW} with dozens of candidates) can't be resolved by
        // name+exits, so we sit in Suspect with no anchor. While parked there,
        // every passive redisplay of the same room — an Enter echo, a
        // cash-on-ground notice, a party arrival line — re-enters here. Without
        // this guard each identical redisplay accrues a Suspect strike and the
        // player is declared Lost (marker vanishes) despite never having moved.
        // Only a redisplay that follows an actual move can carry new information,
        // so a repeat with no move since we last processed it is a no-op.
        if (IsRepeatRedisplayWithoutMove(observation))
        {
            State.LastUpdatedAt = when;
            return;
        }

        Room? current = State.CurrentRoom;
        if (current is not null && MatchesPredicted(current, observation))
        {
            // Glitch resolved — server caught up. Back to Confirmed.
            // No PersistConfirmedAnchor — re-confirming the same room
            // we were already at adds no strict signal beyond what was
            // already on disk.
            State.SuspectStrikes = 0;
            ClearPendingAndSteps();
            SetConfidence(RoomConfidence.Confirmed, when, "suspect resolved (current room re-confirmed)");
            return;
        }

        if (TryFearMove(current, observation, when)) return;

        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 candidate from Suspect", isStrictAnchor: true);
            return;
        }

        // Still ambiguous. With no engine driving, narrow across the moves we're
        // making by hand before escalating; a convergence re-anchors us here. Runs
        // on every ambiguous Suspect observation so the footprint keeps closing in
        // during the wander, not only once the strike limit trips.
        if (TryPassiveGridLocate(observation, when)) return;

        // Still mismatched. Either escalate or replay.
        if (State.SuspectStrikes + 1 >= SuspectStrikeLimit)
        {
            if (TryReplayRecover(observation, when)) return;
            if (TryReanchorByName(observation, when, "suspect strike limit")) return;
            SetRoom(room: null, RoomConfidence.Lost, when,
                $"suspect strike limit ({SuspectStrikeLimit}) reached; replay + name re-anchor failed");
            return;
        }

        EnterSuspect(when, $"suspect mismatch continues; candidates={candidates.Count}");
    }

    // True when the incoming observation is identical to the last one we fully
    // processed AND no move command has gone out since then — i.e. the player is
    // standing still and the server just redisplayed the same room. Such a
    // redisplay carries no new position signal, so the caller skips the Suspect
    // strike that would otherwise march a parked-in-ambiguity player to Lost.
    private bool IsRepeatRedisplayWithoutMove(RoomObservation observation)
    {
        if (_lastObservation is not { } prev) return false;
        if (!SameObservation(prev, observation)) return false;
        if (LastMoveSentAt is { } sent && sent > _lastObservationAt) return false;
        return true;
    }

    private static bool SameObservation(RoomObservation a, RoomObservation b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && a.Exits.SetEquals(b.Exits);

    private void LandFromCandidateSearch(RoomObservation observation, DateTimeOffset when)
    {
        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);

        switch (candidates.Count)
        {
            case 1:
                Room? room = _graph.GetRoom(candidates[0]);
                if (room is null)
                {
                    SetRoom(room: null, RoomConfidence.Lost, when, "graph inconsistency");
                    return;
                }
                ClearPendingAndSteps();
                SetRoom(room, RoomConfidence.Confirmed, when, "1-of-1 candidate", isStrictAnchor: true);
                break;

            case 0:
                if (TryReplayRecover(observation, when)) return;
                if (TryReanchorByName(observation, when, "no exact candidate")) return;
                SetRoom(room: null, RoomConfidence.Lost, when, "no graph candidate; replay + name re-anchor failed");
                break;

            default:
                // Ambiguous from Unknown / Lost — no single graph room fits this
                // display. With no engine driving, narrow across the moves we're
                // making by hand before giving up; a convergence re-anchors us.
                if (TryPassiveGridLocate(observation, when)) return;
                // Still ambiguous — drop into Suspect with no anchor room (we never
                // had one). Counter stays distinct so the next observation can
                // either resolve or trip Lost on its own merits.
                SetRoom(room: null, RoomConfidence.Suspect, when,
                    $"{candidates.Count} candidates (ambiguous)");
                break;
        }
    }

    // Walk _recentSteps through the graph starting at the most-recent
    // HistoryEntry. If the projected endpoint matches observation, land Confirmed
    // there. Returns true when recovery succeeded; the caller proceeds to Lost on
    // false.
    private bool TryReplayRecover(RoomObservation observation, DateTimeOffset when)
    {
        // Nothing to replay is the common "no pending trail" case (any mismatch
        // with a clean step list) — stay silent so it doesn't drown the Debug
        // channel. Once there's a real trail to project we trace every abort
        // below, so a "still lost after a client restart" bug report shows
        // exactly where the projection diverged.
        if (_history.First is null) return false;
        if (_recentSteps.Count == 0) return false;

        // Walk forward through the persisted steps from the room the trail is anchored
        // at — where the first buffered step was sent — NOT _history.First (the newest
        // confirmed room), which drifts ahead of the trail during ambiguous non-strict
        // confirms and made the projection abort on step 0 (report paradigm-20260818-081025).
        // Falls back to _history.First when no anchor was captured (legacy / just-hydrated).
        RoomKey start = _stepsAnchor ?? _history.First.Value.Room;
        Room? cursor = _graph.GetRoom(start);
        if (cursor is null)
        {
            _log?.Debug("RoomTracker",
                $"Replay-recovery abort: history anchor {start} isn't in the active graph.");
            return false;
        }

        foreach (DirectionDto step in _recentSteps)
        {
            RoomExit exit;
            if (step.Cardinal is { } direction)
            {
                if (!cursor.Exits.TryGetValue(direction, out exit))
                {
                    _log?.Debug("RoomTracker",
                        $"Replay-recovery abort: no {direction} exit from {cursor.Key} ({cursor.Name}).");
                    return false;
                }
            }
            else if (step.Command is { } command && TryResolveTextExit(cursor, command, out exit))
            {
                // Text-exit step ("go path") — a deterministic graph edge, so
                // it replays through the cursor room's matching Text exit just
                // like a cardinal step. Resolved into exit above.
            }
            else
            {
                _log?.Debug("RoomTracker",
                    $"Replay-recovery abort at {cursor.Key} ({cursor.Name}): step '{step.Command ?? "?"}' matches no exit out of this room.");
                return false;
            }

            // A cast-on-walk exit's target is only passed through: the trail goes on
            // from where its spell put us.
            Room? next = PredictedLanding(exit);
            if (next is null)
            {
                _log?.Debug("RoomTracker",
                    $"Replay-recovery abort: step out of {cursor.Key} targets {exit.Target}, which isn't in the graph.");
                return false;
            }
            cursor = next;
        }

        if (!MatchesPredicted(cursor, observation))
        {
            _log?.Debug("RoomTracker",
                $"Replay-recovery no match: {start} + {_recentSteps.Count} steps → {cursor.Key} ({cursor.Name}), " +
                $"but the redisplay was '{observation.Name}' — falling through to the mismatch path.");
            return false;
        }

        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Replay-recovery succeeded: {start} + {_recentSteps.Count} steps → {cursor.Key} ({cursor.Name}).");

        ClearPendingAndSteps();
        SetRoom(cursor, RoomConfidence.Confirmed, when, "replay-from-last-Confirmed succeeded");
        return true;
    }

    // Last-ditch re-anchor before Lost: when the exact (Name, ExitMask) search
    // misses because a closed door / hidden exit changed the observed mask, fall
    // back to the name bucket and accept a 1-of-1 reachable room whose graph
    // exits COVER the observation. A name that's unique in the reachable graph
    // re-latches through the door instead of freezing the marker at Lost.
    // Ambiguous (>1) or empty results defer to the caller's Lost/Suspect path.
    // Deliberately NOT a strict anchor — like replay-recovery, this is a
    // deduction, so it updates in-memory state without overwriting the stronger
    // on-disk LastKnownRoom.
    private bool TryReanchorByName(RoomObservation observation, DateTimeOffset when, string context)
    {
        IReadOnlyList<RoomKey> covering = _graph.FindByNameCoveringExits(observation.Name, observation.Exits);
        if (covering.Count != 1) return false;
        if (_graph.GetRoom(covering[0]) is not { } room) return false;

        ClearPendingAndSteps();
        SetRoom(room, RoomConfidence.Confirmed, when,
            $"name-unique re-anchor ({context}); observed exits subset of graph exits");
        return true;
    }

    // Engine-independent recovery for a same-name grid with nobody driving. Called
    // from the ambiguous (>1 candidate) Suspect / Lost paths, where the single-endpoint
    // replay + 1-of-1 + name-covering deductions have all missed. Seeds the SLAM
    // footprint from the current ambiguous observation's candidate set, then folds one
    // (move, observation) pair per subsequent call; a convergence to a single room
    // re-anchors us with NO command sent. Returns true only when it re-anchored — the
    // caller then returns; otherwise it falls through to its normal Suspect / Lost path
    // while the footprint keeps narrowing across the wander.
    //
    // Stands fully down whenever an engine is attached: the gate's own tier-2 forward
    // localiser owns recovery then, and two localisers must never both re-anchor.
    private bool TryPassiveGridLocate(RoomObservation observation, DateTimeOffset when)
    {
        if (_isEngineAttached?.Invoke() == true)
        {
            ClearPassiveGrid();
            return false;
        }

        if (!_passiveGridActive)
        {
            // Seed from the ambiguous observation. A unique (or empty) candidate set
            // isn't ours — the caller's 1-of-1 / name-covering paths handle those.
            IReadOnlyList<RoomKey> seeds = _graph.FindCandidates(observation.Name, observation.Exits);
            if (seeds.Count <= 1) return false;
            _passiveGrid.Reset(seeds);
            _passiveGridActive = true;
            _passivePendingMove = null;   // the seed already reflects where we stand now
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Passive grid locate seeded: {seeds.Count} candidates from '{observation.Name}' (no engine attached — narrowing by hand-walked moves, no commands sent).");
            return false;
        }

        // Fold the move that carried us to THIS observation. A redisplay with no move
        // since the last fold carries no new spatial constraint, so skip it.
        if (_passivePendingMove is not { } move) return false;
        _passivePendingMove = null;
        _passiveGrid.Step(move, observation);

        if (_passiveGrid.IsConverged && _graph.GetRoom(_passiveGrid.Candidates.Single()) is { } room)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Passive grid locate converged → {room.Name} {room.Key} after {_passiveGrid.Depth} moves; re-anchoring (no command sent).");
            ClearPendingAndSteps();   // also retires the passive footprint
            // A deduction, like replay-recovery — non-strict so it updates in-memory
            // position without overwriting the stronger on-disk LastKnownRoom anchor.
            SetRoom(room, RoomConfidence.Confirmed, when, "passive grid re-localise converged", isStrictAnchor: false);
            return true;
        }

        if (_passiveGrid.IsExhausted)
        {
            // The walked footprint fits no graph path (a stale map, or an unobserved
            // forced move such as fear). Drop it and re-seed from the next observation.
            _log?.Log(LogSeverity.Info, "RoomTracker",
                "Passive grid locate exhausted: walked footprint fits no graph path; re-seeding on the next observation.");
            ClearPassiveGrid();
        }
        return false;
    }

    private void ClearPassiveGrid()
    {
        _passivePendingMove = null;
        if (!_passiveGridActive) return;
        _passiveGrid.Clear();
        _passiveGridActive = false;
    }

    // Diagnostics for the bug report: whether the passive grid re-localiser is mid-narrow
    // and how far it has closed in, or idle. A "stuck Lost with automation off" report
    // shows here whether the passive path was even running and how ambiguous it still is.
    public string PassiveGridStatus => _passiveGridActive
        ? $"active — {_passiveGrid.Candidates.Count} candidates, depth {_passiveGrid.Depth}"
        : "idle";

    private void EnterSuspect(DateTimeOffset when, string reason)
    {
        int strikes = State.SuspectStrikes + 1;
        State.SuspectStrikes = strikes;
        Room? prevRoom = State.CurrentRoom;
        RoomConfidence prev = State.Confidence;
        State.Confidence = RoomConfidence.Suspect;
        State.LastUpdatedAt = when;
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Suspect strike {strikes}/{SuspectStrikeLimit}: {reason}.");
        // Ask for an authoritative `rm` fix on the way into Suspect. AppServices
        // gates this to the no-engine case (the recovery gate covers engine-driven
        // walks), and the resolver throttles + no-ops on stock realms, so this only
        // rescues the manual-transport gap (boat disembark, teleport drag) that the
        // gate can't see.
        RequestAuthoritativeResync?.Invoke(reason);
        RaiseStateChanged(prev, RoomConfidence.Suspect, prevRoom, prevRoom);
    }

    // Resolve the graph exit a pending move takes out of the source room. A
    // cardinal move indexes the exit slot directly; a text-exit move ("go path")
    // is a deterministic edge too — the source room's Text exit carries the exact
    // target + direction, so we follow it rather than skipping prediction. A
    // null-cardinal go-path would otherwise fall through to name+exits candidate
    // search, which can't match a go-path destination (its hidden text exit sits
    // in the graph ExitMask but never on the "Obvious exits:" line). Returns
    // false when the move maps to no known exit (stale graph, mistyped command).
    private static bool TryResolvePendingExit(Room source, PendingMove head, out RoomExit exit)
    {
        if (head.Cardinal is { } direction)
            return source.Exits.TryGetValue(direction, out exit);
        if (head.Command is { } command)
            return TryResolveTextExit(source, command, out exit);
        exit = default;
        return false;
    }

    // A display that also fits the room a move is leaving, with nothing to say the
    // move has been carried out: a passive re-look of that room, not the move's
    // outcome. The reasoning is set out at the ambiguity guard in
    // ReconcileFromPending.
    private bool LooksLikeSourceRelook(Room source, PendingMove head, RoomObservation observation, DateTimeOffset when)
    {
        bool notEchoedOrTimingHold = _everReceivedEcho
            ? !HeadMoveEchoed(head)                             // echo-capable statline: causal, timing-independent
            : (when - head.SentAt < AmbiguousRedisplayFloor);  // never echoed (custom statline): timing fallback
        return !head.IsFollowDrag
            && MatchesPredicted(source, observation)
            && notEchoedOrTimingHold
            && RedisplayExitsUnchanged(observation);
    }

    // The landing a cast-on-walk teleport takes going by what we have: the first
    // entry whose item we hold, or the first with no item check, the order the game
    // tries the textblock's lines in. Holding counts carried, worn and key-ring
    // alike, as the game's `checkitem` does (GAME_MECHANICS "Room-command
    // refusals"). While the inventory is unread it is the first landing, the one
    // the route beyond the exit counts on. Null when we hold none of the items and
    // no entry is unconditional: the spell moves us nowhere and we stay in the
    // exit's target.
    private Room? InferCastLanding(in RoomExit exit, out string basis)
    {
        basis = "holding nothing its spell asks for";
        if (exit.CastLandings is not { } landings) return null;
        int missing = 0;
        foreach ((int itemId, RoomKey room) in landings)
        {
            if (itemId == 0)
            {
                basis = missing > 0 ? $"not holding item {missing}" : "its only landing";
                return _graph.GetRoom(room);
            }
            switch (_holdsItem?.Invoke(itemId))
            {
                case true:
                    basis = $"holding item {itemId}";
                    return _graph.GetRoom(room);
                case null:
                    basis = $"inventory not read yet, so taken as holding item {landings[0].ItemId}";
                    return _graph.GetRoom(exit.Landing);
            }
            missing = itemId;
        }
        return null;
    }

    // Where a cast-on-walk teleport put us, read off a display of the room we stand
    // in. The display outranks the inventory: one that fits a single landing
    // settles it whatever we think we hold. When it fits several (the golden idol's
    // two landings are both a one-exit "Earthen Catacombs"), the inventory picks,
    // and ambiguous tells the caller the room is a deduction. Null when the display
    // fits no landing.
    private Room? PickCastLanding(in RoomExit exit, RoomObservation observation, out bool ambiguous, out string basis)
    {
        ambiguous = false;
        basis = string.Empty;
        if (exit.CastLandings is not { } landings) return null;

        List<Room> fitting = LandingsFitting(landings, observation);
        if (fitting.Count == 0) return null;

        ambiguous = fitting.Count > 1;
        if (InferCastLanding(exit, out string held) is { } inferred
            && fitting.Find(r => r.Key == inferred.Key) is { } pick)
        {
            basis = ambiguous ? $"{held}; its landings look alike" : held;
            return pick;
        }
        basis = ambiguous ? "its landings look alike" : "the room shown fits only this landing";
        return fitting[0];
    }

    private List<Room> LandingsFitting(IReadOnlyList<(int ItemId, RoomKey Room)> landings, RoomObservation observation)
    {
        List<Room> fitting = new(landings.Count);
        foreach ((int _, RoomKey key) in landings)
        {
            if (fitting.Exists(r => r.Key == key)) continue;
            if (_graph.GetRoom(key) is { } room && MatchesPredicted(room, observation)) fitting.Add(room);
        }
        return fitting;
    }

    // The room a pending move should end in when nothing on screen can say: an
    // ordinary exit's target, or for a cast-on-walk teleport the landing our
    // inventory points at.
    private Room? PredictedLanding(in RoomExit exit) =>
        (exit.CastLandings is null ? null : InferCastLanding(exit, out _)) ?? _graph.GetRoom(exit.Target);

    // Find the room's Text exit whose comma-separated command alternatives
    // include command (case-insensitive). One matched token ("go path") is enough
    // — the exit's Target + direction are the deterministic landing.
    private static bool TryResolveTextExit(Room room, string command, out RoomExit exit)
    {
        foreach (RoomExit candidate in room.Exits.Values)
        {
            if (candidate.TextCommands is not { } commands) continue;
            foreach (string token in commands)
            {
                if (string.Equals(token, command, StringComparison.OrdinalIgnoreCase))
                {
                    exit = candidate;
                    return true;
                }
            }
        }
        exit = default;
        return false;
    }

    // Human-readable label for a pending move — its cardinal short form, or the
    // verbatim text command for text exits. Used only in log / reason strings.
    private static string DescribeMove(PendingMove move) =>
        move.Cardinal?.ToString() ?? move.Command ?? "?";

    // Looser match: name match plus observed-exits-are-subset of graph-exits.
    // Tolerates "Obvious exits:" hiding closed doors / searchable / conditional
    // exits the graph still knows about. Strict equality fired too often on real
    // game data.
    // While afraid, the game runs us through the room's obvious exits — only those on
    // the last `Obvious exits:` list we saw, never a hidden or text exit (GAME_MECHANICS
    // "Fear"). So an unexpected display is most likely the room one of them leads to:
    // when exactly one such neighbour matches it, follow there. An ambiguous or
    // unmatched display falls through to the ordinary recovery.
    private bool TryFearMove(Room? current, RoomObservation observation, DateTimeOffset when)
    {
        if (current is null || _isFeared?.Invoke() != true) return false;
        IReadOnlySet<Direction>? seen = State.ObservedExitDirections;
        Room? match = null;
        Direction via = default;
        foreach ((Direction dir, RoomExit exit) in current.Exits)
        {
            bool obvious = seen is { Count: > 0 }
                ? seen.Contains(dir)
                : exit.Hint is not (RoomExitHint.Text or RoomExitHint.Teleport
                    or RoomExitHint.SearchableHidden or RoomExitHint.MultiActionHidden);
            if (!obvious || _graph.GetRoom(exit.Target) is not { } next || !MatchesPredicted(next, observation))
                continue;
            if (match is not null && match.Key != next.Key) return false;   // two neighbours fit
            (match, via) = (next, dir);
        }
        if (match is null) return false;
        ClearPendingAndSteps();
        SetRoom(match, RoomConfidence.Confirmed, when, $"fear-moved {via}");
        return true;
    }

    private static bool MatchesPredicted(Room target, RoomObservation observation)
    {
        if (!string.Equals(target.Name, observation.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;

        // Subset: every observed exit is present in the graph.
        return (observedMask & target.ExitMask) == observedMask;
    }

    // True when this observation shows the SAME name + exits as the last one we
    // fully processed — i.e. it could be a passive re-look of the room we were
    // already in. A missing prior display counts as unchanged: with nothing to
    // compare, we can't prove a move happened, so the timing-based ambiguity guard
    // stays in force (the identically-named-AND-exited corridor case). When the
    // exits differ, the room's content changed — a real move — so the guard must
    // not swallow it. Compares live-display exit SETS (not the cumulative graph
    // mask), which is what separates a re-look from a same-named neighbour.
    private bool RedisplayExitsUnchanged(RoomObservation observation)
    {
        if (_lastObservation is not { } prev) return true;
        if (!string.Equals(prev.Name, observation.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        uint prevMask = 0;
        foreach (Direction d in prev.Exits) prevMask |= 1u << (int)d;
        uint obsMask = 0;
        foreach (Direction d in observation.Exits) obsMask |= 1u << (int)d;
        return prevMask == obsMask;
    }

    // The most recent command the server echoed back, and when. Set by
    // NoteInboundMoveEcho; read by HeadMoveEchoed. Single slot — see
    // NoteInboundMoveEcho for why that's sufficient.
    private (string Command, DateTimeOffset At)? _lastInboundEcho;

    // Latches true the first time NoteInboundMoveEcho fires this session — proof
    // the user's statline is one the echo scanner can read. Until then the re-look
    // guard can't trust the ABSENCE of an echo (a custom statline the scanner
    // can't split never produces one), so it falls back to AmbiguousRedisplayFloor.
    private bool _everReceivedEcho;

    // Timing fallback for the re-look guard when no move-echo is available (a
    // statline the scanner can't parse). A real move's confirming display takes a
    // full server round-trip; a stray same-room re-look landing faster than this is
    // treated as noise. This is the pre-echo heuristic, kept only as the fallback —
    // echo-readable statlines use the causal echo signal instead, which is why the
    // reported "double move" through identically-named grids needed the echo gate.
    private static readonly TimeSpan AmbiguousRedisplayFloor = TimeSpan.FromMilliseconds(400);

    // Has the server echoed the head pending move since it was sent? True only
    // when the most recent inbound echo BOTH matches the head move's command AND
    // arrived at/after the move was sent — i.e. the server is processing THIS move,
    // so the next matching room display is its genuine landing rather than a stray
    // re-look. The "at/after SentAt" bound is what keeps a stale echo from an
    // already-confirmed earlier move (same direction) from applying to a later one.
    // Never consulted for a follow-drag: that path is exempted in the guard above
    // (the game drags a follower with no typed command, so there is no echo).
    private bool HeadMoveEchoed(PendingMove head) =>
        _lastInboundEcho is { } echo
        && echo.At >= head.SentAt
        && EchoMatchesHead(echo.Command, head);

    // Does this line name the move currently in flight? Lets the echo scanner
    // recognise an echo that arrived detached from the prompt without having to
    // forward every bare line as a candidate: only the exact token of the move
    // we're waiting on qualifies, so ordinary content (a room name, a chat line)
    // can never be mistaken for an echo.
    public bool MatchesPendingHeadMove(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        return _pending.TryPeek(out PendingMove head) && EchoMatchesHead(command.Trim(), head);
    }

    // Does an echoed command string name the head pending move? A cardinal move
    // echoes its wire token ("e", "ne", "u"); a text-exit move ("go path") echoes
    // the verbatim command. Case-insensitive.
    private static bool EchoMatchesHead(string echoed, PendingMove head)
    {
        if (head.Cardinal is { } dir)
            return DirectionExtensions.TryFromToken(echoed, out Direction d) && d == dir;
        if (head.Command is { } cmd)
            return string.Equals(echoed, cmd, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // Subset-only match against a null-name target. Used by the null-name
    // learning path — we have no name to match against, so the ExitMask is the
    // only signal that the observation is "this neighbour".
    private static bool MatchesPredictedNameless(Room target, RoomObservation observation)
    {
        if (!target.HasUnknownName) return false;
        return ObservedExitsAreOf(target, observation);
    }

    private static bool ObservedExitsAreOf(Room target, RoomObservation observation)
    {
        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;
        return (observedMask & target.ExitMask) == observedMask;
    }

    // The nameless match for a room the data leaves unnamed that bears a learned
    // name: the display has another name, and its exits are the room's. Never
    // true of a room the data names; the name the data gives is held to.
    private bool MatchesPredictedRenamed(Room target, RoomObservation observation) =>
        !target.HasUnknownName
        && _graph.IsUnnamedInData(target.Key)
        && !string.Equals(target.Name, observation.Name, StringComparison.OrdinalIgnoreCase)
        && ObservedExitsAreOf(target, observation);

    // Whether the display fits a room the data leaves unnamed under a name it
    // doesn't bear: a room with no name, or one bearing a learned name the
    // display doesn't have, whose exits cover the ones shown. The name shown is
    // no evidence either way: it is the sysop's own text, often one the data has
    // elsewhere (a "Kitchen", a "Hallway"). ReadUnnamedArrival says whether the
    // display is the arrival there.
    private bool ArrivesInRoomTheDataLeavesUnnamed(Room target, RoomObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.Name)
        && (MatchesPredictedNameless(target, observation) || MatchesPredictedRenamed(target, observation));

    private enum UnnamedArrival { Taken, NotYet, Declined }

    // Whether a display that fits the room the head move is heading for, a room
    // the data leaves unnamed (ArrivesInRoomTheDataLeavesUnnamed), is the arrival
    // there, to be adopted and named from it. What decides is which other rooms
    // could have shown it. A room anywhere on the map could, by name and exits,
    // but the walk rules those out: the room one step along the move just sent is
    // the one shown. It leaves open the room being left (re-looked: someone walked
    // in) and that room's other neighbours (a `look <dir>` answered first).
    //
    // Where the statline echoes moves, the echo settles it: the game echoes a
    // move before it shows the room the move reaches, so a display before the
    // echo that one of those rooms could have shown is that room's (NotYet), and
    // a display after it is the arrival. A follow drag is only redisplayed on
    // arrival. With no echo, nothing tells them apart:
    //   - a room that bears a name is held to the timing test Strategy 1 makes for
    //     a re-look of the room being left (NotYet);
    //   - a room the data names beside the room being left, whose own display
    //     this could be, is taken to be the one shown (Declined), and the name
    //     re-anchor further down finds it. A `look <dir>` into it is then dropped
    //     as the look's (ObservationLooksLikePendingMoveOutcome). For a room that
    //     bears a name, the room being left counts too, when the data names it:
    //     shown or not since we came to it, its re-look is as near;
    //   - otherwise the display is the arrival, a room with no name taken on its
    //     exits alone as it always was.
    private UnnamedArrival ReadUnnamedArrival(
        Room source, PendingMove head, Room target, RoomObservation observation, DateTimeOffset when)
    {
        if (head.IsFollowDrag) return UnnamedArrival.Taken;
        if (_everReceivedEcho)
        {
            bool anotherRoomCouldShowIt = LooksLikeSourceRelook(source, head, observation, when)
                || MayBeSourceUnderAnotherName(source, observation)
                || NamedNeighbourCouldShow(source, target, observation, orTheRoomBeingLeft: true);
            return HeadMoveEchoed(head) || !anotherRoomCouldShowIt ? UnnamedArrival.Taken : UnnamedArrival.NotYet;
        }
        if (!target.HasUnknownName && LooksLikeSourceRelook(source, head, observation, when)) return UnnamedArrival.NotYet;
        return NamedNeighbourCouldShow(source, target, observation, orTheRoomBeingLeft: !target.HasUnknownName)
            ? UnnamedArrival.Declined
            : UnnamedArrival.Taken;
    }

    // Whether the display could be the room being left under a name it doesn't
    // bear: a room the data leaves unnamed, not shown since we came to it (we came
    // back into the game in it, or were put there by hand), whose exits are the
    // display's. It may have been renamed while we were away, so the name it bears
    // is no evidence that the display isn't of it.
    private bool MayBeSourceUnderAnotherName(Room source, RoomObservation observation) =>
        _graph.IsUnnamedInData(source.Key)
        && !string.IsNullOrWhiteSpace(observation.Name)
        && !string.Equals(source.Name, observation.Name, StringComparison.OrdinalIgnoreCase)
        && ObservedExitsAreOf(source, observation)
        && !(_lastObservation is { } shown && MatchesPredicted(source, shown));

    // Whether a room the data names, a neighbour of the room being left other than
    // the room ahead (or, orTheRoomBeingLeft, that room itself), could have shown
    // this display as its own (RoomGraphManager.NamedInDataCouldShow).
    private bool NamedNeighbourCouldShow(Room source, Room target, RoomObservation observation, bool orTheRoomBeingLeft = false)
    {
        if (orTheRoomBeingLeft && _graph.NamedInDataCouldShow(source.Key, observation.Name, observation.Exits)) return true;
        foreach (RoomExit exit in source.Exits.Values)
            if (exit.Target != target.Key && _graph.NamedInDataCouldShow(exit.Target, observation.Name, observation.Exits))
                return true;
        return false;
    }

    // With no echo on the statline: the last display held as possibly a re-look of
    // the room being left, and the move it was held for (HoldDisplay).
    private (PendingMove Head, RoomObservation Display)? _heldDisplay;

    private void HoldDisplay(PendingMove head, RoomObservation observation, DateTimeOffset when)
    {
        State.LastUpdatedAt = when;
        _heldDisplay = _everReceivedEcho ? null : (head, observation);
    }

    // With no echo, a display that looks like the room being left (same name, same
    // exits, too soon after the move) is held as a re-look of it (HoldDisplay). In
    // a house whose neighbouring rooms share a name and exits it may have been the
    // arrival, and the tracker then falls a step behind. When the next display for
    // the same move fits the room the move after it leads to, the held one was the
    // arrival: it is taken for the move, and this display for the next. Otherwise
    // this display would be taken for the room ahead, which would take the name of
    // the room beyond it. Only for a room the data leaves unnamed; a room the data
    // names is left to Strategy 1 as it always was.
    private bool TryTakeHeldArrival(PendingMove head, in RoomExit exit, RoomObservation observation, DateTimeOffset when)
    {
        if (_heldDisplay is not { } held || held.Head != head) return false;
        if (exit.CastLandings is not null || State.CurrentRoom is not { } source
            || _graph.GetRoom(exit.Target) is not { } target || !_graph.IsUnnamedInData(target.Key)) return false;
        PendingMove[] queued = _pending.ToArray();
        if (queued.Length < 2 || !TryResolvePendingExit(target, queued[1], out RoomExit next)
            || next.CastLandings is not null || _graph.GetRoom(next.Target) is not { } beyond) return false;

        bool heldNamesTarget = !MatchesPredicted(target, held.Display);
        if (heldNamesTarget
            && (!ArrivesInRoomTheDataLeavesUnnamed(target, held.Display)
                || NamedNeighbourCouldShow(source, target, held.Display, orTheRoomBeingLeft: true))) return false;
        // The room ahead shown under the name it bears is its own arrival, and the
        // held display, under another name, was the room being left re-looked.
        if (heldNamesTarget && MatchesPredicted(target, observation)) return false;
        // The new display must be the room beyond's. Where it could as well be the
        // room ahead under a new name, only the room beyond's own name says so.
        if (!MatchesPredicted(beyond, observation)
            && (!ArrivesInRoomTheDataLeavesUnnamed(beyond, observation)
                || (heldNamesTarget && ArrivesInRoomTheDataLeavesUnnamed(target, observation)))) return false;

        _heldDisplay = null;
        string moveLabel = DescribeMove(head);
        if (heldNamesTarget && _graph.LearnRoomName(target.Key, held.Display.Name) is { } learned)
        {
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Learned name '{held.Display.Name}' for {target.Key} (from the display held for move {moveLabel}; it bore '{target.Name}').");
            NameLearned?.Invoke(new NameLearnedEvent(target.Key, held.Display.Name));
            target = learned;
        }
        _pending.TryDequeue(out _);
        State.SuspectStrikes = 0;
        SetRoom(target, RoomConfidence.Pending, when,
            $"move {moveLabel} confirmed by the display held for it, the next move's room showing now");
        MoveConfirmed?.Invoke();
        ReconcileFromPending(observation, when);
        return true;
    }

    // Search the source room's cardinal neighbours for a null-name room whose
    // ExitMask covers the observation. If exactly one neighbour qualifies, learn
    // the observed name into the in-memory graph (via
    // RoomGraphManager.LearnRoomName) and return the updated Room for the FSM to
    // land on. Multiple matches are treated as ambiguous (no learn) — defer to
    // the standard Suspect path.
    private bool TryMatchNullNameNeighbour(
        Room source,
        RoomObservation observation,
        out Room? learned)
    {
        learned = null;
        if (string.IsNullOrWhiteSpace(observation.Name)) return false;

        Room? match = null;
        foreach ((Direction _, RoomExit exit) in source.Exits)
        {
            Room? candidate = _graph.GetRoom(exit.Target);
            if (candidate is null) continue;
            if (!MatchesPredictedNameless(candidate, observation)) continue;
            if (match is not null) return false;       // ambiguous — defer to Suspect
            match = candidate;
        }
        if (match is null) return false;
        // With no move to say which way we went, a room the data names beside us
        // that could have shown this as its own display is as near as the nameless
        // one, and is not given away: the name re-anchor finds it.
        if (NamedNeighbourCouldShow(source, match, observation)) return false;

        learned = _graph.LearnRoomName(match.Key, observation.Name);
        if (learned is null) return false;

        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Learned name '{observation.Name}' for {match.Key} (was unnamed).");
        NameLearned?.Invoke(new NameLearnedEvent(match.Key, observation.Name));
        return true;
    }

    private void SetRoom(
        Room? room,
        RoomConfidence confidence,
        DateTimeOffset when,
        string reason,
        bool isStrictAnchor = false)
    {
        Room? prevRoom = State.CurrentRoom;
        RoomConfidence prev = State.Confidence;

        State.CurrentRoom = room;
        State.Confidence = confidence;
        State.LastUpdatedAt = when;
        // ObservedExitDirections describes the last room we actually SAW. Any room
        // change routed through here that isn't a live observation — a blind /
        // dark-room dead-reckon advance, an `rm` manual locate, a Lost/replay
        // recovery — moves us somewhere we haven't seen, so the previous room's
        // exit set is now stale and must not answer the walker's "is this exit
        // already revealed?" pre-check. Clear it; NoteRoomObserved re-applies the
        // fresh set right after its reconcile switch on a real display. (A stale set
        // here made the walker skip the required `sea <dir>` and ram a hidden exit
        // — report paradigm-20260921-145800.)
        State.ObservedExitDirections = null;
        if (confidence == RoomConfidence.Confirmed) State.SuspectStrikes = 0;

        if (confidence == RoomConfidence.Confirmed && room is not null)
        {
            PushHistory(room.Key, when);
            // Only persist LastKnownRoom when this Confirmed transition
            // is anchored on (a) a true 1-of-1 graph match or (b) the
            // user's manual locate. Predicted-neighbour / null-name-
            // learned / replay-recovery Confirmed transitions still
            // update in-memory state but don't overwrite the on-disk
            // anchor — the existing anchor is a stronger trust signal
            // than any deduction.
            if (isStrictAnchor) PersistConfirmedAnchor(room, when);
        }

        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"{prev} → {confidence} ({reason}): " +
            (room is null ? "(no room)" : $"{room.Name} {room.Key}"));

        RaiseStateChanged(prev, confidence, prevRoom, room);
    }

    private void SetConfidence(RoomConfidence confidence, DateTimeOffset when, string reason)
    {
        RoomConfidence prev = State.Confidence;
        if (prev == confidence)
        {
            State.LastUpdatedAt = when;
            return;
        }
        State.Confidence = confidence;
        State.LastUpdatedAt = when;
        if (confidence == RoomConfidence.Confirmed) State.SuspectStrikes = 0;

        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"{prev} → {confidence} ({reason}).");

        RaiseStateChanged(prev, confidence, State.CurrentRoom, State.CurrentRoom);
    }

    private void RaiseStateChanged(
        RoomConfidence previousConfidence,
        RoomConfidence newConfidence,
        Room? previousRoom,
        Room? newRoom)
    {
        StateChanged?.Invoke(new RoomTransition(
            previousConfidence, newConfidence, previousRoom, newRoom, State.LastUpdatedAt,
            Displayed: _reconcilingDisplay && !_bookedOffAnotherRoomsDisplay));
    }

    // ----- queue / step / history housekeeping ------------------------

    private void EnqueuePending(PendingMove move)
    {
        LastMoveSentAt = move.SentAt;
        _pending.Enqueue(move);
        // Bounded queue — drain oldest entries past the cap.
        while (_pending.Count > PendingQueueCap && _pending.TryDequeue(out _)) { /* drop */ }
    }

    private void DropMostRecentPending()
    {
        if (_pending.IsEmpty) return;
        // ConcurrentQueue has no remove-tail; rebuild by drain + reinsert.
        var keep = new List<PendingMove>(_pending.Count);
        while (_pending.TryDequeue(out PendingMove m)) keep.Add(m);
        for (int i = 0; i < keep.Count - 1; i++) _pending.Enqueue(keep[i]);
        DropMostRecentStep();
    }

    // Peek (without dropping) the cardinal of the most-recently queued pending
    // move — the one a just-seen refusal line pertains to. Null when the queue is
    // empty or the tail was a text-command move carrying no cardinal.
    private Direction? MostRecentPendingCardinal() => MostRecentPending()?.Cardinal;

    // Peek the most-recently queued pending move. ConcurrentQueue has no
    // tail-peek, so drain + reinsert.
    private PendingMove? MostRecentPending()
    {
        if (_pending.IsEmpty) return null;
        var keep = new List<PendingMove>(_pending.Count);
        while (_pending.TryDequeue(out PendingMove m)) keep.Add(m);
        foreach (PendingMove m in keep) _pending.Enqueue(m);
        return keep.Count > 0 ? keep[^1] : null;
    }

    // Drop one direction from the cached open-door set (the door just reported
    // closed). Rebuilds the immutable set minus that direction; nulls it when the
    // last entry goes so the pre-open fast-path in both movement engines falls
    // back to the door FSM instead of trusting a stale "already open" reading.
    private void ClearOpenDoorDirection(Direction dir)
    {
        if (State.ClosedDoorDirections is not { } shut || !shut.Contains(dir))
            State.ClosedDoorDirections = new HashSet<Direction>(State.ClosedDoorDirections ?? new HashSet<Direction>()) { dir };
        if (State.OpenDoorDirections is not { } open || !open.Contains(dir)) return;
        var next = new HashSet<Direction>(open);
        next.Remove(dir);
        State.OpenDoorDirections = next.Count > 0 ? next : null;
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Door {dir} reported closed — cleared stale open-door flag; next move will re-open it.");
    }

    private void AppendStep(DirectionDto step)
    {
        // Anchor the replay trail at the room we're standing in as the FIRST buffered step
        // goes out — the room the whole chain projects from. Starting the projection at
        // _history.First instead (the newest confirmed room) overshoots after a run of
        // ambiguous non-strict confirms and aborts on step 0 (report paradigm-20260818-081025).
        if (_recentSteps.Count == 0) _stepsAnchor = State.CurrentRoom?.Key;
        _recentSteps.Add(step);
        PersistSteps();
    }

    private void DropMostRecentStep()
    {
        if (_recentSteps.Count == 0) return;
        _recentSteps.RemoveAt(_recentSteps.Count - 1);
        PersistSteps();
    }

    private void ClearPendingAndSteps()
    {
        // Position resolved by any means — retire any in-flight passive grid
        // narrowing so a fresh episode starts clean rather than folding stale
        // candidates. Cheap no-op when it wasn't running.
        ClearPassiveGrid();
        while (_pending.TryDequeue(out _)) { /* drain */ }
        EndCastCrossing();
        _stepsAnchor = null;
        if (_recentSteps.Count == 0) return;
        _recentSteps.Clear();
        PersistSteps();
    }

    private void PushHistory(RoomKey key, DateTimeOffset when)
    {
        _history.AddFirst(new HistoryEntry(key, when));
        while (_history.Count > HistoryCap) _history.RemoveLast();
    }

    private void PersistConfirmedAnchor(Room room, DateTimeOffset when)
    {
        if (_profile is null) return;
        _profile.LastKnownRoom = new RoomRef(room.Key.Map, room.Key.Room);
        // RecentSteps reset on Confirmed — clear in-memory + persist.
        _recentSteps.Clear();
        _stepsAnchor = null;
        _profile.RecentSteps = null;
        _ = when;                                                // reserved for future per-step timestamp persistence
    }

    private void PersistSteps()
    {
        if (_profile is null) return;
        _profile.RecentSteps = _recentSteps.Count == 0
            ? null
            : new List<DirectionDto>(_recentSteps);
    }
}

// One entry in the rolling confirmed-position history buffer.
internal readonly record struct HistoryEntry(RoomKey Room, DateTimeOffset ConfirmedAt);

// Payload of RoomTracker.StateChanged. Both PreviousConfidence/NewConfidence and
// PreviousRoom/NewRoom are surfaced so handlers can branch on what actually
// changed without re-querying RoomState (which would race with the next
// transition).
//
// Displayed is true when a room display decided the transition and NewRoom is the
// room that display showed: an arrival, a re-display of the room we stand in (raised
// with both rooms the same), the first display after a reconnect, a booked landing
// its own display corrects. It is false for every other way the tracker settles on
// a room: a dark or blind move, a manual locate, a profile's saved room, a move the
// game refused, a landing booked ahead of its own display. A consumer that reads
// what a room's display did or didn't show (death recovery: "no corpse on this
// floor") needs to know the room was shown at all.
public readonly record struct RoomTransition(
    RoomConfidence PreviousConfidence,
    RoomConfidence NewConfidence,
    Room? PreviousRoom,
    Room? NewRoom,
    DateTimeOffset At,
    bool Displayed = false);

// Payload of RoomTracker.NameLearned. Carries the room the tracker just adopted
// by ExitMask + the verbatim observed name it learned. The main window's
// prompt-handler decides whether the new name is kept with the realm's data
// (LearnedRoomNameStore); the game data files are never written.
public readonly record struct NameLearnedEvent(RoomKey Key, string ObservedName);
