using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.0b — owns <see cref="PlayerState.InCombat"/> and the
/// <c>Combat</c> gate on <see cref="MovementCoordinator"/>. Subscribes
/// to <see cref="RoomEntityClassifier.EntitiesObserved"/> for the
/// gate's room-clear logic and to combat-line patterns for the
/// <see cref="PlayerState.InCombat"/> flag.
/// </summary>
/// <remarks>
/// <para>
/// Gate semantics per docs/10-phase-9 § Cross-cut 1: the
/// <see cref="MovementCoordinator.CombatGate"/> is held while the
/// current room contains at least one classified
/// <see cref="EntityKind.Monster"/> the user is configured to engage.
/// Until per-monster <c>AttackPriority</c> wires through Phase 9
/// PR 9.A, "engageable" = monster has a populated
/// <see cref="MonsterMessageRecord.DeathLine"/> (i.e. it's killable —
/// shopkeepers and quest-givers carry empty DeathLine lists and don't
/// hold the gate).
/// </para>
/// <para>
/// Plus a master switch: <c>isAutoAttackEnabled</c> short-circuits the
/// gate to never-assert when off, so a fresh character (default
/// <c>CombatSettings.MasterAutoAttackEnabled = false</c>) walks
/// through every room unimpeded until the user opts in.
/// </para>
/// <para>
/// <see cref="PlayerState.InCombat"/> flips:
/// <list type="bullet">
/// <item><b>True</b> on <see cref="KnownPatterns.CombatStatus"/>
/// <c>Engaged</c> OR <see cref="KnownPatterns.UserHits"/> OR
/// <see cref="KnownPatterns.MobHits"/> OR <see cref="KnownPatterns.MobMisses"/>.</item>
/// <item><b>False</b> on <see cref="KnownPatterns.CombatStatus"/>
/// <c>Off</c>.</item>
/// </list>
/// Damage-line-driven decay (no damage for 5s = InCombat false) lands
/// in PR 9.0c with <see cref="RoundDamageTracker"/>.
/// </para>
/// </remarks>
public sealed class CombatStateTracker : IDisposable
{
    /// <summary>Identifier this tracker uses when asserting / clearing
    /// the Combat gate. Surfaces in
    /// <see cref="MovementCoordinator.History"/> +
    /// <c>[Gate]</c> log lines.</summary>
    public const string AsserterName = "CombatStateTracker";

    /// <summary>LogService category for tracker-emitted rows.</summary>
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
    private bool _disposed;

    private Func<bool>? _clearWhenSeenHidden;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<int, bool>? _hasSeeHidden;
    private bool _seeHiddenClearLatch;

    /// <summary>
    /// True while the room currently contains at least one engageable
    /// (Enemy-relationship, killable) monster. Drives the
    /// <see cref="MovementCoordinator.CombatGate"/> + lets HealthManager
    /// gate the rest decision so we don't try to rest while a mob is
    /// here — every combat round would otherwise break rest and we'd
    /// never actually recover. Clears authoritatively when an Also-Here
    /// observation shows no engageable monsters (room cleared).
    /// </summary>
    public bool HasEngageableHostiles => _gateAsserted;

    /// <summary>
    /// True while the current room contains at least one NPC / monster
    /// of any relationship (Enemy, Friend, Neutral — shopkeepers and
    /// quest-givers included). Sneak cannot be established while any NPC
    /// is present, so the StealthManager pre-move hook consults this to
    /// suppress a doomed <c>sn</c>. Updated on every Also-Here
    /// observation, independent of the auto-attack gate (which only
    /// reacts to <em>engageable</em> hostiles).
    /// </summary>
    public bool HasRoomNpc => _anyNpcPresent;

