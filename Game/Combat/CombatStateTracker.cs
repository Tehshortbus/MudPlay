using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Owns PlayerState.InCombat and the Combat gate on MovementCoordinator.
// Subscribes to RoomEntityClassifier.EntitiesObserved for the gate's room-clear
// logic and to combat-line patterns for the PlayerState.InCombat flag.
//
// Gate semantics: the MovementCoordinator.CombatGate is held while the current
// room contains at least one classified monster the user is configured to
// engage. "Engageable" is resolved from the monster's overlay relationship (see
// IsEngageable) — shopkeepers and quest-givers are marked Friend / Neutral and
// don't hold the gate.
//
// Plus a master switch: isAutoAttackEnabled (Auto-Combat,
// GeneralSettings.AutoMode.AutoCombat) short-circuits the gate to never-assert
// when off, so the walker passes through every room unimpeded.
//
// PlayerState.InCombat flips true on CombatStatus Engaged OR UserHits OR MobHits
// OR MobMisses. It does NOT flip false on CombatStatus Off — see
// OnCombatStatus. The authoritative end-of-combat signal is the room going clear
// of engageable monsters (OnEntitiesObserved's "room cleared" branch).
public sealed class CombatStateTracker : IDisposable
{
    // Identifier this tracker uses when asserting / clearing the Combat gate.
    // Surfaces in MovementCoordinator.History + [Gate] log lines.
    public const string AsserterName = "CombatStateTracker";

    // LogService category for tracker-emitted rows.
    public const string LogCategory = "CombatGate";

    // Idle-stall watchdog. The Combat gate clears authoritatively only when a
    // room re-display shows the room empty of engageable monsters
    // (OnEntitiesObserved's "room cleared" branch). Normally the death of the
    // last monster forces that re-display (CombatManager.NoteUnattributedDeath /
    // OnCommandNoEffect send a bare CR). But when a final kill routes through
    // neither path, the empty room emits no "Also here:" line so the classifier
    // fires no observation at all — the gate stays asserted and the walker hangs
    // "fighting" an empty room until a manual redisplay.
    //
    // The watchdog runs off the 1s heartbeat (TickEngine.HeartbeatElapsed) and is
    // single-phase: once the gate has been held for IdleStallThreshold with zero
    // combat activity, it BOTH sends a benign resync CR AND force-clears the gate
    // in the same tick. The clear is optimistic — 6s of total silence while the
    // gate is held means an empty room (a live fight emits a line every 5s
    // round), so releasing the walker is correct. The CR is a safety probe: if a
    // real monster lingered (a laggy >6s round), its re-displayed "Also here:"
    // re-asserts the gate a beat later, so an over-eager clear self-heals. Driving
    // this off the 1s heartbeat rather than the coarse 5s combat tick is what
    // keeps total recovery at ~6s instead of quantizing it up to ~10-15s.
    private static readonly TimeSpan IdleStallThreshold = TimeSpan.FromSeconds(6);

    private readonly MovementCoordinator _coordinator;
    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay>? _resolveOverlay;
    private readonly PlayerState _state;
    private readonly Func<bool> _isAutoAttackEnabled;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    // The set's damage-free monster hits, on us or on another: the fight is live.
    private readonly IDisposable _mobHitsNoDamageSub;
    private readonly IDisposable _mobHitsOtherNoDamageSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _mobAttacksSub;
    private readonly IDisposable _userDodgesSub;
    private readonly IDisposable _combatStatusSub;

    private bool _gateAsserted;
    private bool _anyNpcPresent;
    private bool _hostilePresent;
    private bool _disposed;

    // Idle-stall watchdog stamp: the last sign of a live fight (a combat
    // damage/miss line or a room observation still holding a monster). The
    // watchdog fires once this goes IdleStallThreshold stale with the gate held.
    private DateTimeOffset _lastCombatActivityAt = DateTimeOffset.MinValue;

    // Watchdog diagnostics: when the gate was asserted, and what last refreshed
    // the activity stamp. Logged when the watchdog fires so a capture can tell a
    // post-kill idle release (last activity = a combat line) from a pattern gap
    // (last activity = only the room-entry observation — our attack lines never
    // matched, so they never refreshed the stamp).
    private DateTimeOffset _gateAssertedAt;
    private string _lastCombatActivityDesc = "none";

    private Func<bool>? _clearWhenSeenHidden;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<int, bool>? _hasSeeHidden;
    private Func<int, bool>? _canEngage;
    // RawName → did the user hand-engage this passive neutral? Wired from CombatManager
    // so the walker gate (HasEngageableHostiles) also holds while the user's manually-
    // engaged neutral is still alive, matching CombatManager's attack takeover.
    private Func<string, bool>? _isUserEngagedInstance;
    private bool _seeHiddenClearLatch;
    private Func<bool>? _clearWhenSneakFails;
    // Our sneak failed entering the room we're in (StealthManager.SneakEntry false);
    // a move sent drops it, since the failure belonged to the room it left.
    private bool _sneakFailedHere;
    private bool _sneakFailClearLatch;

    // A see-hidden monster broke this stealth runner's sneak, and it hasn't been
    // sneaking again since. The break belongs to the character, not to the room it
    // happened in: carried into the next room, by a move already on the wire or past
    // a room outside the Min/Max window, the character is still exposed for the same
    // reason, so the first room that meets the window is cleared (user, 2026-10-07).
    // It lasts until a sneak takes again (NoteSneakRegained) or the option is
    // switched off.
    private bool _sneakBrokenBySeeHidden;
    public bool SneakBrokenBySeeHidden => _sneakBrokenBySeeHidden;
    // Says once per room that the break is on but the room is outside the window.
    private bool _outsideWindowLogged;

    // A fresh `sn` was answered cleanly: the character is sneaking again.
    public void NoteSneakRegained()
    {
        if (!_sneakBrokenBySeeHidden) return;
        _sneakBrokenBySeeHidden = false;
        _log?.Info(LogCategory, "sneaking again — rooms are no longer cleared for the see-hidden break");
    }

    private Action<byte[]>? _wireSender;
    private Func<bool>? _breakBeforeRunning;
    private Func<bool>? _attackInFlight;

    // CombatSettings.MinMonstersInRoom / MaxMonstersInRoom reader — see
    // SetMonsterCountWindow. Null (unwired) fails open: the gate asserts for any
    // actionable hostile regardless of room population, matching this tracker's
    // behavior before the window existed.
    private Func<CombatSettings>? _readSettings;

    // Reports whether a movement engine (walker / loop / auto-lair) is
    // currently attached and driving us through rooms — see
    // SetMovementActiveGate. The Min/Max window below only makes sense while
    // something is actually trying to move us past this room; standing here
    // with nowhere to go (no engine attached) should hold the gate and fight
    // regardless of population. Null (unwired) fails open to "active" — the
    // window applies unconditionally, matching this tracker's behavior before
    // this gate existed.
    private Func<bool>? _isMovementActive;

