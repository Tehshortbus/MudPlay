using System.Collections.Generic;
using System.Linq;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Central pause-gate aggregator for the Phase 7 movement stack.
/// <see cref="AutoWalkManager"/> (walk-to), <c>LoopManager</c> (PR 7.8),
/// and <c>AutoLairScheduler</c> (PR 7.18+) all share this single
/// instance so a pause from any source (user button, @wait, health
/// auto-rest, stealth window, etc.) halts whichever movement engine
/// is active.
/// </summary>
/// <remarks>
/// <para>
/// Gates are named so the UI / log can surface "why are we paused?" —
/// the active reason is the gate name. Gates are tri-state via the
/// <see cref="AssertGate"/> / <see cref="ClearGate"/> pair; callers
/// (HealthManager, StealthManager, party @wait, the user pause button)
/// own the state of their own gate.
/// </para>
/// <para>
/// Phase 9 cross-cut 1 names five PascalCase gate constants and adds
/// the <see cref="History"/> ring buffer + canonical <c>[Gate]</c> log
/// writer. Engines never call each other's pause — they all assert /
/// clear on this one instance, optionally passing
/// <paramref name="asserter"/> + <paramref name="reason"/> so the log
/// timeline reads cleanly:
/// </para>
/// <code>
/// [Gate] asserted — gate=Combat asserter=CombatStateTracker reason=room-entry targetable=3 first=fierce_warrior
/// [Gate] cleared  — gate=Combat asserter=CombatStateTracker reason=lastKill=giant_rat heldFor=14.2s
/// </code>
/// </remarks>
public sealed class MovementCoordinator
{
    /// <summary>Manual pause button on the Navigation window. Built-in.</summary>
    public const string UserGate = "User";

    /// <summary>Phase 9 PR 9.0b — asserted by
    /// <c>CombatStateTracker</c> while the room contains
    /// auto-attack-eligible monsters. Holds until the room is cleared
    /// (NOT just <c>*Combat Off*</c>).</summary>
    public const string CombatGate = "Combat";

    /// <summary>Phase 9 PR 9.B — asserted by <c>HealthManager</c> when
    /// HP drops below the configured rest trigger; clears when HP
    /// recovers past the configured rest target.</summary>
    public const string HealthRecoveryGate = "HealthRecovery";

    /// <summary>Phase 9 PR 9.B — asserted by <c>HealthManager</c> when
    /// the caster pool drops below the configured meditate trigger;
    /// clears when MA recovers past the configured target.</summary>
    public const string ManaRecoveryGate = "ManaRecovery";

    /// <summary>Phase 9 PR 9.J — asserted by the in-room acquisition
    /// engine (PR 9.L auto-get) while the <c>loot</c> step runs after a
    /// fight clears; clears when all flagged ground items + coins are
    /// resolved. This is the <c>get-clear</c> contributor to the in-room
    /// loop's movement gate (<c>fight-clear ∧ get-clear ∧ vitals-OK</c>).</summary>
    public const string AcquisitionGate = "Acquisition";

    /// <summary>Phase 9 PR 9.I — asserted by
    /// <c>DeathRecoveryManager</c> while the corpse-recovery loop is
    /// running. Clears when recovery finishes.</summary>
    public const string CorpseRecoveryGate = "CorpseRecovery";

    /// <summary>Asserted by <c>PartyVitalsGate</c> while any other party
    /// member's HP% is below the Party-tab "wait if members are below"
    /// threshold. Clears when every observed member recovers past it.
    /// Lets the party loop hold so the hurt member can rest / be healed
    /// before the group moves on.</summary>
    public const string PartyVitalsGate = "PartyVitals";

    /// <summary>Asserted by <c>AutoPartyManager</c> while a loop is running
    /// and we're waiting for an auto-invited player to join. Holds the
    /// circuit so the group forms up before moving on; clears when they
    /// join or the Party-tab "If leading, wait only" window elapses (at
    /// which point we <c>uninvite</c> them and resume).</summary>
    public const string PartyInviteGate = "PartyInvite";

    /// <summary>Asserted by <c>PartyFollowerMovementGate</c> while the local
    /// character is a party <em>follower</em> (in a party but not the leader).
    /// MajorMUD movement is leader-driven — the leader walks and the game drags
    /// followers along — so a follower's own walk / loop / auto-lair is held
    /// silently to avoid fighting the leader's drag. Clears the moment we lead
    /// the party or leave it, so a queued route resumes on its own.</summary>
    public const string FollowerGate = "Follower";

    private const int HistoryCapacity = 200;

