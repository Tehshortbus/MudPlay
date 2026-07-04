using System.Collections.Generic;
using System.Linq;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Central pause-gate aggregator for the movement stack. AutoWalkManager
// (walk-to), the loop manager, and the auto-lair scheduler all share this
// single instance so a pause from any source (user button, @wait, health
// auto-rest, stealth window, etc.) halts whichever movement engine is active.
//
// Gates are named so the UI / log can surface "why are we paused?" — the
// active reason is the gate name. Gates are tri-state via the AssertGate /
// ClearGate pair; callers (HealthManager, StealthManager, party @wait, the
// user pause button) own the state of their own gate.
//
// Engines never call each other's pause — they all assert / clear on this one
// instance, optionally passing asserter + reason so the log timeline reads
// cleanly:
//
//   [Gate] asserted — gate=Combat asserter=CombatStateTracker reason=room-entry targetable=3 first=fierce_warrior
//   [Gate] cleared  — gate=Combat asserter=CombatStateTracker reason=lastKill=giant_rat heldFor=14.2s
public sealed class MovementCoordinator
{
    // Manual pause button on the Navigation window.
    public const string UserGate = "User";

    // Asserted by CombatStateTracker while the room contains
    // auto-attack-eligible monsters. Holds until the room is cleared (NOT just
    // *Combat Off*).
    public const string CombatGate = "Combat";

    // Asserted by HealthManager when HP drops below the configured rest
    // trigger; clears when HP recovers past the configured rest target.
    public const string HealthRecoveryGate = "HealthRecovery";

    // Asserted by HealthManager when the caster pool drops below the
    // configured meditate trigger; clears when MA recovers past the target.
    public const string ManaRecoveryGate = "ManaRecovery";

    // Asserted by the in-room acquisition engine while the loot step runs
    // after a fight clears; clears when all flagged ground items + coins are
    // resolved. This is the get-clear contributor to the in-room loop's
    // movement gate (fight-clear ∧ get-clear ∧ vitals-OK).
    public const string AcquisitionGate = "Acquisition";

    // Asserted by DeathRecoveryManager while the corpse-recovery loop is
    // running. Clears when recovery finishes.
    public const string CorpseRecoveryGate = "CorpseRecovery";

    // Asserted by PartyVitalsGate while any other party member's HP% is below
    // the Party-tab "wait if members are below" threshold. Clears when every
    // observed member recovers past it. Lets the party loop hold so the hurt
    // member can rest / be healed before the group moves on.
    public const string PartyVitalsGate = "PartyVitals";

    // Asserted by AutoPartyManager while a loop is running and we're waiting
    // for an auto-invited player to join. Holds the circuit so the group forms
    // up before moving on; clears when they join or the Party-tab "If leading,
    // wait only" window elapses (at which point we uninvite them and resume).
    public const string PartyInviteGate = "PartyInvite";

    // Asserted by PartyFollowerMovementGate while the local character is a
    // party follower (in a party but not the leader). MajorMUD movement is
    // leader-driven — the leader walks and the game drags followers along — so
    // a follower's own walk / loop / auto-lair is held silently to avoid
    // fighting the leader's drag. Clears the moment we lead the party or leave
    // it, so a queued route resumes on its own.
    public const string FollowerGate = "Follower";

    private const int HistoryCapacity = 200;

    private readonly LogService? _log;
    private readonly HashSet<string> _assertedGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<GateTransitionEntry> _history = new(HistoryCapacity);
    private readonly object _historyLock = new();

    // True when at least one gate is asserting pause.
    public bool IsPaused => _assertedGates.Count > 0;

    // Read-only snapshot of currently-asserted gate names. Useful for the
    // Navigation status strip: "paused: User, HealthRecovery".
    public IReadOnlyCollection<string> AssertedGates => _assertedGates.ToArray();

    // Fires after every transition that changes IsPaused from false to true or
    // vice versa. Single-gate changes that don't flip the overall paused state
    // do not fire (e.g. clearing HealthRecovery while User is still asserted).
    public event Action<bool>? PauseStateChanged;

    // Fires after EVERY real gate assert/clear, whether or not it flips the
    // overall paused state. PauseStateChanged is the coarse "are we moving or
    // not" signal; this is the fine-grained "which gate changed" signal a UI
    // needs to keep a live "why are we paused" label accurate — e.g. Combat
    // clearing into a still-asserted HealthRecovery keeps IsPaused true (so
    // PauseStateChanged stays silent) but the reason shown to the user must
    // switch from "Fighting" to "Resting."
    public event Action? GatesChanged;

    // Last HistoryCapacity gate transitions, oldest first. Backs a gate
    // timeline debug view and grep-friendly forensics on what asserted /
    // cleared when.
    public IReadOnlyList<GateTransitionEntry> History
    {
        get { lock (_historyLock) { return _history.ToArray(); } }
    }

    public MovementCoordinator(LogService? log = null)
    {
        _log = log;
    }

    // Assert gate. Idempotent — re-asserting an already-asserted gate doesn't
    // refire the event or duplicate a history entry. gate uses one of the
    // constants (UserGate, CombatGate, etc.). asserter names the subsystem
    // (e.g. "CombatStateTracker") and reason a human-readable cause (e.g.
    // "room-entry targetable=3"); both are recorded with the transition.
    public void AssertGate(string gate, string? asserter = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        bool wasPaused = IsPaused;
        if (!_assertedGates.Add(gate)) return;
        RecordTransition(gate, asserted: true, asserter, reason);
        if (!wasPaused) PauseStateChanged?.Invoke(true);
        GatesChanged?.Invoke();
    }

    // Clear gate. Idempotent — clearing a gate that wasn't asserted is a no-op
    // (no history entry, no event). asserter names the clearing subsystem and
    // reason a human-readable cause (e.g. "lastKill=giant_rat heldFor=14.2s").
    public void ClearGate(string gate, string? asserter = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        if (!_assertedGates.Remove(gate)) return;
        RecordTransition(gate, asserted: false, asserter, reason);
        if (!IsPaused) PauseStateChanged?.Invoke(false);
        GatesChanged?.Invoke();
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

// One row in MovementCoordinator.History. Timestamp is the wall-clock time of
// the transition; Gate is one of the MovementCoordinator constants; Asserted
// is true for assert, false for clear; Asserter/Reason are null when the
// caller didn't tag itself.
public readonly record struct GateTransitionEntry(
    DateTimeOffset Timestamp,
    string Gate,
    bool Asserted,
    string? Asserter,
    string? Reason);
