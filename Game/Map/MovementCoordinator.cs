using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Map;

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

    // Asserted by AutoWalkManager when a walk-to leaves a room with an engaged
    // target still alive — the walker holds so we don't sprint away from a fight
    // that followed us. Distinct from UserGate so an abandoned-combat hold is an
    // engine wait, NOT a user pause: it auto-clears the moment the room is clear
    // of hostiles (Combat gate drops), without the user pressing Resume. Kept
    // out of the manual-override tier so the toolbar's Start/Pause/Stop never
    // flip on it.
    public const string AbandonedCombatGate = "AbandonedCombat";

    // Asserted by DarkRoomMovementSettle for a brief window after we dead-reckon
    // a move into a too-dark room. A dark room reveals its occupant only a beat
    // AFTER the move confirms — via the mob's "strides in" arrival or its first
    // dark-cyan attack line — but the dark advance confirms synchronously, so
    // without this the loop fires the NEXT move before the monster surfaces and
    // marches straight past the fight. This holds the walker for that beat: if a
    // hostile reveals, the Combat gate asserts and takes over the hold; if the
    // room really is empty, the settle timer clears and the loop steps on. Kept
    // out of the manual-override tier (like AbandonedCombatGate) so the toolbar
    // Start/Pause/Stop never flip on it. Self-clears on its own timer.
    public const string DarkRoomSettleGate = "DarkRoomSettle";

    // Asserted by CombatRedisplaySettle for a brief window when a combat line
    // arrives in a LIT room our view shows empty — something is swinging at us
    // that the room lost (a hostile that leapt in a beat after an empty render).
    // The room re-render (a CR "where am I") lands after the move confirms, so
    // without this the loop steps past the fight before the mob reveals. This
    // holds the walker until the re-display resolves: a hostile surfaces and the
    // Combat gate takes over the hold, or the room is truly empty and the settle
    // clears so the loop steps on. The dark-room analogue is DarkRoomSettleGate;
    // this is the lit-room twin (dark rooms suppress the CR, so the two never
    // overlap). Engine-wait tier — never flips the toolbar Start/Pause/Stop.
    // Self-clears on the next room observation or a short timeout.
    public const string CombatRedisplaySettleGate = "CombatRedisplaySettle";

    // Asserted by SummonOnDeathSettle for a brief window when the engine kills a
    // monster whose DeathSpell summons another. The kill clears the Combat gate and
    // steps the walker synchronously, before the summon's arrival line is received,
    // so without this the walker drags the fresh summon into the next room. Holds
    // the walker while a CR re-scans: the summon surfaces and the Combat gate takes
    // over, or the room is empty and the settle clears so the walker steps on.
    // Engine-wait tier — never flips the toolbar Start/Pause/Stop. Self-clears on
    // the next room observation or a short timeout.
    public const string SummonDeathSettleGate = "SummonDeathSettle";

    // Asserted by HealthManager when HP drops below the configured rest
    // trigger; clears when HP recovers past the configured rest target.
    public const string HealthRecoveryGate = "HealthRecovery";

    // Asserted by HealthManager when the caster pool drops below the
    // configured meditate trigger; clears when MA recovers past the target.
    public const string ManaRecoveryGate = "ManaRecovery";

    // Asserted for the WHOLE time the Auto-All kill switch is engaged: with Auto-All
    // off, no movement engine (walk / loop / auto-lair / a right-click Queue-walk-to)
    // may run — a start plans but holds here until Auto-All is restored, then auto-
    // resumes. Engine-wait tier (like SearchGate) — never flips the toolbar Start /
    // Pause / Stop, so the user's own Pause/Resume face is untouched.
    public const string AutoAllGate = "AutoAll";

    // Asserted by the in-room acquisition engine while the loot step runs
    // after a fight clears; clears when all flagged ground items + coins are
    // resolved. This is the get-clear contributor to the in-room loop's
    // movement gate (fight-clear ∧ get-clear ∧ vitals-OK).
    public const string AcquisitionGate = "Acquisition";

    // Asserted by AutoSearchManager while a room-wide `sea` is owed after a fight:
    // a search won't run mid-combat, so the engine defers it, holds here through
    // the fight, fires the `sea` the moment the room clears, and keeps holding a
    // short settle so the revealed "You notice" survey comes back and the get
    // engines collect it before the loop sneaks and steps on. Engine-wait tier —
    // never flips the toolbar Start/Pause/Stop; self-clears on the settle timer.
    public const string SearchGate = "Search";

    // Asserted by GhSweepManager (Roomba Mode) while a get/drop dispatched at
    // a gang-house circuit room is being searched or a transaction is outstanding, so the sweep's own loop
    // doesn't step out mid-dispatch. Engine-wait tier — never flips the
    // toolbar Start/Pause/Stop.
    public const string GhSortGate = "GhSort";

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

    // Asserted by PartyWaitMovementGate while at least one other party member has
    // asked us to hold via an inbound @wait telepath (or a .@held say, which the
    // PartyAilmentTracker routes through the same NotePause path). Clears when
    // every waiting member sends @ok. The leader-side "ignore @wait when leading"
    // opt-out is honoured upstream in PartyEssentialHandlers.NotePause, so this
    // gate only reflects waits the user hasn't chosen to ignore.
    public const string PartyWaitGate = "PartyWait";

    // Asserted by PlayerDroppedGate while the local character is mortally wounded
    // (HP <= 0). A dropped character can't move on their own — they're dragged by a
    // party member, not walking — so every movement engine pauses until HP climbs
    // back positive. Auto-clears on recovery, unlike the death halt's UserGate,
    // which waits for a manual resume.
    public const string MortallyWoundedGate = "MortallyWounded";

    // Asserted by AllyDroppedHandler while a party / recently-partied ally is down
    // (dropped to the ground). We hold our own movement so we stay in the room and
    // keep aiding / healing them instead of walking the farm loop off without the
    // downed member. Clears when the last downed ally recovers, rejoins, dies, or
    // the rescue window times out.
    public const string AllyDownGate = "AllyDown";

    // Asserted by PartyDisconnectMovementGate (leader side) while a party follower
    // has dropped connection and we're inside the reconnect grace window. Holds so
    // we don't sprint off without them — a returning member can reconnect and
    // re-party in place. Clears when the dropped member re-follows us or the window
    // (Settings → Party "If leading, wait only") elapses.
    public const string MemberDisconnectGate = "MemberDisconnect";

    // Asserted by ConfusionMovementGate while the local character is confused. A
    // follower afflicted with a curable ailment telepaths the leader @wait so the
    // party pauses; a leader (or solo player) has no one to signal — the eaten
    // @wait left our own navigation running while confused. This gate is the local
    // analogue: our own confusion holds our walk / loop / auto-lair until it
    // clears, matching the party-pause a confused follower already triggers.
    // Honours the Ignore Confusion setting (same gate the @wait obeys).
    public const string ConfusionGate = "Confusion";

    // Asserted by SelfHeldResponder while the local character is held / knocked
    // down (MovementPrevented). A knockdown blocks movement server-side — every
    // move sent while down bonks ("You are flat on your back!") — so a loop that
    // kept walking would hammer the server with refused moves and strand the
    // RoomTracker on the unresolved step. This holds our walk / loop / auto-lair
    // for the duration so nothing is sent until "You get back on your feet."
    // clears the condition. Unlike confusion there's no opt-out — held always
    // holds. A held follower's own movement is already gated (FollowerGate); this
    // matters for a held leader / solo whose .@held pause has no one to signal.
    public const string HeldGate = "Held";

    private const int HistoryCapacity = 200;

    private readonly LogService? _log;
    private readonly HashSet<string> _assertedGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<GateTransitionEntry> _history = new(HistoryCapacity);
    private readonly object _historyLock = new();

    // True once the user has engaged autonomous movement (a walk-to, a loop,
    // or Auto-Lair) and it hasn't been explicitly stopped since. Distinct
    // from IsPaused (an asserted gate halts sends but leaves the engine
    // engaged) and from "is an engine currently attached" to
    // EngineRecoveryGate: a genuinely Lost tracker makes every engine refuse
    // to (re)attach in the first place (AutoWalkManager.WalkTo /
    // LoopRunner.StartInternal both bail on a null source room before ever
    // touching the gate) — that refusal is the whole reason PassiveRelocalizer
    // exists, so "attached" can't double as ITS gate for whether Stage 2 may
    // run. This is the persistent latch it reads instead.
    public bool AutomationEngaged { get; private set; }

    // Fires only on a real AutomationEngaged flip (Engage/Disengage are both
    // idempotent no-ops when already at the target state, matching
    // PauseStateChanged's own "only real transitions" contract). PassiveRelocalizer
    // is the reason this exists: a character can go Suspect/Lost BEFORE the user
    // presses Play, and RoomTracker.StateChanged — the relocalizer's only other
    // subscription — has nothing left to fire once the tracker is already sitting
    // in that state, so engaging automation would otherwise never re-evaluate it.
    public event Action<bool>? AutomationEngagedChanged;

    // Called by AutoWalkManager.WalkTo / LoopRunner.StartInternal /
    // AutoLairManager.Start — the one choke point each engine's own "start"
    // funnels through regardless of which surface invoked it (toolbar,
    // Navigation window, an internal detour/retry/reroute), so a latch wired
    // into only one caller can't miss another. Idempotent.
    public void EngageAutomation()
    {
        if (AutomationEngaged) return;
        AutomationEngaged = true;
        _log?.Info("Gate", "automation engaged");
        AutomationEngagedChanged?.Invoke(true);
    }

    // Called by the same three engines' own Stop(reason), plus the toolbar
    // and Navigation-window master Stop actions directly (those must disarm
    // even when none of the three engines happens to be active — the exact
    // shape PassiveRelocalizer's own Stage-2 walk runs in). Never called from
    // an engine's internal failure teardown (AbortFromRecoveryFailure, a
    // blocked-after-retries reset, ...) — those call their private Reset()
    // directly, not Stop(), so the recovery walk this latch protects isn't
    // disarmed by the very failure it exists to recover from.
    public void DisengageAutomation()
    {
        if (!AutomationEngaged) return;
        AutomationEngaged = false;
        _log?.Info("Gate", "automation disengaged");
        AutomationEngagedChanged?.Invoke(false);
    }

    // True when at least one gate is asserting pause.
    public bool IsPaused => _assertedGates.Count > 0;

    // Read-only snapshot of currently-asserted gate names. Useful for the
    // Navigation status strip: "paused: User, HealthRecovery".
    public IReadOnlyCollection<string> AssertedGates => _assertedGates.ToArray();

    // True when the named gate is currently asserting pause. Allocation-free
    // single-gate probe — for callers that only care about one gate on a hot
    // path (e.g. the dark-room settle watcher checking whether Combat took over
    // its window) rather than the whole AssertedGates snapshot.
    public bool IsGateAsserted(string gate) => _assertedGates.Contains(gate);

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
