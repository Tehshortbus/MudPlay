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
    private readonly PlayerState _state;
    private readonly Func<bool> _isAutoAttackEnabled;
    private readonly LogService? _log;

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;

    private bool _gateAsserted;
    private bool _disposed;

    public CombatStateTracker(
        MessageRouter router,
        MovementCoordinator coordinator,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        PlayerState state,
        Func<bool> isAutoAttackEnabled,
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
        _state       = state;
        _isAutoAttackEnabled = isAutoAttackEnabled;
        _log         = log;

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _userHitsSub      = router.Subscribe(KnownPatterns.UserHits,    OnAnyCombatLine);
        _mobHitsSub       = router.Subscribe(KnownPatterns.MobHits,     OnAnyCombatLine);
        _mobMissesSub     = router.Subscribe(KnownPatterns.MobMisses,   OnAnyCombatLine);
        _combatStatusSub  = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (!_isAutoAttackEnabled())
        {
            // Auto-attack off → never hold the gate. Defensive clear
            // in case it was asserted just before the user toggled off.
            ClearGate("auto-attack disabled");
            return;
        }

        int targetable = 0;
        string? first = null;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (!IsEngageable(e)) continue;
            targetable++;
            first ??= e.ResolvedName;
        }

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
    /// Until PR 9.A wires per-monster AttackPriority, "engageable"
    /// means the monster has a populated DeathLine — i.e. there's
    /// a known death message for it, so it's a real killable mob
    /// (not a shopkeeper / quest-giver / friendly NPC). Returns true
    /// when the classifier-emitted entity has no associated
    /// MonsterMessageRecord (defensive — better to wait on an
    /// unknown-to-store mob than walk past it).
    /// </summary>
    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;
        MonsterMessageRecord? rec = _monsters.FindByMonsterNumber(n);
        if (rec is null) return true;
        return rec.DeathLine.Count > 0;
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
