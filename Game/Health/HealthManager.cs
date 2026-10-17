using System.ComponentModel;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Health;

// Passive HP/MA threshold behavior. Asserts and clears
// MovementCoordinator.HealthRecoveryGate + ManaRecoveryGate on configured
// thresholds and drives the rest / stand cycle with pre- / post-rest command
// sequencing. Does NOT decide spell casts — those route through CastingDirector.
//
// State model — three transitions per pool (HP and MA each track independently):
//   Threshold breach: HP / MA drops to or below the configured rest-trigger.
//     Asserts the corresponding recovery gate. Walker (and any other gate
//     consumer) pauses immediately. The gate itself doesn't commit to a rest —
//     ConfirmHpGate / ConfirmMaGate re-check the pool one dispatch tick later,
//     so a momentary dip that a regen tick immediately undoes (both parsed from
//     the same wire read) retracts the gate instead of sitting down for it.
//     Before the rest command actually goes out, a confirmed gate keeps
//     re-checking against that same trigger on every tick — a fight can run on
//     for several more rounds after the confirm, and if regen climbs the pool
//     back past the trigger before combat ends, the gate clears there instead
//     of holding out for the full target (see hpClearFloor / maClearFloor).
//   Rest-out: when either gate is HELD AND CONFIRMED AND the player is out of
//     combat (PlayerState.InCombat false), send any configured pre-rest
//     command(s) and then `rest`. Idempotent — won't re-send rest while one is
//     already in flight.
//   Recovery complete: once the rest/meditate send is in flight, the clear
//     floor switches from the trigger to the configured rest-target (a much
//     higher bar) — hysteresis so an active rest holds out to a full recovery
//     instead of standing back up the instant the pool ticks one point above
//     the trigger. Both pools reaching target clears both gates and emits any
//     post-rest command(s); the next move stands the character up. Walker
//     resumes when the last gate clears.
//
// In-combat semantics: the HP/MA gates can assert mid-fight (so the walker
// doesn't try to leave the room when a fight is going badly), but `rest` is NEVER
// sent while PlayerState.InCombat is true. As soon as CombatStateTracker clears
// the CombatGate and InCombat flips false, the next Evaluate tick fires the rest
// command.
//
// Pre/post-rest commands honour the ^M-or-; chaining convention documented on
// HealthSettings.PreRestCommand: split the string on either marker, trim each
// fragment, send each as its own wire line.
//
// Run-if-below: when PlayerState.Hp drops to or below HealthSettings.RunIfBelowHp
// OR the caster pool drops to or below HealthSettings.RunIfBelowMa mid-combat AND
// a movement engine is active, the active engine is paused and the character flees
// CombatSettings.RunDistance rooms, optionally preceded by `break`. Backward mode
// (the default) runs BFS from the current room back to the active engine's
// JourneyOrigin and walks the first RunDistance directions of that path — the
// reverse of the trail we came in on. Anchoring on the fixed origin is what keeps
// the retreat heading away from the fight instead of bouncing back into it. When
// the reverse path can't be computed (no origin / unknown room / no graph) it falls
// back to inverting the last sent direction for a single step. Forward mode ("go
// backwards if running" off) instead keeps pressing along the engine's own planned
// route toward its destination — the next RunDistance moves it would have sent
// anyway. The engine resumes via IRecoverableEngine.ResumeAfterRecovery once BOTH
// pools climb back above their run-triggers. Multi-step flee advances one queued
// direction per NoteRoomChanged.
//
// Hang-if-below: PlayerState.Hp at or below HealthSettings.HangIfBelowHp, with
// a hostile in the room, fires a single-shot hard disconnect via the configured
// exit command. 0 is a live trigger, not an off switch (DisableHangups is). The
// trigger stays live all the way through the bleeding-out window: a MajorMUD
// character at 0 HP or below hasn't died yet (death happens at the per-realm
// negative floor, RealmProfile.PlayerDiesAtHp) and
// can still hang up, so the disconnect keeps firing down to — but not past — that
// floor, giving a dropped-but-not-yet-dead character a last chance to escape.
public sealed class HealthManager : IDisposable
{
    // LogService category — appears as [Health] rows per assert / clear / rest /
    // stand decision.
    public const string LogCategory = "Health";

    // Identifier the HealthManager uses when flipping the HealthRecovery /
    // ManaRecovery gates. Surfaces in MovementCoordinator.History.
    public const string AsserterName = "HealthManager";

    private readonly PlayerState _state;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<HealthSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string>? _readHangupCommand;
    private readonly Func<Map.IRecoverableEngine?>? _getActiveMovementEngine;
    private readonly Func<Map.Direction?>? _getLastSentDirection;
    private readonly Func<Map.RoomKey, Map.RoomKey, IReadOnlyList<Map.Direction>?>? _findReversePath;
    private readonly Func<Models.Profile.CombatSettings>? _readCombatSettings;
    private readonly Func<Models.Profile.GeneralSettings>? _readGeneralSettings;
    private readonly Func<bool>? _hasEngageableHostiles;
    private readonly Func<bool>? _hasHostileInRoom;
    // Live Auto-Combat toggle + a poke into CombatManager. The engage-to-clear
    // override (see Evaluate's rest section) only fires when Auto-Combat is OFF —
    // with it ON the engine already fights the blocker, so there's nothing to force.
    private Func<bool>? _isAutoCombatEnabled;
    private Action? _requestRestClearEngage;
    // Defers a reaction one dispatch tick so a LATER line in the same wire read
    // can settle before we commit to it. Two uses: the flee branch in Evaluate
    // (the round's death line is parsed AFTER the end-of-round prompt in the
    // same read) and ConfirmHpGate / ConfirmMaGate (a regen tick can follow the
    // prompt that dropped a pool below its rest-trigger). Synchronous (a => a())
    // when unwired.
    private readonly Action<Action> _post;
    private readonly Func<int>? _readDeathFloor;
    private readonly HangupSignal? _hangupSignal;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private Action<byte[]>? _hangupWireSender;  // un-wrapped: pierces EngineSendGate
    private Action? _requestHangupDisconnect;   // hard-close the socket after the exit command
    private Func<string, bool>? _tryWimpyGoto;  // sys-goto-wimpy escape substitute for the hangup
    private Action<bool>? _logHangupPenalty;    // the realm's hang-up penalty, for the log; arg = the PvP response's hang-up
    private Func<Models.Profile.PartySettings>? _readPartySettings; // @panic send/ignore gates
    private Func<bool>? _selfIsPartyLeader;     // in a party AND leading it (@panic broadcast gate)
    private Func<bool>? _isPartyFollower;       // in a party AND not the leader
    private Action? _requestPartyWait;          // ping leader to halt (PartyRestSync)

    // The note for our @wait: HP and mana share one wait reason, and only the gates
    // here say which pool is short. HP is named when both are — it is the one that
    // gets a follower killed. Null with neither asserted.
    public string? PartyWaitNote =>
        _hpGateAsserted ? PartyRestSync.HpNote
        : _maGateAsserted ? PartyRestSync.ManaNote
        : null;
    private Action? _requestPartyOk;            // release leader
    private Func<bool>? _isLeaderResting;       // follower + leader is resting/meditating
    private Func<bool>? _isLeaderWaited;        // WE lead + a member has @wait-held us
    private Func<bool>? _isSelfPoisoned;        // local character is currently poisoned
    private Action? _requestPartyHeal;          // follower flee-substitute: broadcast @heal
    private Func<bool>? _shadowRestClass;       // race or class has the ShadowRest ability (code 1103)
    private Func<bool>? _sneakKept;             // SneakGuard.Holds — rest / meditate end a sneak
    private Func<bool>? _shadowRestStealthed;   // currently hidden or sneaking
    private Func<bool>? _shadowRestSolo;        // not in a party (ShadowRest is a solo behavior)
    private Action? _onShadowRestRecovered;     // recovery hit rest-max — resume combat
    private bool _shadowRestWasHolding;         // falling-edge latch for the resume callback
    private Action? _onRecoveryComplete;        // any rest gate topped off — resume a held neutral engage
    private bool _wasRecovering;                // falling-edge latch for _onRecoveryComplete
    private Func<bool>? _shouldSkipRestHere;    // running loop's current room is a "do not rest" waypoint
    private Func<bool>? _isRestEnabled;         // the Auto-Rest switch; unset = resting follows the engine switch
    private Func<(bool Hp, bool Mana)>? _restHere;  // running loop's current room is a "rest up here" waypoint
    private Func<bool>? _equipmentApplying;     // a gear-set swap is streaming wear/rem — hold rest so we don't thrash it
    // Rest-target pool ceilings. _defaultSetMax* = the DEFAULT gear set's max HP/mana
    // — the loadout the user's rest %s are tuned against, so a Pre-rest set that swaps
    // a +MaxHP/+MaxMana item doesn't move the target. _realMax* = the CURRENT gear's
    // authoritative max (stat-screen MaxHits/MaxMana) — a hard cap so a rest set that
    // LOWERS the pool can never push a rest target out of reach and strand the rest
    // (report paradigm-20260902-052036). Null until wired → fall back to live _state.MaxHp/MaxMa.
    private Func<int>? _defaultSetMaxHp;
    private Func<int>? _defaultSetMaxMa;
    private Func<int>? _realMaxHp;
    private Func<int>? _realMaxMa;
    private bool _skipRestDeferredRecovery;     // a do-not-rest room made us skip a needed rest; re-arm on the next room change
    private bool _partyWaitSignaled;            // @wait sent, awaiting @ok
    private bool _wasHpBelowFloor;              // HP recovery gate held at the last Evaluate
    private bool _wasMaBelowFloor;              // mana recovery gate held at the last Evaluate
    private DateTimeOffset _lastWaitResentAt;   // rate-limits the dragged-while-recovering re-ask
    private static readonly TimeSpan WaitResendInterval = TimeSpan.FromSeconds(5);
    private bool _hpGateAsserted;
    private bool _maGateAsserted;
    // True once a just-asserted gate has survived one deferred dispatch-tick
    // re-check (ConfirmHpGate / ConfirmMaGate below) without recovering back
    // above its trigger. Only the actual rest/meditate SEND waits on this —
    // the gate flags above still assert immediately, so the walker still
    // pauses and @wait still fires the instant a pool dips, unchanged.
    private bool _hpGateConfirmed;
    private bool _maGateConfirmed;
    private bool _restInFlight;          // sent rest, awaiting recovery

    // A rest under way was broken off outside a fight and hasn't been re-sent yet.
    // The re-send usually goes out on the same pass, but it can be put off (a
    // ShadowRest sneaks first), and on the next pass nothing else says we were
    // mid-recovery: the gate then cleared against the rest trigger instead of the
    // rest target, and the character stood there at 64% with the rest dropped
    // (report paradigm-20261004-202253). Held until the rest goes out again, a
    // fight starts, we move, or the gates clear.
    private bool _restResumeOwed;
    private bool _restConfirmedByPrompt; // observed (Resting) since the last rest emit
    private bool _wasPoisoned;           // poison state last Evaluate — for the poison-cleared re-rest edge

    // "You are too sick to rest!" / "…to meditate!": poisoned, so the rest never
    // started. Hold the re-send while we know we're poisoned, and otherwise retry
    // after a while — the refusal is often the only sign of a poison the par screen
    // hasn't reported yet, and waiting on a poison-cleared edge that never comes
    // left the character standing below its rest floor.
    private DateTimeOffset? _restRefusedSickAt;
    private static readonly TimeSpan RestRefusedSickRetry = TimeSpan.FromSeconds(15);

    // "Meditation will not help at this time.": mana is already full, whatever our
    // stats say. Rest instead for a while rather than re-send a meditate the game
    // keeps turning down.
    private DateTimeOffset? _meditateNotNeededAt;
    private static readonly TimeSpan MeditateNotNeededWindow = TimeSpan.FromSeconds(30);

    // The mana the game called full on that refusal, and the max we believed then.
    // While the believed max is unchanged, reaching that mana counts as rested: our
    // max can overshoot the game's (report paradigm-20260928-223148 — a Pre-rest Mana
    // set put us at 423/448 when the game said 423 was full, so recovery chased 448
    // forever and the party @ok never went out).
    private int? _manaGameFullAt;
    private int _manaGameFullMax;

    // A follower's @ok waits while a Pre-rest set is still worn (or a swap is
    // streaming): back in Default gear first, a fresh prompt checks the pools, then
    // @ok (user, 2026-09-28). Capped so a revert that never comes can't strand it.
    private Func<bool>? _holdPartyOk;
    private DateTimeOffset? _partyOkHeldSince;
    private static readonly TimeSpan PartyOkHoldCap = TimeSpan.FromSeconds(20);

    // True while a rested follower's @ok is waiting on the gear revert.
    public bool IsPartyOkHeldForGear => _partyOkHeldSince is not null;

    public void SetPartyOkHold(Func<bool> hold) => _holdPartyOk = hold;

    // A follower's @ok also waits for the pools to STAY at rest-max for a moment. HP
    // can read a few points over rest-max on one prompt and back under the rest floor
    // on the next — report paradigm-20260929-233636: 199→200→203→199 in one burst with
    // the floor at 201 and rest-max at 203 — and an @ok on that blip went out between
    // two @waits. The scheduler re-evaluates once the window passes, since a rested
    // character's prompt can go quiet.
    private DateTimeOffset? _partyOkRestedSince;
    private static readonly TimeSpan PartyOkSettle = TimeSpan.FromSeconds(1);
    private Action<TimeSpan, Action>? _schedule;

    public void SetScheduler(Action<TimeSpan, Action> schedule) => _schedule = schedule;
    // The idle-stall watchdog force-clears combat OPTIMISTICALLY and sends a resync
    // CR; the re-display that re-confirms a still-present monster lands a beat later.
    // Resting the instant InCombat flips false fires in that gap — a blinded / slow
    // monster still in the room got a `rest` sent at it (paradigm-20260814-225055).
    // Hold the rest-out branch until the next room observation re-confirms presence:
    // a lingering hostile re-asserts the hostiles guard, an empty room lets the held
    // rest through. Set on force-clear, cleared on the next observation / room change.
    private bool _restHeldPendingReconfirm;
    // When the reconfirm hold was set. An empty, static room never emits the
    // "Also here:" line that clears the hold, and a stationary character never
    // triggers a room change, so the hold is released after RestReconfirmTimeout
    // as a backstop — by then the resync re-display has had time to re-assert any
    // real hostile (which the hostiles guard then blocks). Kept short.
    private DateTimeOffset _restHoldSetAt;
    private static readonly TimeSpan RestReconfirmTimeout = TimeSpan.FromSeconds(3);
    // Armed when the reconfirm hold times out (the room's stayed empty for the
    // window, so it's safe to rest). The idle-stall force-clear that set the hold
    // does NOT clear CombatStateTracker's hostile latch — that only re-derives on a
    // fresh room observation, which an empty static room never emits — so a
    // stationary character held below the mana trigger would sit forever, its
    // meditate/rest blocked by a stale hostiles guard while it passively regens
    // (report paradigm-20260827-082222: "meditating state but not Medding / no gear
    // swap"). This bypasses that stale guard for the held rest; cleared on the next
    // genuine observation / room change (which re-derives presence for real).
    private bool _restHostilesBypassArmed;
    private readonly Func<DateTimeOffset> _now;
    // A hostile is blocking a needed rest while Auto-Combat is OFF and HP is still
    // above the run (flee) trigger — CombatManager reads this to engage-to-clear the
    // room despite being disabled. Re-poke throttle so one engage carries the fight
    // (the server auto-repeats the swing) but a stalled auto-repeat is re-kicked.
    private bool _forceClearForRest;
    private DateTimeOffset _restClearLastEngageAt;
    private static readonly TimeSpan RestClearReEngageInterval = TimeSpan.FromSeconds(5);
    // Latch so the "rest-clear held despite a blocker" diagnostic logs once per held
    // stretch, not every Evaluate tick.
    private bool _restClearHeldLogged;
    // A gear-set swap's max-pool confirmations ("You are now wearing X") stream in over
    // several seconds AFTER the paced send finishes — EquipmentMaxPoolSync applies a
    // +MaxHP/+MaxMana delta per echo — so PlayerState.MaxHp/MaxMa, and every rest
    // threshold derived from it (DefaultSetMaxPool = liveMax − worn + def, plus the
    // real-max caps), walk through partial values while the swap settles. Re-deciding a
    // rest gate against that moving max flips it around a pinned pool value: the target
    // drops under the current pool → gate clears → recovery-complete reverts to Default
    // → the max jumps back → the pool now reads below trigger → gate re-asserts → the
    // pre-rest swap fires again … the Pre-rest⇄Default gear thrash of report
    // paradigm-20260916-141742. Freeze each gate's assert/clear transition until its
    // pool's max has held steady for this window, so the gate only ever re-decides
    // against a coherent (settled) max. This only DELAYS the decision by the window —
    // once settled the real-max caps still apply (the lowered-pool strand fixes
    // paradigm-20260902-052036 / -20260903-110346 stand), and a static-max fight is
    // never frozen (report paradigm-20260912-103110 unaffected). Resolves on the next
    // incidental Evaluate after the window (prompts / meditate ticks fire ~1/s).
    private static readonly TimeSpan MaxPoolSettleWindow = TimeSpan.FromMilliseconds(1500);
    private int _lastSeenMaxHp = -1;
    private int _lastSeenMaxMa = -1;
    private DateTimeOffset _maxHpChangedAt;
    private DateTimeOffset _maxMaChangedAt;
    // True between the run-if-below reaction (a flee for leader / solo, or a
    // broadcast @heal for a party follower) and the next time
    // PlayerState.InCombat goes false. Single-shot per combat so a low-HP fight
    // can't burn the reaction on every HP-changed event.
    private bool _fledThisCombat;

    // The flee move last sent, and the ways out the game refused during this run by
    // the room they were tried from. Dropped when the run ends.
    private Map.Direction? _lastFleeMove;
    private readonly HashSet<(Map.RoomKey Room, Map.Direction Dir)> _refusedFleeMoves = new();
    private bool _wasInCombat;           // previous Evaluate's InCombat — falling-edge detection for the rest-send reconfirm
    private bool _hangFired;             // emergency-hangup latch; re-arms when danger passes
    private Map.IRecoverableEngine? _fleeEngine;     // engine we paused mid-flee
    private readonly Queue<Map.Direction> _fleeQueue = new(); // remaining flee steps, one per room arrival
    private Map.RoomKey? _lastKnownRoom;             // updated on every NoteRoomChanged
    private bool _disposed;

    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(state, coordinator, readSettings, isEnabled, readHangupCommand: null, log) { }

