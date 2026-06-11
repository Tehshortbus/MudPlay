using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

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
    /// Room-scoped damage-immunity map (CS-c) — canonical species →
    /// single-target attack-spell actions that produced a
    /// "Your spell has no effect on X." line this room. The chooser reads
    /// it (via <see cref="CombatSpellContext.ImmuneAttackSpells"/>) and
    /// skips the immune slot down the attack cascade. Only
    /// <see cref="CombatSpellAction.NormalAttackSpell"/> /
    /// <see cref="CombatSpellAction.AlternateAttackSpell"/> are ever
    /// recorded — multi-attack room spells are never gated (one immune
    /// mob doesn't mean the spell isn't damaging the rest of the room) and
    /// debuffs aren't attack spells. Cleared on room-cleared. MudProxy
    /// <c>CombatManager</c> immune-set design.
    /// </summary>
    private readonly Dictionary<string, HashSet<CombatSpellAction>> _attackSpellImmuneSpecies =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The action of the last successful cast this round. The
    /// "no effect" line doesn't name which spell failed, so we attribute
    /// it to whatever we last cast — but only mark it immune when it's a
    /// single-target attack spell (see <see cref="OnSpellNoEffect"/>).
    /// Cleared by every weapon swing (via <see cref="SendAttack"/>) and on
    /// room-cleared.
    /// </summary>
    private CombatSpellAction? _lastCastAction;

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
    /// Decide and dispatch this round's action for the freshly-picked
    /// target, honouring the user-configured category order (Backstab /
    /// Preattack / Spells / Physical). The pure <see cref="CombatSpellChooser"/>
    /// owns the ordering; this maps its decision onto the wire — a backstab
    /// verb, a combat-spell cast, or the weapon attack command. Spell
    /// categories only participate when the caster is wired
    /// (<see cref="CombatSpellsWired"/>); otherwise the chooser sees them as
    /// unavailable and the order collapses to Backstab vs Physical, exactly
    /// the pre-spell weapon engine.
    /// </summary>
    private void DispatchRoundAction(
        CombatSettings settings, EngageableCandidate picked, int enemyCount,
        RoomEntitiesObservation obs)
    {
        CombatSpellContext ctx = CombatSpellsWired
            ? BuildContext(settings, obs, picked.RawName, enemyCount)
            : BuildWeaponContext(settings, obs, picked.RawName, enemyCount);

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);

        switch (decision.Action)
        {
            case CombatSpellAction.WeaponAttack:
                // Per-target dedup — if the picked species is in this room's
                // failed-vs-normal set, pre-emptively swap to the alternate
                // weapon. SendWeaponAttack sets CurrentTarget.
                bool useAlt = _normalWeaponFailedMonsters.Contains(picked.ResolvedName);
                SendWeaponAttack(settings, picked.RawName, useAlt, picked.Priority);
                break;

            case CombatSpellAction.Backstab:
                // The BS weapon was pre-equipped at room-clear (OnRoomCleared);
                // when none is configured we backstab with whatever is equipped.
                SendAttack("bs", picked.RawName, picked.Priority);
                _currentTarget = picked.RawName;
                break;

            default:
                // A combat spell owns this round (a spell IS the round's
                // action — it does not stack with a swing). Enter spell mode
                // so the tick heartbeat re-issues each round. Set the bridge
                // even when the coordinator is on cooldown — we stay in spell
                // mode and retry next tick rather than swinging the weapon.
                _castingSpellTarget = picked.RawName;
                if (_cast!.TryCast(decision.Spell!, picked.RawName))
                {
                    _spellChooser.MarkCast(decision, picked.RawName);
                    _lastCastAction = decision.Action;
                    _combatOff = false;
                }
                _currentTarget = picked.RawName;
                break;
        }
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
        CombatSpellContext ctx = BuildContext(settings, obs, target, CountEngageable(obs));

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action is CombatSpellAction.WeaponAttack or CombatSpellAction.Backstab)
        {
            // Spell conditions lapsed mid-room — fall to the weapon command
            // once. SendWeaponAttack clears the bridge (via SendAttack), so
            // the heartbeat goes quiet and the server's auto-repeat takes over.
            // (Backstab can't occur mid-combat — sneak is already broken — but
            // we treat it as the weapon fallback defensively.)
            bool useAlt = _normalWeaponFailedMonsters.Contains(ResolveSpeciesFromCurrentTarget());
            SendWeaponAttack(settings, target, useAlt);
            return;
        }

        if (_cast!.TryCast(decision.Spell!, target))
        {
            _spellChooser.MarkCast(decision, target);
            _lastCastAction = decision.Action;
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

    /// <summary>
    /// Build the per-round chooser context for <paramref name="target"/>,
    /// reading live mana and folding in any room-scoped attack-spell
    /// immunity (CS-c) for that target's species. Shared by the initial
    /// cast decision (<see cref="TryCastCombatSpell"/>, passed
    /// <paramref name="enemyCount"/> from the candidate build) and the
    /// per-round heartbeat (<see cref="OnCombatTick"/>, counting the live
    /// observation).
    /// </summary>
    private CombatSpellContext BuildContext(
        CombatSettings settings, RoomEntitiesObservation obs, string target, int enemyCount)
    {
        (int ma, int maxMa) = _readMana!();
        return new CombatSpellContext(
            EnemyCount:         enemyCount,
            TargetRawName:      target,
            Mana:               ma,
            MaxMana:            maxMa,
            BackstabPending:    BackstabPending(settings, obs),
            ImmuneAttackSpells: ImmuneActionsFor(target),
            SpellsAvailable:    true);
    }

    /// <summary>
    /// Build a spell-free chooser context for the weapon engine when no
    /// combat-spell caster is wired. Reports <see cref="CombatSpellContext.SpellsAvailable"/>
    /// false so the chooser skips the Preattack / Spells categories and the
    /// order collapses to Backstab vs Physical — and reads no mana (none is
    /// available without the wired reader).
    /// </summary>
    private CombatSpellContext BuildWeaponContext(
        CombatSettings settings, RoomEntitiesObservation obs, string target, int enemyCount) =>
        new(EnemyCount:      enemyCount,
            TargetRawName:   target,
            Mana:            0,
            MaxMana:         0,
            BackstabPending: BackstabPending(settings, obs),
            ImmuneAttackSpells: null,
            SpellsAvailable: false);

    /// <summary>The single-target attack-spell actions the species of
    /// <paramref name="target"/> has proven immune to this room, or
    /// <c>null</c> when nothing is immune (the common case).</summary>
    private IReadOnlySet<CombatSpellAction>? ImmuneActionsFor(string target)
    {
        string species = ResolveSpeciesByName(target);
        if (string.IsNullOrEmpty(species)) return null;
        return _attackSpellImmuneSpecies.TryGetValue(species, out HashSet<CombatSpellAction>? set)
            ? set
            : null;
    }

    /// <summary>
    /// "Your spell has no effect on X." — the attack spell we just cast
    /// can't hurt that species (priest <c>harm</c> vs an acid slime, etc.).
    /// Mark the last-cast action immune for the species so the chooser
    /// skips it down the attack cascade (primary → alternate → weapon) for
    /// the rest of the room, then re-decide this round. Only single-target
    /// attack spells fall back: multi-attack room spells are never gated
    /// (one immune mob doesn't mean the spell isn't damaging the rest of
    /// the room) and debuffs aren't attack spells, so a no-effect line that
    /// follows one of those is ignored here.
    /// </summary>
    private void OnSpellNoEffect(MatchResult match)
    {
        if (!CombatSpellsWired || !_isEnabled()) return;
        if (_lastCastAction is not (CombatSpellAction.NormalAttackSpell
                                  or CombatSpellAction.AlternateAttackSpell))
            return;

        string species = match.Groups.Count > 0
            ? ResolveSpeciesByName(match.Groups[0].Trim())
            : ResolveSpeciesFromCurrentTarget();
        if (string.IsNullOrEmpty(species)) return;

        if (!_attackSpellImmuneSpecies.TryGetValue(species, out HashSet<CombatSpellAction>? set))
        {
            set = new HashSet<CombatSpellAction>();
            _attackSpellImmuneSpecies[species] = set;
        }
        if (set.Add(_lastCastAction.Value))
            _log?.Info(LogCategory,
                $"spell-no-effect species={species} action={_lastCastAction.Value} — marking immune");

        ReDecideAfterImmunity(_readSettings());
    }

    /// <summary>
    /// After recording an immunity, re-run the chooser against the live
    /// room for the current spell target. If the cascade has reached the
    /// weapon command, swing immediately (MudProxy's immediate-melee
    /// fallback) so the round isn't burned idle. If it still resolves to a
    /// spell (primary immune → alternate), stay in spell mode and let the
    /// heartbeat issue it next tick — the cast cooldown blocks an immediate
    /// re-cast this round. Clears <see cref="_lastCastAction"/> so a second
    /// no-effect line this round can't double-mark.
    /// </summary>
    private void ReDecideAfterImmunity(CombatSettings settings)
    {
        if (_castingSpellTarget is not { } target) return;
        if (_classifier.Current is not { } obs) return;
        if (!TargetPresent(obs, target)) return;

        CombatSpellContext ctx = BuildContext(settings, obs, target, CountEngageable(obs));
        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action is CombatSpellAction.WeaponAttack or CombatSpellAction.Backstab)
        {
            bool useAlt = _normalWeaponFailedMonsters.Contains(ResolveSpeciesByName(target));
            SendWeaponAttack(settings, target, useAlt);
            return;
        }
        _lastCastAction = null;
    }
}
