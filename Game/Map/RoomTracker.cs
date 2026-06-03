using System.Collections.Concurrent;
using System.Collections.Generic;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Trust-by-default room-tracking FSM. Maintains <see cref="RoomState"/>
/// from the four signals every player session produces: outgoing move
/// commands, observed room displays, observed move refusals, and the
/// user's manual "I am here" override.
/// </summary>
/// <remarks>
/// <para>
/// State semantics:
/// </para>
/// <list type="bullet">
///   <item><see cref="RoomConfidence.Unknown"/> — fresh tracker, no observation yet.</item>
///   <item><see cref="RoomConfidence.Confirmed"/> — current room is trusted.</item>
///   <item><see cref="RoomConfidence.Pending"/> — one or more moves sent, awaiting confirmation.</item>
///   <item><see cref="RoomConfidence.Suspect"/> — observation didn't line up; current room preserved as best guess; counter incremented. Internal-only, no UI churn.</item>
///   <item><see cref="RoomConfidence.Lost"/> — replay-from-last-Confirmed failed; user must manually pick or wait for a confirming observation.</item>
/// </list>
/// <para>
/// Recovery: a single tier — replay the persisted
/// <see cref="CharacterProfile.RecentSteps"/> from the last Confirmed
/// room through the graph; if the endpoint matches the current
/// observation, we Confirm there. No fuzzy footprint matching.
/// </para>
/// <para>
/// Persistence: every <see cref="RoomConfidence.Confirmed"/> transition
/// updates <see cref="CharacterProfile.LastKnownRoom"/> and resets
/// <see cref="CharacterProfile.RecentSteps"/>. Every
/// <see cref="NoteMoveSent(Direction, DateTimeOffset?)"/> appends a
/// step. The profile flushes to disk on the normal save cycle (app
/// close, settings Apply, explicit save).
/// </para>
/// </remarks>
public sealed class RoomTracker
{
    /// <summary>
    /// Maximum back-to-back moves we'll track in flight. Anything beyond
    /// this is dropped to keep the queue bounded — a sustained
    /// observation drought past 15 moves is a parser problem, not a
    /// tracking one.
    /// </summary>
    private const int PendingQueueCap = 15;

    /// <summary>
    /// Confirmed-position rolling history retained for debugging /
    /// future tier-2 recovery. Capped to keep memory bounded.
    /// </summary>
    private const int HistoryCap = 50;

    /// <summary>
    /// Strike count at which the next mismatch triggers replay-recovery
    /// instead of incrementing further. Matches the user's "3 strikes
    /// before Lost" directive.
    /// </summary>
    private const int SuspectStrikeLimit = 3;

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;
    private readonly ConcurrentQueue<PendingMove> _pending = new();
    private readonly LinkedList<HistoryEntry> _history = new();
    private readonly List<DirectionDto> _recentSteps = new();

    /// <summary>
    /// Profile the tracker is currently writing into. Set by
    /// <see cref="Hydrate"/>; cleared by <see cref="OnProfileClosed"/>.
    /// When null, persistence operations are no-ops — the tracker still
    /// runs in memory but doesn't touch any profile.
    /// </summary>
    private CharacterProfile? _profile;

    /// <summary>
    /// Look-direction suppression deadline. While the wall clock is at
    /// or before this timestamp, the next
    /// <see cref="NoteRoomObserved"/> call is treated as a peek (room
    /// preview from a <c>look &lt;dir&gt;</c> command) and discarded.
    /// The 3-second window auto-clears even if no peek display arrives,
    /// so the suppression can't eat a future genuine observation.
    /// </summary>
    private DateTimeOffset? _suppressObservationUntil;
    private const int LookSuppressWindowMs = 3000;

    /// <summary>The state class itself — bound by the UI, mutated only by this tracker.</summary>
    public RoomState State { get; } = new();

    /// <summary>
    /// Fires after every transition that changes
    /// <see cref="RoomState.CurrentRoom"/> or
    /// <see cref="RoomState.Confidence"/>. Carries the full state
    /// snapshot so handlers can branch on both fields without racing.
    /// </summary>
    public event Action<RoomTransition>? StateChanged;

    public RoomTracker(RoomGraphManager graph) : this(graph, log: null) { }