    // Constructor with a readHangupCommand selector so the hangup-on-emergency
    // path uses the user's configured exit command (the realm's Game exit
    // command on Settings → BBS + Display). Without it, the hangup path no-ops
    // with a log warning. AppServices wires () => GameCommands.ExitCommand.
    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        Func<string>? readHangupCommand,
        LogService? log = null)
        : this(state, coordinator, readSettings, isEnabled,
               readHangupCommand,
               getActiveMovementEngine: null,
               getLastSentDirection: null,
               readCombatSettings: null,
               readGeneralSettings: null,
               hasEngageableHostiles: null,
               readDeathFloor: null,
               log) { }

    // Full constructor. The additional selectors wire the flee path:
    //   getActiveMovementEngine — returns the IRecoverableEngine that's currently
    //     running (Walker / Loop / AutoLair are exclusive). Returns null when no
    //     engine is active — flee then no-ops, since flee-if-below only fires
    //     while a movement engine is running.
    //   getLastSentDirection — most recent outbound direction, inverted for the
    //     Backward flee fallback when no reverse path can be computed. Typically
    //     wired to the last entry on EngineRecoveryGate.ExecutedSinceAnchor.
    //   findReversePath — (from, to) → the BFS direction list from one room to
    //     another, or null when unreachable. The Backward flee calls this with
    //     (current room, engine JourneyOrigin) to lay the reverse trail. Wired to
    //     BfsMapper.FindPath; null takes the fallback.
    //   readCombatSettings — for the flee knobs CombatSettings.RunDirection,
    //     BreakBeforeFleeing and RunDistance.
    //   readGeneralSettings — for GeneralSettings.AllowHangupInAllOffMode, the
    //     emergency-hangup carve-out.
    //   hasEngageableHostiles — returns true while the room contains at least one
    //     engageable monster. Gates the rest-out branch so we don't spam `rest`
    //     every tick while a hostile keeps breaking it (a room with hostiles
    //     breaks resting every combat round, so the room must be cleared first).
    //     Typically wired to CombatStateTracker.HasEngageableHostiles.
    //   hasHostileInRoom — returns true while a hostile monster is in the room,
    //     independent of the auto-attack master switch (unlike
    //     hasEngageableHostiles, which reports false whenever auto-attack is off).
    //     Gates the emergency hangup: a low-HP disconnect is an escape from a
    //     fight, so with no hostile present there's nothing to flee and dropping
    //     the carrier would only strand a safe-but-wounded character in a
    //     reconnect loop. Wired to CombatStateTracker.HasHostileMonster.
    //   readDeathFloor — the realm's negative-HP death floor (RealmProfile.
    //     PlayerDiesAtHp, e.g. -25). The emergency-hangup path fires anywhere in
    //     the bleeding-out window (hang-trigger down to this floor) but bails once
    //     HP has fallen past it — a character at or below the floor is already
    //     dead, so there's nothing left to disconnect. Null defaults to -25.
    //   hangupSignal — flags an intentional disconnect so the reactive-reconnect
    //     path stands down. The emergency hangup drops the carrier on purpose;
    //     without signalling it, MainWindowViewModel would classify the drop as
    //     unexpected and dial straight back in — exactly what a low-HP hangup is
    //     meant to prevent. Wired to AppServices.HangupSignal.
    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        Func<string>? readHangupCommand,
        Func<Map.IRecoverableEngine?>? getActiveMovementEngine,
        Func<Map.Direction?>? getLastSentDirection,
        Func<Models.Profile.CombatSettings>? readCombatSettings,
        Func<Models.Profile.GeneralSettings>? readGeneralSettings,
        Func<bool>? hasEngageableHostiles,
        Func<int>? readDeathFloor = null,
        LogService? log = null,
        HangupSignal? hangupSignal = null,
        Func<bool>? hasHostileInRoom = null,
        Func<Map.RoomKey, Map.RoomKey, IReadOnlyList<Map.Direction>?>? findReversePath = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null,
        Func<Models.Profile.PartySettings>? readPartySettings = null,
        Func<bool>? selfIsPartyLeader = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _state = state;
        _coordinator = coordinator;
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        _readHangupCommand = readHangupCommand;
        _getActiveMovementEngine = getActiveMovementEngine;
        _getLastSentDirection = getLastSentDirection;
        _findReversePath = findReversePath;
        _readCombatSettings = readCombatSettings;
        _readGeneralSettings = readGeneralSettings;
        _hasEngageableHostiles = hasEngageableHostiles;
        _hasHostileInRoom = hasHostileInRoom;
        _readDeathFloor = readDeathFloor;
        _log = log;
        _hangupSignal = hangupSignal;
        _post = post ?? (a => a());
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _readPartySettings = readPartySettings;
        _selfIsPartyLeader = selfIsPartyLeader;
        _state.PropertyChanged += OnStateChanged;
    }

    // Bind the wire sender. Until set, the engine logs decisions but doesn't
    // actually send rest / stand / pre- / post-rest commands.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind a SEPARATE, un-wrapped wire sender for the emergency low-HP hangup.
    // Every other HealthManager send flows through _wireSender, which the app
    // wraps through EngineSendGate — so when a hold is up (e.g. the dropped /
    // mortally-wounded hold) those sends silently drop. That's correct for rest
    // / stand / flee (a dropped character can't do them anyway), but the hangup
    // MUST survive the very hold that a drop raises: hanging up is still allowed
    // at or below 0 HP, and it's the dropped character's last escape. Wiring
    // this to the raw un-wrapped SendUserInput lets the hangup pierce the gate.
    // Falls back to _wireSender when unset (pre-wire).
    public void SetHangupWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _hangupWireSender = sender;
    }

    // Wire the client-side socket close for the emergency hangup. The exit
    // command alone leaves the drop to the server (which may not act, or acts
    // slowly, leaving a mortally-wounded character sitting connected); this lets
    // us also close the carrier ourselves. The callback owns the flush timing —
    // it must let the just-sent exit command reach the wire before disposing the
    // socket (see MainWindowViewModel.RequestHangupDisconnect). Unset (pre-wire)
    // means the pre-existing "send exit, wait for server" behaviour.
    public void SetHangupDisconnect(Action requestDisconnect)
    {
        ArgumentNullException.ThrowIfNull(requestDisconnect);
        _requestHangupDisconnect = requestDisconnect;
    }

    // Wire the "sys goto wimpy instead of hanging" escape. When set and the
    // character's HealthSettings opt in with a location, the emergency low-HP path
    // calls this instead of dropping the carrier: it breaks combat and fires
    // `sys goto <location>`. The callback returns true when it dispatched the jump
    // (so the hangup is skipped), false when it couldn't — power off on this BBS,
    // the location no longer in the table — so the hangup runs as the fallback.
    // Unset (minimal wiring) means no substitute: the normal hangup fires.
    public void SetWimpyGoto(Func<string, bool> tryWimpyGoto)
    {
        ArgumentNullException.ThrowIfNull(tryWimpyGoto);
        _tryWimpyGoto = tryWimpyGoto;
    }

    // Wire the log line that says what the realm's hang-up penalty (Settings →
    // BBS) makes of a hang-up this manager has just sent. The argument is true
    // for the PvP response's hang-up, the one case only the caller here knows.
    // It reports and nothing more: the hang-up has gone out by the time it runs.
    public void SetHangupPenaltyLog(Action<bool> logHangupPenalty)
    {
        ArgumentNullException.ThrowIfNull(logHangupPenalty);
        _logHangupPenalty = logHangupPenalty;
    }

    // Wire party-role-aware recovery. isPartyFollower returns true when the local
    // character is following a party leader (in a party AND not the leader). While
    // following:
    //   The recovery gates clear as soon as a pool climbs just past the
    //     rest-trigger floor (target = trigger + 1) rather than the full rest-max
    //     — a follower tops off to safety, not to full, so it doesn't hold the
    //     party for a routine heal. The party healer / leader owns full topoff.
    //   requestPartyWait fires when a recovery gate first asserts (dropped below
    //     the floor → ping the leader to halt); requestPartyOk fires when the last
    //     gate clears (back above the floor → release the leader).
    // Until wired — or when not following — recovery targets rest-max and no party
    // signals are emitted (solo / leader behavior). The callbacks (typically
    // PartyRestSync.RequestWait / RequestOk) self-gate on party membership, so
    // invoking them solo is a safe no-op.
    //
    // isLeaderResting (optional) reports whether we're a follower and the party
    // leader is currently resting / meditating. When true and no recovery gate is
    // held, Evaluate opportunistically tops off to rest-max during the leader's
    // downtime — a follower mirrors the leader's rest, gated on the auto-heal
    // master switch AND not being poisoned (isSelfPoisoned). Left null preserves
    // the old gate-only rest behavior.
    //
    // isLeaderWaited (optional) reports whether WE lead the party and a member has
    // telepathed @wait, so our movement is held. When true and not poisoned, the
    // leader uses the forced downtime to top off to rest-max — same "rest to use
    // the wait" behavior as the follower's opportunistic path, just triggered by
    // our own held state instead of the leader's posture. Left null: leaders never
    // rest just because they're waited.
    //
    // isSelfPoisoned (optional) reports whether the local character is poisoned.
    // Gates BOTH downtime-rest paths (leader-waited and follower-mirrors-leader):
    // a poisoned character skips the opportunistic rest (poison ticks break rest
    // and waste the downtime). Does NOT gate the normal threshold-driven rest —
    // a poisoned character below its floor still needs to recover. Left null
    // treats us as never poisoned (the pre-gate behavior).
    //
    // requestPartyHeal (optional) is the follower's flee-substitute: when the
    // run-if-below HP trigger fires AND we're a follower, Evaluate invokes this
    // instead of TryFlee — a follower must not run off alone (it breaks party
    // formation), so it broadcasts @heal and stays put while the party healer tops
    // it up. Leader / solo still flee. Left null, a follower's low HP is held
    // with nothing asked: it still doesn't run (TryFlee). Typically wired to
    // PartyRestSync.RequestHeal.
    public void SetPartyRoleSync(
        Func<bool> isPartyFollower,
        Action requestPartyWait,
        Action requestPartyOk,
        Func<bool>? isLeaderResting = null,
        Action? requestPartyHeal = null,
        Func<bool>? isLeaderWaited = null,
        Func<bool>? isSelfPoisoned = null)
    {
        ArgumentNullException.ThrowIfNull(isPartyFollower);
        ArgumentNullException.ThrowIfNull(requestPartyWait);
        ArgumentNullException.ThrowIfNull(requestPartyOk);
        _isPartyFollower = isPartyFollower;
        _requestPartyWait = requestPartyWait;
        _requestPartyOk = requestPartyOk;
        _isLeaderResting = isLeaderResting;
        _requestPartyHeal = requestPartyHeal;
        _isLeaderWaited = isLeaderWaited;
        _isSelfPoisoned = isSelfPoisoned;
    }

    // A loop room flagged "rest up here" (HP and/or mana): rest to that pool's rest-max
    // there, even above the rest-if-below trigger, unless it's already at rest-max.
    public void SetRestHereSelector(Func<(bool Hp, bool Mana)> restHere) => _restHere = restHere;

    // The loop's pre-step check: in a rest-up-here room, raise the rest hold now —
    // before the next move goes out — rather than on the next prompt. True while a
    // rest hold is up, so the step waits for it.
    public bool HoldForRestHere()
    {
        if (_restHere?.Invoke() is not { } f || !(f.Hp || f.Mana)) return false;
        Evaluate();
        return _hpGateAsserted || _maGateAsserted;
    }

    // Do-not-rest, a room whose own spell does damage, and Auto-Rest being off, win
    // over rest-up-here on the same room.
    private (bool Hp, bool Mana) RestHereNow() =>
        RestSwitchedOff() || _shouldSkipRestHere?.Invoke() == true || _roomSpellHurting?.Invoke() is not null
            ? default
            : _restHere?.Invoke() ?? default;

    // Wire the damaging-room probe: the name of the spell on the room we stand in
    // when it bars resting (Settings → Periodic Damage Room Spells) and no counter
    // is worn or held against it, else null. It is asked afresh each time, so a
    // change saved in Settings is in effect at the next check. No rest or meditate
    // is started in such a room (user, 2026-10-09: "we
    // should heal but not actively try to rest in a room like this"): the game
    // re-casts the spell every six seconds and each hit breaks the rest, so resting
    // there is a rest / stand loop that recovers nothing. It behaves as a do-not-rest
    // room does: no recovery hold is raised, so a walk or loop carries on out of it,
    // and the rest is taken up in the next room that doesn't hurt. Healing by spell
    // goes on meanwhile (RestDeferredByRoomSpell).
    public void SetRoomSpellDamageProbe(Func<string?> roomSpellHurting)
    {
        ArgumentNullException.ThrowIfNull(roomSpellHurting);
        _roomSpellHurting = roomSpellHurting;
    }

    private Func<string?>? _roomSpellHurting;
    private bool _roomSpellSkipLogged;       // once per room: the "not resting here" line
    private Map.RoomKey? _roomSpellSkipRoom; // the placed room that flag stands for, null when not known
    private bool _restOwedFromDamagingRoom;  // a rest was put off for a room's spell and hasn't started since

    // The spell whose damage is keeping a due rest from starting in this room, or
    // null. While set with HP under its rest trigger, CastingDirector heals as it
    // would during the rest that isn't happening (HpRestDeferredByRoomSpell).
    public string? RestDeferredByRoomSpell { get; private set; }

    public bool HpRestDeferredByRoomSpell { get; private set; }

    // Wire the Auto-Rest switch. The engine switch (isEnabled) turns the whole
    // manager off; this one turns off the resting — the recovery holds and the
    // rest / meditate sends — and the low-HP emergency hangup with it
    // (LowHpHangupHeld), but leaves the flee running, so a character healing by
    // spell alone still runs when it must.
    public void SetRestEnabledGate(Func<bool> isRestEnabled)
    {
        ArgumentNullException.ThrowIfNull(isRestEnabled);
        _isRestEnabled = isRestEnabled;
    }

    private bool RestSwitchedOff() => _isRestEnabled?.Invoke() == false;

    // Wire the master switch: isOff reads it, noteHeld counts a hang-up it held
    // back (AutoModeController.Blocks). Unwired, the switch reads as on.
    public void SetMasterSwitch(Func<bool> isOff, Action<string?> noteHeld)
    {
        ArgumentNullException.ThrowIfNull(isOff);
        ArgumentNullException.ThrowIfNull(noteHeld);
        _masterSwitchOff = isOff;
        _noteHeldByMasterSwitch = noteHeld;
    }

    private Func<bool>? _masterSwitchOff;
    private Action<string?>? _noteHeldByMasterSwitch;

    // The master-switch rule every hang-up of ours follows (user, 2026-10-09): with
    // the switch off, "none of our auto systems should respond" unless Allow hangup
    // in all-off mode is ticked, and then every hang-up is respected. what names
    // the hang-up for the log; null for the low-HP check, which is asked on every
    // prompt inside its window.
    private bool HeldByMasterSwitch(GeneralSettings? general, string? what)
    {
        if (_masterSwitchOff?.Invoke() != true) return false;
        if (general is { AllowHangupInAllOffMode: true }) return false;
        _noteHeldByMasterSwitch?.Invoke(what);
        return true;
    }

    // Wire the loop's per-waypoint "do not rest here" check: returns true when a
    // loop is running and the room we're standing in is a waypoint flagged
    // DoNotRest, so the rest hold is suppressed and the loop advances out of it.
    // Left unwired, resting is unaffected.
    public void SetDoNotRestSelector(Func<bool> shouldSkipRestHere)
    {
        ArgumentNullException.ThrowIfNull(shouldSkipRestHere);
        _shouldSkipRestHere = shouldSkipRestHere;
    }

    // Wire the "gear swap in flight" probe (EquipmentManager.IsApplyingSet). While a
    // set applies, its paced `wear`/`rem` commands each stand the character up; hold
    // the rest re-issue so we don't fire `rest` between every command (the rest/stand
    // thrash of report paradigm-20260825-103537). The one rest lands after the swap.
    public void SetEquipmentApplyingProbe(Func<bool> equipmentApplying)
    {
        ArgumentNullException.ThrowIfNull(equipmentApplying);
        _equipmentApplying = equipmentApplying;
    }

    // Wire the "wear the rest gear first" hook (AutoEquipCoordinator
    // .WearRestGearBeforeResting): true when it started a swap, which the `rest` then
    // waits out (see the send site).
    public void SetRestGearFirst(Func<bool> wearRestGearFirst)
    {
        ArgumentNullException.ThrowIfNull(wearRestGearFirst);
        _wearRestGearFirst = wearRestGearFirst;
    }
    private Func<bool>? _wearRestGearFirst;

    // Wire the rest-target pool ceilings (see the _defaultSetMax* / _realMax* fields).
    // Both providers are optional — unset leaves the pre-existing live-max behaviour.
    public void SetRestPoolMaxProviders(
        Func<int>? defaultSetMaxHp, Func<int>? defaultSetMaxMa,
        Func<int>? realMaxHp, Func<int>? realMaxMa)
    {
        _defaultSetMaxHp = defaultSetMaxHp;
        _defaultSetMaxMa = defaultSetMaxMa;
        _realMaxHp = realMaxHp;
        _realMaxMa = realMaxMa;
    }

    // Resolve a rest trigger + rest-max for a pool, anchored to the DEFAULT gear set's
    // max and capped at the current gear's real max (see RestThresholds for the why).
    // Invokes the wired providers, then delegates to the pure resolver.
    private (int Trigger, int Max) ResolveRestThresholds(
        ThresholdMode mode, int triggerPct, int maxPct,
        Func<int>? defaultMax, Func<int>? realMax, int liveMax)
        => RestThresholds.Resolve(mode, triggerPct, maxPct,
            defaultMax?.Invoke() ?? 0, realMax?.Invoke() ?? 0, liveMax);

    // The extra max-pool a currently-worn Pre-rest gear set adds OVER the Default
    // loadout — the amount live HP/MA is inflated by, and the amount that will be
    // stripped when the set reverts to Default at rest completion. Used to raise the
    // rest clear floor so a rest doesn't "finish" on inflated pool only to drop back
    // below the trigger the instant the boosting gear comes off. Zero when in Default
    // gear (live == default) or when the Default basis isn't known (returns 0 → the
    // clear floor is unchanged from the pre-fix behavior).
    private static int PoolBoostOverDefault(int liveMax, Func<int>? defaultMax)
    {
        int basis = defaultMax?.Invoke() ?? 0;
        return basis > 0 ? Math.Max(0, liveMax - basis) : 0;
    }

    // A single HP / MA threshold (flee / hang trigger) resolved against the same
    // Default-set basis + real-max cap the rest gates use — so heal/run/hang anchor to
    // the loadout the user tuned rather than a Pre-rest set's altered pool.
    private int ResolveHpThreshold(ThresholdMode mode, int pct)
        => RestThresholds.ResolveValue(mode, pct,
            _defaultSetMaxHp?.Invoke() ?? 0, _realMaxHp?.Invoke() ?? 0, _state.MaxHp);
    private int ResolveMaThreshold(ThresholdMode mode, int pct)
        => RestThresholds.ResolveValue(mode, pct,
            _defaultSetMaxMa?.Invoke() ?? 0, _realMaxMa?.Invoke() ?? 0, _state.MaxMa);

    // Wire ShadowRest (Paradigm): classes with the ability can rest while
    // hidden/sneaking in a room with monsters without being attacked (see
    // GAME_MECHANICS "ShadowRest"). All three predicates plus the
    // HealthSettings.UtilizeShadowRest toggle must hold for the behavior to engage:
    //   shadowRestClass — the character's class carries ability code 1103.
    //   isStealthed     — currently hidden OR sneaking (StealthManager.IsStealthed).
    //   isSolo          — not in a party (resting hidden un-targets party heals).
    // onRecovered fires once when recovery reaches rest-max (a held rest gate
    // clears) while ShadowRest was holding — the resume signal that lets
    // CombatManager re-open with a backstab now that we're topped off and still
    // stealthed. Left unwired, ShadowRest simply never engages.
    public void SetShadowRest(
        Func<bool> shadowRestClass,
        Func<bool> isStealthed,
        Func<bool> isSolo,
        Action onRecovered)
    {
        ArgumentNullException.ThrowIfNull(shadowRestClass);
        ArgumentNullException.ThrowIfNull(isStealthed);
        ArgumentNullException.ThrowIfNull(isSolo);
        ArgumentNullException.ThrowIfNull(onRecovered);
        _shadowRestClass = shadowRestClass;
        _shadowRestStealthed = isStealthed;
        _shadowRestSolo = isSolo;
        _onShadowRestRecovered = onRecovered;
    }

    // Fires once each time a rest gate tops off to rest-max (falling edge of
    // IsRecoveringRest). Wired to CombatManager.ResumeAfterRecovery so a combat hold
    // that deferred a passive-neutral engage for this rest re-engages when we're
    // topped off. Left unwired, recovery still works — nothing re-engages.
    public void SetRecoveryCompleteCallback(Action onRecoveryComplete)
    {
        ArgumentNullException.ThrowIfNull(onRecoveryComplete);
        _onRecoveryComplete = onRecoveryComplete;
    }

    // True when a room hostile is blocking a needed rest, Auto-Combat is OFF, and HP
    // is still above the run (flee) trigger — the deadlock where we can neither rest,
    // fight, nor flee. CombatManager reads this to engage-to-clear the room despite
    // being disabled; released the moment the blocker's gone (or we drop into flee).
    public bool ForceClearForRest => _forceClearForRest;

    // Wire the engage-to-clear override: isAutoCombatEnabled reports the live
    // Auto-Combat toggle (the override only fires when it's off), requestEngage pokes
    // CombatManager to attack the room's hostile. Left unwired, the deadlock stands.
    public void SetRestClearEngage(Func<bool> isAutoCombatEnabled, Action requestEngage)
    {
        ArgumentNullException.ThrowIfNull(isAutoCombatEnabled);
        ArgumentNullException.ThrowIfNull(requestEngage);
        _isAutoCombatEnabled = isAutoCombatEnabled;
        _requestRestClearEngage = requestEngage;
    }

    // True when every ShadowRest precondition holds: the user opted in, the class
    // has the ability, and we're solo and currently stealthed. This is the "can
    // rest safely with a monster in the room" condition — it relaxes the rest-out
    // hostiles guard.
    // Rest and meditate end a sneak (GAME_MECHANICS "Sneaking — commands, equip
    // order, and the sneak state machine" → "What ends a sneak"), so while
    // SneakGuard keeps one they wait — unless the race or class has ShadowRest, whose
    // rest keeps the stealth.
    public void SetSneakKeptProbe(Func<bool> sneakKept) => _sneakKept = sneakKept;

    // StealthManager.SneakBeforeRest — a ShadowRest character sneaks first so its
    // rest stays stealthed.
    private Func<bool>? _sneakBeforeRest;

    public void SetSneakBeforeRestProbe(Func<bool> sneakBeforeRest) => _sneakBeforeRest = sneakBeforeRest;

    // True while a rest is held for the sneak that goes first. The sneak's answer
    // re-runs Evaluate (AppServices), so the rest goes out the moment it lands rather
    // than at the next HP or mana change (report paradigm-20260930-192045).
    public bool RestWaitingOnSneak { get; private set; }

    private bool SneakingBeforeShadowRest(HealthSettings s)
    {
        bool waiting = s.UtilizeShadowRest && _shadowRestClass?.Invoke() == true
            && _sneakBeforeRest?.Invoke() == true;
        if (waiting && !RestWaitingOnSneak)
            _log?.Combat(LogCategory, "rest held a moment — sneaking first so it's a ShadowRest");
        RestWaitingOnSneak = waiting;
        return waiting;
    }

    // The user utilizes ShadowRest and the race or class has it: a rest keeps the
    // stealth, so sneaking before (or during) one is worth it.
    public bool UsesShadowRest => _readSettings().UtilizeShadowRest && _shadowRestClass?.Invoke() == true;

    // Not mid-fight: an attack ends the sneak in game, but the client only drops its
    // stealth state once the room clears, so a fight still reads as stealthed. Holding
    // combat for a "ShadowRest" there let a monster swing at us unanswered until regen
    // lifted HP past the rest trigger (report paradigm-20260930-192645).
    private bool ShadowRestActive() =>
        _readSettings().UtilizeShadowRest
        && !_state.InCombat
        && _shadowRestClass?.Invoke() == true
        && _shadowRestStealthed?.Invoke() == true
        && _shadowRestSolo?.Invoke() == true;

    private bool _loggedRestHeldForSneak;

    // A rest the gates call for (rest-if-below) goes out even if it ends the sneak
    // (user, 2026-09-28); only an optional rest — opportunistic, or using a party
    // wait — holds for it.
    private bool RestHeldForSneak(bool gateDriven)
    {
        bool held = !gateDriven && _sneakKept?.Invoke() == true && _shadowRestClass?.Invoke() != true;
        if (held && !_loggedRestHeldForSneak)
            _log?.Combat(LogCategory, "rest held — resting would end the sneak we're keeping");
        _loggedRestHeldForSneak = held;
        return held;
    }

    // True while a ShadowRest recovery is in progress — ShadowRest is active AND a
    // rest gate is held (HP/MA below its floor, climbing toward rest-max). Combat
    // reads this to stand down so the character stays hidden and rests; it drops to
    // false the moment recovery tops off (the gate clears), which is the falling
    // edge that fires the resume callback.
    public bool ShadowRestHolding =>
        ShadowRestActive() && (_hpGateAsserted || _maGateAsserted);

    // True while the HP gate is held.
    public bool HpGateAsserted => _hpGateAsserted;

    // True while the MA gate is held.
    public bool MaGateAsserted => _maGateAsserted;

    // True while an auto-rest recovery is in flight — HP or MA fell below its
    // rest-if-below trigger and we're resting back up to rest-max. This is the
    // "triggered rest" the auto-bless engine holds during (as opposed to idle /
    // standing / idly resting), so blessing defers to recovery unless the user
    // opts into "bless while resting."
    public bool IsRecoveringRest => _hpGateAsserted || _maGateAsserted;

    // A rest or meditate still doing its job where we stand, whoever sent it — the
    // engine or the player by hand: the posture is up and a pool is short of rest-max.
    public bool RestingShortOfRestMax =>
        (_state.Position is PlayerPosition.Resting or PlayerPosition.Meditating)
        && NeedsOpportunisticTopOff(_readSettings());

    // A rest we sent finished (the pools reached their targets) within window — for a
    // sit the server confirms only after that, which is the tail of a finished rest.
    public bool RecoveredWithin(TimeSpan window) => _now() - _recoveredAt <= window;
    private DateTimeOffset _recoveredAt = DateTimeOffset.MinValue;

    // True from the rest emit until the recovery completes or the rest is broken.
    public bool RestInFlight => _restInFlight;

    // The rest and flee latches that are set right now, for a bug report: what a
    // character that won't rest, won't stop resting or won't come back is held on.
    public IReadOnlyList<string> DescribeWaits()
    {
        List<string> waits = new();
        if (_hpGateAsserted) waits.Add("HP recovery gate asserted");
        if (_maGateAsserted) waits.Add("mana recovery gate asserted");
        if (_restInFlight)
            waits.Add("rest sent" + (_restConfirmedByPrompt ? ", confirmed by the prompt" : ", not yet confirmed by the prompt"));
        if (_restResumeOwed) waits.Add("a rest resume is owed");
        if (_restHeldPendingReconfirm)
            waits.Add($"rest held pending a re-read since {_restHoldSetAt.ToLocalTime():HH:mm:ss}");
        if (_restOwedFromDamagingRoom) waits.Add("a rest put off for the room's spell is owed");
        if (RestWaitingOnSneak) waits.Add("rest waiting on a kept sneak");
        if (_partyWaitSignaled) waits.Add("@wait sent, @ok not yet");
        if (_partyOkHeldSince is { } okHeld) waits.Add($"@ok held for gear since {okHeld.ToLocalTime():HH:mm:ss}");
        if (_fledThisCombat) waits.Add("fled this combat (waits for it to end)");
        if (_fleeEngine is not null) waits.Add($"fleeing, {_fleeQueue.Count} step(s) left");
        if (_deferredFleeReason is { } deferred) waits.Add($"a flee is deferred: {deferred}");
        if (_fleeResumeAwaitsPrompt) waits.Add("the flee's resume waits for a prompt");
        if (_escapeSentAt is { } escape) waits.Add($"escape sent at {escape.ToLocalTime():HH:mm:ss}");
        if (_forceClearForRest) waits.Add("engaging to clear a rest-blocker");
        return waits;
    }

    // True while an HP-triggered flee retreat is actively in progress — the
    // engine is paused and we're walking (or parked awaiting HP recovery on) the
    // flee route. Drops back to false once HP climbs above the run-trigger and
    // the engine resumes. The room-entity classifier reads this so a monster that
    // pursues us mid-flee does NOT re-arm the combat gate: we keep running instead
    // of turning to fight the thing we're fleeing. Distinct from _fledThisCombat,
    // which stays true for the rest of the combat even after the retreat ends.
    public bool IsFleeing => _fleeEngine is not null;

    // A flee just began (the retreat is planned and the engine paused for it).
    public event Action? FleeStarted;

    // A flee the run-if-below HP / MA gates started — not a hit-and-run or a failed
    // backstab's run. Only this one lets the emergency heal cut ahead of the
    // re-sneak (user, 2026-09-28).
    public bool IsGateFleeing => _fleeEngine is not null && _fleeFromGates;
    private bool _fleeFromGates;
    private bool _fleeCarriesOn;   // the engine goes on from where this run lands (see TryFlee)
    private bool _deferredFleeFromGates;

    // The last confirmed room before the current one (RoomTracker history) — the
    // Backward flee's fallback when there's no trail to the engine's origin (we're
    // standing on it). Null when unknown.
    public Func<Map.RoomKey?>? PreviousRoom { get; set; }

    // A room's exits by direction (RoomGraph). Lets a flee with no trail left pick a
    // way out that isn't back into the room it just fled. Null when unknown.
    public Func<Map.RoomKey, IReadOnlyDictionary<Map.Direction, Map.RoomKey>?>? RoomExits { get; set; }

    // Whether a room is a boss room and its lair's max spawn — what a flee picking its
    // own way out steers around. Null when unwired (every room reads safe).
    public Func<Map.RoomKey, (bool Boss, int LairMax)>? RoomRisk { get; set; }

    // Whether routes keep out of a room (MovementFilter.IsClosedToRoutes: Crystal Lake's
    // teleporting rooms). A flee builds its own retreat, so the filter that keeps routes
    // out would not reach it. Null when unwired (no room is closed).
    public Func<Map.RoomKey, bool>? IsClosedToRoutes { get; set; }

    // True while a move is sent but its landing isn't confirmed (RoomTracker Pending).
    // A flee started then is held until the room confirms (see TryFlee).
    public Func<bool>? IsMovePending { get; set; }

    // True while the user has the walk, loop or Auto-Lair run paused (the Pause
    // button, a typed move, a Stop-held errand): MovementController.IsUserPaused. A
    // fight or a rest holding the engine is not this. Read by FleeFromMonster, for
    // which a paused engine is no engine (user, 2026-10-10: "a paused loop or walk
    // to or autolair, does not count as running, this should be idling").
    public Func<bool>? IsNavigationPausedByUser { get; set; }

    // True while the server is auto-attacking (CombatStateTracker.IsServerEngaged).
    // Null keeps the always-break behavior.
    public Func<bool>? IsServerEngaged { get; set; }

    // The room the flee's latest step left from — a "change" back to it isn't a landing.
    private Map.RoomKey? _fleeFromRoom;

    // The flee's last step has landed. Until then the engine stays paused whatever the
    // pools read — a flee at healthy HP (hit and run, a failed backstab) would otherwise
    // "complete" while its move is still in flight (report paradigm-20260927-011624).
    private bool _fleeLanded;

    // A flee is moving and hasn't landed yet — combat holds its engages meanwhile.
    public bool IsFleeInFlight => (_fleeEngine is not null && !_fleeLanded) || _deferredFleeReason is not null;
    private string? _deferredFleeReason;
    private Func<bool>? _deferredFleeStillWanted;   // the held flee's own landing check (see TryFlee)

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.Ma):
            case nameof(PlayerState.InCombat):
            case nameof(PlayerState.HasPromptData):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.MaxMa):
            case nameof(PlayerState.Position):
                Evaluate();
                break;
        }
    }

    // Re-evaluate gate state + rest/stand pacing against the current player
    // state.
    public void Evaluate()
    {
        if (!_isEnabled())
        {
            // Engine off: Auto-Heal and Auto-Rest are both off.
            // Defensive clear in case it was asserted just before the
            // user toggled off.
            if (_hpGateAsserted)
            {
                _hpGateAsserted = false;
                _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                    AsserterName, "auto-heal disabled");
            }
            if (_maGateAsserted)
            {
                _maGateAsserted = false;
                _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
                    AsserterName, "auto-heal disabled");
            }
            // The confirmations have to drop with the gates that earned them.
            // They're only ever cleared alongside an ASSERTED gate elsewhere, so
            // a confirmation left standing here survives the toggle — and on
            // re-enable it feeds anyGateConfirmed with no gate asserted at all,
            // firing a rest at full health that re-fires every time _restInFlight
            // clears (nothing can reach the clear branch to retract it).
            _hpGateConfirmed = false;
            _maGateConfirmed = false;
            // Don't leave a follower's leader hanging on a stale @wait when
            // the engine toggles off mid-recovery.
            if (_partyWaitSignaled)
            {
                _partyWaitSignaled = false;
                _requestPartyOk?.Invoke();
            }
            _restInFlight = false;
            _restResumeOwed = false;
            _restConfirmedByPrompt = false;
            _wasPoisoned = false;
            _fledThisCombat = false;
            RestDeferredByRoomSpell = null;
            HpRestDeferredByRoomSpell = false;
            _restOwedFromDamagingRoom = false;
            // A stale engage-to-clear latch (report paradigm-20260903-073107) would let
            // CombatManager keep bypassing the auto-combat-off gate — firing a swing /
            // drain at the next room's hostile — even though the rest engine is now off.
            // Drop it here too so disabling rest releases the override.
            if (_forceClearForRest)
            {
                _forceClearForRest = false;
                _log?.Combat(LogCategory, "engage-to-clear released — auto-heal / rest engine disabled");
            }

            // The engine is off, but the low-HP hang-up has its own rule: with the
            // master switch off it still runs when the user opted into Allow
            // hangup in all-off mode, so an AFK character isn't left dying.
            // TryEmergencyHangup decides; only that branch runs, everything else
            // above already cleared.
            if (_state.HasPromptData) TryEmergencyHangup(_readSettings());
            return;
        }
        if (!_state.HasPromptData) return;
        HealthSettings s = _readSettings();

        // Emergency hangup evaluates first and runs through the whole
        // bleeding-out window: a dropped character (Hp <= 0 but not yet at the
        // realm death floor) can still hang up, so this must precede the
        // dead/dropped early-return below — otherwise a bleeding-out non-caster
        // (Ma also 0) would skip the disconnect entirely. When it actually
        // fires there's nothing left to rest / flee for, so we're done.
        if (TryEmergencyHangup(s)) return;

        // At or below 0 HP the character is dropped / mortally wounded (or dead)
        // and can't rest / stand / flee — the game rejects every action command.
        // The emergency hangup already ran above (it's the one send allowed while
        // dropped), so there's nothing left for this tick to do. Bailing on Hp
        // alone (not Hp && Ma) also skips the zero-on-zero prompt-race assert:
        // PromptParser writes Hp + MaxHp before flipping HasPromptData, so a real
        // live character is never at Hp <= 0 here. PlayerDroppedGate holds the
        // engine + movement gates for the whole dropped window; recovery routing
        // for an actual death runs through DeathLineWatcher.
        if (_state.Hp <= 0) return;

        // Rest-interruption recovery on a resting-state change. Two-step
        // latch so we don't race the (Resting)/(Meditating) prompt arrival:
        //   1. We send `rest` or `meditate` and set _restInFlight=true.
        //   2. On the FIRST Evaluate tick where Position is Resting OR
        //      Meditating, we flip _restConfirmedByPrompt=true — the
        //      server has put us into one of the two resting-family
        //      positions (ChooseRestCommand picks whichever command the
        //      moment calls for; either lands here).
        //   3. Any subsequent tick where Position is neither (server
        //      broke our rest because we took damage, entered combat, cast
        //      a bless, or moved) drops _restInFlight so the rest-out
        //      branch below re-fires.
        // Without step 2, a fast follow-up HP-changed tick that fires
        // before the prompt arrives would spuriously clear _restInFlight
        // and double-send the command. Checking only Resting here (report:
        // meditate never re-engaged after a bless interrupted it) left
        // step 2 permanently unreached for Meditating — _restConfirmedByPrompt
        // never flipped true, so step 3's guard never tripped either, and
        // _restInFlight stuck true until the next room move (NoteRoomChanged
        // clears it unconditionally) masked the bug for movers but not for a
        // party member sitting still recovering mana.
        bool restingFamily = _state.Position is PlayerPosition.Resting or PlayerPosition.Meditating;
        // Snapshot whether we were genuinely mid-recovery THIS tick, before the
        // interruption branch below resets it — used only to pick this tick's
        // clear-floor further down, never persisted. Without it, a same-tick
        // interruption (a due self-buff standing us up under
        // SelfBlessWhileResting, or any other momentary stand) flips
        // _restInFlight false BEFORE the clear-floor check runs, which
        // silently drops the floor from the rest-MAX target down to the much
        // lower rest-trigger — clearing a still-below-target gate as if the
        // trigger were "good enough" and releasing movement having recovered
        // only a fraction of the intended pool (report paradigm-20260915-211744:
        // a vlwa recast stood the character up at 356/682 mana, the gate cleared
        // against the 273 trigger instead of the 614 target, and the loop
        // resumed immediately).
        bool wasActivelyResting = _restInFlight && _restConfirmedByPrompt;
        if (_restInFlight && restingFamily)
        {
            _restConfirmedByPrompt = true;
            _restRefusedSickAt = null;
        }
        else if (_restInFlight && _restConfirmedByPrompt && !restingFamily)
        {
            _restInFlight = false;
            _restConfirmedByPrompt = false;
            // Stood up outside a fight (our own cast, a swap): the rest is owed again,
            // whenever the command can next go out.
            _restResumeOwed = !_state.InCombat;
            _log?.Combat(LogCategory,
                $"rest interrupted — position now {_state.Position} " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa} " +
                $"inCombat={_state.InCombat})");
        }

        // Role-aware recovery target: a follower clears the gate just
        // above the rest floor (target = trigger + 1) so it doesn't make
        // the party wait for a full topoff; solo / leader recover to
        // rest-max. Defaults to leader/solo when no role selector is wired.
        bool follower = _isPartyFollower?.Invoke() ?? false;
        if (_state.InCombat) _restResumeOwed = false;

        // A loop can flag the room it's standing in as "do not rest here" (too
        // dangerous to sit still). While in such a room we never raise the rest
        // hold — the loop stays running and advances out of it — and we release
        // the hold if it was already up. Only THIS room is protected; the moment
        // the loop steps into another room this re-evaluates and rests normally.
        // Auto-Rest off behaves like a do-not-rest room everywhere: no hold is raised
        // and one already up is released.
        bool restOff = RestSwitchedOff();
        bool doNotRest = _shouldSkipRestHere?.Invoke() ?? false;
        // A room whose own spell damages us is a do-not-rest room of the game's
        // making (SetRoomSpellDamageProbe). Asked only when a rest could otherwise
        // happen, so "deferred for the room" below never names a rest that Auto-Rest
        // being off or Sprint mode had already ruled out.
        string? roomHurts = restOff || doNotRest ? null : _roomSpellHurting?.Invoke();
        bool skipRest = restOff || doNotRest || roomHurts is not null;
        (bool restHereHp, bool restHereMa) = RestHereNow();

        // Falling edge: combat was on as of the previous Evaluate call, off now.
        // See the re-confirm block below (after the gate transitions) for why
        // this matters. Captured before _wasInCombat updates for next time.
        bool combatJustEnded = _wasInCombat && !_state.InCombat;
        _wasInCombat = _state.InCombat;

        // Stamp when either pool's max last moved between two real (>0) values — the
        // 0→N first-prompt population only seeds the baseline (no stamp), so a fresh
        // manager isn't born "unsettled" and the very first assert isn't frozen. A pool
        // whose max changed within MaxPoolSettleWindow is held below, so the rest gate
        // never re-decides against a mid-gear-swap partial max.
        if (_lastSeenMaxHp <= 0) _lastSeenMaxHp = _state.MaxHp;
        else if (_state.MaxHp > 0 && _state.MaxHp != _lastSeenMaxHp) { _lastSeenMaxHp = _state.MaxHp; _maxHpChangedAt = _now(); }
        if (_lastSeenMaxMa <= 0) _lastSeenMaxMa = _state.MaxMa;
        else if (_state.MaxMa > 0 && _state.MaxMa != _lastSeenMaxMa) { _lastSeenMaxMa = _state.MaxMa; _maxMaChangedAt = _now(); }
        bool hpMaxUnsettled = _maxHpChangedAt != default && _now() - _maxHpChangedAt < MaxPoolSettleWindow;
        bool maMaxUnsettled = _maxMaChangedAt != default && _now() - _maxMaChangedAt < MaxPoolSettleWindow;
        // The game's full mark holds only for the max we believed at the refusal, and
        // a higher reading since means its max is at least that (so a stale-low prompt
        // at the refusal can't make every later reading count as full).
        if (_manaGameFullAt is { } gameFull)
        {
            if (_state.MaxMa != _manaGameFullMax) _manaGameFullAt = null;
            else if (_state.Ma > gameFull) _manaGameFullAt = _state.Ma;
        }

        // ----- HP gate transitions ---------------------------------
        (int hpRestTrigger, int hpRestMax) = ResolveRestThresholds(
            s.HpThresholdMode, s.RestIfBelowHp, s.RestMaxHp,
            _defaultSetMaxHp, _realMaxHp, _state.MaxHp);
        // A rest-up-here room rests whenever HP is under rest-max, to rest-max.
        if (restHereHp) hpRestTrigger = hpRestMax;
        int hpRestTarget  = follower && !restHereHp
            ? Math.Min(hpRestTrigger + 1, hpRestMax)
            : hpRestMax;
        // Before the rest command has actually gone out, the gate's clear floor
        // is the ordinary trigger, not the (much higher) target: a gate can assert
        // and confirm mid-fight, then combat runs on for several more rounds while
        // regen climbs the pool back past the trigger well before the fight ends —
        // holding out for the full target in that window means committing to a
        // rest the instant combat clears even though the pool isn't actually low
        // anymore (report paradigm-20260912-103110). Once the rest/meditate SEND
        // has fired (_restInFlight), switch to the target floor so the sit-down
        // holds out to a full recovery instead of standing back up the moment it
        // ticks one point above the trigger.
        // Pool-boost compensation: while a Pre-rest gear set that adds max HP is
        // worn, live HP is inflated by the set's extra pool over Default — but the
        // rest target is anchored to the DEFAULT set's pool. Clearing on the inflated
        // HP finishes the rest, then the revert to Default strips that bonus and drops
        // HP back below the trigger → re-assert → an endless gear-swap/rest thrash
        // (report paradigm-20260921-114318: 398↔423 forever off a +25-HP ring). Raise
        // the resting-case clear floor by that bonus so the rest holds until
        // DEFAULT-equivalent HP reaches the target, leaving HP at/above target once the
        // set comes off. Bounded: target + boost ≤ liveMax (target ≤ defaultBasis,
        // boost = liveMax − defaultBasis), so it can never strand. Zero in Default gear
        // and when the Default basis is unknown (fail-safe → no change from before).
        int hpBoost = PoolBoostOverDefault(_state.MaxHp, _defaultSetMaxHp);
        bool midRecovery = _restInFlight || wasActivelyResting || _restResumeOwed;
        int hpClearFloor = midRecovery ? hpRestTarget + hpBoost : hpRestTrigger;

        // Strictly below — "rest if below N" rests only when the pool is
        // under N, never AT N. (Equal-or-less traps a level-2 mystic: 1 max
        // KAI, trigger 0, spend the KAI → MA 0 == trigger 0 would pause for
        // mana forever.)
        if (!skipRest && !hpMaxUnsettled && !_hpGateAsserted && _state.MaxHp > 0 && _state.Hp < hpRestTrigger)
        {
            _hpGateAsserted = true;
            _hpGateConfirmed = false;
            _coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} < rest-trigger={hpRestTrigger}{(restHereHp ? " (loop room: rest up here)" : "")}");
            // Re-check one dispatch tick later before committing to an actual
            // rest send — see ConfirmHpGate for why.
            _post(ConfirmHpGate);
        }
        else if (_hpGateAsserted && (skipRest || (!hpMaxUnsettled && _state.Hp >= hpClearFloor)))
        {
            _hpGateAsserted = false;
            _hpGateConfirmed = false;
            _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                restOff ? "auto-rest is off"
                    : roomHurts is not null ? $"this room's {roomHurts} does damage — not resting here"
                    : skipRest
                    ? "do-not-rest room — advancing instead of resting"
                    : midRecovery
                        ? $"HP {_state.Hp}/{_state.MaxHp} >= clear-floor={hpClearFloor} (rest-target={hpRestTarget}{(hpBoost > 0 ? $" + pre-rest boost {hpBoost}" : "")})"
                        : $"HP {_state.Hp}/{_state.MaxHp} recovered above rest-trigger={hpRestTrigger} before rest started");
        }

        // ----- MA gate transitions ---------------------------------
        (int maRestTrigger, int maRestMax) = ResolveRestThresholds(
            s.MaThresholdMode, s.RestIfBelowMa, s.RestMaxMa,
            _defaultSetMaxMa, _realMaxMa, _state.MaxMa);
        if (restHereMa) maRestTrigger = maRestMax;
        int maRestTarget  = follower && !restHereMa
            ? Math.Min(maRestTrigger + 1, maRestMax)
            : maRestMax;
        // See hpClearFloor above — same pre-send-vs-resting distinction for MA, and
        // the same Pre-rest Mana-set pool-boost compensation.
        int maBoost = PoolBoostOverDefault(_state.MaxMa, _defaultSetMaxMa);
        int maClearFloor = midRecovery ? maRestTarget + maBoost : maRestTrigger;

        // Strictly below (see HP gate above) — the mystic-at-level-2 case.
        if (!skipRest && !maMaxUnsettled && !_maGateAsserted && _state.Ma < maRestTrigger && _state.MaxMa > 0)
        {
            _maGateAsserted = true;
            _maGateConfirmed = false;
            _coordinator.AssertGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                $"MA {_state.Ma}/{_state.MaxMa} < rest-trigger={maRestTrigger}{(restHereMa ? " (loop room: rest up here)" : "")}");
            // Re-check one dispatch tick later before committing to an actual
            // rest send — see ConfirmMaGate for why.
            _post(ConfirmMaGate);
        }
        else if (_maGateAsserted && (skipRest || (!maMaxUnsettled && (_state.Ma >= maClearFloor || ManaAtGameFull()))))
        {
            _maGateAsserted = false;
            _maGateConfirmed = false;
            _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                restOff ? "auto-rest is off"
                    : roomHurts is not null ? $"this room's {roomHurts} does damage — not resting here"
                    : skipRest
                    ? "do-not-rest room — advancing instead of resting"
                    : ManaAtGameFull() && _state.Ma < maClearFloor
                        ? $"MA {_state.Ma}/{_state.MaxMa} — the game says mana is full"
                    : midRecovery
                        ? $"MA {_state.Ma}/{_state.MaxMa} >= clear-floor={maClearFloor} (rest-target={maRestTarget}{(maBoost > 0 ? $" + pre-rest boost {maBoost}" : "")})"
                        : $"MA {_state.Ma}/{_state.MaxMa} recovered above rest-trigger={maRestTrigger} before rest started");
        }
        if (!_hpGateAsserted && !_maGateAsserted) _restResumeOwed = false;

        // A do-not-rest room can't raise (and clears) the recovery gate even when
        // the pool is below a rest trigger. A plain Standing hop OUT of that room
        // raises no prompt change, so Evaluate wouldn't re-run to re-arm the gate
        // in the next restable room — the deficit would ride untended until some
        // later prompt happens to change (typically back at the loop's circle
        // start, where do-not-rest eats it again: the reported "whole loop won't
        // rest"). Latch the deferred deficit here so NoteRoomChanged re-arms on the
        // next room change.
        if (skipRest && !restOff && (_state.Hp < hpRestTrigger || _state.Ma < maRestTrigger))
            _skipRestDeferredRecovery = true;

        // The same deficit, when it is the room's spell that keeps the rest from
        // starting: said once per room, and remembered so the log can say where the
        // rest finally starts. CastingDirector reads the HP half to heal meanwhile.
        bool hpRestDue = _state.MaxHp > 0 && _state.Hp < hpRestTrigger;
        bool maRestDue = _state.MaxMa > 0 && _state.Ma < maRestTrigger;
        RestDeferredByRoomSpell = roomHurts is not null && (hpRestDue || maRestDue) ? roomHurts : null;
        HpRestDeferredByRoomSpell = roomHurts is not null && hpRestDue;
        if (RestDeferredByRoomSpell is not null)
        {
            _restOwedFromDamagingRoom = true;
            LogRestSkippedForRoomSpell(roomHurts!,
                $"rest due (hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }
        else if (roomHurts is null && _restOwedFromDamagingRoom)
        {
            _restOwedFromDamagingRoom = false;
            if (_hpGateAsserted || _maGateAsserted)
                _log?.Info(LogCategory,
                    $"the rest put off in a damaging room starts here (hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }

        // Auto-Rest switched off while a follower's @wait is out: the pools may never
        // reach rest-max now, so release the leader rather than hold it for a rest
        // that isn't coming. The same in a room whose spell bars resting, which the
        // leader dragged us into or which began to hurt under us: the wait was asked
        // for a rest that can't be taken here, and holding the leader in the heat for
        // its whole wait window helps nobody. The follower keeps following (user,
        // 2026-10-10) and asks again in the next room it can rest in.
        if ((restOff || roomHurts is not null) && _partyWaitSignaled)
        {
            _partyWaitSignaled = false;
            _partyOkRestedSince = null;
            _partyOkHeldSince = null;
            if (roomHurts is not null && _isPartyFollower?.Invoke() == true)
                _log?.Info(LogCategory,
                    $"a @wait is out, but this room's {roomHurts} does damage — releasing the leader with @ok; "
                    + "asking again in the next room that doesn't hurt");
            _requestPartyOk?.Invoke();
        }

        // ----- party-follower @wait / @ok --------------------------
        // @wait fires when a recovery gate first asserts (we dropped below a
        // rest floor). But @ok must NOT ride the same gate: a follower's
        // movement gate releases at trigger+1 (so it keeps pace without a
        // full topoff), and releasing @ok there tells the leader to resume
        // while we're one point above the floor — the party lurches forward,
        // we re-drop, and @wait/@ok flap (report 222618). So hold the signal
        // until BOTH pools reach the full rest-max ceiling — the level the
        // user considers "rested" — decoupled from the movement floor.
        // PartyRestSync self-gates on membership, so these no-op solo/as leader.
        //
        // A FRESH drop re-asks even while still signaled: the @ok waits for full
        // rest-max, so the signal can stay latched across several separate drops,
        // and the leader stops honouring a wait after its "If leading, wait only"
        // window — a follower that never re-asked got walked off mid-rest (report
        // paradigm-20260925-210928: mana gate held for minutes, HP re-dropped with no
        // @wait, the leader moved on).
        bool droppedBelowFloor = _hpGateAsserted || _maGateAsserted;
        // Per pool: an HP re-drop while the mana gate is already held is still fresh.
        // A re-drop seconds after the last @wait is the same dip, not a new one — HP
        // bouncing across the floor re-asked within a second (report
        // paradigm-20260929-233636); the leader's wait window is far longer than that.
        bool freshDrop = ((_hpGateAsserted && !_wasHpBelowFloor) || (_maGateAsserted && !_wasMaBelowFloor))
            && _now() - _lastWaitResentAt >= WaitResendInterval;
        _wasHpBelowFloor = _hpGateAsserted;
        _wasMaBelowFloor = _maGateAsserted;
        if (droppedBelowFloor || !_partyWaitSignaled) _partyOkRestedSince = null;
        if (droppedBelowFloor && (!_partyWaitSignaled || freshDrop))
        {
            _partyWaitSignaled = true;
            _lastWaitResentAt = _now();
            if (_isPartyFollower?.Invoke() == true)
                _log?.Info(LogCategory, "dropped below a rest floor while following — asking the leader to @wait");
            _requestPartyWait?.Invoke();
        }
        // Never release while a recovery gate is still held — below a rest floor is
        // not "rested", whatever the rest-max comparison says — and not while a pool's
        // max is still settling after a gear change. A Pre-rest set streaming on piece
        // by piece moves the live max and the worn set out of step, so the Default-set
        // basis (and with it rest-max) wobbles for a moment; reading "rested" off that
        // wobble sent @ok, the next prompt re-sent @wait, and the pair flapped 3-4
        // times a second (report paradigm-20260926-121252).
        else if (_partyWaitSignaled && !droppedBelowFloor && !hpMaxUnsettled && !maMaxUnsettled)
        {
            bool hpRested = _state.MaxHp <= 0 || _state.Hp >= hpRestMax;
            bool maRested = _state.MaxMa <= 0 || _state.Ma >= maRestMax || ManaAtGameFull();
            if (hpRested && maRested && _partyOkRestedSince is null)
            {
                _partyOkRestedSince = _now();
                _schedule?.Invoke(PartyOkSettle, Evaluate);
            }
            if (hpRested && maRested && _now() - _partyOkRestedSince >= PartyOkSettle)
            {
                bool gearHeld = _holdPartyOk?.Invoke() == true;
                if (gearHeld && _partyOkHeldSince is null)
                {
                    _partyOkHeldSince = _now();
                    _log?.Info(LogCategory, "rested — @ok waits for the Default gear to go back on and the pools to be checked");
                }
                if (!gearHeld || _now() - _partyOkHeldSince >= PartyOkHoldCap)
                {
                    if (gearHeld)
                        _log?.Info(LogCategory, $"Default gear not back after {PartyOkHoldCap.TotalSeconds:0}s — sending @ok anyway");
                    _partyOkHeldSince = null;
                    _partyOkRestedSince = null;
                    _partyWaitSignaled = false;
                    _requestPartyOk?.Invoke();
                }
            }
            else if (!(hpRested && maRested))
            {
                _partyOkHeldSince = null;
                _partyOkRestedSince = null;
            }
        }

        // ----- flee on critical HP/MA mid-combat -------------------
        // Run-if-below: either pool triggers — HP at/below RunIfBelowHp OR the
        // caster pool at/below RunIfBelowMa (an out-of-mana caster is as stuck
        // as a low-HP fighter). Fires only when a movement engine is active —
        // "if you aren't running a movement engine, the flee-if-below wouldn't
        // fire". On trigger: optionally send `break` to disengage combat, then
        // begin a multi-step flee over CombatSettings.RunDistance rooms
        // (Backward = the reverse-BFS trail toward the engine's JourneyOrigin;
        // Forward = the engine's own next planned moves toward its destination).
        // Subsequent steps advance one per NoteRoomChanged; the paused engine
        // auto-resumes once BOTH pools climb back above their run-triggers
        // (recovery branch below).
        // A flee held for a move that never confirmed (a bonk demotes the tracker
        // without a room change) — once nothing is in flight, start it anyway.
        if (_deferredFleeReason is { } stuckReason && _fleeEngine is null
            && IsMovePending?.Invoke() != true)
        {
            _deferredFleeReason = null;
            bool stuckFromGates = _deferredFleeFromGates;
            Func<bool>? stuckStillWanted = _deferredFleeStillWanted;
            _deferredFleeStillWanted = null;
            _post(() =>
            {
                if (stuckStillWanted is not null && !HeldMonsterRunStillWanted(stuckStillWanted))
                    _log?.Combat(LogCategory, $"held flee dropped — what it ran from has gone, or navigation was paused meanwhile ({stuckReason})");
                else
                    TryFlee(stuckReason, stuckFromGates, stuckStillWanted);
            });
        }

        if (!_state.InCombat)
        {
            _fledThisCombat = false;
        }
        else if (!_fledThisCombat)
        {
            int hpRunTrigger = ResolveHpThreshold(s.HpThresholdMode, s.RunIfBelowHp);
            int maRunTrigger = ResolveMaThreshold(s.MaThresholdMode, s.RunIfBelowMa);
            // A run-trigger of 0 means "never flee on this pool" — the pool's
            // flee is off. Gate on the RAW setting so "off" is mode-agnostic
            // (0% and absolute-0 both resolve to a 0 trigger, and without this
            // the MA branch would flee every time mana bottoms out at 0).
            bool hpFleeEnabled = s.RunIfBelowHp > 0;
            bool maFleeEnabled = s.RunIfBelowMa > 0;
            bool hpRun = hpFleeEnabled && _state.MaxHp > 0 && _state.Hp > 0 && _state.Hp <= hpRunTrigger;
            bool maRun = maFleeEnabled && _state.MaxMa > 0 && _state.Ma <= maRunTrigger;
            if (hpRun || maRun)
            {
                _fledThisCombat = true;
                string reason = hpRun
                    ? $"HP {_state.Hp}/{_state.MaxHp} <= run-trigger={hpRunTrigger}"
                    : $"MA {_state.Ma}/{_state.MaxMa} <= run-trigger={maRunTrigger}";
                bool fleeAsFollower = follower;
                // Defer the flee one dispatch tick, then re-verify a live hostile
                // before committing. The end-of-round prompt that dropped us into
                // flee territory is parsed BEFORE the round's death line in the same
                // wire read (PromptScanner.Append runs ahead of Emulator.Feed), so
                // right now a monster we killed this round still reads as present
                // (InCombat true, hostile present). Posting lets the death line
                // process first: a killing blow that empties the room then falls
                // through to rest instead of running from nothing, while a fresh
                // hostile that survived still fires the flee (report
                // stock-20260730-160706). The single-shot latch is set now so a
                // burst of HP updates doesn't queue several flees.
                _post(() => CommitFleeReaction(hpRun, fleeAsFollower, reason));
            }
        }

        // Auto-resume — when a fled engine is paused AND BOTH pools have
        // climbed back above their run-triggers AND no more flee steps
        // are queued, hand control back to the engine. Requiring mana too
        // (when the character has a caster pool) stops us resuming straight
        // into another mana-triggered flee. Backward mode retraces its path
        // from the current room; Forward continues toward the destination.
        // A hit-and-run retreat runs at healthy HP, so "recovered" is already true the
        // moment it starts — hold the resume until its last step has actually landed.
        if (_fleeEngine is not null && _fleeQueue.Count == 0 && _state.MaxHp > 0
            && _fleeLanded && !_fleeResumeAwaitsPrompt)
        {
            int hpRunTrigger = ResolveHpThreshold(s.HpThresholdMode, s.RunIfBelowHp);
            int maRunTrigger = ResolveMaThreshold(s.MaThresholdMode, s.RunIfBelowMa);
            // A disabled pool (run-trigger 0) never blocks resume — otherwise a
            // caster with MA flee off could never climb "above" a 0 trigger with
            // 0 mana and would stay paused forever.
            bool hpRecovered = s.RunIfBelowHp <= 0 || _state.Hp > hpRunTrigger;
            bool maRecovered = s.RunIfBelowMa <= 0 || _state.MaxMa <= 0 || _state.Ma > maRunTrigger;
            // A flee from a player stays away for a while before walking back.
            bool stayedAway = _fleeResumeNotBefore is not { } notBefore || _now() >= notBefore;
            if (hpRecovered && maRecovered && stayedAway && _lastKnownRoom is { } room)
            {
                ClearPlayerFlee();
                _log?.Combat(LogCategory,
                    $"flee complete — resuming engine={_fleeEngine.Name} at {room} " +
                    $"(HP {_state.Hp}/{_state.MaxHp} > {hpRunTrigger}, MA {_state.Ma}/{_state.MaxMa} > {maRunTrigger})");
                _fleeEngine.ResumeAfterFlee(room, _fleeCarriesOn);
                _fleeEngine = null;
                _fleeCarriesOn = false;
                ForgetRefusedMovesElsewhere();
            }
        }

        // ----- rest pacing ------------------------------------------
        // On recovery we send the user's configured post-rest chain
        // (if any) and clear _restInFlight. No "stand" — that's not a
        // valid MajorMUD command; the server auto-stands the player
        // when they next move or act, and the walker's next move
        // (which the resumed nav engine fires once both gates clear)
        // is what actually exits the (resting) state.
        // Combat just ended while a gate is still confirmed: the same same-burst
        // race ConfirmHpGate/ConfirmMaGate guard against at the initial breach
        // can recur right here — a regen tick can land on Hp/Ma's PropertyChanged
        // AFTER InCombat's within the same wire read, so THIS Evaluate call would
        // otherwise fire the send off a value that's about to be overwritten a
        // moment later in the same burst (report paradigm-20260912-123108: MA
        // read 191 mid-fight, was confirmed, climbed to 239 by the time the kill
        // landed, but the stale confirmation sent meditate anyway). Re-run the
        // identical one-tick confirm the initial breach already goes through,
        // gated on the combat-end edge instead of the assert edge — this tick's
        // anyGateConfirmed below sees the reset, so the send waits one more tick.
        if (combatJustEnded)
        {
            if (_hpGateConfirmed)
            {
                _hpGateConfirmed = false;
                _post(ConfirmHpGate);
            }
            if (_maGateConfirmed)
            {
                _maGateConfirmed = false;
                _post(ConfirmMaGate);
            }
        }

        bool anyGate = _hpGateAsserted || _maGateAsserted;
        // Confirmed subset of anyGate — only a gate that's survived its one-tick
        // ConfirmHpGate/ConfirmMaGate re-check. anyGate itself stays the gate used
        // everywhere else below (walker pause already happened via AssertGate,
        // @wait, engage-to-clear-a-blocker) — those are fine to react immediately;
        // it's only the actual rest/meditate SEND further down that waits on this.
        bool anyGateConfirmed = _hpGateConfirmed || _maGateConfirmed;

        // A poisoned character skips the downtime-rest paths below: the game won't
        // let you rest or meditate while poisoned ("You are too sick to rest!"), so
        // sitting during the leader's / our own wait just burns wire round-trips.
        // This gate applies ONLY to the opportunistic paths — below its own rest
        // floor the character still tries through the anyGate branch (the poison
        // flag can lag), and NoteRestRefusedSick holds the re-send once refused.
        bool selfPoisoned = _isSelfPoisoned?.Invoke() ?? false;

        // Poison-cleared re-rest. A rest sent while poisoned never reaches the (Resting)
        // state (poison refuses / breaks it), so it latches _restInFlight but the two-step
        // interruption latch above never clears it (that path needs _restConfirmedByPrompt,
        // which only flips once we're actually Resting). The stale _restInFlight then blocks
        // the re-send once poison wears off — the reported "sat standing below the rest floor
        // after the poison cleared" bug. On the poison falling edge, drop an unconfirmed rest
        // latch so THIS tick's rest-out branch re-sends a fresh rest now that resting will take.
        if (_wasPoisoned && !selfPoisoned && _restInFlight && !_restConfirmedByPrompt)
        {
            _restInFlight = false;
            _log?.Combat(LogCategory,
                $"poison cleared — dropping unconfirmed rest latch to re-rest " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }
        _wasPoisoned = selfPoisoned;

        // Opportunistic follower rest: the leader has stopped to rest /
        // meditate, so we use the downtime to top off too — even above our
        // own rest-trigger floors, up to rest-max. No gate is asserted (we're
        // not below a floor, so we must NOT @wait a leader who's already
        // voluntarily halted, and we don't hold the movement gate). It only
        // engages when there's actually something to recover; once both pools
        // hit rest-max NeedsOpportunisticTopOff goes false and the post-rest
        // chain fires through the shared !shouldRest recovery branch.
        bool opportunisticWanted = !anyGate
            && !restOff
            && !selfPoisoned
            && (_isLeaderResting?.Invoke() ?? false)
            && NeedsOpportunisticTopOff(s);

        // Leader-waited rest: WE lead and a member has @wait-held us, so we're
        // stuck in this room anyway — use the forced downtime to top off. Unlike the
        // follower's opportunistic path (which stops at rest-max), a @wait is bounded
        // downtime the user wants spent fully: rest toward FULL for the whole wait,
        // ending only when the wait itself releases (the member's @ok or the leading
        // wait-window timer), not at an intermediate rest-max floor (report
        // paradigm-20260827-132906). The movement gate keeps us sitting until the
        // @wait clears, at which point the resumed engine's next move stands us up.
        bool leaderWaitedWanted = !anyGate
            && !restOff
            && !selfPoisoned
            && (_isLeaderWaited?.Invoke() ?? false)
            && NeedsWaitDowntimeTopOff();

        // Neither downtime rest is taken in a room whose spell does damage: it would
        // be broken on the spell's next tick like any other.
        bool opportunistic = opportunisticWanted && roomHurts is null;
        bool leaderWaitedRest = leaderWaitedWanted && roomHurts is null;
        if (roomHurts is not null && (opportunisticWanted || leaderWaitedWanted))
            LogRestSkippedForRoomSpell(roomHurts,
                opportunisticWanted ? "the leader is resting" : "waited on by a member");

        bool shouldRest = anyGate || opportunistic || leaderWaitedRest;
        // Gate for the actual send below — a just-asserted, unconfirmed gate
        // doesn't count yet (see anyGateConfirmed above), so a maybe-transient
        // breach can't fire the command before its one-tick re-check runs.
        bool shouldSendRestCommand = anyGateConfirmed || opportunistic || leaderWaitedRest;

        // Don't even try to rest while the room contains an engageable
        // hostile — every combat round breaks rest, so spamming `rest`
        // burns a wire round-trip per swing and we still don't recover.
        // Wait for CombatManager to clear the room (CombatStateTracker
        // flips HasEngageableHostiles false on the next Also-Here),
        // then this same Evaluate tick re-enters here with a clean
        // gate and the rest goes out. If a fresh mob arrives during
        // rest, NoteRoomChanged + a new EntitiesObserved will set
        // HasEngageableHostiles true again and the next breach repeats
        // the cycle (kill → rest → kill → rest), as per user direction.
        bool hostilesPresent = _hasEngageableHostiles?.Invoke() ?? false;

        // A gear-set swap in flight streams paced `wear`/`rem` commands, each of which
        // stands the character up. Hold the rest re-issue until the swap finishes, or
        // we fire `rest` between every command and thrash the whole burst (report
        // paradigm-20260825-103537). The single rest lands once the swap completes and
        // the character is standing with the (e.g. pre-rest mana) loadout on.
        bool equipmentApplying = _equipmentApplying?.Invoke() ?? false;
        if (equipmentApplying && shouldRest && !_state.InCombat && !_restInFlight)
            _log?.Combat(LogCategory, "rest held — a gear-set swap is in flight (avoiding rest/stand thrash)");

        // ShadowRest relaxes the hostiles guard: a solo, stealthed ShadowRest
        // character rests in place even with a monster in the room — the game
        // keeps it un-attacked while stealthed, and combat stands down (reading
        // ShadowRestHolding) so the rest isn't broken by our own swing. Recovery
        // runs to rest-max, then the gate clears and the resume callback re-opens
        // combat with a backstab (we're still stealthed, opener unspent).
        bool shadowRest = ShadowRestActive();
        if (shadowRest && hostilesPresent && shouldRest && !_state.InCombat && !_restInFlight)
            _log?.Combat(LogCategory, "shadowrest — resting with hostile in room (staying stealthed)");

        // Engage-to-clear a rest-blocker with Auto-Combat OFF. The deadlock (report
        // paradigm-20260901-093301): a room hostile keeps InCombat / hostiles-present
        // true so we can't rest, Auto-Combat OFF means CombatManager won't fight it,
        // and HP is still above the run (flee) trigger so we won't flee either — the
        // character sits taking damage. When a rest is DUE (HP or MA gate) and a
        // hostile blocks it, drive the combat engine to clear the room; once it's dead
        // InCombat drops and this same Evaluate rests. If HP falls to the run trigger
        // while fighting, the flee block above takes over instead. Auto-Combat ON needs
        // nothing (the engine already engages); ShadowRest rests through it stealthed.
        bool autoCombatOn = _isAutoCombatEnabled?.Invoke() ?? true;
        int restClearRunTrigger = ResolveHpThreshold(s.HpThresholdMode, s.RunIfBelowHp);
        bool hpFleeWorthy = s.RunIfBelowHp > 0 && _state.Hp > 0 && _state.Hp <= restClearRunTrigger;
        // A rest-blocker registers two ways, and they can fall out of sync: the room
        // roster (hostilesPresent, from the last Also-Here) OR the live combat lines that
        // keep us InCombat. A monster chasing us mid-walk HITS us — flipping InCombat —
        // before, or without, a fresh room re-display lists it in the roster. Keying the
        // clear off the roster alone let a stale-empty roster deadlock the rest: InCombat
        // blocks the rest send, no roster hostile blocks the clear, so we sit and take
        // hits ("stopped to rest, monster entered, didn't fight back"). Arm off EITHER
        // signal — RequestRestClearEngage refreshes the roster when it's the InCombat
        // side that fired, and a genuinely empty re-display clears a stale InCombat.
        bool beingAttacked = _state.InCombat;
        bool restBlocked = hostilesPresent || beingAttacked;
        bool wantRestClear = !autoCombatOn && restBlocked && shouldRest
            && !hpFleeWorthy && !_fledThisCombat && !shadowRest;
        if (wantRestClear)
        {
            if (!_forceClearForRest)
                _log?.Combat(LogCategory,
                    $"engage-to-clear — a hostile blocks rest with auto-combat off " +
                    $"(present={hostilesPresent} inCombat={beingAttacked} hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}); clearing the room to recover");
            // Kick the first attack (deferred past this Evaluate, like the flee post).
            // The server auto-repeats the swing so one engage carries the fight; a
            // stalled auto-repeat (interrupt / no-effect) is re-kicked once a round.
            if (!_forceClearForRest || _now() - _restClearLastEngageAt >= RestClearReEngageInterval)
            {
                _restClearLastEngageAt = _now();
                _post(() => _requestRestClearEngage?.Invoke());
            }
            _forceClearForRest = true;
            _restClearHeldLogged = false;
        }
        else
        {
            if (_forceClearForRest)
                _log?.Combat(LogCategory,
                    "engage-to-clear released — blocker cleared / fleeing / auto-combat back on");
            // Diagnostic (once per held stretch): a rest is due with combat off and a
            // blocker IS present, yet we're NOT engaging-to-clear — record which gate
            // suppressed it (fleeing / already fled / shadow-rest) so a capture explains
            // the "sits there not fighting back" case instead of leaving it invisible.
            else if (!autoCombatOn && shouldRest && restBlocked && !_restClearHeldLogged)
            {
                _log?.Combat(LogCategory,
                    $"engage-to-clear held — blocker present (roster={hostilesPresent} inCombat={beingAttacked}) "
                    + $"but suppressed: fleeWorthy={hpFleeWorthy} fled={_fledThisCombat} shadowRest={shadowRest}");
                _restClearHeldLogged = true;
            }
            else if (!restBlocked || autoCombatOn || !shouldRest)
                _restClearHeldLogged = false;   // condition gone — re-arm the once-log
            _forceClearForRest = false;
        }

        // Reconfirm-hold backstop: an empty, static room never emits the "Also here:"
        // line NoteRoomEntitiesReconfirmed waits on, and a stationary character never
        // triggers NoteRoomChanged — so a post-force-clear hold could sit forever,
        // silently blocking auto-rest (reports paradigm-20260818-050950 / -092532:
        // below the rest threshold, out of combat, yet never resting). Release the hold
        // after a short window: by then the watchdog's resync re-display has had ample
        // time to re-assert any real hostile — which the hostiles guard below still
        // blocks — so a room that's genuinely clear is safe to rest in.
        if (_restHeldPendingReconfirm && _now() - _restHoldSetAt > RestReconfirmTimeout)
        {
            _restHeldPendingReconfirm = false;
            // The room stayed empty for the window — arm the hostiles-guard bypass so
            // the held rest actually fires past a stale hostile latch the empty-room
            // re-display never cleared (report paradigm-20260827-082222).
            _restHostilesBypassArmed = true;
            _log?.Combat(LogCategory,
                "rest reconfirm-hold timed out — no room re-display re-asserted a hostile, releasing to rest");
        }

        if (shouldRest && !_state.InCombat && !_restInFlight && _restHeldPendingReconfirm
            && (!hostilesPresent || shadowRest))
        {
            // A watchdog force-clear dropped InCombat optimistically; wait for the
            // resync re-display to re-confirm the room is actually empty before
            // resting, so we don't rest at a monster the re-display re-asserts.
            _log?.Combat(LogCategory,
                $"rest held — combat force-cleared, awaiting room re-confirm " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }
        else if (shouldSendRestCommand && !_state.InCombat && !_restInFlight && !equipmentApplying
            && (!RestRefusedSickHolding(selfPoisoned) || MeditatesWhilePoisoned(s, selfPoisoned))
            && (!hostilesPresent || shadowRest || _restHostilesBypassArmed)
            && !RestHeldForSneak(anyGate)
            && !SneakingBeforeShadowRest(s))
        {
            // Pick rest vs meditate based on user settings + which
            // pool is the proximate trigger.
            //
            // - UseMeditateAbility is the master toggle (default off; turn it
            //   on for a class that meditates).
            // - MeditateBeforeResting flips the order when BOTH pools
            //   are gated: meditate fills MA first, then rest fills
            //   HP. Without this, rest is sent regardless.
            // - With only MA gated (HP at max), prefer meditate when
            //   UseMeditateAbility is on — rest doesn't recover MA on
            //   most classes.
            // The opportunistic path has no gate to read, so it picks on
            // live pool percentages instead (ChooseOpportunisticRestCommand).
            string command = MeditatesWhilePoisoned(s, selfPoisoned) ? "meditate"
                : anyGateConfirmed ? ChooseRestCommand(s)
                : ChooseOpportunisticRestCommand(s);

            string restReason = anyGateConfirmed ? ""
                : leaderWaitedRest ? " (waited — resting to use the downtime)"
                : " (opportunistic, leader resting)";
            // Every wear stands a resting character up, and resting again starts the
            // rest's timer over (user, 2026-09-29) — so the rest gear goes on first and
            // `rest` follows once the swap has streamed (the swap-done re-evaluate sends
            // it, the gear already worn). Meditation survives a swap, so it goes out now
            // and its gear follows the sit.
            // Already in the posture this command asks for: the player lay down by
            // hand, or the engine was switched off and back on mid-rest. Sending it
            // again restarts the game's count toward the next rest / meditate tick
            // (GAME_MECHANICS "Rest and meditate tick timing"), so take the rest
            // under way as ours instead.
            bool alreadyThere = command == "rest"
                ? _state.Position == PlayerPosition.Resting
                : _state.Position == PlayerPosition.Meditating;
            if (alreadyThere)
            {
                _restInFlight = true;
                _restResumeOwed = false;
                _restConfirmedByPrompt = true;
                _log?.Combat(LogCategory,
                    $"already {(command == "rest" ? "resting" : "meditating")}{restReason} — not sending {command} again " +
                    $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            }
            else if (command == "rest" && _wearRestGearFirst?.Invoke() == true)
            {
                _log?.Combat(LogCategory,
                    $"rest held — wearing the rest gear first so the swap can't break the rest " +
                    $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            }
            else
            {
                SendChained(s.PreRestCommand);
                SendCommand(command);
                _log?.Combat(LogCategory,
                    $"{command}{restReason} " +
                    $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
                _restInFlight = true;
                _restResumeOwed = false;
            }
        }
        else if (!shouldRest && _restInFlight)
        {
            // The post-rest chain is for a rest that ran its course. One given up
            // because the room began to bar it (the counter came off, the spell was
            // ticked) recovered nothing, and its commands would follow a rest that
            // never finished. The stamp below still stands: a sit the game confirms
            // late is this rest's tail either way.
            if (roomHurts is null)
            {
                SendChained(s.PostRestCommand);
                _log?.Combat(LogCategory,
                    $"recovered hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            }
            else
                _log?.Combat(LogCategory,
                    $"rest given up — this room's {roomHurts} does damage; no post-rest commands " +
                    $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _restInFlight = false;
            _recoveredAt = _now();
            _restConfirmedByPrompt = false;
        }

        // ShadowRest resume: recovery topped off to rest-max (the held gate just
        // cleared) while ShadowRest was holding. Fire once on the falling edge so
        // CombatManager re-runs the room and opens with a backstab — we're still
        // stealthed and the opener is unspent because combat stayed suppressed.
        // Only the gate clearing counts: a buff cast mid-rest ends the sneak for a
        // moment, which drops ShadowRestHolding while the rest is far from done, and
        // resuming there re-ran the room at low HP (report paradigm-20260930-192045).
        bool shadowRestHolding = ShadowRestHolding;
        if (_shadowRestWasHolding && !shadowRestHolding && !IsRecoveringRest)
        {
            _log?.Combat(LogCategory,
                $"shadowrest recovered to rest-max — resuming combat " +
                $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _onShadowRestRecovered?.Invoke();
        }
        _shadowRestWasHolding = shadowRestHolding || (_shadowRestWasHolding && IsRecoveringRest);

        // General recovery resume: a rest gate topped off to rest-max (falling edge of
        // IsRecoveringRest). CombatManager may have been holding engagement of a
        // passive KillOnSight neutral to let this rest happen — poke it to re-engage
        // now that we're topped off. Gated on its own hold flag, so an ordinary
        // recovery with nothing held is a no-op.
        bool recovering = IsRecoveringRest;
        if (_wasRecovering && !recovering) _onRecoveryComplete?.Invoke();
        _wasRecovering = recovering;
    }

    // Once per room: every prompt in the room asks again.
    private void LogRestSkippedForRoomSpell(string spell, string why)
    {
        if (_roomSpellSkipLogged) return;
        _roomSpellSkipLogged = true;
        _log?.Info(LogCategory,
            $"{why}, but this room's {spell} does damage — not resting here; "
            + "healing as set, and resting in the next room that doesn't hurt");
    }

    // Re-verify a just-asserted HP threshold breach one dispatch tick later,
    // before letting it commit to an actual rest/meditate send — the same
    // shape of race the flee defer above closes (see _post's field comment):
    // PromptScanner parses prompt digits ahead of the rest of a wire read, so
    // a hit that drops a pool below its trigger can be followed, within the
    // same burst, by a regen tick that brings it back up — and without this,
    // the gate would already have fired the rest command off the momentary
    // low reading before that later line was even processed (report
    // paradigm-20260912-093819: HP/MA never actually stayed below the
    // rest-trigger, but the character sat down anyway). If the pool is still
    // below its trigger once this runs, the breach was real: confirm it and
    // re-run Evaluate so the rest-out branch can act on the confirmation right
    // away instead of waiting on some unrelated future state change. That
    // re-entrant call is safe — Evaluate's own !_hpGateAsserted / !_restInFlight
    // guards make a second pass (this one, or the outer call resuming after
    // _post returns) a no-op once the first pass has acted, the same way any
    // two Evaluate calls in quick succession already coexist. Otherwise the
    // breach was a blip: retract the gate instead of resting on stale,
    // momentary information — nothing else to do; ClearGate itself is what
    // un-pauses the walker.
    private void ConfirmHpGate()
    {
        if (!_hpGateAsserted || _hpGateConfirmed) return;
        HealthSettings s = _readSettings();
        (int hpRestTrigger, int hpRestMax) = ResolveRestThresholds(
            s.HpThresholdMode, s.RestIfBelowHp, s.RestMaxHp,
            _defaultSetMaxHp, _realMaxHp, _state.MaxHp);
        if (RestHereNow().Hp) hpRestTrigger = hpRestMax;
        // A "recovered above trigger" reading while the max is unsettled (a gear swap's
        // confirmations still streaming) can't be trusted — the trigger is riding a
        // transient max (report paradigm-20260916-141742). The breach that asserted this
        // gate was real (assert only fires against a settled max), so hold it confirmed
        // rather than stand down on stale info.
        bool hpMaxUnsettled = _maxHpChangedAt != default && _now() - _maxHpChangedAt < MaxPoolSettleWindow;
        if (hpMaxUnsettled || (_state.MaxHp > 0 && _state.Hp < hpRestTrigger))
        {
            _hpGateConfirmed = true;
            Evaluate();
            return;
        }
        _hpGateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
            AsserterName,
            $"HP {_state.Hp}/{_state.MaxHp} recovered above rest-trigger={hpRestTrigger} before rest committed");
        _log?.Combat(LogCategory,
            $"HP rest-trigger breach was momentary — recovered to {_state.Hp}/{_state.MaxHp} " +
            "before the rest command went out; standing down");
    }

    // MA counterpart of ConfirmHpGate — see its comment for the full rationale.
    private void ConfirmMaGate()
    {
        if (!_maGateAsserted || _maGateConfirmed) return;
        HealthSettings s = _readSettings();
        (int maRestTrigger, int maRestMax) = ResolveRestThresholds(
            s.MaThresholdMode, s.RestIfBelowMa, s.RestMaxMa,
            _defaultSetMaxMa, _realMaxMa, _state.MaxMa);
        if (RestHereNow().Mana) maRestTrigger = maRestMax;
        // See ConfirmHpGate — a transient max can't retract a real breach (report
        // paradigm-20260916-141742).
        bool maMaxUnsettled = _maxMaChangedAt != default && _now() - _maxMaChangedAt < MaxPoolSettleWindow;
        if (maMaxUnsettled || (_state.MaxMa > 0 && _state.Ma < maRestTrigger))
        {
            _maGateConfirmed = true;
            Evaluate();
            return;
        }
        _maGateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
            AsserterName,
            $"MA {_state.Ma}/{_state.MaxMa} recovered above rest-trigger={maRestTrigger} before rest committed");
        _log?.Combat(LogCategory,
            $"MA rest-trigger breach was momentary — recovered to {_state.Ma}/{_state.MaxMa} " +
            "before the rest command went out; standing down");
    }

    // Re-check ONLY the emergency-hangup gate — wired to room-entity observations
    // so a hostile that wanders in or spawns while we're already below the trigger
    // fires the disconnect, even though nothing about our own PlayerState changed
    // to drive the normal Evaluate. Deliberately narrow: it must not run the
    // rest / run / flee machinery, which a room change would otherwise re-trigger
    // (e.g. spuriously re-issuing `rest`). TryEmergencyHangup applies the same
    // switches as on the Evaluate path.
    public void ReevaluateEmergencyHangup()
    {
        if (!_state.HasPromptData) return;
        TryEmergencyHangup(_readSettings());
    }

    // The low-HP hang-up's own switches. Master switch off: only with Allow hangup
    // in all-off mode. Master switch on: it sits behind Auto-Rest, the engine that
    // watches HP ("if auto-rest is off, it shouldnt work"; user, 2026-10-09). It
    // used to run under Auto-Heal alone too.
    private bool LowHpHangupHeld(GeneralSettings? general) =>
        _masterSwitchOff?.Invoke() == true
            ? HeldByMasterSwitch(general, what: null)
            : RestSwitchedOff() || !_isEnabled();

    // Hangup-on-emergency: HP at or below HealthSettings.HangIfBelowHp WITH a
    // hostile in the room triggers a hard disconnect via the configured Game-Exit
    // command. Latched so the command goes once per danger episode (not every tick
    // while HP stays low), and the log captures it for postmortem. The latch
    // re-arms as soon as the danger passes — HP back above the trigger, or the
    // room clear of hostiles — so a later low-HP-with-hostile crossing (e.g. after
    // reconnecting into a safe room, then a monster wanders in) fires afresh.
    // Defaults: HangIfBelowHp=5 (%). Called from the normal evaluate path, the
    // room-observation re-check (ReevaluateEmergencyHangup), and — when
    // GeneralSettings.AllowHangupInAllOffMode is set — the engine-disabled carve-out.
    //
    // The trigger is a point on one continuous HP scale — 100 %/max down through
    // 0 into the negatives (HP% goes negative while bleeding out, exactly as the
    // game's par display shows). So the trigger has no zero sentinel: 0 is a live
    // "hang the moment I drop" value, and negatives let the user hang up deep in
    // the bleeding-out band, closer to death. Turning the feature off is the
    // GeneralSettings.DisableHangups master switch's job, not a magic threshold.
    //
    // The fire window is (deathFloor, hangTrigger]: it stays live through the
    // bleeding-out zone below 0 HP because a dropped character can still hang up,
    // but bails once HP has fallen to or past the realm death floor — at that
    // point the character is already dead and there's nothing to disconnect
    // (this also guards against dead/respawned chars reading garbage HP). The
    // floor is clamped to <= 0: a misconfigured positive value collapses to 0.
    // A trigger resolved at or below the floor yields an empty window (never
    // fires) — the natural "never hang up" position at the bottom of the scale.
    //
    // Returns true only when it actually sent the disconnect this call, or another
    // escape went out a moment ago (EscapeJustWentOut), so the Evaluate caller can
    // short-circuit the rest of the recovery machinery. A couldn't-send (no exit
    // command configured) still latches _hangFired but returns false, letting
    // normal rest / flee run as a fallback.
    private bool TryEmergencyHangup(HealthSettings s)
    {
        // Disable Hangups: the user has declared only an explicit local action
        // may drop the carrier. Hard-overrides AllowHangupInAllOffMode — an
        // opted-out character won't auto-disconnect even at low HP.
        GeneralSettings? general = _readGeneralSettings?.Invoke();
        if (general is { DisableHangups: true }) return false;
        if (_state.MaxHp <= 0) return false;

        int hangTrigger = ResolveHpThreshold(s.HpThresholdMode, s.HangIfBelowHp);
        int deathFloor = Math.Min(0, _readDeathFloor?.Invoke() ?? -25);
        bool inWindow = _state.Hp > deathFloor && _state.Hp <= hangTrigger;

        // The disconnect is an escape from a fight that's killing us. With no
        // hostile in the room there's nothing to flee, so a low-HP character is
        // safe to stay connected and rest — dropping the carrier would only
        // strand it in a reconnect loop it can't heal out of (log back in still
        // below the trigger, hang up again, repeat). Gate on hostile presence and
        // re-arm the single-shot the moment the danger passes (HP recovered above
        // the trigger, or the room went clear) so a fresh hostile that wanders in
        // or spawns while we're still low fires a new disconnect. Selector unwired
        // (minimal ctor) fails open — behaves as the pre-gate hangup did.
        bool hostile = _hasHostileInRoom?.Invoke() ?? true;
        if (!inWindow || !hostile)
        {
            _hangFired = false;
            return false;
        }
        if (_hangFired) return false;
        // After the window check, so a held hang-up is only counted while there
        // is one to hold, and unlatched, so it fires the moment its switch allows.
        if (LowHpHangupHeld(general)) return false;

        // Another escape has only just gone out (a Hangup monster or a PvP enemy in
        // the same room roster): the danger is answered, so no second exit command
        // and no @panic behind it. Not latched: if we are still here and still in
        // danger once the moment has passed, this fires as normal.
        // Debug: this is asked on every prompt and roster change inside the window.
        if (EscapeJustWentOut(out TimeSpan since))
        {
            _log?.Debug(LogCategory,
                $"low-HP hang-up (HP {_state.Hp}/{_state.MaxHp} <= hang-trigger={hangTrigger}) not sent: an escape went out {since.TotalMilliseconds:0} ms ago");
            return true;
        }

        _hangFired = true;

        // @panic broadcast (MegaMUD parity): when we're leading a party and opted
        // in, warn the whole party at the instant our own emergency floor is
        // crossed — so partymates that don't ignore it bail with us. Sent BEFORE we
        // escape (we may drop the carrier or jump away next) so the warning reaches
        // the room first.
        MaybeBroadcastPanic(s);

        return Acted(ExecuteEscape(s, $"HP {_state.Hp}/{_state.MaxHp} <= hang-trigger={hangTrigger}",
            allowCarrierDrop: true, pvpResponse: false));
    }

    private static bool Acted(EscapeOutcome outcome) =>
        outcome is EscapeOutcome.HungUp or EscapeOutcome.Jumped or EscapeOutcome.AlreadyEscaping;

    // An escape that went out this recently has answered the danger. A second one
    // asked for inside the window (low HP, a Hangup monster and a PvP enemy can all
    // be read off one room roster) would only repeat the exit command, the carrier
    // drop and the penalty line. Time alone ends it, so it cannot go stale and hold
    // back a later hang-up.
    private static readonly TimeSpan EscapeRepeatWindow = TimeSpan.FromSeconds(2);
    private DateTimeOffset? _escapeSentAt;

    private bool EscapeJustWentOut(out TimeSpan since)
    {
        since = default;
        if (_escapeSentAt is not { } at) return false;
        since = _now() - at;
        // A clock set back must not stretch the window.
        return since >= TimeSpan.Zero && since < EscapeRepeatWindow;
    }

    // The low-HP escape action, shared by our own emergency hangup
    // (TryEmergencyHangup), a received @panic (RespondToReceivedPanic), the PvP
    // response (HangUpForPvp) and a Hangup-relationship monster
    // (HangUpForMonster): the sysop wimpy jump if the character opted in with a
    // location AND the jump dispatched, else drop the carrier via the Game-Exit
    // command. Returns which of the two went out, NotSent, or AlreadyEscaping when
    // either kind went out a moment ago (EscapeJustWentOut).
    //
    // The wimpy jump is tried first: rather than drop the carrier, break combat and
    // jump to the configured escape location. Only when opted in with a location AND
    // the delegate dispatched (it gates on the per-BBS sysop-goto power + the
    // location still being in the table). It fires at ANY HP, bleeding-out included:
    // `sys` commands aren't subject to the mortally-wounded restriction (confirmed
    // mechanic), so the jump works even below 0 HP — the delegate sends it on a wire
    // that pierces the mortally-wounded send-gate hold (see SysopGotoManager wiring).
    // A refused / unwired jump falls through to the hangup, so a mis-set escape never
    // leaves the character sitting in the fight.
    //
    // allowCarrierDrop gates the hangup fallback: true for our own emergency (the
    // DisableHangups master switch was already checked upstream); a received @panic
    // passes !DisableHangups, so an opted-out character still wimpy-jumps (no carrier
    // drop) but is never force-disconnected by someone else's panic.
    //
    // pvpResponse only words the hang-up penalty log line (SetHangupPenaltyLog).
    private EscapeOutcome ExecuteEscape(HealthSettings s, string reason, bool allowCarrierDrop, bool pvpResponse)
    {
        if (EscapeJustWentOut(out TimeSpan since))
        {
            _log?.Info(LogCategory,
                $"escape ({reason}) not repeated: one went out {since.TotalMilliseconds:0} ms ago");
            return EscapeOutcome.AlreadyEscaping;
        }

        if (s.SysGotoWimpyInsteadOfHanging
            && !string.IsNullOrWhiteSpace(s.SysGotoWimpyLocation)
            && _tryWimpyGoto?.Invoke(s.SysGotoWimpyLocation.Trim()) == true)
        {
            _log?.Warn(LogCategory,
                $"WIMPY GOTO instead of hangup ({reason}) → break + 'sys goto {s.SysGotoWimpyLocation.Trim()}'");
            _escapeSentAt = _now();
            return EscapeOutcome.Jumped;
        }

        if (!allowCarrierDrop)
        {
            _log?.Warn(LogCategory,
                $"escape ({reason}) — carrier-drop suppressed (DisableHangups) and no wimpy location set; staying put.");
            return EscapeOutcome.NotSent;
        }

        string? hangCmd = _readHangupCommand?.Invoke();
        if (string.IsNullOrWhiteSpace(hangCmd))
        {
            _log?.Warn(LogCategory,
                $"HANGUP ({reason}) but no hangup command configured — set the realm's Game exit command on Settings → BBS + Display.");
            return EscapeOutcome.NotSent;
        }

        _log?.Warn(LogCategory, $"HANGUP ({reason}) cmd='{hangCmd}' (sending exit, then closing carrier)");
        _escapeSentAt = _now();
        // Declare the drop intentional before it lands so MainWindowViewModel's
        // reactive-reconnect path stands down — otherwise the very disconnect we
        // just triggered gets classified as unexpected and immediately dialled back.
        _hangupSignal?.SignalHangup();
        // Route through the un-wrapped hangup sender so a low-HP hangup fires even
        // while the mortally-wounded EngineSendGate hold is up (that hold gates
        // every OTHER engine send, but the escape hangup must pierce it).
        SendHangup(hangCmd);
        // Don't wait for the server to notice the exit command — close the socket
        // ourselves so a stuck / slow drop can't leave the character connected.
        // The callback flushes the just-sent exit command before disposing.
        _requestHangupDisconnect?.Invoke();
        // Last, so nothing the penalty line reads can come between the trigger
        // and the hang-up.
        _logHangupPenalty?.Invoke(pvpResponse);
        return EscapeOutcome.HungUp;
    }

    // Broadcast a bare '.@panic' on say when we're the party leader and the user
    // opted in (PartySettings.UsePanicWhileLeading). Rides the un-wrapped hangup
    // sender so it pierces the mortally-wounded send-gate hold, exactly like the
    // hangup command — a leader bleeding out below 0 HP must still be able to warn
    // the party. No-op solo, when following, when opted out, or with no sender.
    private void MaybeBroadcastPanic(HealthSettings s)
    {
        if (_selfIsPartyLeader?.Invoke() != true) return;
        if (_readPartySettings?.Invoke() is not { UsePanicWhileLeading: true }) return;
        Action<byte[]>? sender = _hangupWireSender ?? _wireSender;
        if (sender is null) return;
        // '.' is MajorMUD's say-channel prefix; the bare "@panic" matches MegaMUD.
        sender(Encoding.Latin1.GetBytes(".@panic\r"));
        _log?.Warn(LogCategory, "@panic broadcast to party (leading, emergency floor crossed)");
    }

    // A party member broadcast @panic and we don't ignore @panics — bail the same
    // way our own low-HP emergency would (sys-goto-wimpy if opted in + configured,
    // else hang up). Unlike our own emergency this runs regardless of our HP — the
    // panic IS the trigger — but it still honours the DisableHangups master switch
    // for the carrier-drop path (an opted-out character wimpy-jumps if configured
    // but is never force-dropped by someone else's panic). Returns true when it
    // acted. Wired to Game.Conditions.PanicResponder; the receive-side IgnorePanics
    // gate is checked there before this is called. With the master switch off it
    // is held, jump included, unless Allow hangup in all-off mode is ticked.
    public bool RespondToReceivedPanic(string fromWhom)
    {
        HealthSettings s = _readSettings();
        GeneralSettings? general = _readGeneralSettings?.Invoke();
        if (HeldByMasterSwitch(general, $"@panic from {fromWhom}")) return false;
        bool allowDrop = general is not { DisableHangups: true };
        _log?.Warn(LogCategory, $"received @panic from {fromWhom} — bailing (wimpy-or-hang)");
        return Acted(ExecuteEscape(s, $"@panic from {fromWhom}", allowDrop, pvpResponse: false));
    }

    // The PvP response's hang-up: the same escape a received @panic takes, so the
    // sysop wimpy jump stands in when it is set up, and DisableHangups and the
    // master switch are honoured the same way.
    public bool HangUpForPvp(string reason)
    {
        GeneralSettings? general = _readGeneralSettings?.Invoke();
        if (HeldByMasterSwitch(general, $"PvP hang-up ({reason})")) return false;
        bool allowDrop = general is not { DisableHangups: true };
        _log?.Warn(LogCategory, $"PvP — bailing (wimpy-or-hang): {reason}");
        return Acted(ExecuteEscape(_readSettings(), $"PvP: {reason}", allowDrop, pvpResponse: true));
    }

    // A monster whose Game Data relationship is Hangup is in the room
    // (MonsterRelationshipWatcher). It takes the escape our own low-HP trigger takes, at
    // any HP: the sight is the trigger. Two switches stop it, the wimpy jump
    // included: Disable Hangups, and the master switch being off without Allow
    // hangup in all-off mode (user, 2026-10-09: "none of our auto systems should
    // respond"). With the master switch on it needs no toggle: Auto-Rest gates
    // the low-HP hang-up only.
    public EscapeOutcome HangUpForMonster(string reason)
    {
        GeneralSettings? general = _readGeneralSettings?.Invoke();
        if (general is { DisableHangups: true }) return EscapeOutcome.HangupsDisabled;
        if (HeldByMasterSwitch(general, $"monster hang-up ({reason})")) return EscapeOutcome.AllOff;
        return ExecuteEscape(_readSettings(), reason, allowCarrierDrop: true, pvpResponse: false);
    }

    // A monster whose Game Data relationship is Flee is in the room
    // (MonsterRelationshipWatcher; user, 2026-10-09: "yes" to seeing one running
    // the character away "through the existing flee settings"). It is the retreat
    // the run-if-below triggers make, at any HP: the sight is the trigger. What
    // stops that flee stops this one: the engine being off (Auto-Heal and
    // Auto-Rest both off), a character that is down, a party follower, and no walk
    // or loop to retreat along. One the user has paused counts as none, for this
    // flee alone. The master switch is the watcher's to ask, ahead
    // of this. Nothing goes out in a run's place: no hang-up (the Health tab has
    // no flee that ends in one), and no @heal, which asks for HP.
    //
    // stillHere is asked when the run had to wait for a move in flight to land.
    // That move may have carried us out of the monster's room, and "back" from
    // where it landed would lead to it.
    public FleeOutcome FleeFromMonster(string reason, Func<bool> stillHere)
    {
        ArgumentNullException.ThrowIfNull(stillHere);
        if (!_isEnabled()) return FleeOutcome.EngineOff;
        // The game refuses every move at 0 HP or below, and a flee that can never
        // land would hold the engine for good.
        if (_state.HasPromptData && _state.Hp <= 0) return FleeOutcome.Down;
        if (EscapeJustWentOut(out _)) return FleeOutcome.Escaping;
        // Before the follower rule: a flee already under way is the answer for
        // whoever started it. It also leaves a held run's own landing check alone.
        if (IsFleeInFlight) return FleeOutcome.AlreadyRunning;
        if (_isPartyFollower?.Invoke() == true) return FleeOutcome.Follower;
        if (_getActiveMovementEngine?.Invoke() is null) return FleeOutcome.NoEngine;
        // Asked of this flee only: the low-HP, hit-and-run and player flees take a
        // paused engine over as they always have.
        if (IsNavigationPausedByUser?.Invoke() == true) return FleeOutcome.Paused;
        // A player's flee that has landed and is staying away still holds its own
        // run length. This run is the Combat tab's; the stay-away itself stands.
        int? playersDistance = _fleeDistanceOverride;
        _fleeDistanceOverride = null;
        try { return TryFlee(reason, stillWanted: stillHere) ? FleeOutcome.Started : FleeOutcome.NoRoute; }
        finally { _fleeDistanceOverride = playersDistance; }
    }

    // The PvP response's flee: the retreat a low-HP run makes, but `rooms` long, and
    // once it lands the engine stays paused for stayAway before it walks back.
    // Needs a running walk or loop, like every flee; false when none could start.
    // onLanded is told once, when the last of those rooms is behind us.
    public bool FleeFromPlayer(string reason, int rooms, TimeSpan stayAway, Action? onLanded = null)
    {
        _fleeDistanceOverride = Math.Max(1, rooms);
        _fleeStayAway = stayAway;
        _fleeResumeNotBefore = null;
        _playerFleeLanded = onLanded;
        if (TryFlee(reason)) return true;
        ClearPlayerFlee();
        return false;
    }

    // Set for a flee the PvP response asked for; a flee of our own leaves them clear.
    private int? _fleeDistanceOverride;
    private TimeSpan _fleeStayAway;
    private DateTimeOffset? _fleeResumeNotBefore;
    private Action? _playerFleeLanded;

    private void ClearPlayerFlee()
    {
        _playerFleeLanded = null;
        _fleeDistanceOverride = null;
        _fleeStayAway = TimeSpan.Zero;
        _fleeResumeNotBefore = null;
    }

    // Deferred flee reaction — runs one dispatch tick after the run-trigger
    // tripped, once the round's death line has settled. Stands down when the room
    // emptied in the meantime (the flee-triggering round also killed the last
    // monster): with no live hostile there's nothing to flee, so we stay and rest.
    // Otherwise dispatches the same action the synchronous path would have — a
    // party follower's @heal / hold, or a solo/leader TryFlee. `_fledThisCombat`
    // was latched at decision time; a room-clear resets it via the InCombat→false
    // Evaluate, so a fresh hostile that wanders in re-triggers the flee.
    private void CommitFleeReaction(bool hpRun, bool follower, string reason)
    {
        if (!_state.InCombat || !(_hasHostileInRoom?.Invoke() ?? true))
        {
            _log?.Combat(LogCategory,
                $"flee stood down — no live hostile once the round settled; resting ({reason})");
            return;
        }
        if (follower)
        {
            if (hpRun && _requestPartyHeal is { } requestHeal)
            {
                _log?.Combat(LogCategory,
                    $"party follower low HP — requesting heal instead of fleeing ({reason})");
                requestHeal();
            }
            else if (hpRun)
            {
                _log?.Combat(LogCategory,
                    $"party follower low HP — holding (a follower never flees, and no party heal can be asked for) ({reason})");
            }
            else
            {
                _log?.Combat(LogCategory,
                    $"party follower low MA — holding (no solo flee; heal can't restore mana) ({reason})");
            }
            return;
        }
        TryFlee(reason, fromGates: true);
    }

    // Public entry for CombatManager's backstab-failure flee (wired via
    // Combat.SetBackstabFailureFlee). Routes through the shared TryFlee, which
    // requires an active movement engine and honors BreakBeforeFleeing /
    // RunDirection / RunDistance — so a hand-walked failure just logs and no-ops.
    public void RunFromBackstabFailure() => TryFlee("backstab failed");

    // Hit and Run tactics (CombatSettings.HitAndRunTactics): backstab everything, as
    // many times as it takes; stay only when a backstab kills the lone target.
    // CombatManager reports every landed backstab here (runNow when anything is still
    // standing), and asks RunInsteadOfFight for everything else it runs from: a
    // backstab that swung without surprise, a sneak that broke on the way in, and
    // any fight that would open with a plain attack (a walk-in, a chaser). It covers
    // Run if BS fails entirely. Each run retreats like any flee; the engine resumes
    // once it lands, re-sneaks and comes back for another backstab.
    //
    // HitAndRunMaxRuns caps the runs between backstabs (the first included): past it,
    // re-stealthing isn't going to happen, so we stand and fight. A landed backstab
    // starts the count over.
    private int _hitAndRunRuns;

    public int HitAndRunRuns => _hitAndRunRuns;

    public void BackstabLanded(bool runNow)
    {
        _hitAndRunRuns = 0;
        if (runNow) RunInsteadOfFight("backstab landed — something's still standing");
    }

    // Try to retreat instead of opening a fight without a backstab. False — fight it —
    // when the run budget is spent or no flee can start (no engine, no route).
    public bool RunInsteadOfFight(string reason)
    {
        // Already running (a flee under way, or one held for a move to land) — that
        // IS the answer; don't fight, and don't charge the budget twice (report
        // paradigm-20260927-003304: the held run's engage asked again, found the
        // budget spent, attacked — then the held run fired anyway).
        if (_deferredFleeReason is not null || (_fleeEngine is not null && _fleeQueue.Count > 0)
            || IsFleeInFlight)
        {
            _log?.Combat(LogCategory, $"hit and run — already running; not engaging ({reason})");
            return true;
        }
        int maxRuns = Math.Max(1, (_readCombatSettings?.Invoke() ?? new Models.Profile.CombatSettings()).HitAndRunMaxRuns);
        if (_hitAndRunRuns >= maxRuns)
        {
            _log?.Info(LogCategory, $"hit and run — {_hitAndRunRuns} run(s) without a backstab; standing to fight ({reason})");
            _hitAndRunRuns = 0;
            return false;
        }
        _hitAndRunRuns++;
        if (TryFlee($"hit and run {_hitAndRunRuns}/{maxRuns} — {reason}")) return true;
        _hitAndRunRuns--;
        return false;
    }

    // Try to begin a flee. No-ops (with a log line) when no movement engine is
    // active or when no flee direction can be resolved. On success it pauses the
    // engine, queues the full flee route, optionally sends `break`, and dispatches
    // the first step; the remaining steps advance one per NoteRoomChanged. Returns
    // whether a flee started (or is held for the move in flight to land).
    //
    // stillWanted replaces the landing check of a held flee. Left out, the run is
    // from a fight and is dropped when no hostile is where the move landed; a run
    // from something that is not counted a hostile (a Flee monster) says for
    // itself whether it is still there.
    private bool TryFlee(string reason, bool fromGates = false, Func<bool>? stillWanted = null)
    {
        // One retreat at a time: a second one started mid-flight plans from a room we
        // haven't confirmed leaving and sends its own move on top.
        if (_fleeEngine is not null && !_fleeLanded)
        {
            _log?.Combat(LogCategory, $"already fleeing — {reason}");
            return true;
        }

        // A party follower never flees, whatever asked for the run (user,
        // 2026-10-10): a run of its own walks it out of the party. Every flee comes
        // through here: low HP or mana, hit and run, a failed backstab, a player, a
        // Flee monster. A run already begun before we became a follower goes on.
        if (_fleeEngine is null && _isPartyFollower?.Invoke() == true)
        {
            _log?.Combat(LogCategory, $"flee skipped (a party follower never flees) — {reason}");
            return false;
        }

        Map.IRecoverableEngine? engine = _getActiveMovementEngine?.Invoke();
        if (engine is null)
        {
            _log?.Combat(LogCategory,
                $"flee skipped (no active movement engine) — {reason}");
            return false;
        }

        // A flee decided on a room display that arrived ahead of its move's confirm
        // would plan its route from the room we just LEFT — and that route's first
        // step walks us straight on past (report paradigm-20260926-230835: "back"
        // from the old room was D, sent from the new one, into a room off the loop).
        // Wait for the move to land, then plan from where we really are.
        if (_fleeEngine is null && IsMovePending?.Invoke() == true)
        {
            _deferredFleeReason = reason;
            _deferredFleeFromGates = fromGates;
            _deferredFleeStillWanted = stillWanted;
            _log?.Combat(LogCategory, $"flee waits for the move in flight to land — {reason}");
            return true;
        }

        Models.Profile.CombatSettings combat = _readCombatSettings?.Invoke()
            ?? new Models.Profile.CombatSettings();

        // A run already under way that a hostile followed: its next leg mustn't walk
        // straight back into the room the last leg fled (report paradigm-20260930-192727).
        Map.RoomKey? lastLegFrom = _fleeEngine is not null ? _fleeFromRoom : null;
        List<Map.Direction> steps = BuildFleeSteps(engine, combat, lastLegFrom);
        if (steps.Count == 0)
        {
            _log?.Warn(LogCategory,
                $"flee skipped (couldn't resolve {combat.RunDirection} route) — {reason}");
            return false;
        }

        // Pause the engine first so it doesn't queue planned steps
        // on top of our flee moves. Engine resumes via
        // ResumeAfterRecovery when HP climbs back above the
        // run-trigger (handled in Evaluate's recovery branch).
        engine.PauseForFlee($"flee — {reason}");

        if (_fleeEngine is null) ForgetRefusedMovesElsewhere();   // a new run starts with every way out open, but for this room's
        _fleeEngine = engine;
        _fleeFromGates = fromGates;
        // A run that went forward to get away from something (low HP or mana, a Flee
        // monster) goes on from where it lands (user, 2026-10-10). A hit-and-run or a
        // failed backstab's run comes back for the monster, and a player's is walked
        // back after its stay-away, whichever way they ran.
        _fleeCarriesOn = combat.RunDirection == Models.Profile.RunDirection.Forward
            && (fromGates || stillWanted is not null);
        FleeStarted?.Invoke();
        _fleeQueue.Clear();
        foreach (Map.Direction d in steps) _fleeQueue.Enqueue(d);

        // Only break a fight the server is actually swinging in — after the backstab
        // already killed its target, *Combat Off* has ended it and a `break` is just
        // noise (report paradigm-20260927-003231).
        if (combat.BreakBeforeFleeing && IsServerEngaged?.Invoke() != false)
            SendCommand("break");

        _fleeFromRoom = _lastKnownRoom;
        _fleeLanded = false;
        Map.Direction first = _fleeQueue.Dequeue();
        _log?.Combat(LogCategory,
            $"flee start engine={engine.Name} mode={combat.RunDirection} " +
            $"route=[{string.Join(",", steps)}] first={first} ({reason})");
        _lastFleeMove = first;
        engine.SendBacktrackMove(first);
        return true;
    }

    // Resolve the ordered list of directions the flee will walk. Backward mode
    // (the default) runs BFS from the current room back to the engine's fixed
    // JourneyOrigin and takes the first RunDistance directions — the reverse of
    // the trail we came in on, which always heads away from the fight. It falls
    // back to a single inverted last-move when the reverse path can't be computed
    // (no origin, unknown current room, or no reverse-path selector / graph).
    // Forward mode walks the engine's own next RunDistance planned moves — it
    // keeps heading toward the destination instead of retreating.
    private List<Map.Direction> BuildFleeSteps(
        Map.IRecoverableEngine engine, Models.Profile.CombatSettings combat, Map.RoomKey? lastLegFrom = null)
    {
        int distance = _fleeDistanceOverride ?? combat.RunDistance;
        if (distance < 1) distance = 1;

        var steps = new List<Map.Direction>();
        switch (combat.RunDirection)
        {
            case Models.Profile.RunDirection.Backward:
                if (_findReversePath is not null
                    && _lastKnownRoom is { } from
                    && engine.JourneyOrigin is { } origin
                    && !from.Equals(origin)
                    && _findReversePath(from, origin) is { Count: > 0 } path
                    && !(lastLegFrom is { } fled && ExitLeadsTo(from, path[0], fled)))
                {
                    for (int i = 0; i < path.Count && i < distance; i++)
                        steps.Add(path[i]);
                }
                else if (_lastKnownRoom is { } at && AwayFromThePlan(engine, at, lastLegFrom) is { } away)
                {
                    // No trail left (we're at the walk's start), or the trail runs back
                    // into the room we just fled: run the opposite way to the plan out of
                    // this room, else any way but back (user, 2026-09-30; report
                    // paradigm-20260930-192727).
                    steps.Add(away);
                }
                else if (_findReversePath is not null
                         && _lastKnownRoom is { } here
                         && PreviousRoom?.Invoke() is { } prev
                         && !prev.Equals(here)
                         && _findReversePath(here, prev) is { Count: > 0 } toPrev)
                {
                    // Standing on the loop's origin (a two-room loop lands here
                    // after one flee) leaves no trail "back" — retreat into the
                    // room we came from instead of not fleeing at all (report
                    // paradigm-20260926-221012).
                    for (int i = 0; i < toPrev.Count && i < distance; i++)
                        steps.Add(toPrev[i]);
                }
                else if (Reverse(_getLastSentDirection?.Invoke()) is { } back)
                {
                    // No map to plan a multi-room retreat — step back into the
                    // room we just left (known to exist) and stop there rather
                    // than blindly repeating one direction into a wall.
                    steps.Add(back);
                }
                // A run a hostile followed, with every way out leading back into the
                // room the last leg fled: there's nowhere to run, so stand and fight.
                if (lastLegFrom is { } fledRoom && _lastKnownRoom is { } now && steps.Count > 0
                    && RoomExits?.Invoke(now) is { } exits
                    && (!exits.TryGetValue(steps[0], out Map.RoomKey into) || into.Equals(fledRoom)))
                {
                    _log?.Combat(LogCategory, $"nowhere to run from {now} but back to {fledRoom} — standing to fight");
                    steps.Clear();
                }
                break;
            case Models.Profile.RunDirection.Forward:
                // "Go backwards if running" is OFF — keep pressing along the
                // engine's own planned route toward its destination. Walk the
                // next RunDistance moves it would have sent anyway rather than
                // repeating a single direction into a wall on the first turn.
                steps.AddRange(engine.PeekPlannedDirections(distance));
                break;
        }

        // The game already refused this way out of this room during this run: take
        // another, the plan's own way included, rather than send it again. With every
        // way out refused there is nowhere to run, and an empty route lets combat
        // fight back.
        // The same goes for a route that came up empty once a refused way was
        // left out of the picking.
        if (_lastKnownRoom is { } standing
            && (steps.Count > 0
                ? _refusedFleeMoves.Contains((standing, steps[0]))
                : _refusedFleeMoves.Any(r => r.Room.Equals(standing))))
        {
            steps.Clear();
            if (AnotherWayOut(engine, standing) is { } other)
            {
                _log?.Combat(LogCategory, $"flee: a way out of {standing} was refused — running {other} instead");
                steps.Add(other);
            }
            else
                _log?.Combat(LogCategory, $"flee: every way out of {standing} was refused — standing to fight");
        }

        // A flee goes out one direction at a time (the engine sends a text exit's own
        // command for its slot), so the route has to be cardinals all the way. Both
        // sources can yield a CMD-teleport hop —
        // BFS routes through them, and so does the engine's own plan — and a
        // teleport is crossed by that exit's command, which we don't have here.
        // Truncate at the first one: retreat as far as the cardinals go and stop.
        // Report Crash-20260908-181131 — fleeing out of the Negative Power Plane,
        // whose trail back is a teleport, threw off the dispatcher and killed the
        // app mid-fight at -467 HP.
        int blocked = steps.FindIndex(d => !d.IsCardinal());
        if (blocked >= 0)
        {
            _log?.Warn(LogCategory,
                $"flee route truncated at step {blocked + 1} — {steps[blocked]} can't be sent as a "
                + $"move; retreating {blocked} room(s) instead of {steps.Count}.");
            steps.RemoveRange(blocked, steps.Count - blocked);
        }

        // A flee is no route, so the filter that keeps routes out of Crystal Lake's
        // teleporting rooms does not reach it: stop short of the first one. The room
        // we stand in is no concern, only the steps into one.
        if (CutAtClosedRoom(steps) && steps.Count == 0
            && combat.RunDirection == Models.Profile.RunDirection.Backward
            && _lastKnownRoom is { } cutAt && AwayFromThePlan(engine, cutAt, lastLegFrom) is { } open)
            steps.Add(open);
        return steps;
    }

    // Drops the first step that leads into a room closed to routes, and every step after
    // it. A step whose target the graph cannot tell is left alone.
    private bool CutAtClosedRoom(List<Map.Direction> steps)
    {
        if (IsClosedToRoutes is not { } closed || _lastKnownRoom is not { } here) return false;
        for (int i = 0; i < steps.Count; i++)
        {
            if (RoomExits?.Invoke(here) is not { } exits || !exits.TryGetValue(steps[i], out Map.RoomKey target)) return false;
            if (closed(target))
            {
                _log?.Combat(LogCategory,
                    $"flee route cut at step {i + 1} — {steps[i]} leads into {target}, a room routes keep out of; retreating {i} room(s) instead of {steps.Count}");
                steps.RemoveRange(i, steps.Count - i);
                return true;
            }
            here = target;
        }
        return false;
    }

    private bool ExitLeadsTo(Map.RoomKey from, Map.Direction direction, Map.RoomKey to) =>
        RoomExits?.Invoke(from) is { } exits && exits.TryGetValue(direction, out Map.RoomKey target) && target.Equals(to);

    // A way out of this room that is neither along the plan nor back into `avoid` (the
    // room the last leg fled). Steers clear of boss rooms, then of bigger lairs, where
    // another exit allows (user, 2026-09-30), and otherwise takes the opposite of the
    // plan's way out.
    private Map.Direction? AwayFromThePlan(Map.IRecoverableEngine engine, Map.RoomKey at, Map.RoomKey? avoid)
    {
        if (RoomExits?.Invoke(at) is not { } exits) return null;
        Map.Direction? ahead = engine.PlannedDirectionFrom(at);
        Map.Direction? opposite = Reverse(ahead);
        return exits
            .Where(e => e.Key.IsCardinal() && e.Key != ahead && !(avoid is { } a && e.Value.Equals(a))
                && !_refusedFleeMoves.Contains((at, e.Key))
                && IsClosedToRoutes?.Invoke(e.Value) != true)
            .Select(e => (Dir: e.Key, Risk: RoomRisk?.Invoke(e.Value) ?? (false, 0)))
            .OrderBy(e => e.Risk.Boss)
            .ThenBy(e => e.Risk.LairMax)
            .ThenBy(e => e.Dir != opposite)
            .Select(e => (Map.Direction?)e.Dir)
            .FirstOrDefault();
    }

    // A way out of this room the game hasn't refused during this run: away from the
    // plan first (AwayFromThePlan's pick), then the plan's own way.
    private Map.Direction? AnotherWayOut(Map.IRecoverableEngine engine, Map.RoomKey at)
    {
        if (AwayFromThePlan(engine, at, avoid: null) is { } away) return away;
        return engine.PlannedDirectionFrom(at) is { } ahead && ahead.IsCardinal()
            && !_refusedFleeMoves.Contains((at, ahead))
            && RoomExits?.Invoke(at) is { } exits && exits.TryGetValue(ahead, out Map.RoomKey aheadRoom)
            && IsClosedToRoutes?.Invoke(aheadRoom) != true
                ? ahead : null;
    }

    // While a low-HP / MA run is on and a hostile is here: run again rather than turn
    // and fight (user, 2026-09-30; report paradigm-20260930-192727). True when we're
    // running (a leg underway or a new one started); false when there's nowhere to
    // run, so combat fights back instead of standing there being hit.
    public bool KeepRunning()
    {
        if (!IsGateFleeing) return false;
        if (!_fleeLanded) return true;
        HealthSettings s = _readSettings();
        bool hpLow = s.RunIfBelowHp > 0 && _state.MaxHp > 0
            && _state.Hp <= ResolveHpThreshold(s.HpThresholdMode, s.RunIfBelowHp);
        bool maLow = s.RunIfBelowMa > 0 && _state.MaxMa > 0
            && _state.Ma <= ResolveMaThreshold(s.MaThresholdMode, s.RunIfBelowMa);
        if (!hpLow && !maLow) return false;
        return TryFlee("a hostile is here and we're still below the run trigger", fromGates: true);
    }

    private static Map.Direction? Reverse(Map.Direction? d) => d switch
    {
        Map.Direction.N  => Map.Direction.S,
        Map.Direction.S  => Map.Direction.N,
        Map.Direction.E  => Map.Direction.W,
        Map.Direction.W  => Map.Direction.E,
        Map.Direction.NE => Map.Direction.SW,
        Map.Direction.SW => Map.Direction.NE,
        Map.Direction.NW => Map.Direction.SE,
        Map.Direction.SE => Map.Direction.NW,
        Map.Direction.U  => Map.Direction.D,
        Map.Direction.D  => Map.Direction.U,
        _ => null,
    };

    private string ChooseRestCommand(HealthSettings s)
    {
        // No meditate ability → always rest.
        if (!s.UseMeditateAbility || MeditateRecentlyRefused()) return "rest";

        bool needsHp = _hpGateAsserted;
        bool needsMa = _maGateAsserted;

        if (needsMa && !needsHp) return "meditate";
        if (needsHp && needsMa && s.MeditateBeforeResting) return "meditate";
        // Default: rest covers both pools for most classes; user can
        // flip MeditateBeforeResting for casters where mana recovery
        // matters more than HP catchup.
        return "rest";
    }

    // Poison refuses `rest` on both realms, and `meditate` too on Stock; Paradigm
    // still lets a poisoned character meditate (GAME_MECHANICS "Poison prevents
    // resting"). So on Paradigm, poisoned with mana to recover, meditate instead of
    // sitting out the refusal — the pre-rest mana set then swaps in as usual.
    private Func<bool>? _meditateWhilePoisoned;

    public void SetMeditateWhilePoisonedProbe(Func<bool> allowed) => _meditateWhilePoisoned = allowed;

    private bool MeditatesWhilePoisoned(HealthSettings s, bool selfPoisoned) =>
        selfPoisoned && _meditateWhilePoisoned?.Invoke() == true
        && s.UseMeditateAbility && !MeditateRecentlyRefused()
        && _state.MaxMa > 0 && _state.Ma < _state.MaxMa;

    private bool RestRefusedSickHolding(bool selfPoisoned) =>
        _restRefusedSickAt is { } at && (selfPoisoned || _now() - at < RestRefusedSickRetry);

    private bool MeditateRecentlyRefused() =>
        _meditateNotNeededAt is { } at && _now() - at < MeditateNotNeededWindow;

    // For another engine that waits on mana: it is as high as the game will take it,
    // though under the maximum the client believes in (see _manaGameFullAt).
    public bool ManaIsAtGameFull => ManaAtGameFull();

    // True while mana is at the level the game last called full (see _manaGameFullAt).
    private bool ManaAtGameFull() =>
        _manaGameFullAt is { } full && _state.MaxMa == _manaGameFullMax && _state.Ma >= full;

    // The game refused our rest / meditate because we're poisoned. Only reacts to a
    // rest we sent that hasn't taken yet.
    public void NoteRestRefusedSick()
    {
        if (!_restInFlight || _restConfirmedByPrompt) return;
        _restInFlight = false;
        _restRefusedSickAt = _now();
        _log?.Combat(LogCategory,
            $"rest refused — too sick (poisoned); holding the re-send while poisoned, else retrying in "
            + $"{RestRefusedSickRetry.TotalSeconds:0}s (hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
    }

    // The game says meditating won't help: mana is already full. Drop the unanswered
    // meditate so the next tick can rest (or finish) instead of waiting on it.
    public void NoteMeditateNotNeeded()
    {
        _meditateNotNeededAt = _now();
        _manaGameFullAt = _state.Ma;
        _manaGameFullMax = _state.MaxMa;
        if (_restInFlight && !_restConfirmedByPrompt) _restInFlight = false;
        _log?.Combat(LogCategory,
            $"meditate refused — mana already full; resting instead for {MeditateNotNeededWindow.TotalSeconds:0}s "
            + $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
    }

    // True when a follower riding the leader's rest downtime still has something
    // to top off — either pool sitting below its rest-max. Goes false once both
    // pools reach rest-max, which trips the shared recovery branch (post-rest
    // chain + latch clear). Guards each pool on Max > 0 so a class with no mana
    // pool never reports a phantom MA deficit before prompt data loads.
    private bool NeedsOpportunisticTopOff(HealthSettings s)
    {
        int hpTarget = ResolveRestThresholds(s.HpThresholdMode, s.RestMaxHp, s.RestMaxHp,
            _defaultSetMaxHp, _realMaxHp, _state.MaxHp).Max;
        int maTarget = ResolveRestThresholds(s.MaThresholdMode, s.RestMaxMa, s.RestMaxMa,
            _defaultSetMaxMa, _realMaxMa, _state.MaxMa).Max;
        bool needHp = _state.MaxHp > 0 && _state.Hp < hpTarget;
        bool needMa = _state.MaxMa > 0 && _state.Ma < maTarget && !ManaAtGameFull();
        return needHp || needMa;
    }

    // True while a @wait-held leader still has any pool short of FULL. Unlike
    // NeedsOpportunisticTopOff (rest-max ceiling), a wait is bounded downtime the
    // user wants spent recovering all the way — so the target is Max, not rest-max.
    // The wait's own release (member @ok / wait-window timer) ends the rest; this
    // only decides "is there anything left to recover". Each pool guards on Max > 0
    // so a class with no mana pool never reports a phantom MA deficit.
    private bool NeedsWaitDowntimeTopOff()
    {
        // "Full" here means the CURRENT gear's real ceiling — cap at it so a
        // stale-high live max (an expired +MaxHP buff / a rest-set swap the prompt
        // high-water never walked back down) can't leave a pool eternally "not full"
        // and hold the wait open forever (same stale-max trap as report
        // paradigm-20260902-052036).
        int hpMax = _realMaxHp?.Invoke() is int rh and > 0 ? Math.Min(_state.MaxHp, rh) : _state.MaxHp;
        int maMax = _realMaxMa?.Invoke() is int rm and > 0 ? Math.Min(_state.MaxMa, rm) : _state.MaxMa;
        return (hpMax > 0 && _state.Hp < hpMax)
            || (maMax > 0 && _state.Ma < maMax);
    }

    // Rest-vs-meditate pick for the opportunistic (leader-resting) path: with no
    // meditate ability it's always rest; otherwise meditate when "meditate before
    // resting" is set and we're short any mana, else meditate when our mana% is
    // below our hp% (recover the more-depleted pool first), else rest. Distinct
    // from ChooseRestCommand, which reads the asserted gates — here no gate is
    // held, so the choice is driven by live pool fill.
    private string ChooseOpportunisticRestCommand(HealthSettings s)
    {
        if (!s.UseMeditateAbility || MeditateRecentlyRefused()) return "rest";

        bool missingMana = _state.MaxMa > 0 && _state.Ma < _state.MaxMa;
        if (s.MeditateBeforeResting && missingMana) return "meditate";

        double hpPct = _state.MaxHp > 0 ? _state.Hp * 100.0 / _state.MaxHp : 100.0;
        double maPct = _state.MaxMa > 0 ? _state.Ma * 100.0 / _state.MaxMa : 100.0;
        return maPct < hpPct ? "meditate" : "rest";
    }

    // The idle-stall watchdog force-cleared combat optimistically (it sent a resync
    // CR and is waiting on the re-display to self-heal). Hold the rest-out branch
    // until that re-display re-confirms the room, so we don't rest in the flicker
    // before a still-present monster re-asserts.
    public void NoteCombatForceCleared()
    {
        _restHeldPendingReconfirm = true;
        _restHoldSetAt = _now();
    }

    // A room observation arrived after a force-clear — the room model is now
    // authoritative. Release the hold and re-evaluate: a hostile that re-appeared
    // is now reflected in HasEngageableHostiles (this is wired after the combat
    // tracker's own EntitiesObserved handler), so the hostiles guard blocks the
    // rest; a genuinely empty room lets the held rest through.
    public void NoteRoomEntitiesReconfirmed()
    {
        // A genuine occupant observation re-derives the hostile latch, so the
        // post-timeout bypass has done its job — retire it and let the real guard
        // be authoritative again (a hostile that re-appeared now blocks the rest).
        _restHostilesBypassArmed = false;
        if (!_restHeldPendingReconfirm) return;
        _restHeldPendingReconfirm = false;
        Evaluate();
    }

    // Reset States: drop a flee in progress, and the engine it would resume once HP
    // recovers, so a stopped loop / walk isn't restarted afterwards.
    public void CancelFlee()
    {
        if (EndFlee("reset")) _post(Evaluate);
    }

    // A flee that cannot go on is over: the character died (user, 2026-10-10: "if
    // we died, the flee needs to end"), the line dropped, another profile was
    // loaded, or the states were reset. Left standing it read as in flight until
    // the next move, with combat declining every engage meanwhile, and that move
    // was then taken for the run's next arrival and sent its next queued step.
    // Returns whether there was one to end.
    //
    // handBackLiveEngine is for a drop of the line alone. A loop is stopped by the
    // disconnect and restarted by its own reconnect resume, but a walk lives through
    // it: one this flee had paused would stay paused for good with nobody to resume
    // it. So where the paused engine is still the active one the retreat only stops
    // where it stands, as after a refused move, and the engine is handed back at the
    // first game prompt of the next stay (NoteInGamePrompt). Not before: the prompt
    // data is as it was when the line dropped, so a resume left to Evaluate would
    // run offline.
    //
    // stopItsEngine is for a flee cut off by the character going away (a profile
    // loaded or closed): the walk or loop this flee had paused belongs to that
    // character, and dropping the reference alone left it paused with nobody to
    // resume it. It is stopped through StopMovementEngine, after the flee's state
    // is cleared so the engine's stop events find no flee standing.
    public bool EndFlee(string why, bool handBackLiveEngine = false, bool stopItsEngine = false)
    {
        // Kept past a run for the room they were refused in (NoteMoveBlocked), so
        // they go here whether or not a run is on.
        _refusedFleeMoves.Clear();
        if (_fleeEngine is null && _deferredFleeReason is null && _fleeQueue.Count == 0) return false;
        Map.IRecoverableEngine? live = handBackLiveEngine && _fleeEngine is { } paused
            && ReferenceEquals(_getActiveMovementEngine?.Invoke(), paused) ? paused : null;
        Map.IRecoverableEngine? toStop = stopItsEngine ? _fleeEngine : null;
        _log?.Info(LogCategory, live is not null
            ? $"flee ended — {why}; {live.Name} is handed back at the next game prompt"
            : toStop is not null
                ? $"flee ended — {why}; {toStop.Name} is stopped"
                : $"flee ended — {why}; nothing will be resumed");
        _fleeEngine = live;
        _fleeLanded = live is not null;
        _fleeResumeAwaitsPrompt = live is not null;
        _fleeQueue.Clear();
        _fleeFromRoom = null;
        _lastFleeMove = null;
        _fleeFromGates = false;
        _fleeCarriesOn = false;
        _fledThisCombat = false;
        _deferredFleeReason = null;
        _deferredFleeFromGates = false;
        _deferredFleeStillWanted = null;
        ClearPlayerFlee();
        if (toStop is not null) StopMovementEngine?.Invoke(toStop, why);
        return true;
    }

    // Stops the walk or loop (the engine's own Stop). Set by AppServices, which owns
    // the engines; HealthManager only knows them through IRecoverableEngine.
    public Action<Map.IRecoverableEngine, string>? StopMovementEngine { get; set; }

    // Set by EndFlee when an engine is to be handed back after a reconnect.
    private bool _fleeResumeAwaitsPrompt;

    // A run from a Flee monster that waited for a move to land is asked again what
    // it was asked when the monster was seen: is one still there, and has the user
    // paused the walk or loop since (a paused one is idle for that flee).
    private bool HeldMonsterRunStillWanted(Func<bool> monsterStillHere) =>
        monsterStillHere() && IsNavigationPausedByUser?.Invoke() != true;

    // The ways out the game refused are kept for the room we stand in when a run
    // ends: the next sighting there would otherwise start the same refused run
    // again, a `break` and a move into the wall each time. They go when we leave.
    private void ForgetRefusedMovesElsewhere() =>
        _refusedFleeMoves.RemoveWhere(r => _lastKnownRoom is not { } here || !r.Room.Equals(here));

    // A game prompt: the character is in the game. The first one after a
    // disconnect hands back the walk a cut-off flee had paused.
    public void NoteInGamePrompt()
    {
        if (!_fleeResumeAwaitsPrompt) return;
        _fleeResumeAwaitsPrompt = false;
        _post(Evaluate);
    }

    // A flee move was refused — the route ran into a wall. Stop the retreat where we
    // stand rather than wait forever for a landing that can't come (report
    // paradigm-20260927-011659: stuck "already running" beside an acid slime).
    //
    // The way just tried is remembered as shut for the rest of this run. Still under
    // the run trigger with the monster still here, the next leg is planned at once,
    // and without that it planned the very same step: 23 times into the same wall,
    // one every three seconds, until the character died (report
    // paradigm-20261004-201232).
    public void NoteMoveBlocked()
    {
        if (_fleeEngine is null || _fleeLanded) return;
        if (_fleeFromRoom is { } room && _lastFleeMove is { } tried)
        {
            _refusedFleeMoves.Add((room, tried));
            _log?.Combat(LogCategory, $"flee move {tried} refused at {room} — stopping the retreat here; that way out won't be tried again while we stand in this room");
        }
        else
            _log?.Combat(LogCategory, "flee move refused — stopping the retreat here");
        _fleeQueue.Clear();
        _fleeLanded = true;
        _fledThisCombat = false;
        _post(Evaluate);
    }

    // Called by an external observer (RoomTracker via AppServices) when the
    // player's location changes. Server-side resting state is auto-cleared on
    // move, so our _restInFlight latch must drop too — otherwise the next
    // recovery cycle would skip the rest emit because we'd still think we were
    // sitting. The overload captures the new room key so the flee path can (a)
    // step its multi-move queue on every arrival and (b) call
    // IRecoverableEngine.ResumeAfterRecovery with the correct anchor once HP
    // recovers.
    public void NoteRoomChanged(Map.RoomKey? newRoom)
    {
        if (newRoom is { } r)
        {
            // Outside a run, the refused ways out belong to the room just left.
            if (_fleeEngine is null && !r.Equals(_lastKnownRoom)) _refusedFleeMoves.Clear();
            _lastKnownRoom = r;
        }

        // The user's Stop (or any end of the walk or loop) does not tell this class.
        // A retreat whose engine is no longer the running one has nobody to send its
        // steps through: a stopped loop swallows them, and a stopped walker sends them
        // anyway. A paused engine still counts as running, so the engine this flee
        // itself holds is not caught by this.
        if (_fleeEngine is { } fleeing && !_fleeLanded
            && !ReferenceEquals(_getActiveMovementEngine?.Invoke(), fleeing))
        {
            EndFlee("the walk or loop was stopped");
            return;
        }

        // A flee step only lands on a confirmed arrival somewhere new. While its move
        // is still in flight a re-display of the room we're leaving can swing the
        // tracker back to it — that isn't the retreat landing (report
        // paradigm-20260927-010239: a "flee complete" in the fight room re-armed the
        // engage and we swung at the monster we were running from).
        bool fleeArrival = _fleeEngine is null
            || (IsMovePending?.Invoke() != true
                && !(newRoom is { } nr && _fleeFromRoom is { } from && nr.Equals(from)));

        // A flee held for the move in flight: that move just landed, so start it now,
        // routed from this room. This arrival is the flee's starting point, not one of
        // its steps (and not the hit-and-run engine "moving on").
        bool startedHeldFlee = false;
        if (_deferredFleeReason is { } heldReason && _fleeEngine is null)
        {
            _deferredFleeReason = null;
            Func<bool>? stillWanted = _deferredFleeStillWanted;
            _deferredFleeStillWanted = null;
            // The move in flight may have carried us OUT of the fight (the loop's step
            // leaving the room as a monster walked in) — then there's nothing here to
            // run from, and "back" would lead straight to it (report
            // paradigm-20260927-011659: a held run sent us back down into the kobold).
            // A run from a monster that is not counted a hostile asks after it instead.
            if (stillWanted is not null ? !HeldMonsterRunStillWanted(stillWanted) : _hasHostileInRoom?.Invoke() == false)
            {
                _log?.Combat(LogCategory, stillWanted is not null
                    ? $"held flee dropped — what it ran from is not where the move landed, or navigation was paused meanwhile ({heldReason})"
                    : $"held flee dropped — no hostile where the move landed ({heldReason})");
                ClearPlayerFlee();
            }
            else
            {
                TryFlee(heldReason, _deferredFleeFromGates, stillWanted);
                startedHeldFlee = _fleeEngine is not null;
            }
        }

        // Flee step continuation — fire BEFORE the rest-latch reset
        // so the engine's pause flag doesn't get cleared by a
        // racing post-flee rest cycle.
        if (startedHeldFlee || !fleeArrival)
        {
            // Nothing to step or land yet — the held flee's first move just went out,
            // or this "change" isn't the flee's move arriving.
        }
        else if (_fleeEngine is not null && _fleeQueue.Count > 0)
        {
            _fleeFromRoom = _lastKnownRoom;
            Map.Direction next = _fleeQueue.Dequeue();
            _lastFleeMove = next;
            _fleeEngine.SendBacktrackMove(next);
            _log?.Combat(LogCategory,
                $"flee step engine={_fleeEngine.Name} dir={next} " +
                $"remaining={_fleeQueue.Count}");
        }
        else if (_fleeEngine is not null)
        {
            // The flee's last step landed. If a hostile is waiting here (or walks in)
            // and we're still under the run-trigger, that's a new fight to run from —
            // re-arm the one-shot latch and re-check once the room has been read, or
            // the engine fights it at run-trigger HP (report paradigm-20260926-221012).
            // The same re-check resumes the engine when nothing's wrong — a hit-and-run
            // retreat after a landed backstab has no HP change to wake it otherwise.
            _fledThisCombat = false;
            _fleeLanded = true;
            Action? landed = _playerFleeLanded;
            _playerFleeLanded = null;
            landed?.Invoke();
            if (_fleeStayAway > TimeSpan.Zero && _fleeResumeNotBefore is null)
            {
                _fleeResumeNotBefore = _now() + _fleeStayAway;
                _log?.Info(LogCategory,
                    $"flee from a player landed — staying away {_fleeStayAway.TotalSeconds:0}s before walking back");
                _schedule?.Invoke(_fleeStayAway + TimeSpan.FromMilliseconds(200), Evaluate);
            }
            _post(Evaluate);
        }

        if (_restInFlight)
        {
            _restInFlight = false;
            _restConfirmedByPrompt = false;
            _log?.Combat(LogCategory, "rest-in-flight cleared on room change");
        }
        _restResumeOwed = false;
        _roomSpellSkipLogged = false;
        _roomSpellSkipRoom = newRoom;

        // Arrived, still holding a rest gate from the room just left, in a room whose
        // spell bars resting: the leader's wait window ran out and it walked on into
        // the heat with us in tow. Settle it now, before the re-ask below: the gate
        // comes down, the wait that is out is released, and the rest is owed for the
        // next room that doesn't bar it. Left to the next prompt, the re-ask went out
        // first and parked the party in the damage with nobody resting. Not mid-flee,
        // like the re-arm at the end: the flee drives its own arrivals.
        if ((_hpGateAsserted || _maGateAsserted) && _fleeEngine is null && _roomSpellHurting?.Invoke() is not null)
            Evaluate();

        // Moved while still below a rest floor as a follower: our own movement is held
        // by the recovery gate, so this is the leader walking on — it isn't (or no
        // longer is) waiting for us. Re-ask, rate-limited so a multi-room drag sends
        // one @wait, not one per room.
        if (_partyWaitSignaled && (_hpGateAsserted || _maGateAsserted)
            && _isPartyFollower?.Invoke() == true
            && _now() - _lastWaitResentAt >= WaitResendInterval)
        {
            _lastWaitResentAt = _now();
            _log?.Info(LogCategory, "moved while still recovering — the leader isn't waiting; re-sending @wait");
            _requestPartyWait?.Invoke();
        }

        // A move re-observes the room, so any post-force-clear rest hold is resolved
        // by the new room's observation — drop it here too (covers the dark-room
        // force-clear that sends no resync CR). The stale-hostiles bypass retires
        // with it: the new room re-derives presence from scratch.
        _restHeldPendingReconfirm = false;
        _restHostilesBypassArmed = false;

        // Re-arm a rest that a do-not-rest room forced us to skip: the hop out of
        // that room carries no prompt change to re-run Evaluate on its own, so do
        // it once here. Skipped mid-flee — the flee queue above drives its own
        // arrivals and a rest re-arm would fight it. Evaluate re-sets the latch if
        // the new room is ALSO a do-not-rest room with a deficit.
        if (_skipRestDeferredRecovery && _fleeEngine is null)
        {
            _skipRestDeferredRecovery = false;
            Evaluate();
        }
    }

    // The room we stand in stopped being a placed one, or became one: the tracker
    // lost its place leaving a room, or found it again. NoteRoomChanged is told only
    // of a move between two placed rooms, so without this a rest put off for a room's
    // spell stayed "owed here" after the room was left for one the map doesn't hold:
    // the once-per-room log flag stood, and CastingDirector went on casting the
    // rest-time heal standing, until some later HP change re-ran the check. The
    // probe answers for the room as it is now (an unplaced room bars nothing), so
    // asking again is all it takes. Not a move as far as the rest latch goes: the
    // tracker can lose its place without the character leaving the room.
    //
    // placedIn is the room found, null when the place was lost. The once-per-room
    // log line is said again only for a room other than the one it was last said
    // in: a tracker flapping between placed and lost in a grid of barred rooms
    // comes back to the same room each time, and said the line once per flap.
    public void NoteRoomPlacementChanged(Map.RoomKey? placedIn = null)
    {
        if (placedIn is { } room && !room.Equals(_roomSpellSkipRoom))
        {
            _roomSpellSkipLogged = false;
            _roomSpellSkipRoom = room;
        }
        if (_fleeEngine is not null) return;
        // Found again in a room that bars resting while a rest gate is up counts too.
        bool barredNow = (_hpGateAsserted || _maGateAsserted) && _roomSpellHurting?.Invoke() is not null;
        if (!barredNow && !_skipRestDeferredRecovery && RestDeferredByRoomSpell is null && !_restOwedFromDamagingRoom)
            return;
        _skipRestDeferredRecovery = false;
        Evaluate();
    }

    // Send the pre-/post-rest chain: one wire line per command, split by the same
    // rule macros, triggers and aliases use. A blank field skips the phase.
    private void SendChained(string raw)
    {
        foreach (string part in MacroStore.SplitCommandSteps(raw))
            SendCommand(part);
    }

    // A hand-typed rest stood up by its own pre-rest gear (AutoEquipCoordinator):
    // sit again. The health engine never owned that rest, so its in-flight state
    // is left alone.
    public void ResendRestAfterGearSwap()
    {
        SendCommand("rest");
        _log?.Info(LogCategory, "rest re-sent after the pre-rest gear stood a hand-typed rest up");
    }

    private void SendCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        _wireSender(bytes);
    }

    // Emergency-hangup send. Prefers the un-wrapped hangup sender (which bypasses
    // EngineSendGate) so it fires even while a hold is up; falls back to the
    // ordinary wrapped sender when no hangup sender was bound (pre-wire).
    private void SendHangup(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Action<byte[]>? sender = _hangupWireSender ?? _wireSender;
        if (sender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        sender(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
    }
}