    /// <summary>
    /// True while a combat-off "clear hostiles when seen Hidden" force-clear
    /// is latched for the current room. A stealth runner (AutoSneak on)
    /// sprinting a route with combat OFF that hits a room holding a
    /// <c>SeeHidden</c> monster can't re-sneak there; running onward would
    /// drag and stack monsters across rooms, lethal when solo. When
    /// <see cref="CombatSettings.ClearHostilesWhenSeenHidden"/> is on, this
    /// latches on entry to such a room — holding the Combat gate (so the
    /// walker actually stops) until every engageable hostile is gone.
    /// <see cref="CombatManager"/> reads this to engage despite combat-off.
    /// </summary>
    public bool SeeHiddenClearActive => _seeHiddenClearLatch;

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

    /// <summary>
    /// Construct with a per-monster overlay resolver so the engageable
    /// predicate matches CombatManager (Relationship-based). Without
    /// it, the tracker falls back to "every monster engageable" which
    /// can spuriously assert the Combat gate against shopkeepers
    /// (CombatManager would skip them but the walker would still
    /// pause). AppServices wires the same delegate it gives
    /// CombatManager so the two stay in sync.
    /// </summary>
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

    /// <summary>
    /// Wire the combat-off "clear hostiles when seen Hidden" override:
    /// <paramref name="clearWhenSeenHidden"/> reads
    /// <see cref="CombatSettings.ClearHostilesWhenSeenHidden"/>,
    /// <paramref name="isAutoSneakEnabled"/> reports whether the character
    /// is stealthing its route (AutoSneak auto-mode on), and
    /// <paramref name="hasSeeHidden"/> reports whether a monster Number
    /// carries SeeHidden (<see cref="SeeHiddenIndex"/>). With all wired,
    /// entering a room that breaks a stealth runner's sneak latches a
    /// force-clear (see <see cref="SeeHiddenClearActive"/>). Until set, the
    /// override stays dormant and the gate behaves exactly as before.
    /// </summary>
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

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        // Single pass over the room: ANY monster (friendly or hostile)
        // blocks sneak (NPC-presence signal, independent of the gate);
        // engageable hostiles drive the gate; a SeeHidden occupant arms
        // the combat-off clear override.
        _anyNpcPresent = false;
        int targetable = 0;
        string? first = null;
        bool roomHasSeeHidden = false;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            _anyNpcPresent = true;
            if (IsEngageable(e))
            {
                targetable++;
                first ??= e.ResolvedName;
            }
            if (!roomHasSeeHidden && e.MonsterNumber is int n
                && _hasSeeHidden?.Invoke(n) == true)
            {
                roomHasSeeHidden = true;
            }
        }

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
                if (targetable > 0)
                {
                    _seeHiddenClearLatch = true;
                    AssertGate("seehidden clear (combat-off override)");
                    return;
                }
                _seeHiddenClearLatch = false;   // room cleared — release.
            }
            // Auto-attack off and no override → never hold the gate.
            // Defensive clear in case it was asserted just before toggle.
            ClearGate("auto-attack disabled");
            return;
        }

        // Auto-attack on — the normal gate owns pausing; the override
        // latch is a combat-off concept, so drop it.
        _seeHiddenClearLatch = false;

        if (targetable > 0)
        {
            string reason = first is null
                ? $"room-entry targetable={targetable}"
                : $"room-entry targetable={targetable} first={first}";
            AssertGate(reason);
        }
        else
        {
            ClearGate("room cleared");
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

    /// <summary>
    /// Engageable = MonsterOverlay.Relationship is Enemy (or null,
    /// which defaults to Enemy). Shopkeepers / quest-givers / friendly
    /// NPCs are marked Friend / Neutral / Hangup explicitly in the
    /// overlay seed; un-tagged monsters are treated as fightable so
    /// the engine doesn't sit through a respawn just because the data
    /// table is missing a DeathLine (152 of 1100 monsters in stock
    /// data ship with empty DeathLine — acid slime, etc.).
    /// </summary>
    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;
        if (_resolveOverlay is null) return true;        // legacy ctor — engage everything
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return (overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy;
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
