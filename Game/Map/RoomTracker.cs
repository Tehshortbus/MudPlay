using System.Collections.Generic;
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
/// PR 7.1 ships the FSM core; the wire-side parser that turns
/// MessageRouter events into <see cref="RoomObservation"/>s and the
/// pattern catalogue for movement-refusal lines land in PR 7.1b. Tier 1
/// replay-from-last-known and Tier 2 footprint matching land in PR 7.2
/// (and the late-phase Tier 2 follow-up per the planning conversation).
/// </para>
/// <para>
/// State semantics:
/// <list type="bullet">
///   <item><see cref="RoomConfidence.Unknown"/> — fresh tracker, no observation yet.</item>
///   <item><see cref="RoomConfidence.Located"/> — current room is trusted.</item>
///   <item><see cref="RoomConfidence.Pending"/> — move sent, awaiting confirmation.</item>
///   <item><see cref="RoomConfidence.Reconciling"/> — observation didn't match; searching for a single graph candidate.</item>
///   <item><see cref="RoomConfidence.Lost"/> — no candidate matched; only manual override can recover (until PR 7.2).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class RoomTracker
{
    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    /// <summary>
    /// Direction of the last move sent from a <see cref="RoomConfidence.Located"/>
    /// or <see cref="RoomConfidence.Pending"/> state, or <c>null</c>
    /// when no move is in flight. Reset on every transition out of
    /// <see cref="RoomConfidence.Pending"/>.
    /// </summary>
    private Direction? _pendingDirection;

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

        // Set the initial timestamp; CurrentRoom stays null and
        // Confidence stays Unknown until the first observation lands.
        State.LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    // ----- inputs -----------------------------------------------------

    /// <summary>
    /// The walker (or any other caller that just sent a move) reports
    /// the direction. The tracker remembers it so the next
    /// <see cref="NoteRoomObserved"/> can be validated against the
    /// expected exit target.
    /// </summary>
    /// <remarks>
    /// Calling this from <see cref="RoomConfidence.Reconciling"/> or
    /// <see cref="RoomConfidence.Lost"/> is a no-op for FSM purposes —
    /// we can't predict where the move will land, so we just keep
    /// waiting for a usable observation. PR 7.2 will use these moves to
    /// drive the replay-from-last-known recovery.
    /// </remarks>
    public void NoteMoveSent(Direction direction, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

        switch (State.Confidence)
        {
            case RoomConfidence.Located:
            case RoomConfidence.Pending:
                // Pending → Pending is a chain (we sent another move
                // before the previous landed). The pending direction
                // updates to the latest; the FSM treats the next
                // observation as confirmation of the latest move only.
                _pendingDirection = direction;
                SetConfidence(RoomConfidence.Pending, when, "move sent");
                break;

            case RoomConfidence.Unknown:
            case RoomConfidence.Reconciling:
            case RoomConfidence.Lost:
                // Record nothing; next observation will be evaluated on
                // its own merits via the 1-of-1 path. (Replay buffer is
                // PR 7.2's concern.)
                break;
        }
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

        switch (State.Confidence)
        {
            case RoomConfidence.Unknown:
                // First observation — promote to Located only if the
                // graph contains exactly one candidate matching
                // (name, exits). Anything else lands in Reconciling
                // (multiple candidates) or Lost (no candidates).
                LandFromCandidateSearch(observation, when);
                break;

            case RoomConfidence.Located:
                ReconcileFromLocated(observation, when);
                break;

            case RoomConfidence.Pending:
                ReconcileFromPending(observation, when);
                break;

            case RoomConfidence.Reconciling:
            case RoomConfidence.Lost:
                LandFromCandidateSearch(observation, when);
                break;
        }
    }

    /// <summary>
    /// A movement-refusal line was seen (e.g. "You are too paralyzed
    /// to move." / "You can't go that way."). If we were in
    /// <see cref="RoomConfidence.Pending"/>, the move didn't actually
    /// take place — revert to <see cref="RoomConfidence.Located"/> at
    /// the current room.
    /// </summary>
    public void NoteMoveBlocked(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

        if (State.Confidence == RoomConfidence.Pending)
        {
            _pendingDirection = null;
            SetConfidence(RoomConfidence.Located, when, "move blocked");
        }
        // From any other state, a refusal is just noise — nothing in
        // flight to revert.
    }

    /// <summary>
    /// Tier 3 manual override — the user pointed at a room on the map
    /// and said "I'm here". Hard sets the current room and promotes
    /// to <see cref="RoomConfidence.Located"/>.
    /// </summary>
    public void SetLocated(RoomKey key, DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;

        Room? room = _graph.GetRoom(key);
        if (room is null)
        {
            // The user picked a room that isn't in the active set —
            // refuse to set state to something we can't reason about.
            _log?.Log(LogSeverity.Warn, "RoomTracker",
                $"Manual locate refused: room key {key} not present in active graph.");
            return;
        }

        _pendingDirection = null;
        SetRoom(room, RoomConfidence.Located, when, "manual locate");
    }

    /// <summary>
    /// Subscribed by <see cref="Services.AppServices"/> to
    /// <see cref="RoomGraphManager.GraphReloaded"/>. The active set
    /// just rebuilt — drop any per-set state and start over.
    /// </summary>
    public void OnGraphReloaded(DateTimeOffset? whenUtc = null)
    {
        DateTimeOffset when = whenUtc ?? DateTimeOffset.UtcNow;
        _pendingDirection = null;
        SetRoom(room: null, RoomConfidence.Unknown, when, "graph reloaded");
    }

    // ----- FSM internals ----------------------------------------------

    private void ReconcileFromLocated(RoomObservation observation, DateTimeOffset when)
    {
        Room? current = State.CurrentRoom;
        if (current is not null && MatchesPredicted(current, observation))
        {
            // Same room — refresh timestamp, no state churn. Subset
            // match tolerates exits the live display hides relative
            // to the graph (closed doors, hidden / searchable, gated).
            State.LastUpdatedAt = when;
            RaiseStateChanged(RoomConfidence.Located, RoomConfidence.Located, current, current);
            return;
        }

        // We thought we knew where we were but the room display
        // disagrees. Search for a single matching candidate.
        LandFromCandidateSearch(observation, when);
    }

    private void ReconcileFromPending(RoomObservation observation, DateTimeOffset when)
    {
        Room? source = State.CurrentRoom;
        Direction? dir = _pendingDirection;

        if (source is not null
            && dir is { } direction
            && source.Exits.TryGetValue(direction, out RoomExit exit))
        {
            Room? expected = _graph.GetRoom(exit.Target);

            // Strategy 1 — predicted neighbour matches by name AND
            // observed exits are at least a subset of the graph's
            // exits. Subset (not strict equality) tolerates exits the
            // game hides on the "Obvious exits:" line (closed doors,
            // hidden / searchable exits, conditional exits) — those
            // surface in the graph but not in the live observation.
            if (expected is not null && MatchesPredicted(expected, observation))
            {
                _pendingDirection = null;
                SetRoom(expected, RoomConfidence.Located, when, $"move {direction} confirmed");
                return;
            }

            // Strategy 1b — refused-move redisplay. A single move
            // command can land us at the predicted neighbour OR keep
            // us at the source (server refused / delayed: combat
            // re-engaged, paralysed without an explicit refusal line,
            // follower lag). If the observation matches the source
            // room we're at, treat it as a refusal and stay put.
            if (MatchesPredicted(source, observation))
            {
                _pendingDirection = null;
                SetConfidence(RoomConfidence.Located, when, "move-refused redisplay");
                return;
            }
        }

        // Neither predicted nor refused — search the graph.
        _pendingDirection = null;
        LandFromCandidateSearch(observation, when);
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
                SetRoom(room, RoomConfidence.Located, when, "1-of-1 candidate");
                break;

            case 0:
                SetRoom(room: null, RoomConfidence.Lost, when, "no graph candidate");
                break;

            default:
                // Ambiguous — clear the room and wait for Tier 2/3 to pick.
                // Holding the previous room around would let walker /
                // loop / auto-lair callers act on stale state; null is
                // honest about "we don't know".
                SetRoom(room: null, RoomConfidence.Reconciling, when,
                    $"{candidates.Count} candidates");
                break;
        }
    }

    /// <summary>
    /// Strict match used by <see cref="LandFromCandidateSearch"/> —
    /// name AND exit set must match exactly. Used when we have no
    /// other anchor to narrow ambiguous candidates.
    /// </summary>
    private static bool MatchesCurrent(Room current, RoomObservation observation)
    {
        if (!string.Equals(current.Name, observation.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        uint observedMask = 0;
        foreach (Direction d in observation.Exits) observedMask |= 1u << (int)d;
        return observedMask == current.ExitMask;
    }

    /// <summary>
    /// Looser match used when we already have a predicted target
    /// (Strategy 1 / 1b) — name match plus observed-exits-are-subset
    /// of graph-exits. Tolerates "Obvious exits:" hiding closed
    /// doors / searchable / conditional exits the graph still knows
    /// about. Strict equality fired too often on real game data
    /// where the live display omits the gated exits.
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
}

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