    public RoomTracker(RoomGraphManager graph, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
        State.LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    // ----- profile hydrate / save -------------------------------------

    /// <summary>
    /// Adopt the supplied profile as our persistence target and seed
    /// state from its <see cref="CharacterProfile.LastKnownRoom"/> +
    /// <see cref="CharacterProfile.RecentSteps"/>. Called by
    /// <see cref="AppServices"/> on
    /// <see cref="ProfileService.ProfileLoaded"/>. Idempotent — calling
    /// twice with the same profile reuses the seed.
    /// </summary>
    /// <remarks>
    /// Seeding strategy: if <c>LastKnownRoom</c> resolves to a room in
    /// the active graph, we land Confirmed there and prime the
    /// <c>_recentSteps</c> list with the persisted entries (so a
    /// subsequent failed observation triggers replay). If the room
    /// can't be resolved (stale profile, different game-data set,
    /// graph not yet loaded), the tracker stays Unknown and the
    /// next observation lands normally.
    /// </remarks>
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

    /// <summary>Called when the profile unloads. Detaches the persistence target.</summary>
    public void OnProfileClosed()
    {
        _profile = null;
    }

    // ----- inputs -----------------------------------------------------

    /// <summary>
    /// The walker (or any other caller that just sent a move) reports
    /// the direction. The tracker enqueues a pending move and prepares
    /// to validate against the next observation.
    /// </summary>
    public void NoteMoveSent(Direction direction, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Text-exit move (e.g. <c>"go path"</c>) — used by the
    /// look-direction-interception work for arbitrary text commands
    /// that don't map to a <see cref="Direction"/>.
    /// </summary>
    public void NoteMoveSent(string command, Direction? cardinal = null, DateTimeOffset? whenUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        EnqueuePending(new PendingMove(cardinal, command, when));
        AppendStep(new DirectionDto(cardinal, command));

        if (State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending)
        {
            SetConfidence(RoomConfidence.Pending, when, $"move '{command}' sent");
        }
    }

    /// <summary>
    /// The user typed a peek command (<c>look &lt;dir&gt;</c> or
    /// equivalent). Arm the suppression flag so the next room display
    /// is treated as a preview and dropped instead of being parsed as
    /// a move outcome. The flag auto-clears after
    /// <see cref="LookSuppressWindowMs"/>.
    /// </summary>
    public void NoteLookSent(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        _suppressObservationUntil = when.AddMilliseconds(LookSuppressWindowMs);
        _log?.Log(LogSeverity.Info, "RoomTracker",
            "Look-direction sent — next observation will be ignored as a peek.");
    }

    /// <summary>
    /// The server-side observation parser reports the room it just
    /// saw — name + the set of directions on the
    /// <c>Obvious exits:</c> line. The tracker reconciles this against
    /// the expected outcome of any pending move.
    /// </summary>
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
    }

    /// <summary>
    /// The death-message detector saw the post-suicide / killed-in-combat
    /// <c>You now have N lives remaining.</c> line. Capture a
    /// <see cref="DeathRecord"/> on the loaded profile (room captured =
    /// where we were when the death message fired), drain pending
    /// state, and transition to <see cref="RoomConfidence.PendingRespawn"/>
    /// so the next observation lands as the new authoritative position
    /// without churning Suspect strikes.
    /// </summary>
    public void NoteDeath(int livesRemaining, string? messageText = null, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        Room? died = State.CurrentRoom;

        if (_profile is not null)
        {
            var record = new DeathRecord(
                when,
                died is null ? null : new RoomRef(died.Key.Map, died.Key.Room),
                livesRemaining,
                messageText);
            _profile.DeathHistory ??= new List<DeathRecord>();
            _profile.DeathHistory.Add(record);
            _log?.Log(LogSeverity.Info, "RoomTracker",
                $"Death recorded at {(died?.Key.ToString() ?? "(unknown room)")}; {livesRemaining} lives remaining.");
        }

        while (_pending.TryDequeue(out _)) { /* drain */ }
        _recentSteps.Clear();
        PersistSteps();
        SetRoom(room: null, RoomConfidence.PendingRespawn, when, "death recorded");
    }

    /// <summary>
    /// A movement-refusal line was seen (e.g. "You are too paralyzed
    /// to move." / "You can't go that way."). Drains the most recently
    /// queued pending move (since that's the one the server just
    /// refused) and reverts toward Confirmed at the current room.
    /// </summary>
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

    /// <summary>
    /// Tier-3 manual override — the user pointed at a room on the map
    /// and said "I'm here". Hard sets the current room and promotes to
    /// <see cref="RoomConfidence.Confirmed"/>.
    /// </summary>
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
        SetRoom(room, RoomConfidence.Confirmed, when, "manual locate");
    }

    /// <summary>
    /// Subscribed by <see cref="Services.AppServices"/> to
    /// <see cref="RoomGraphManager.GraphReloaded"/>. The active set
    /// just rebuilt — drop any per-set state and start over.
    /// </summary>
    public void OnGraphReloaded(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        ClearPendingAndSteps();
        _history.Clear();
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
        // disagrees. Two possibilities: (a) a 1-of-1 candidate exists
        // in the graph and the user got teleported / dragged → land
        // Confirmed at the new room; (b) ambiguous / zero → escalate
        // to Suspect at the current room.
        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 silent desync");
            return;
        }

