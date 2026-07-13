using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

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
// Plus a master switch: isAutoAttackEnabled short-circuits the gate to
// never-assert when off, so a fresh character (default
// CombatSettings.MasterAutoAttackEnabled = false) walks through every room
// unimpeded until the user opts in.
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

    private readonly MovementCoordinator _coordinator;
    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay>? _resolveOverlay;
    private readonly PlayerState _state;
    private readonly Func<bool> _isAutoAttackEnabled;
    private readonly LogService? _log;

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;

    private bool _gateAsserted;
    private bool _anyNpcPresent;
    private bool _hostilePresent;
    private bool _disposed;

    private Func<bool>? _clearWhenSeenHidden;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<int, bool>? _hasSeeHidden;
    private Func<int, bool>? _canEngage;
    private bool _seeHiddenClearLatch;

    private Action<byte[]>? _wireSender;
    private Func<bool>? _breakBeforeRunning;

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
    // this latches on entry to such a room — holding the Combat gate (so the
    // walker actually stops) until every engageable hostile is gone.
    // CombatManager reads this to engage despite combat-off.
    public bool SeeHiddenClearActive => _seeHiddenClearLatch;

    // Fires when a confirmed room change happens while the combat gate is held —
    // an in-flight move carried us out of a room where we'd engaged an
    // actionable hostile before it died. The walker subscribes and halts so the
    // route doesn't keep going deeper past a fight we committed to. The argument
    // is a human-readable reason for the log / walk event. (The gate itself is
    // still cleared for the new room — see OnEntitiesObserved's RoomChange arm.)
    public event Action<string>? EngagedTargetAbandoned;

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
        LogService? log = null)
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

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _userHitsSub      = router.Subscribe(KnownPatterns.UserHits,    OnAnyCombatLine);
        _mobHitsSub       = router.Subscribe(KnownPatterns.MobHits,     OnAnyCombatLine);
        _mobMissesSub     = router.Subscribe(KnownPatterns.MobMisses,   OnAnyCombatLine);
        _combatStatusSub  = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
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

    // Wire path for the break-before-run disengage. Bound at connect time (the
    // same gate-wrapped engineSend every other engine receives). Until set, the
    // break-before-run step no-ops.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the CombatSettings.BreakBeforeFleeing reader. When set and true,
    // toggling auto-attack OFF mid-fight sends `break` before the Combat gate
    // releases the walker, so the disengage lands ahead of the walker's next
    // move. Until set, no break is ever sent (behaviour unchanged).
    public void SetBreakBeforeRunGate(Func<bool> breakBeforeRunning)
    {
        ArgumentNullException.ThrowIfNull(breakBeforeRunning);
        _breakBeforeRunning = breakBeforeRunning;
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
        // the walker for a fight, and on InCombat so a routine walk never breaks.
        // Fires once — this handler runs only on the toggle transition, not on
        // every room observation.
        if (!_isAutoAttackEnabled()
            && _gateAsserted
            && _state.InCombat
            && (_breakBeforeRunning?.Invoke() ?? false))
        {
            _log?.Info(LogCategory,
                "auto-attack off mid-combat — sending break before releasing walker (BreakBeforeFleeing)");
            SendCommand("break");
        }

        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);
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
        string? first = null;
        bool roomHasSeeHidden = false;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            _anyNpcPresent = true;
            if (IsEngageable(e))
            {
                targetable++;
                if (IsActionable(e))
                {
                    actionable++;
                    first ??= e.ResolvedName;
                }
            }
            if (!roomHasSeeHidden && e.MonsterNumber is int n
                && _hasSeeHidden?.Invoke(n) == true)
            {
                roomHasSeeHidden = true;
            }
        }

        // Raw hostile presence, updated on every observation ahead of the
        // auto-attack branches below so it stays accurate even when the gate
        // itself is short-circuited off (manual player). Read by the
        // emergency-hangup gate.
        _hostilePresent = targetable > 0;

        if (!_isAutoAttackEnabled())
        {
            // Combat-off override for stealth runners. Arm on entry to a
            // room that breaks sneak (SeeHidden present, AutoSneak on,
            // toggle on); once latched, HOLD the walker gate until every
            // engageable hostile is gone so the room is fully cleared —
            // even after the SeeHidden monster itself dies. Then release.
            bool armNow = _clearWhenSeenHidden?.Invoke() == true
                          && _isAutoSneakEnabled?.Invoke() == true
                          && roomHasSeeHidden;
            if (_seeHiddenClearLatch || armNow)
            {
                // Hold only while something here is actually killable. If the
                // remaining hostiles are all un-actionable, standing to clear
                // a room we can't clear would deadlock the runner — release
                // and let the walker move past (the move-past rule wins even
                // over the re-sneak concern: a fight we can't win is worse).
                if (actionable > 0)
                {
                    _seeHiddenClearLatch = true;
                    AssertGate("seehidden clear (combat-off override)");
                    return;
                }
                _seeHiddenClearLatch = false;   // room cleared / un-actionable — release.
            }
            // Auto-attack off and no override → never hold the gate.
            // Defensive clear in case it was asserted just before toggle.
            ClearGate("auto-attack disabled");
            // A room clear of engageable hostiles is the authoritative
            // out-of-combat signal even with auto-attack off — otherwise
            // InCombat stays stuck true and HealthManager never rests
            // (CombatStatus=Off is unreliable, see OnCombatStatus). A hostile
            // still here keeps InCombat true so we don't rest next to a mob.
            if (targetable == 0 && _state.InCombat) _state.InCombat = false;
            return;
        }

        // Auto-attack on — the normal gate owns pausing; the override
        // latch is a combat-off concept, so drop it.
        _seeHiddenClearLatch = false;

        if (actionable > 0)
        {
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
            if (obs.Source == RoomObservationSource.RoomChange && _gateAsserted)
                EngagedTargetAbandoned?.Invoke("left a room with an engaged target still alive");

            // targetable>0 here means hostiles remain but none are killable —
            // release the gate and move past (per the move-past rule). The
            // "room cleared" wording stays for the genuine empty-room case.
            ClearGate(targetable > 0
                ? $"room un-actionable: {targetable} hostile(s), none hittable — moving on"
                : "room cleared");
            // Room is now clear of engageable monsters → combat truly
            // ended. This is the authoritative "we're out of combat"
            // signal; CombatStatus=Off is unreliable (server emits it
            // when we cast a spell mid-round, with the mob still
            // alive — see CombatStateTracker.OnCombatStatus). Tying
            // the false transition to the gate clear means
            // HealthManager won't start resting while a hostile mob
            // is still here.
            if (_state.InCombat) _state.InCombat = false;
        }
    }

    // Engageable = MonsterOverlay.Relationship is Enemy (or null, which defaults
    // to Enemy). Shopkeepers / quest-givers / friendly NPCs are marked Friend /
    // Neutral / Hangup explicitly in the overlay seed; un-tagged monsters are
    // treated as fightable so the engine doesn't sit through a respawn just
    // because the data table is missing a DeathLine (152 of 1100 monsters in
    // stock data ship with empty DeathLine — acid slime, etc.).
    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;
        if (_resolveOverlay is null) return true;        // legacy ctor — engage everything
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return (overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy;
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

    private void AssertGate(string reason)
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    private void ClearGate(string reason)
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    // ----- InCombat plumbing ----------------------------------------

    private void OnAnyCombatLine(MatchResult _)
    {
        if (!_state.InCombat) _state.InCombat = true;
    }

    private void OnCombatStatus(MatchResult match)
    {
        // (?<status>Engaged|Off) capture in DefaultPatterns.
        if (match.Groups.Count == 0) return;
        string status = match.Groups[0];

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
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
    }
}
