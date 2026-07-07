using System.Collections.Concurrent;
using System.Collections.Generic;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

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

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;
    private readonly ConcurrentQueue<PendingMove> _pending = new();
    private readonly LinkedList<HistoryEntry> _history = new();
    private readonly List<DirectionDto> _recentSteps = new();

    // Profile the tracker is currently writing into. Set by Hydrate; cleared by
    // OnProfileClosed. When null, persistence operations are no-ops — the
    // tracker still runs in memory but doesn't touch any profile.
    private CharacterProfile? _profile;

    // Optional live-inventory snapshot provider. When set, NoteDeath records the
    // deathpile contents (worn + carried items) on the death record. Bound by
    // AppServices once the InventoryManager exists; null in tests / before
    // wiring, in which case death records simply carry no item lists.
    private Func<InventorySnapshot>? _inventorySnapshot;

    // Look-direction suppression deadline. While the wall clock is at or before
    // this timestamp, the next NoteRoomObserved call is treated as a peek (room
    // preview from a look <dir> command) and discarded. The 3-second window
    // auto-clears even if no peek display arrives, so the suppression can't eat
    // a future genuine observation.
    private DateTimeOffset? _suppressObservationUntil;
    private const int LookSuppressWindowMs = 3000;

    // Wall-clock time the most recent move command was enqueued, or null before
    // the first move this session. Lets observers tell a post-move room display
    // ("the room we just walked into") apart from a pre-move stale observation —
    // the RoomEntityClassifier uses it to avoid wiping the new room's
    // freshly-parsed occupants on the move-confirming transition (the occupants
    // line arrives before the exits line that confirms the move).
    public DateTimeOffset? LastMoveSentAt { get; private set; }

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
    // Subscribers — the main window in particular — prompt the user to write the
    // learned name back to the active game-data set's Rooms.json.
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

    public RoomTracker(RoomGraphManager graph) : this(graph, log: null) { }

    public RoomTracker(RoomGraphManager graph, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
        State.LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    // Snapshot of the rolling confirmed-position history, newest-first. [0] is
    // the most recently confirmed room (typically the current room); [1] the one
    // before it, and so on, capped at the internal history window. Used by
    // PartyComebackManager to walk the leader backwards along the path just taken
    // when a stranded follower sends @comeback without a target room.
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
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
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

        // Set Confirmed without writing back to the profile (we just
        // read from it). Bypass the SetRoom persistence path so we
        // don't immediately wipe RecentSteps before they're useful.
        Room? prev = State.CurrentRoom;
        RoomConfidence prevConf = State.Confidence;
        State.CurrentRoom = room;
        State.Confidence = RoomConfidence.Confirmed;
        State.SuspectStrikes = 0;
        State.LastUpdatedAt = DateTimeOffset.UtcNow;
        PushHistory(room.Key, State.LastUpdatedAt);
        _log?.Log(LogSeverity.Info, "RoomTracker",
            $"Hydrate: Confirmed at {room.Name} {room.Key} with {_recentSteps.Count} pending replay steps.");
        RaiseStateChanged(prevConf, RoomConfidence.Confirmed, prev, room);
    }

    // Called when the profile unloads. Detaches the persistence target.
    public void OnProfileClosed()
    {
        _profile = null;
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
        => NoteMoveSentCore(direction, isEngineAnnouncement: true, whenUtc ?? DateTimeOffset.UtcNow);

    private void NoteMoveSentCore(Direction direction, bool isEngineAnnouncement, DateTimeOffset when)
    {
        if (isEngineAnnouncement)
        {
            _cardinalEchoClaim = (direction, when + EchoClaimExpiry);
        }
        EnqueuePending(PendingMove.FromDirection(direction, when));
        AppendStep(new DirectionDto(direction));

        if (State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending)
        {
            SetConfidence(RoomConfidence.Pending, when, $"move {direction} sent");
        }
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
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        if (_cardinalEchoClaim is { Dir: var claimDir, ExpiresAt: var expiresAt }
            && claimDir == direction
            && when < expiresAt)
        {
            _cardinalEchoClaim = null;
            _log?.Log(LogSeverity.Debug, "RoomTracker",
                $"Consumed engine echo of move {direction}; not re-enqueued.");
            return;
        }
        NoteMoveSentCore(direction, isEngineAnnouncement: false, when);
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
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
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
    }

    private (Direction Dir, DateTimeOffset ExpiresAt)? _cardinalEchoClaim;
    private (string Command, DateTimeOffset ExpiresAt)? _textEchoClaim;
    // A move's bytes normally echo back within a few milliseconds, but a
    // synchronous map re-root on entering a new area can stall the round-trip;
    // the expiry only has to outlast that worst-case pump, while staying short
    // enough that an orphaned claim (echo never arrived) can't shadow a later
    // manual move of the same direction.
    private static readonly TimeSpan EchoClaimExpiry = TimeSpan.FromSeconds(2);

    // Text-exit move (e.g. "go path") — used by the look-direction-interception
    // work for arbitrary text commands that don't map to a Direction.
    public void NoteMoveSent(string command, Direction? cardinal = null, DateTimeOffset? whenUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        NoteMoveSentCore(command, cardinal, isEngineAnnouncement: true, whenUtc ?? DateTimeOffset.UtcNow);
    }

    private void NoteMoveSentCore(string command, Direction? cardinal, bool isEngineAnnouncement, DateTimeOffset when)
    {
        if (isEngineAnnouncement)
        {
            _textEchoClaim = (command, when + EchoClaimExpiry);
        }
        EnqueuePending(new PendingMove(cardinal, command, when));
        AppendStep(new DirectionDto(cardinal, command));

        if (State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending)
        {
            SetConfidence(RoomConfidence.Pending, when, $"move '{command}' sent");
        }
    }

    // The user typed a peek command (look <dir> or equivalent). Arm the
    // suppression flag so the next room display is treated as a preview and
    // dropped instead of being parsed as a move outcome. The flag auto-clears
    // after LookSuppressWindowMs.
    public void NoteLookSent(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        _suppressObservationUntil = when.AddMilliseconds(LookSuppressWindowMs);
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
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

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
        if (_graph.GetRoom(exit.Target) is not { } expected)
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
        string moveLabel = DescribeMove(head);
        RoomConfidence target = _pending.IsEmpty
            ? RoomConfidence.Confirmed
            : RoomConfidence.Pending;
        SetRoom(expected, target, when, $"dark-room advance via {moveLabel}", isStrictAnchor: false);
    }

    // Read-only peek check: true when a look-direction peek is currently armed
    // (a look <dir> was sent within the suppression window and its preview
    // display hasn't been consumed yet). Unlike NoteRoomObserved, this does NOT
    // consume the flag — the room-display consumers that fire BEFORE the exits
    // line (cash pickup, auto-get, the room-entity classifier that drives the
    // combat gate) call this to skip acting on a peeked room, while the flag
    // stays armed until NoteRoomObserved fires on "Obvious exits:" and consumes
    // it. Without this, a `look <dir>` peek renders a full room display and the
    // engines fire get / equip / combat against a room the player never entered.
    public bool IsPeekSuppressed(DateTimeOffset? whenUtc = null)
    {
        if (_suppressObservationUntil is not { } until) return false;
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        return when <= until;
    }

    // The server-side observation parser reports the room it just saw — name +
    // the set of directions on the "Obvious exits:" line. The tracker reconciles
    // this against the expected outcome of any pending move.
    public void NoteRoomObserved(RoomObservation observation, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

        if (_suppressObservationUntil is { } until)
        {
            _suppressObservationUntil = null;
            if (when <= until)
            {
                _log?.Log(LogSeverity.Info, "RoomTracker",
                    $"Dropped peek observation: '{observation.Name}'.");
                return;
            }
            // window expired — fall through and process normally
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

        // Mirror open-door modifiers from the latest observation into
        // state so the walker can pre-check before kicking off the
        // door FSM ("open door south" → skip the bash/pick wait,
        // send the cardinal move directly).
        State.OpenDoorDirections = observation.OpenDoorDirections;

        // Mirror the full observed-exit set so the walker can skip a
        // redundant `sea <dir>` when a graph-hidden exit is already
        // showing in the live display.
        State.ObservedExitDirections = observation.Exits;

        switch (State.Confidence)
        {
            case RoomConfidence.Unknown:
                LandFromCandidateSearch(observation, when);
                break;

            case RoomConfidence.Confirmed:
                ReconcileFromConfirmed(observation, when);
                break;

            case RoomConfidence.Pending:
                ReconcileFromPending(observation, when);
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

        // Record what we just fully processed so the next observation can tell
        // a passive redisplay of the same room from a genuine move outcome. Set
        // after the switch so the reconcile paths above still see the previous
        // value; skipped on the peek-suppression early-return so a dropped peek
        // doesn't masquerade as the last real observation.
        _lastObservation = observation;
        _lastObservationAt = when;
    }

    // The death-message detector saw a post-death lives readout — either "You now
    // have N lives remaining." (plain / suicide death) or "You have N lives left."
    // (miracle-save death). Capture a DeathRecord on the loaded profile (room
    // captured = where we were when the death message fired),
    // drain pending state, and transition to PendingRespawn so the next
    // observation lands as the new authoritative position without churning
    // Suspect strikes.
    public void NoteDeath(int livesRemaining, string? messageText = null, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        Room? died = State.CurrentRoom;

        if (_profile is not null)
        {
            _profile.DeathHistory ??= new List<DeathRecord>();
            var record = new DeathRecord(
                when,
                died is null ? null : new RoomRef(died.Key.Map, died.Key.Room),
                livesRemaining,
                messageText)
            {
                RecordNumber = _profile.DeathHistory.Count + 1,
                RoomName = died?.Name,
                Status = DeathRecoveryStatus.Active,
            };
            if (_inventorySnapshot is { } provider)
            {
                InventorySnapshot snapshot = provider();
                (List<DeathItem> equipped, List<DeathItem> lost) =
                    DeathLootCapture.FromSnapshot(snapshot);
                record.EquippedAtDeath = equipped;
                record.LostItems = lost;
                record.CoinsAtDeath = snapshot.Currency;
            }
            _profile.DeathHistory.Add(record);
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Death recorded at {(died?.Key.ToString() ?? "(unknown room)")}; {livesRemaining} lives remaining; " +
                $"deathpile worn={record.EquippedAtDeath?.Count ?? 0}, carried={record.LostItems?.Count ?? 0}.");
        }

        while (_pending.TryDequeue(out _)) { /* drain */ }
        _recentSteps.Clear();
        PersistSteps();
        IsInDarkRoom = false;
        SetRoom(room: null, RoomConfidence.PendingRespawn, when, "death recorded");

        // Broadcast the death AFTER the PendingRespawn transition but BEFORE the
        // graveyard's own room display confirms. A loop stopping here resets its
        // recovery state, so the later PendingRespawn → Confirmed(graveyard)
        // transition can't drive a recovery-reroute back out.
        PlayerDeathObserved?.Invoke();
    }

    // A movement-refusal line was seen (e.g. "You are too paralyzed to move." /
    // "You can't go that way."). Drains the most recently queued pending move
    // (since that's the one the server just refused) and reverts toward
    // Confirmed at the current room.
    public void NoteMoveBlocked(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

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
    }

    // Tier-3 manual override — the user pointed at a room on the map and said
    // "I'm here". Hard sets the current room and promotes to Confirmed.
    public void SetLocated(RoomKey key, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

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
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        ClearPendingAndSteps();
        _history.Clear();
        IsInDarkRoom = false;
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

        EnterSuspect(when, $"observation mismatched from Confirmed; candidates={candidates.Count}");
    }

    private void ReconcileFromPending(RoomObservation observation, DateTimeOffset when)
    {
        Room? source = State.CurrentRoom;

        // Try to match against the head pending move first — that's the
        // oldest move the server is most likely to be confirming. The head may
        // be a cardinal step or a text exit ("go path"); both resolve to a
        // single deterministic graph edge out of the source room.
        if (_pending.TryPeek(out PendingMove head)
            && source is not null
            && TryResolvePendingExit(source, head, out RoomExit exit))
        {
            Room? expected = _graph.GetRoom(exit.Target);
            string moveLabel = DescribeMove(head);

            // Strategy 1 — predicted neighbour matches.
            if (expected is not null && MatchesPredicted(expected, observation))
            {
                _pending.TryDequeue(out _);
                State.SuspectStrikes = 0;
                // Predicted-neighbour is a deduction — only strict (i.e.
                // worth persisting to LastKnownRoom) when the landing
                // room is also a 1-of-1 graph match for the observation.
                bool strict = _graph.FindCandidates(observation.Name, observation.Exits).Count == 1;
                if (_pending.IsEmpty)
                    SetRoom(expected, RoomConfidence.Confirmed, when, $"move {moveLabel} confirmed", isStrictAnchor: strict);
                else
                {
                    // More moves still in flight — land Confirmed at
                    // the new room (we know where we are) but keep
                    // Pending posture if more confirmations are due.
                    SetRoom(expected, RoomConfidence.Pending, when, $"move {moveLabel} confirmed, queue not empty");
                }
                return;
            }

            // Strategy 1a — predicted neighbour is null-name and the
            // observation's exits cover its ExitMask. Adopt the
            // neighbour, learn the observed name, fire NameLearned.
            // Without this branch a walk INTO a null-name ganghouse
            // room never resolves: MatchesPredicted fails on the null
            // name, candidate search returns 0, tracker bumps Suspect.
            if (expected is not null
                && expected.HasUnknownName
                && MatchesPredictedNameless(expected, observation)
                && !string.IsNullOrWhiteSpace(observation.Name))
            {
                Room? learned = _graph.LearnRoomName(expected.Key, observation.Name);
                if (learned is not null)
                {
                    _log?.Log(LogSeverity.Info, "RoomTracker",
                        $"Learned name '{observation.Name}' for {expected.Key} (pending-target null-name match).");
                    NameLearned?.Invoke(new NameLearnedEvent(expected.Key, observation.Name));
                    _pending.TryDequeue(out _);
                    State.SuspectStrikes = 0;
                    RoomConfidence target = _pending.IsEmpty
                        ? RoomConfidence.Confirmed
                        : RoomConfidence.Pending;
                    SetRoom(learned, target, when, $"move {moveLabel} confirmed (null-name learned)");
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
                State.LastUpdatedAt = when;
                return;
            }
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

        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 candidate from Suspect", isStrictAnchor: true);
            return;
        }

        // Still mismatched. Either escalate or replay.
        if (State.SuspectStrikes + 1 >= SuspectStrikeLimit)
        {
            if (TryReplayRecover(observation, when)) return;
            SetRoom(room: null, RoomConfidence.Lost, when,
                $"suspect strike limit ({SuspectStrikeLimit}) reached; replay failed");
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
                SetRoom(room: null, RoomConfidence.Lost, when, "no graph candidate; replay failed");
                break;

            default:
                // Ambiguous from Unknown / Lost — drop into Suspect with
                // no anchor room (we never had one). Counter stays
                // distinct so the next observation can either resolve
                // or trip Lost on its own merits.
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

        // Walk forward from the newest confirmed room through the
        // persisted steps.
        RoomKey start = _history.First.Value.Room;
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

            Room? next = _graph.GetRoom(exit.Target);
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
    private static bool MatchesPredicted(Room target, RoomObservation observation)
    {
        if (!string.Equals(target.Name, observation.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;

        // Subset: every observed exit is present in the graph.
        return (observedMask & target.ExitMask) == observedMask;
    }

    // Subset-only match against a null-name target. Used by the null-name
    // learning path — we have no name to match against, so the ExitMask is the
    // only signal that the observation is "this neighbour".
    private static bool MatchesPredictedNameless(Room target, RoomObservation observation)
    {
        if (!target.HasUnknownName) return false;
        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;
        return (observedMask & target.ExitMask) == observedMask;
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
            previousConfidence, newConfidence, previousRoom, newRoom, State.LastUpdatedAt));
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

    private void AppendStep(DirectionDto step)
    {
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
        while (_pending.TryDequeue(out _)) { /* drain */ }
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
public readonly record struct RoomTransition(
    RoomConfidence PreviousConfidence,
    RoomConfidence NewConfidence,
    Room? PreviousRoom,
    Room? NewRoom,
    DateTimeOffset At);

// Payload of RoomTracker.NameLearned. Carries the room the tracker just adopted
// by ExitMask + the verbatim observed name it learned. The main window's
// prompt-handler decides whether to persist the new name back to the active
// set's Rooms.json.
public readonly record struct NameLearnedEvent(RoomKey Key, string ObservedName);