        EnterSuspect(when, $"observation mismatched from Confirmed; candidates={candidates.Count}");
    }

    private void ReconcileFromPending(RoomObservation observation, DateTimeOffset when)
    {
        Room? source = State.CurrentRoom;

        // Try to match against the head pending move first — that's the
        // oldest move the server is most likely to be confirming.
        if (_pending.TryPeek(out PendingMove head)
            && source is not null
            && head.Cardinal is { } direction
            && source.Exits.TryGetValue(direction, out RoomExit exit))
        {
            Room? expected = _graph.GetRoom(exit.Target);

            // Strategy 1 — predicted neighbour matches.
            if (expected is not null && MatchesPredicted(expected, observation))
            {
                _pending.TryDequeue(out _);
                State.SuspectStrikes = 0;
                if (_pending.IsEmpty)
                    SetRoom(expected, RoomConfidence.Confirmed, when, $"move {direction} confirmed");
                else
                {
                    // More moves still in flight — land Confirmed at
                    // the new room (we know where we are) but keep
                    // Pending posture if more confirmations are due.
                    SetRoom(expected, RoomConfidence.Pending, when, $"move {direction} confirmed, queue not empty");
                }
                return;
            }

            // Strategy 1b — refused-move redisplay (same room as source).
            if (MatchesPredicted(source, observation))
            {
                _pending.TryDequeue(out _);
                DropMostRecentStep();                       // the move didn't actually take place
                State.SuspectStrikes = 0;
                RoomConfidence target = _pending.IsEmpty
                    ? RoomConfidence.Confirmed
                    : RoomConfidence.Pending;
                // CurrentRoom already == source; reuse SetConfidence so
                // history stays correctly seeded with the unchanged room.
                if (target == RoomConfidence.Confirmed)
                    PersistConfirmedAnchor(source, when);
                SetConfidence(target, when, "move-refused redisplay");
                return;
            }
        }

        // Neither predicted nor refused — fall back to graph search.
        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 candidate after Pending miss");
            return;
        }

        EnterSuspect(when, $"Pending observation didn't match queue head; candidates={candidates.Count}");
    }

    private void ReconcileFromSuspect(RoomObservation observation, DateTimeOffset when)
    {
        Room? current = State.CurrentRoom;
        if (current is not null && MatchesPredicted(current, observation))
        {
            // Glitch resolved — server caught up. Back to Confirmed.
            State.SuspectStrikes = 0;
            ClearPendingAndSteps();
            PersistConfirmedAnchor(current, when);
            SetConfidence(RoomConfidence.Confirmed, when, "suspect resolved (current room re-confirmed)");
            return;
        }

        IReadOnlyList<RoomKey> candidates = _graph.FindCandidates(observation.Name, observation.Exits);
        if (candidates.Count == 1
            && _graph.GetRoom(candidates[0]) is { } single)
        {
            ClearPendingAndSteps();
            SetRoom(single, RoomConfidence.Confirmed, when, "1-of-1 candidate from Suspect");
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
                SetRoom(room, RoomConfidence.Confirmed, when, "1-of-1 candidate");
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

    /// <summary>
    /// Walk <see cref="_recentSteps"/> through the graph starting at
    /// the most-recent <see cref="HistoryEntry"/>. If the projected
    /// endpoint matches <paramref name="observation"/>, land Confirmed
    /// there. Returns <c>true</c> when recovery succeeded; the caller
    /// proceeds to Lost on <c>false</c>.
    /// </summary>
    private bool TryReplayRecover(RoomObservation observation, DateTimeOffset when)
    {
        if (_history.First is null) return false;
        if (_recentSteps.Count == 0) return false;

        // Walk forward from the newest confirmed room through the
        // persisted steps.
        RoomKey start = _history.First.Value.Room;
        Room? cursor = _graph.GetRoom(start);
        if (cursor is null) return false;

        foreach (DirectionDto step in _recentSteps)
        {
            if (step.Cardinal is not { } direction) return false;     // text exits aren't replayable through the graph
            if (!cursor.Exits.TryGetValue(direction, out RoomExit exit)) return false;
            Room? next = _graph.GetRoom(exit.Target);
            if (next is null) return false;
            cursor = next;
        }

        if (!MatchesPredicted(cursor, observation)) return false;

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

    /// <summary>
    /// Looser match: name match plus observed-exits-are-subset of
    /// graph-exits. Tolerates "Obvious exits:" hiding closed doors /
    /// searchable / conditional exits the graph still knows about.
    /// Strict equality fired too often on real game data.
    /// </summary>
    private static bool MatchesPredicted(Room target, RoomObservation observation)
    {
        if (!string.Equals(target.Name, observation.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;

        // Subset: every observed exit is present in the graph.
        return (observedMask & target.ExitMask) == observedMask;
    }

    private void SetRoom(Room? room, RoomConfidence confidence, DateTimeOffset when, string reason)
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
            PersistConfirmedAnchor(room, when);
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

/// <summary>One entry in the rolling confirmed-position history buffer.</summary>
internal readonly record struct HistoryEntry(RoomKey Room, DateTimeOffset ConfirmedAt);

/// <summary>
/// Payload of <see cref="RoomTracker.StateChanged"/>. Both
/// <see cref="PreviousConfidence"/>/<see cref="NewConfidence"/> and
/// <see cref="PreviousRoom"/>/<see cref="NewRoom"/> are surfaced so
/// handlers can branch on what actually changed without re-querying
/// <see cref="RoomState"/> (which would race with the next transition).
/// </summary>
public readonly record struct RoomTransition(
    RoomConfidence PreviousConfidence,
    RoomConfidence NewConfidence,
    Room? PreviousRoom,
    Room? NewRoom,
    DateTimeOffset At);