    private readonly LogService? _log;
    private readonly HashSet<string> _assertedGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<GateTransitionEntry> _history = new(HistoryCapacity);
    private readonly object _historyLock = new();

    /// <summary>True when at least one gate is asserting pause.</summary>
    public bool IsPaused => _assertedGates.Count > 0;

    /// <summary>
    /// Read-only snapshot of currently-asserted gate names. Useful for
    /// the Navigation status strip: <c>"paused: User, HealthRecovery"</c>.
    /// </summary>
    public IReadOnlyCollection<string> AssertedGates => _assertedGates.ToArray();

    /// <summary>
    /// Fires after every transition that changes <see cref="IsPaused"/>
    /// from <c>false</c> to <c>true</c> or vice versa. Single-gate
    /// changes that don't flip the overall paused state do not fire
    /// (e.g. clearing <c>HealthRecovery</c> while <c>User</c> is still
    /// asserted).
    /// </summary>
    public event Action<bool>? PauseStateChanged;

    /// <summary>
    /// Last <see cref="HistoryCapacity"/> gate transitions, oldest
    /// first. Enables a future "gate timeline" debug view + grep-
    /// friendly forensics on what asserted / cleared when.
    /// </summary>
    public IReadOnlyList<GateTransitionEntry> History
    {
        get { lock (_historyLock) { return _history.ToArray(); } }
    }

    public MovementCoordinator(LogService? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// Assert <paramref name="gate"/>. Idempotent — re-asserting an
    /// already-asserted gate doesn't refire the event or duplicate a
    /// history entry.
    /// </summary>
    /// <param name="gate">The gate name (use one of the constants —
    /// <see cref="UserGate"/>, <see cref="CombatGate"/>, etc.).</param>
    /// <param name="asserter">Optional identifier for the subsystem
    /// asserting the gate (e.g. <c>"CombatStateTracker"</c>). Recorded
    /// in <see cref="History"/> + the <c>[Gate]</c> log line.</param>
    /// <param name="reason">Optional human-readable reason
    /// (e.g. <c>"room-entry targetable=3"</c>). Recorded with the
    /// transition.</param>
    public void AssertGate(string gate, string? asserter = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        bool wasPaused = IsPaused;
        if (!_assertedGates.Add(gate)) return;
        RecordTransition(gate, asserted: true, asserter, reason);
        if (!wasPaused) PauseStateChanged?.Invoke(true);
    }

    /// <summary>
    /// Clear <paramref name="gate"/>. Idempotent — clearing a gate
    /// that wasn't asserted is a no-op (no history entry, no event).
    /// </summary>
    /// <param name="gate">The gate name.</param>
    /// <param name="asserter">Optional identifier for the subsystem
    /// clearing the gate.</param>
    /// <param name="reason">Optional human-readable reason for the
    /// clear (e.g. <c>"lastKill=giant_rat heldFor=14.2s"</c>).</param>
    public void ClearGate(string gate, string? asserter = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        if (!_assertedGates.Remove(gate)) return;
        RecordTransition(gate, asserted: false, asserter, reason);
        if (!IsPaused) PauseStateChanged?.Invoke(false);
    }

    private void RecordTransition(string gate, bool asserted, string? asserter, string? reason)
    {
        GateTransitionEntry entry = new(
            DateTimeOffset.Now, gate, asserted, asserter, reason);
        lock (_historyLock)
        {
            if (_history.Count >= HistoryCapacity) _history.Dequeue();
            _history.Enqueue(entry);
        }
        if (_log is null) return;
        string verb = asserted ? "asserted" : "cleared ";
        string line = $"{verb} — gate={gate}";
        if (!string.IsNullOrWhiteSpace(asserter)) line += $" asserter={asserter}";
        if (!string.IsNullOrWhiteSpace(reason))   line += $" reason={reason}";
        _log.Info("Gate", line);
    }
}

/// <summary>
/// One row in <see cref="MovementCoordinator.History"/>.
/// </summary>
/// <param name="Timestamp">Wall-clock time of the transition.</param>
/// <param name="Gate">Gate name (one of the
/// <see cref="MovementCoordinator"/> constants).</param>
/// <param name="Asserted"><c>true</c> = asserted; <c>false</c> = cleared.</param>
/// <param name="Asserter">Subsystem that flipped the gate, or <c>null</c>
/// when the caller didn't tag itself.</param>
/// <param name="Reason">Human-readable rationale, or <c>null</c>.</param>
public readonly record struct GateTransitionEntry(
    DateTimeOffset Timestamp,
    string Gate,
    bool Asserted,
    string? Asserter,
    string? Reason);