    // Reports whether we've committed to fighting the current room (its count met
    // the engage window) — CombatManager.HasCommittedToCurrentRoom. Drives the
    // "Kill all engaged" below-floor override in IsWithinMonsterCountWindow. Null
    // (unwired) → false (no override; the Min floor behaves exactly as before).
    private Func<bool>? _roomCommitted;
    private Func<int?>? _partyMonsterCap;
    private Func<bool>? _waitingAtLairDoor;

    // Reports whether we're standing in a too-dark room (RoomTracker.IsInDarkRoom).
    // Gates the idle-stall watchdog's resync CR: a CR in the dark re-emits no
    // "Also here:" line (nothing to re-observe) AND its "you can't see anything"
    // reply is dead-reckoned by RoomTracker as a false confirmation of the
    // movement loop's in-flight step, so we skip the probe and just clear the stuck
    // gate. null until wired → fail-open (the resync CR sends as before).
    private Func<bool>? _isInDarkRoom;

    // True while the room currently contains at least one engageable
    // (Enemy-relationship, killable) monster. Drives the
    // MovementCoordinator.CombatGate + lets HealthManager gate the rest decision
    // so we don't try to rest while a mob is here — every combat round would
    // otherwise break rest and we'd never actually recover. Clears
    // authoritatively when an Also-Here observation shows no engageable monsters
    // (room cleared).
    public bool HasEngageableHostiles => _gateAsserted;

    // True while the room holds at least one engageable (Enemy-relationship)
    // monster — the same per-entity predicate that drives the gate, but WITHOUT
    // the auto-attack master-switch short-circuit. HasEngageableHostiles reports
    // false whenever auto-attack is off (a manual player never asserts the gate);
    // this reports the raw danger regardless, so the emergency-hangup gate can ask
    // "is a hostile in the room?" for a character who isn't auto-fighting.
    public bool HasHostileMonster => _hostilePresent;

    // True while the current room contains at least one NPC / monster of any
    // relationship (Enemy, Friend, Neutral — shopkeepers and quest-givers
    // included). Sneak cannot be established while any NPC is present, so the
    // StealthManager pre-move hook consults this to suppress a doomed sn. Updated
    // on every Also-Here observation, independent of the auto-attack gate (which
    // only reacts to engageable hostiles).
    public bool HasRoomNpc => _anyNpcPresent;

    // True while a combat-off "clear hostiles when seen Hidden" force-clear is
    // latched for the current room. A stealth runner (AutoSneak on) sprinting a
    // route with combat OFF that hits a room holding a SeeHidden monster can't
    // re-sneak there; running onward would drag and stack monsters across rooms,
    // lethal when solo. When CombatSettings.ClearHostilesWhenSeenHidden is on,
    // this latches in the first room inside the Min/Max monster window from then
    // on (that room, or a later one) — holding the Combat gate (so the walker
    // actually stops) until every engageable hostile is gone.
    // CombatManager reads this to engage despite combat-off.
    public bool SeeHiddenClearActive => _seeHiddenClearLatch;

    // Whether the see-hidden clear is on for the company the character is keeping:
    // the option itself, and its "while solo" or "while in a party" tick.
    public static bool SeeHiddenClearApplies(bool enabled, bool whileSolo, bool whileInParty, bool inParty)
        => enabled && (inParty ? whileInParty : whileSolo);

    // True while a combat-off "clear hostiles when sneak fails" clear is latched for
    // the current room: a stealth runner's sneaked move failed into a room inside
    // the Min/Max monster window, so the walker holds and CombatManager engages
    // until every engageable hostile is gone, then the route re-sneaks.
    public bool SneakFailClearActive => _sneakFailClearLatch;

    // Fires when a confirmed room change happens while the combat gate is held —
    // an in-flight move carried us out of a room where we'd engaged an
    // actionable hostile before it died. The walker subscribes and halts so the
    // route doesn't keep going deeper past a fight we committed to. The argument
    // is a human-readable reason for the log / walk event. (The gate itself is
    // still cleared for the new room — see OnEntitiesObserved's RoomChange arm.)
    public event Action<string>? EngagedTargetAbandoned;

    // Fires when a room clears of engageable hostiles and combat truly ends, so a
    // stealth consumer can drop the sneak/hide the fight spent. Attacking reveals
    // you (the surprise opener is one-shot — see GAME_MECHANICS), but the stealth
    // FSM has no line-driven signal for it, so IsSneaking stays stale-true after a
    // kill. StealthManager subscribes and resets to Idle; raised BEFORE the Combat
    // gate releases so the walker's pre-move re-sneak sees a clean state and can
    // re-establish stealth for the step out (report stock-20260730-163044).
    // The argument is true when a death emptied the room, false when it only reads
    // clear (a departure, the display of a room we just walked into).
    public event Action<bool>? CombatSpentStealth;

    // Fires when combat is FORCE-cleared (the idle-stall watchdog, or manual Reset
    // States) rather than via a normal room-clear. The normal end-of-fight flushes
    // any pickup deferred "until combat clears" off the ensuing clean room re-look;
    // a force-clear produces no such observation, so a deferred ground-cash / item
    // collect would otherwise strand the Acquisition gate and wedge the walker
    // (report paradigm-20260814-131551). Consumers re-run their post-combat flush
    // against the now-cleared gate (HasEngageableHostiles is already false here).
    public event Action? CombatForceCleared;

    // Fires whenever a held Combat gate is let go (the room cleared, nothing left
    // in it worth fighting, auto-attack switched off, a stealth runner's clear
    // done, a force-clear), just before the release lets the walker step. A hold
    // put up here is in place before that step, whatever ended the fight: a kill
    // of ours, a party member's last blow, a flee.
    public event Action? FightEnded;

    public CombatStateTracker(
        MessageRouter router,
        MovementCoordinator coordinator,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        PlayerState state,
        Func<bool> isAutoAttackEnabled,
        LogService? log = null)
        : this(router, coordinator, classifier, monsters, state,
               isAutoAttackEnabled, resolveOverlay: null, log) { }

    // Construct with a per-monster overlay resolver so the engageable predicate
    // matches CombatManager (Relationship-based). Without it, the tracker falls
    // back to "every monster engageable" which can spuriously assert the Combat
    // gate against shopkeepers (CombatManager would skip them but the walker
    // would still pause). AppServices wires the same delegate it gives
    // CombatManager so the two stay in sync.
    public CombatStateTracker(
        MessageRouter router,
        MovementCoordinator coordinator,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        PlayerState state,
        Func<bool> isAutoAttackEnabled,
        Func<int, MonsterOverlay>? resolveOverlay,
        LogService? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isAutoAttackEnabled);

