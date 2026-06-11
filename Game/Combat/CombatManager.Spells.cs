using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Combat-spell round economy — the opt-in half of <see cref="CombatManager"/>
/// that turns the pure <see cref="CombatSpellChooser"/> decisions into casts
/// on the wire. Split out of <c>CombatManager.cs</c> to keep the weapon
/// engine and the spell sequencing each in a file scoped to one
/// responsibility.
/// </summary>
/// <remarks>
/// <para>
/// Wiring is optional: until <see cref="SetCombatSpellCaster"/> runs, the
/// engine is pure weapon-attack and every existing path is unchanged. Once
/// wired, <see cref="OnEntitiesObserved"/> consults the chooser before the
/// backstab / weapon path, and the per-round heartbeat
/// (<see cref="OnCombatTick"/>) re-issues the chosen cast each round —
/// casts do NOT auto-repeat server-side the way weapon swings do, so the
/// tick boundary is the only thing that keeps a multi-round spell going.
/// </para>
/// <para>
/// Casts route through the shared <see cref="CastCoordinator.TryCast"/> so
/// the one-cast-per-round cooldown is honoured across every casting engine
/// (a survival heal from <c>CastingDirector</c> earlier in the same tick
/// blocks our offensive cast — survival beats offense, by design of the
/// <c>AppServices</c> tick-subscription order).
/// </para>
/// </remarks>
public sealed partial class CombatManager
{
    private readonly CombatSpellChooser _spellChooser = new();
    private CastCoordinator? _cast;
    private Func<(int Ma, int MaxMa)>? _readMana;

    /// <summary>
    /// Opt into combat-spell casting. <paramref name="cast"/> is the shared
    /// <see cref="CastCoordinator"/> (so the per-round cooldown is shared
    /// with every other caster); <paramref name="readMana"/> reports live
    /// MA / max-MA for the chooser's per-cast mana gate. Until called the
    /// engine is weapon-only and the chooser never runs.
    /// </summary>
    public void SetCombatSpellCaster(CastCoordinator cast, Func<(int Ma, int MaxMa)> readMana)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(readMana);
        _cast = cast;
        _readMana = readMana;
    }

    /// <summary>True once <see cref="SetCombatSpellCaster"/> has wired both
    /// the coordinator and the mana reader. Gates every chooser call.</summary>
    private bool CombatSpellsWired => _cast is not null && _readMana is not null;

    /// <summary>
    /// Run the chooser for the freshly-picked target and, if it selects a
    /// spell, cast it. Returns <c>true</c> when a combat spell owns this
    /// round (so the caller suppresses the weapon swing) — including when
    /// the coordinator was on cooldown (another engine already spent this
    /// round's action; we retry on the next tick). Returns <c>false</c> for
    /// <see cref="CombatSpellAction.WeaponAttack"/> (incl. backstab-pending)
    /// so the caller falls through to the backstab / weapon path.
    /// </summary>
    private bool TryCastCombatSpell(
        CombatSettings settings, EngageableCandidate picked, int enemyCount,
        RoomEntitiesObservation obs)
    {
        if (!CombatSpellsWired) return false;

        (int ma, int maxMa) = _readMana!();
        var ctx = new CombatSpellContext(
            EnemyCount:      enemyCount,
            TargetRawName:   picked.RawName,
            Mana:            ma,
            MaxMana:         maxMa,
            BackstabPending: BackstabPending(settings, obs));

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action == CombatSpellAction.WeaponAttack) return false;

        // A combat spell owns this round. Enter spell mode for this target
        // so the tick heartbeat re-issues each round. Set the bridge even
        // when the coordinator is on cooldown — we stay in spell mode and
        // retry next tick rather than swinging the weapon.
        _castingSpellTarget = picked.RawName;
        if (_cast!.TryCast(decision.Spell!, picked.RawName))
        {
            _spellChooser.MarkCast(decision, picked.RawName);
            _combatOff = false;
        }
        return true;
    }

    /// <summary>
    /// Per-round heartbeat — wired to
    /// <see cref="TickEngine.CombatTickElapsed"/> in <c>AppServices</c>
    /// AFTER the coordinator's tick-reset and the <c>CastingDirector</c>'s
    /// survival casts. Only acts while in spell mode
    /// (<see cref="_castingSpellTarget"/> set); re-runs the chooser against
    /// the live room and either re-casts the chosen spell or, when the
    /// spell's conditions have lapsed (mana drained / cast cap hit / room
    /// thinned below MinEnemies), drops to the weapon command once (the
    /// server then auto-repeats and the heartbeat goes quiet).
    /// </summary>
    public void OnCombatTick()
    {
        if (_disposed) return;
        if (!CombatSpellsWired) return;
        if (!_isEnabled()) return;
        if (_combatOff) return;                         // round interrupted; resume path owns re-engage
        if (_castingSpellTarget is not { } target) return;   // weapon / idle mode — nothing to drive

        if (_classifier.Current is not { } obs)
        {
            _castingSpellTarget = null;
            return;
        }

        // Self-heal: the auto-attack target diverged from the spell target
        // (mob died → NoteMonsterDied cleared _currentTarget, or a re-pick
        // switched targets). Drop spell mode; the next observation
        // re-decides cleanly.
        if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase))
        {
            _castingSpellTarget = null;
            return;
        }

        // Target must still be in the room — guards against casting at a
        // mob that left without a death line (OnTargetNotHere would
        // otherwise burn the round before clearing it).
        if (!TargetPresent(obs, target))
        {
            _castingSpellTarget = null;
            return;
        }

        CombatSettings settings = _readSettings();
        (int ma, int maxMa) = _readMana!();
        var ctx = new CombatSpellContext(
            EnemyCount:      CountEngageable(obs),
            TargetRawName:   target,
            Mana:            ma,
            MaxMana:         maxMa,
            BackstabPending: BackstabPending(settings, obs));

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action == CombatSpellAction.WeaponAttack)
        {
            // Spell conditions lapsed mid-room — fall to the weapon command
            // once. SendWeaponAttack clears the bridge (via SendAttack), so
            // the heartbeat goes quiet and the server's auto-repeat takes over.
            bool useAlt = _normalWeaponFailedMonsters.Contains(ResolveSpeciesFromCurrentTarget());
            SendWeaponAttack(settings, target, useAlt);
            return;
        }

        if (_cast!.TryCast(decision.Spell!, target))
        {
            _spellChooser.MarkCast(decision, target);
            _combatOff = false;
        }
        // Blocked (CastDirector spent this round) → stay in spell mode and
        // retry next tick. No weapon fallback — the round's action is taken.
    }

    /// <summary>
    /// Count engageable monsters in the observation using the SAME filter
    /// as the candidate build in <see cref="OnEntitiesObserved"/>
    /// (Monster + known MonsterNumber + Enemy relationship) so the
    /// chooser's MinEnemies math matches the initial cast decision. Distinct
    /// from <see cref="HasEngageable"/>, which treats unknown-number
    /// monsters as engageable for its stale-room safety net.
    /// </summary>
    private int CountEngageable(RoomEntitiesObservation obs)
    {
        int count = 0;
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;
            MonsterOverlay overlay = ResolveOverlay(n);
            if ((overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy)
                count++;
        }
        return count;
    }

    private static bool TargetPresent(RoomEntitiesObservation obs, string rawTarget)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (string.Equals(e.RawName, rawTarget, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