        _coordinator = coordinator;
        _classifier  = classifier;
        _monsters    = monsters;
        _resolveOverlay = resolveOverlay;
        _state       = state;
        _isAutoAttackEnabled = isAutoAttackEnabled;
        _log         = log;
        _now         = clock ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _userHitsSub      = router.Subscribe(KnownPatterns.UserHits,      OnAnyCombatLine);
        _mobHitsSub       = router.Subscribe(KnownPatterns.MobHits,       OnAnyCombatLine);
        _mobHitsNoDamageSub      = router.Subscribe(KnownPatterns.MobHitsNoDamage,      OnAnyCombatLine);
        _mobHitsOtherNoDamageSub = router.Subscribe(KnownPatterns.MobHitsOtherNoDamage, OnAnyCombatLine);
        _mobMissesSub     = router.Subscribe(KnownPatterns.MobMisses,     OnAnyCombatLine);
        // Broad mob-attacks-us line — catches the per-round swings that MobHits /
        // MobMisses miss on realms whose melee carries no damage number or "at
        // you", so an active fight keeps refreshing the idle-stall stamp instead
        // of tripping the watchdog (report stock-20260730-190736).
        _mobAttacksSub    = router.Subscribe(KnownPatterns.MobAttacksYou, OnAnyCombatLine);
        // A realm whose full-dodge wording drops both the article and the attack
        // verb (Paradigm: "whale shark  at you, but you dodge out of the way!")
        // fails MobMisses/MobAttacksYou outright — UserDodges is the only pattern
        // narrow-but-flexible enough to still catch it (report
        // paradigm-20260914-055853: that gap, stacked with a missed gwra hit line
        // in the same round, read as total silence and force-cleared a live
        // combat gate).
        _userDodgesSub    = router.Subscribe(KnownPatterns.UserDodges,    OnAnyCombatLine);
        _combatStatusSub  = router.Subscribe(KnownPatterns.CombatStatus,  OnCombatStatus);
    }

    // Wire the combat-off "clear hostiles when seen Hidden" override:
    // clearWhenSeenHidden reads CombatSettings.ClearHostilesWhenSeenHidden,
    // isAutoSneakEnabled reports whether the character is stealthing its route
    // (AutoSneak auto-mode on), and hasSeeHidden reports whether a monster Number
    // carries SeeHidden (SeeHiddenIndex). With all wired, entering a room that
    // breaks a stealth runner's sneak latches a force-clear (see
    // SeeHiddenClearActive). Until set, the override stays dormant and the gate
    // behaves exactly as before.
    public void SetSeeHiddenClearGate(
        Func<bool> clearWhenSeenHidden,
        Func<bool> isAutoSneakEnabled,
        Func<int, bool> hasSeeHidden)
    {
        ArgumentNullException.ThrowIfNull(clearWhenSeenHidden);
        ArgumentNullException.ThrowIfNull(isAutoSneakEnabled);
        ArgumentNullException.ThrowIfNull(hasSeeHidden);
        _clearWhenSeenHidden = clearWhenSeenHidden;
        _isAutoSneakEnabled = isAutoSneakEnabled;
        _hasSeeHidden = hasSeeHidden;
    }

    // Wire the combat-off "clear hostiles when sneak fails" override: it reads
    // CombatSettings.ClearHostilesWhenSneakFails, and shares the Auto-Sneak reader
    // SetSeeHiddenClearGate wires. Until set, the override stays dormant.
    public void SetSneakFailClearGate(Func<bool> clearWhenSneakFails)
    {
        ArgumentNullException.ThrowIfNull(clearWhenSneakFails);
        _clearWhenSneakFails = clearWhenSneakFails;
    }

    // A sneaked move arrived: held (`Sneaking...`) or not (the loud entry line, or a
    // silent loss). The loud line lands before the room display, whose observation
    // then reads the flag. A silent loss is only known after the display, so the
    // room is re-run for it.
    public void NoteSneakEntry(bool held) => _sneakFailedHere = !held;

    public void NoteSilentSneakLoss()
    {
        _sneakFailedHere = true;
        if (_clearWhenSneakFails?.Invoke() == true) _classifier.ReemitCurrent();
    }

    // A move went out: whatever happened to the sneak happened in the room we left.
    public void NoteMoveSent() => _sneakFailedHere = false;

    // Wire the actionability gate: canEngage reports whether a monster Number is
    // one we can actually kill (a weapon can hit it OR an eligible attack spell
    // can land — see CombatManager.CanEngageMonster). With it wired, the walker
    // gate is held only while at least one engageable hostile is actionable; a
    // room whose remaining hostiles are all un-actionable releases the gate so
    // the walker moves past instead of standing there unable to win. Until set,
    // every engageable hostile counts as actionable (fail-open — the gate behaves
    // exactly as before).
    public void SetActionabilityGate(Func<int, bool> canEngage)
    {
        ArgumentNullException.ThrowIfNull(canEngage);
        _canEngage = canEngage;
    }

    // Wire CombatManager's per-instance "user hand-engaged this passive neutral" set so
    // the walker gate treats that neutral as an engageable hostile too. Without it the
    // gate would release (the neutral is un-tagged, so not engageable on its own) and the
    // walker could stroll off mid-fight while CombatManager is still killing it. Until
    // set, no instance is user-engaged (fail-open — behaves exactly as before).
    public void SetUserEngagedInstanceGate(Func<string, bool> isUserEngagedInstance)
    {
        ArgumentNullException.ThrowIfNull(isUserEngagedInstance);
        _isUserEngagedInstance = isUserEngagedInstance;
    }

    // Wire the CombatSettings.MinMonstersInRoom / MaxMonstersInRoom reader so
    // this tracker's gate agrees with CombatManager's own min/max skip
    // (OnEntitiesObserved's "min/max gate skip" branch) on whether a room is
    // fightable. Without this the two disagreed: CombatManager silently
    // declines to engage a room outside the window, but this tracker — which
    // only reasoned about actionability, not room population — still held the
    // walker gate for ANY actionable hostile regardless of count. That
    // deadlocked the character in a too-crowded room: combat refusing to
    // fight, the walker unable to leave, standing there absorbing hits from
    // every monster in the room with no recourse. A room outside the window
    // now takes the SAME "un-actionable, move past" path as a room full of
    // monsters no weapon or spell can hit. Until set, every engageable
    // hostile counts regardless of room population (fail-open — the gate
    // behaves exactly as before this fix). See also SetMovementActiveGate —
    // the window only applies while something is actually moving us through.
    public void SetMonsterCountWindow(Func<CombatSettings> readSettings)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        _readSettings = readSettings;
    }

    // Wire the movement-active probe (EngineRecoveryGate.AttachedEngine is not
    // null) — see _isMovementActive. Until set, the Min/Max window (once
    // SetMonsterCountWindow is also wired) applies unconditionally.
    public void SetMovementActiveGate(Func<bool> isMovementActive)
    {
        ArgumentNullException.ThrowIfNull(isMovementActive);
        _isMovementActive = isMovementActive;
    }

    // Wire the "we've committed to this room" probe (CombatManager
    // .HasCommittedToCurrentRoom) so the "Kill all engaged" override can hold the
    // walker below the Min floor to finish an engaged room's survivors.
    public void SetRoomCommittedGate(Func<bool> roomCommitted)
    {
        ArgumentNullException.ThrowIfNull(roomCommitted);
        _roomCommitted = roomCommitted;
    }

    // The Party tab's cap on the room's monsters while in a party
    // (MonsterCountGate.PartyCap), as CombatManager reads it. Unwired: none.
    public void SetPartyMonsterCap(Func<int?> partyCap)
    {
        ArgumentNullException.ThrowIfNull(partyCap);
        _partyMonsterCap = partyCap;
    }

    // A loop stands at a lair's door waiting to go in
    // (LairEntryDebuffHold.WaitingAtTheDoor): no Min floor there, as in
    // CombatManager. Unwired: the floor always holds.
    public void SetLairDoorWaitProbe(Func<bool> waitingAtLairDoor)
    {
        ArgumentNullException.ThrowIfNull(waitingAtLairDoor);
        _waitingAtLairDoor = waitingAtLairDoor;
    }

    // Wire path for the break-before-run disengage. Bound at connect time (the
    // same gate-wrapped engineSend every other engine receives). Until set, the
    // break-before-run step no-ops.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the dark-room probe (RoomTracker.IsInDarkRoom). With it set, the
    // idle-stall watchdog skips its resync CR while we can't see (see
    // _isInDarkRoom / OnCombatTick). Until set, the resync CR sends unconditionally.
    public void SetDarkRoomProbe(Func<bool> isInDarkRoom)
    {
        ArgumentNullException.ThrowIfNull(isInDarkRoom);
        _isInDarkRoom = isInDarkRoom;
    }

    // The room the character stands in (RoomTracker's confirmed room), and the one
    // it stood in when a hostile last held the gate or a combat line last arrived
    // with the gate held. A RoomChange wipe is not always a move: CombatManager
    // empties the roster in place when a room spell's kills cover every hostile
    // listed, and that reached the walker as a fight walked out on, a 1 to 2 s hold
    // after every room wiped clean (report paradigm-20261010-145330). Only a wipe
    // that finds us in another room is one. Unwired, or either room unknown, a wipe
    // reads as a move, as it always did.
    private Func<RoomKey?>? _currentRoom;
    private RoomKey? _gateRoom;

    // For the bug report: where the gate was last held.
    public RoomKey? GateRoom => _gateRoom;

    public void SetCurrentRoomProbe(Func<RoomKey?> currentRoom)
    {
        ArgumentNullException.ThrowIfNull(currentRoom);
        _currentRoom = currentRoom;
    }

    private bool LeftTheGateRoom()
    {
        if (_currentRoom is null) return true;
        if (_gateRoom is not { } held || _currentRoom() is not { } here) return true;
        return held != here;
    }

    // True while combat is held on purpose (a ShadowRest resting stealthed beside a
    // monster). The silence is the point then, not a stall: the watchdog leaves the
    // gate up instead of re-displaying the room every 6 s (report
    // paradigm-20260930-193005). A combat line ends the hold, and the watchdog with it.
    private Func<bool>? _combatHeldOnPurpose;

    public void SetCombatHeldOnPurposeProbe(Func<bool> held) => _combatHeldOnPurpose = held;

    // Wire the CombatSettings.BreakBeforeFleeing reader. When set and true,
    // toggling auto-attack OFF mid-fight sends `break` before the Combat gate
    // releases the walker, so the disengage lands ahead of the walker's next
    // move. Until set, no break is ever sent (behaviour unchanged).
    public void SetBreakBeforeRunGate(Func<bool> breakBeforeRunning)
    {
        ArgumentNullException.ThrowIfNull(breakBeforeRunning);
        _breakBeforeRunning = breakBeforeRunning;
    }

    // Wire the "we have already sent an attack here" reader — CombatManager's
    // weapon target OR its announced spell target, since a physical swing and a
    // combat spell are separate modes and either one is an attack to break off.
    // PlayerState.InCombat only flips once the server's *Combat Engaged* is parsed,
    // so an auto-attack toggle landing between our attack going out and that reply
    // arriving sees InCombat false and skips the break (report
    // stock-20260914-003246). The engine's own target answers the same question
    // immediately. Until set, the break path reads InCombat alone.
    public void SetAttackInFlightGate(Func<bool> attackInFlight)
    {
        ArgumentNullException.ThrowIfNull(attackInFlight);
        _attackInFlight = attackInFlight;
    }

    // Re-evaluate the gate + InCombat the instant the auto-attack master toggle
    // flips, rather than waiting for the next room observation. Toggling
    // auto-attack OFF mid-round otherwise left the walker gate asserted (walker
    // stalled) and InCombat stuck true (no rest) until the user forced a room
    // re-display. Re-running the last observation applies the new toggle state
    // at once; it never sends an attack (that's CombatManager's own subscriber).
    public void OnAutoAttackChanged()
    {
        if (_disposed) return;

        // Turning auto-attack OFF mid-fight releases the Combat gate (in the
        // re-run observation below), letting the walker resume. When the user
        // wants a clean disengage first (CombatSettings.BreakBeforeFleeing), send
        // `break` before that release so it lands ahead of the walker's next move.
        // Gate on _gateAsserted so this only fires when we were actually holding
        // the walker for a fight, and on being in a fight so a routine walk never
        // breaks. Fires once — this handler runs only on the toggle transition,
        // not on every room observation.
        //
        // The break exists to stop an announced attack before we move, which lowers
        // the chance the monster chases us. So the question is "did we send an attack
        // here?", answered by InCombat OR a live engine target (weapon or spell) —
        // the same question at different times. InCombat waits on the server's
        // *Combat Engaged*; a toggle landing between our attack going out and that
        // reply arriving saw it false and skipped the break, letting the walker run
        // still engaged (report stock-20260914-003246). The engine's target is set
        // the moment we attack, so it covers that window. With neither true we never
        // attacked and a break would be a wasted command. Deferring the break until
        // *Combat Engaged* instead would be useless — the gate releases and the
        // walker's next move is already on the wire by the time it lands.
        bool inAFight = _state.InCombat || (_attackInFlight?.Invoke() ?? false);
        if (!_isAutoAttackEnabled()
            && _gateAsserted
            && inAFight
            && (_breakBeforeRunning?.Invoke() ?? false))
        {
            _log?.Info(LogCategory,
                "auto-attack off mid-combat — sending break before releasing walker (BreakBeforeFleeing)");
            SendCommand("break");
        }

        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);
    }

    // The master switch (true = off).
    public Func<bool>? MasterSwitchOff { get; set; }

    // Idle-stall watchdog, driven by the 1s heartbeat (TickEngine's
    // HeartbeatElapsed). Rescues two stall shapes after IdleStallThreshold of
    // zero combat activity — a live fight emits a line every 5s round, so total
    // silence means the fight is over and the room is empty:
    //   1. Gate asserted (auto-attack ON) — the classic walker hang: the empty
    //      re-display carried no "Also here:" line for the classifier to observe,
    //      so the gate stayed held over an empty room.
    //   2. InCombat stuck true with the gate NOT asserted (auto-attack OFF) — an
    //      incoming combat line latched InCombat, but with the gate never
    //      asserted the ONLY path that clears InCombat (OnEntitiesObserved's
    //      combat-off branch) runs solely on a room re-display that idle play
    //      never produces. InCombat then hangs, silencing rest AND bless (bless
    //      refuses to fire in combat), which reads to the user as "auto-bless
    //      stopped when I turned auto-combat off."
    // Single-phase recovery for both: send a benign resync CR (the safety probe)
    // AND force the tracker idle in the same tick. If a monster actually lingered
    // — a laggy >6s round — the CR's re-displayed "Also here:" re-observes a beat
    // later, so the optimistic clear self-heals. Running on the 1s heartbeat
    // lands this in ~6s total.
    public void OnCombatTick()
    {
        if (_disposed) return;

        // A held gate in a room that now reads "don't attack here" holds the loop while
        // the engine never attacks (not even in self-defense), so the character stands
        // being hit. It happens when the room's loop suppression flips after the gate
        // went up with nothing re-displaying the room: a reconnect judges the re-entry
        // room with the loop still stopped, and the loop resumes a moment later (report
        // paradigm-20260928-130856). Re-run the room so the gate lets go.
        if (_gateAsserted && !_seeHiddenClearLatch && !_sneakFailClearLatch && !_isAutoAttackEnabled() && _classifier.Current is { } room)
        {
            _log?.Info(LogCategory, "combat gate held but auto-attack is off in this room now (loop 'do not attack' / 'only lair rooms') — releasing");
            OnEntitiesObserved(room);
            return;
        }

        if (_wireSender is null) return;
        if (_combatHeldOnPurpose?.Invoke() == true) return;
        // Nothing to rescue unless the gate is held OR InCombat is stuck true.
        // With auto-attack off InCombat can hang with the gate clear, so the
        // gate alone is not a sufficient trigger — see shape 2 above.
        if (!_gateAsserted && !_state.InCombat) return;
        if (_now() - _lastCombatActivityAt < IdleStallThreshold) return;

        double idleSec = (_now() - _lastCombatActivityAt).TotalSeconds;
        // Skip the resync CR while we can't see: a dark room re-emits no
        // "Also here:" to re-observe, and its "you can't see anything" reply is
        // dead-reckoned as a false confirm of the movement loop's in-flight step.
        // The combat state still clears optimistically either way.
        bool dark = _isInDarkRoom?.Invoke() == true;
        // "Room empty" is the usual reading of the silence, but the room last shown
        // may still hold the monster the gate went up for (report
        // Hang-20261020-103254): the log names it rather than calling the room empty.
        string? stillShown = null;
        foreach (RoomEntity e in _classifier.Current?.Entities ?? Array.Empty<RoomEntity>())
            if (e.Kind == EntityKind.Monster && IsEngageable(e)) { stillShown = e.ResolvedName; break; }
        string seen = stillShown is null ? "room empty" : $"the room still shows '{stillShown}'";
        string held = _gateAsserted
            ? $"combat gate held {(_now() - _gateAssertedAt).TotalSeconds:F1}s"
            : "InCombat stuck (auto-attack off)";
        _log?.Info(LogCategory,
            $"{held}, idle {idleSec:F1}s since last activity "
            + $"({_lastCombatActivityDesc}) — no combat activity for the stall window, "
            + (dark
                ? $"{seen}; dark room, skipping resync CR and clearing stuck combat state"
                : $"{seen}; resyncing and clearing stuck combat state"));
        // With the master switch off the stuck state is still cleared, but the
        // redisplay is not sent: nothing automatic goes on the wire.
        if (!dark && MasterSwitchOff?.Invoke() != true)
            _wireSender(Encoding.Latin1.GetBytes("\r"));
        ResetCombatState($"idle-stall watchdog: no combat activity — {seen}");
    }

    private void SendCommand(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        // Single pass over the room: ANY monster (friendly or hostile)
        // blocks sneak (NPC-presence signal, independent of the gate);
        // engageable hostiles drive the gate; a SeeHidden occupant arms
        // the combat-off clear override.
        _anyNpcPresent = false;
        int targetable = 0;
        int actionable = 0;
        int attacking = 0;
        string? first = null;
        bool roomHasSeeHidden = false;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            _anyNpcPresent = true;
            bool engageable = IsEngageable(e);
            if (engageable)
            {
                targetable++;
                if (IsActionable(e))
                {
                    actionable++;
                    first ??= e.ResolvedName;
                }
            }
            // On-sight attackers only (Enemy relationship) — a passive KOS-neutral is
            // engageable but never attacks until we hit it, so it doesn't count here.
            if (IsAttackingHostile(e)) attacking++;
            LogMonsterDecision(e, engageable);
            if (!roomHasSeeHidden && e.MonsterNumber is int n
                && _hasSeeHidden?.Invoke(n) == true)
            {
                roomHasSeeHidden = true;
            }
        }

        // Raw hostile presence, updated on every observation ahead of the
        // auto-attack branches below so it stays accurate even when the gate
        // itself is short-circuited off (manual player). Read by the
        // emergency-hangup gate + the health rest-block. Enemy-relationship
        // (on-sight attackers) only — a passive KOS-neutral is engageable but not a
        // threat until we hit it, so it must not read as "a hostile is here."
        _hostilePresent = attacking > 0;

        // See-hidden clear for stealth runners — applies with auto-attack ON or OFF
        // (hoisted above the split). A stealth character (AutoSneak on) sprinting a
        // walk-to route enters a room whose SeeHidden monster breaks its sneak;
        // running onward would drag/stack the room's monsters across rooms (lethal
        // solo). The rule (user, 2026-10-07):
        //   - The break is the character's, not the room's (_sneakBrokenBySeeHidden):
        //     it lasts until the character is sneaking again.
        //   - While it lasts, the first room that meets the Min/Max monster window is
        //     where the character stops and clears EVERYTHING, so it can re-sneak. A
        //     room outside the window is walked through, as the settings say for any
        //     fight, with the break still on; so is a room it was carried into by a
        //     move already sent.
        //   - Once stopped the clear is latched: held until every engageable hostile
        //     is gone, whatever the count does meanwhile (CombatManager reads
        //     SeeHiddenClearActive to fight on past the window).
        //   - The re-sneak after the clear ends the break (NoteSneakRegained) and the
        //     route goes back to sneaking past everything.
        bool stealthRunner = _clearWhenSeenHidden?.Invoke() == true
                             && _isAutoSneakEnabled?.Invoke() == true;
        if (!stealthRunner) _sneakBrokenBySeeHidden = false;
        else if (roomHasSeeHidden && !_sneakBrokenBySeeHidden)
        {
            _sneakBrokenBySeeHidden = true;
            _log?.Info(LogCategory, "a see-hidden monster broke the sneak — the next room inside the Min/Max window is cleared, then re-sneak");
        }
        bool seeHiddenArm = _sneakBrokenBySeeHidden && IsWithinMonsterCountWindow(targetable);
        if (_seeHiddenClearLatch || seeHiddenArm)
        {
            // Hold only while something here is actually killable. If the
            // remaining hostiles are all un-actionable, standing to clear a room
            // we can't clear would deadlock the runner — release and let the walker
            // move past (a fight we can't win is worse than a broken sneak).
            if (actionable > 0)
            {
                _seeHiddenClearLatch = true;
                NoteLiveFight("see-hidden clear");
                AssertGate(roomHasSeeHidden
                    ? "seehidden clear (force-clear room)"
                    : "seehidden clear (sneak still broken by a see-hidden monster — clearing to re-sneak)");
                return;
            }
            _seeHiddenClearLatch = false;   // room cleared / un-actionable — release.
        }
        else if (_sneakBrokenBySeeHidden && actionable > 0 && !_outsideWindowLogged)
        {
            _outsideWindowLogged = true;
            _log?.Info(LogCategory, $"sneak broken by a see-hidden monster, but this room is outside the Min/Max monster window ({targetable} hostile(s)) — moving on, still broken");
        }
        if (actionable == 0 || !_sneakBrokenBySeeHidden) _outsideWindowLogged = false;

        // Sneak-fail clear for stealth runners (combat off): a sneaked move failed
        // into this room, and the room is inside the Min/Max monster window, so we
        // stop and clear it — a room outside the window is walked through unsneaked,
        // as without the option. Latched like the see-hidden clear: held until
        // nothing actionable is left, then the route re-sneaks
        // (CombatSpentStealth) and walks on.
        bool sneakFailArm = _sneakFailedHere
                            && _clearWhenSneakFails?.Invoke() == true
                            && _isAutoSneakEnabled?.Invoke() == true
                            && !_isAutoAttackEnabled()
                            && IsWithinMonsterCountWindow(targetable);
        if (_sneakFailClearLatch || sneakFailArm)
        {
            if (actionable > 0)
            {
                if (!_sneakFailClearLatch)
                    _log?.Info(LogCategory, $"sneak failed entering — clearing {actionable} hostile(s) here before sneaking on");
                _sneakFailClearLatch = true;
                NoteLiveFight("sneak-fail clear");
                AssertGate("sneak-fail clear (clear the room, then re-sneak)");
                return;
            }
            _sneakFailClearLatch = false;
        }

        if (!_isAutoAttackEnabled())
        {
            // Auto-attack off and no see-hidden override → never hold the gate.
            // Defensive clear in case it was asserted just before toggle.
            ClearGate("auto-attack disabled");
            // A room clear of engageable hostiles is the authoritative
            // out-of-combat signal even with auto-attack off — otherwise
            // InCombat stays stuck true and HealthManager never rests
            // (CombatStatus=Off is unreliable, see OnCombatStatus). A hostile
            // still here keeps InCombat true so we don't rest next to a mob.
            if (targetable == 0 && _state.InCombat)
            {
                _state.InCombat = false;
                // A combat-OFF force-clear (a see-hidden / rest engage-to-clear) still
                // SPENT sneak during the fight, but with no line to latch the FSM reads
                // stale-Sneaking. Fire the same stealth reset the auto-attack-ON clear
                // path uses so the pre-move / post-cast re-sneak isn't no-op'd on stale
                // state — otherwise a stealth runner leaves a cleared see-hidden room
                // unsneaked. Guarded on InCombat (true only after we engaged), so a pure
                // walk-past of an un-actionable room never resets a sneak we still hold.
                CombatSpentStealth?.Invoke(obs.Source == RoomObservationSource.Death);
            }
            return;
        }

        // Auto-attack on — the normal gate owns pausing.
        bool withinCountWindow = IsWithinMonsterCountWindow(targetable);
        if (actionable > 0 && withinCountWindow)
        {
            NoteLiveFight("room-entry hostile");
            string reason = first is null
                ? $"room-entry actionable={actionable}/{targetable}"
                : $"room-entry actionable={actionable}/{targetable} first={first}";
            AssertGate(reason);
        }
        else
        {
            // A confirmed room change (synthetic empty wipe) while the gate is
            // held means an in-flight move carried us OUT of a room where we'd
            // engaged an actionable hostile — we didn't kill it (a real kill
            // clears the gate on the Death observation first), we left it. That's
            // an abandoned fight, not a room we cleared by winning: signal the
            // walker to halt so it doesn't keep walking the route deeper past a
            // fight we committed to. We still clear the gate below — the new room
            // is genuinely empty of the old target and holding would deadlock (an
            // empty room emits no further observation to release it); if the
            // monster followed, its arrival observation re-asserts within
            // milliseconds.
            //
            // A wipe that leaves us standing where the gate was last held is the
            // roster dropped in place, a room cleared by kills (see _gateRoom).
            if (obs.Source == RoomObservationSource.RoomChange && _gateAsserted)
            {
                if (LeftTheGateRoom())
                    EngagedTargetAbandoned?.Invoke("left a room with an engaged target still alive");
                else
                    _log?.Combat(LogCategory, "roster emptied in the room we fought in — a cleared room, not a fight walked out on");
            }

            // Room is now clear of engageable monsters → combat truly ended. This
            // is the authoritative "we're out of combat" signal; CombatStatus=Off
            // is unreliable (the server emits it when we cast a spell mid-round,
            // with the mob still alive — see OnCombatStatus). Only a room confirmed
            // clear of engageable hostiles flips it, so HealthManager never rests
            // next to a live mob.
            //
            // Ordering is load-bearing — each step feeds a synchronous downstream
            // reaction, so the sequence matters:
            //   1. Drop _gateAsserted first. HasEngageableHostiles is derived from
            //      it, and step 2's InCombat=false fires HealthManager.Evaluate
            //      synchronously; if the gate still read asserted there, the rest
            //      is blocked as "hostile present" and deferred to the next prompt
            //      tick — a multi-second stall after the room clears (report
            //      stock-20260730-184622).
            //   2. Flip InCombat false (+ drop the stealth the fight spent) BEFORE
            //      the coordinator gate releases the walker, so the pre-move
            //      re-sneak sees out-of-combat (report stock-20260730-163044).
            //      CombatSpentStealth only fires when we actually engaged — a pure
            //      walk-past of an un-actionable room never broke our sneak.
            //   3. Release the coordinator Combat gate — resumes the walker + its
            //      pre-move hook now that both InCombat and HasEngageableHostiles
            //      read clear. Inlined rather than via ClearGate so InCombat can
            //      flip between the flag drop (1) and the coordinator release (3).
            // targetable>0 means hostiles remain but either none are killable, or
            // the room's population falls outside the configured Min/Max window
            // while something is actively moving us through — either way, release
            // and move past (the move-past rule); the "room cleared" wording is
            // the genuine empty-room case.
            string clearReason = targetable switch
            {
                > 0 when !withinCountWindow =>
                    $"room outside Min/Max monster window: {targetable} hostile(s) — moving on",
                > 0 => $"room un-actionable: {targetable} hostile(s), none hittable — moving on",
                _ => "room cleared",
            };
            bool wasAsserted = _gateAsserted;
            _gateAsserted = false;                                        // (1)
            if (_state.InCombat)
            {
                _state.InCombat = false;                                  // (2)
                CombatSpentStealth?.Invoke(obs.Source == RoomObservationSource.Death);
            }
            if (wasAsserted)                                              // (3)
            {
                FightEnded?.Invoke();
                _coordinator.ClearGate(MovementCoordinator.CombatGate, AsserterName, clearReason);
            }
        }
    }

    // Mirrors CombatManager.OnEntitiesObserved's own min/max skip so the two
    // never disagree on whether a room is worth fighting — see
    // SetMonsterCountWindow. The window itself only applies while a movement
    // engine is actually attached (SetMovementActiveGate) — standing here idle
    // with nowhere to go should fight regardless of population. The bounds come
    // from MonsterCountGate.Bounds as CombatManager's do: the Party-tab cap in a
    // party (SetPartyMonsterCap) and no floor while a loop waits at a lair's door
    // (SetLairDoorWaitProbe). Unwired, not moving, or a misconfigured Min > Max,
    // all fail open (every count passes — gate behaves exactly as before this fix).
    private bool IsWithinMonsterCountWindow(int targetable)
    {
        if (_readSettings is null) return true;
        if (!(_isMovementActive?.Invoke() ?? true)) return true;
        CombatSettings settings = _readSettings();
        (int min, int max) = MonsterCountGate.Bounds(settings, _partyMonsterCap?.Invoke(),
            waitingAtLairDoor: _waitingAtLairDoor?.Invoke() == true);
        // Shared with CombatManager's own gate (MonsterCountGate) so the two can't
        // diverge. "Kill all engaged" holds the walker below the floor only while
        // we've committed to this room — finish the survivors instead of moving on.
        return MonsterCountGate.WithinWindow(
            targetable, min, max, settings.KillAllEngaged, _roomCommitted?.Invoke() ?? false);
    }

    // Engageable = Enemy (the default for a resolved monster with no relationship) OR a Neutral
    // flagged KillOnSight — see MonsterEngagement. Shopkeepers / quest-givers / friendly NPCs
    // are marked Friend / Neutral / Hangup in the overlay seed; a monster whose Number resolves
    // but whose overlay is missing is treated as fightable, so the engine doesn't sit through a
    // respawn waiting on a tag nobody set.
    //
    // A monster the classifier CANNOT resolve to a Number is NOT engaged — we never proactively
    // hit something we can't identify, so a friendly / neutral NPC whose name didn't pin to its
    // record (a greet-only "old man") is left alone rather than defaulting to a fightable enemy.
    // This is a fail-CLOSED distinct from the resolved-but-untagged fail-open above; the
    // reactive InCombat path still fights back if such a mob actually attacks us.
    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return false;
        // A passive neutral the user hand-engaged fights like a hostile until dead, so it
        // holds the walker gate the same way an enemy does — even before its overlay says
        // so, and regardless of the resolver being wired.
        bool userEngaged = _isUserEngagedInstance?.Invoke(e.RawName) == true;
        if (_resolveOverlay is null) return true;        // legacy ctor — engage everything
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return MonsterEngagement.IsEngageable(overlay, userEngaged);
    }

    // On-sight attacker: an Enemy-relationship monster (one the seed knows with none
    // set, an evil-aligned one it doesn't know, or one with no relationship at all).
    // These hit us every round we share the room, so they block resting. A Neutral —
    // even a KillOnSight one we intend to fight — never attacks first, so it is NOT an
    // attacking hostile (it only hits back once we engage it, which the InCombat flag
    // then reflects). Backs HasHostileMonster / the rest-block.
    private bool IsAttackingHostile(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;   // unknown → assume enemy (fail-safe)
        if (_resolveOverlay is null) return true;
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return (overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy;
    }

    // Per-monster Combat-level trace so a log read explains WHY the engine engaged or skipped
    // each room occupant: the detection (name → resolved record Number), its user Relationship +
    // Kill-on-sight, and the resulting engage / skip decision. Exactly what a "why did it attack
    // the friendly NPC?" report needs. Combat severity — verbose per-observation detail.
    private void LogMonsterDecision(RoomEntity e, bool engageable)
    {
        if (_log is null) return;

        string record = e.MonsterNumber is int n ? $"#{n}" : "unresolved";
        string relationship = "Enemy (default)";
        bool killOnSight = false;
        if (e.MonsterNumber is int mn && _resolveOverlay is not null)
        {
            MonsterOverlay? overlay;
            try { overlay = _resolveOverlay(mn); } catch { overlay = null; }
            relationship = (overlay?.Relationship ?? MonsterRelationship.Enemy).ToString();
            killOnSight = overlay?.KillOnSight == true;
        }

        _log.Combat(LogCategory,
            $"detected '{e.ResolvedName}' ({record}) · relationship {relationship}"
            + (killOnSight ? " · kill-on-sight" : "")
            + $" → {(engageable ? "engage" : "skip")}");
    }

    // Actionable = we can actually kill it (a weapon can hit it OR an eligible
    // attack spell can land). Fail-open: an unwired gate, an unknown monster
    // Number, or a resolver exception all count as actionable so a thin data set
    // never strands the walker. The caller only invokes this for entities that
    // already passed IsEngageable.
    private bool IsActionable(RoomEntity e)
    {
        if (_canEngage is null) return true;             // unwired → fail open
        if (e.MonsterNumber is not int n) return true;   // unknown number → fail open
        try { return _canEngage(n); }
        catch { return true; }
    }

    // A fresh observation still holding a killable monster is a live fight: refresh
    // the stall watchdog's stamp (AssertGate early-outs when the gate's already held,
    // so this is stamped regardless, and a slow-but-real fight never trips it).
    //
    // Every path that holds the gate for a fight stamps it. The two combat-off clears
    // didn't, so the watchdog measured the silence from the last fight's final line:
    // a see-hidden room met after a quiet walk had its gate dropped by the next
    // one-second poll, a tenth of a second after it went up, and the walker took the
    // character out of the room it had just attacked in (report
    // paradigm-20261007-143049).
    private void NoteLiveFight(string what)
    {
        _lastCombatActivityAt = _now();
        _lastCombatActivityDesc = what;
    }

    private void AssertGate(string reason)
    {
        // Re-read on every hold, not only the first: a new room's "Also here:" holds
        // the gate before the move is confirmed, while the tracker still names the
        // room we left; the roster re-emitted on the confirm corrects it.
        _gateRoom = _currentRoom?.Invoke();
        if (_gateAsserted) return;
        _gateAsserted = true;
        _gateAssertedAt = _now();
        _coordinator.AssertGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    private void ClearGate(string reason)
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        FightEnded?.Invoke();
        _coordinator.ClearGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    // Manual escape hatch (Reset States). Force the tracker back to an idle,
    // out-of-combat state: release the Combat gate, drop the combat-off seehidden
    // latch, and clear InCombat. The case this rescues: a stale roster (a fallback
    // death whose forced re-display never wiped the room) leaves the gate asserted
    // and the walker parked "fighting" an empty room with no in-game line left to
    // release it — the idle-stall watchdog keeps re-displaying but the roster
    // never clears. Reset States clearing conditions alone left this stuck, so it
    // routes here too. The next genuine room observation re-derives presence from
    // scratch, so a hostile that really remains re-asserts within a round.
    public void ResetCombatState(string reason, bool forgetSneakBreak = false)
    {
        ClearGate(reason);
        _seeHiddenClearLatch = false;
        _sneakFailClearLatch = false;
        // The stall watchdog only gives up on this room; the sneak is as broken as
        // it was. Reset States is the user saying start over.
        if (forgetSneakBreak) _sneakBrokenBySeeHidden = false;
        if (_state.InCombat) _state.InCombat = false;
        _log?.Info(LogCategory, $"combat state force-cleared — {reason}");
        CombatForceCleared?.Invoke();   // fired last: the gate is now down, so a deferred collect can flush
    }

    // ----- InCombat plumbing ----------------------------------------

    // A room's own damage reads like a hit ("You are seared by the flames for 46
    // damage!") and is no fight: nothing is there to fight. Taken for one it put the
    // client in combat in an empty room, which broke off a rest in the bookkeeping,
    // swapped gear to the fighting set and back, and kept the idle watchdog fed. The
    // probe is the round clock's own test (OffRoundDamageLines, wired by
    // AppServices); unset, every line is a combat line as before.
    private Func<string, bool>? _isNotCombatLine;

    public void SetNotCombatLineProbe(Func<string, bool> isNotCombatLine)
    {
        ArgumentNullException.ThrowIfNull(isNotCombatLine);
        _isNotCombatLine = isNotCombatLine;
    }

    private void OnAnyCombatLine(MatchResult match)
    {
        if (_isNotCombatLine?.Invoke(match.Text) == true) return;
        // A damage/miss line is proof the fight is live — refresh the
        // watchdog's activity stamp so it never fires mid-fight. Server status
        // reports combat only when it flips, so a long fight's lines are all that
        // keep it from reading stalled.
        _lastCombatActivityAt = _now();
        _lastCombatActivityDesc = "combat line";
        if (!_state.InCombat) _state.InCombat = true;
        // The fight is where we stand. A pursuer kept across our move raises no
        // observation in the new room, so this is what moves the gate's room with it.
        if (_gateAsserted) _gateRoom = _currentRoom?.Invoke();
    }

    // The combat manager is holding engagement of a passive KillOnSight neutral so
    // the health engine can rest first — a neutral never attacks until we hit it, so
    // sitting in the room with un-engaged neutrals is safe. Clearing InCombat lets
    // the rest fire; the Combat gate stays asserted (an engageable target remains) so
    // the walker keeps holding. Re-engaging re-sets InCombat via the neutral's first
    // hit back. This tracker owns InCombat, so the write lives here.
    public void ClearInCombatForRecoveryHold()
    {
        if (_state.InCombat) _state.InCombat = false;
    }

    // The server's own auto-attack state: *Combat Engaged* → true, *Combat Off* →
    // false. Unlike InCombat (held until the room is clear) this says whether a
    // `break` would do anything — HealthManager's flee skips it when we're not
    // swinging (a `break` then only draws "Your command had no effect.").
    public bool IsServerEngaged { get; private set; }
    public Func<bool>? GmcpOwnsStatus { get; set; }
    // The server's flag is its auto-attack state, which a mid-fight cast turns off
    // with the monster still alive, as *Combat Off* does. So, as on the text path,
    // it can start a fight but never ends one: InCombat ends when the room clears.
    public void ApplyGmcpCombat(bool combat)
    {
        IsServerEngaged = combat;
        if (!combat) return;
        if (!_state.InCombat) _state.InCombat = true;
        _lastCombatActivityAt = _now();
        _lastCombatActivityDesc = "server combat status";
    }

    private void OnCombatStatus(MatchResult match)
    {
        if (GmcpOwnsStatus?.Invoke() == true) return;
        // (?<status>Engaged|Off) capture in DefaultPatterns.
        if (match.Groups.Count == 0) return;
        string status = match.Groups[0];
        IsServerEngaged = string.Equals(status, "Engaged", StringComparison.OrdinalIgnoreCase);

        // Only Engaged matters — when the server says we're now in
        // combat, mirror that. We do NOT flip to false on
        // *Combat Off* because the server emits Off whenever
        // auto-attack stops for ANY reason, including casting a
        // spell mid-round with the mob still alive. Doing so would
        // let HealthManager start resting while a hostile is right
        // next to us. The authoritative end-of-combat signal is the
        // room going clear of engageable monsters — handled by
        // OnEntitiesObserved's "room cleared" branch.
        if (string.Equals(status, "Engaged", StringComparison.OrdinalIgnoreCase))
        {
            if (!_state.InCombat) _state.InCombat = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobHitsNoDamageSub.Dispose();
        _mobHitsOtherNoDamageSub.Dispose();
        _mobMissesSub.Dispose();
        _mobAttacksSub.Dispose();
        _userDodgesSub.Dispose();
        _combatStatusSub.Dispose();
    }
}
