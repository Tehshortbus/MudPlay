using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Combat-spell round economy — the opt-in half of CombatManager that turns the
// pure CombatSpellChooser decisions into casts on the wire. Split out of
// CombatManager.cs to keep the weapon engine and the spell sequencing each in a
// file scoped to one responsibility.
//
// Wiring is optional: until SetCombatSpellCaster runs, the engine is pure
// weapon-attack and every existing path is unchanged. Once wired,
// OnEntitiesObserved consults the chooser before the backstab / weapon path, and
// the per-round heartbeat (OnCombatTick) re-issues the chosen cast each round —
// casts do NOT auto-repeat server-side the way weapon swings do, so the tick
// boundary is the only thing that keeps a multi-round spell going.
//
// Casts route through the shared CastCoordinator.TryCast so the one-cast-per-
// round cooldown is honoured across every casting engine (a survival heal from
// CastingDirector earlier in the same tick blocks our offensive cast — survival
// beats offense, by design of the AppServices tick-subscription order).
public sealed partial class CombatManager
{
    private readonly CombatSpellChooser _spellChooser = new();
    private CastCoordinator? _cast;
    private Func<(int Ma, int MaxMa)>? _readMana;
    private Func<bool>? _autoNukeGate;

    // Resolves a Spell.Number to its Short cast-code (the per-monster override
    // slots store a Number, but casts go out as the Short). Optional: until wired
    // no per-monster spell override is ever substituted.
    private Func<int, string?>? _spellShortByNumber;

    // ----- In-between debuff bridge (CastingDirector-driven) -----------
    // A debuff is an in-between action, not a combat action, so it casts
    // through the shared in-between window owned by CastingDirector rather
    // than the per-round combat-action path. The combat engine still owns
    // the DECISION (config + once-per-room / once-per-target bookkeeping);
    // it just answers "is there a debuff to fire?" when the director asks.
    // The pending stash defers the once-per-room/target MarkCast until the
    // coordinator confirms the cast actually went out (CommitInBetweenDebuff).

    private CombatSpellDecision? _pendingDebuff;
    private string? _pendingDebuffTarget;

    // ----- Deterministic magic eligibility (game-data gated) ----------
    // Optional, like the spell caster. Until SetMagicEligibility runs, the
    // weapon/spell gating fails open: any weapon hits and no spell is
    // level-blocked, so the chooser/weapon path behave exactly as before.

    private MonsterMagicIndex? _monsterMagic;
    private ItemMagicIndex? _itemMagic;
    private SpellReqLevelIndex? _spellReqLevel;
    private MonsterResistIndex? _monsterResist;
    private SpellAttackTypeIndex? _spellAttackType;

    // Opt into deterministic magic-eligibility gating. monsterMagic supplies each
    // monster's Magical / SpellImmu levels, itemMagic supplies each weapon's
    // HitMagic level, spellReqLevel supplies each spell's ReqLevel, and the pair
    // monsterResist / spellAttackType drive the elemental resist guard (a spell
    // whose damage element the target resists ≥ 100% is skipped pre-emptively).
    // Once wired, normal-vs-alternate weapon selection prefers whichever weapon can
    // actually hit the target (HitMagic ≥ Magical) and the chooser skips
    // single-target spells the target is level-immune to (ReqLevel < SpellImmu) or
    // resists elementally ≥ 100%. Until called, every gate fails open.
    public void SetMagicEligibility(
        MonsterMagicIndex monsterMagic, ItemMagicIndex itemMagic, SpellReqLevelIndex spellReqLevel,
        MonsterResistIndex monsterResist, SpellAttackTypeIndex spellAttackType)
    {
        ArgumentNullException.ThrowIfNull(monsterMagic);
        ArgumentNullException.ThrowIfNull(itemMagic);
        ArgumentNullException.ThrowIfNull(spellReqLevel);
        ArgumentNullException.ThrowIfNull(monsterResist);
        ArgumentNullException.ThrowIfNull(spellAttackType);
        _monsterMagic = monsterMagic;
        _itemMagic = itemMagic;
        _spellReqLevel = spellReqLevel;
        _monsterResist = monsterResist;
        _spellAttackType = spellAttackType;
    }

    // Room-scoped damage-immunity map — canonical species → single-target
    // attack-spell actions that produced a "Your spell has no effect on X." line
    // this room. The chooser reads it (via CombatSpellContext.ImmuneAttackSpells)
    // and skips the immune slot down the attack cascade. Only NormalAttackSpell /
    // AlternateAttackSpell are ever recorded — multi-attack room spells are never
    // gated (one immune mob doesn't mean the spell isn't damaging the rest of the
    // room) and debuffs aren't attack spells. Cleared on room-cleared.
    private readonly Dictionary<string, HashSet<CombatSpellAction>> _attackSpellImmuneSpecies =
        new(StringComparer.OrdinalIgnoreCase);

    // The action of the last successful cast this round. The "no effect" line
    // doesn't name which spell failed, so we attribute it to whatever we last
    // cast — but only mark it immune when it's a single-target attack spell (see
    // OnSpellNoEffect). Cleared by every weapon swing (via SendAttack) and on
    // room-cleared.
    private CombatSpellAction? _lastCastAction;

    // Opt into combat-spell casting. cast is the shared CastCoordinator (so the
    // per-round cooldown is shared with every other caster); readMana reports live
    // MA / max-MA for the chooser's per-cast mana gate. Until called the engine is
    // weapon-only and the chooser never runs.
    public void SetCombatSpellCaster(CastCoordinator cast, Func<(int Ma, int MaxMa)> readMana)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(readMana);
        _cast = cast;
        _readMana = readMana;
    }

    // Wire the Auto-Nuke auto-engine gate. When the predicate returns false, the
    // chooser never offers the multi-target attack spell or either debuff (the
    // single-target Normal / Alternate attack spells stay available — they aren't
    // nukes). Until called, nukes fail open (always allowed).
    public void SetAutoNukeGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _autoNukeGate = gate;
    }

    // Wire the Spell.Number → Short cast-code resolver used to substitute a
    // per-monster spell override (Monster overlay stores the override as a
    // Number; the chooser needs the Short to cast it). Until called, no override
    // is ever substituted and the global Combat-tab spell slots are used as-is.
    public void SetSpellShortResolver(Func<int, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _spellShortByNumber = resolver;
    }

    // True once SetCombatSpellCaster has wired both the coordinator and the mana
    // reader. Gates every chooser call.
    private bool CombatSpellsWired => _cast is not null && _readMana is not null;

    // Decide and dispatch this round's action for the freshly-picked target,
    // honouring the user-configured category order (Backstab / Debuffing / Spells
    // / Physical). The pure CombatSpellChooser owns the ordering; this maps its
    // decision onto the wire — a backstab verb, a combat-spell cast, or the
    // weapon attack command. Spell categories only participate when the caster is
    // wired (CombatSpellsWired); otherwise the chooser sees them as unavailable
    // and the order collapses to Backstab vs Physical, exactly the pre-spell
    // weapon engine.
    private void DispatchRoundAction(
        CombatSettings settings, EngageableCandidate picked, int enemyCount,
        RoomEntitiesObservation obs)
    {
        CombatSpellContext ctx = CombatSpellsWired
            ? BuildContext(settings, obs, picked.RawName, enemyCount, picked.MonsterNumber)
            : BuildWeaponContext(settings, obs, picked.RawName, enemyCount, picked.MonsterNumber);

        // Announce an active per-monster spell override once at target-pick time
        // (not on the per-round heartbeat) so a log read shows why the cast-code
        // differs from the Combat-tab slot for this species.
        if (ctx.OverrideAttackSpell is { } atkOverride)
            _log?.Combat(LogCategory,
                $"per-monster attack override {atkOverride} (#{picked.MonsterNumber}) — bypassing effectiveness gates");
        if (ctx.OverridePreAttackSpell is { } preOverride)
            _log?.Combat(LogCategory,
                $"per-monster pre-attack override {preOverride} (#{picked.MonsterNumber})");

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);

        switch (decision.Action)
        {
            case CombatSpellAction.WeaponAttack:
                // Pick the weapon that can actually hit: alternate when this
                // species already failed vs normal this room, OR when game
                // data says the normal weapon's HitMagic is below the
                // monster's Magical level but the alternate clears it.
                // SendWeaponAttack sets CurrentTarget.
                bool useAlt = ShouldUseAlternateWeapon(settings, picked.ResolvedName, picked.MonsterNumber);
                SendWeaponAttack(settings, picked.RawName, useAlt, picked.Priority);
                break;

            case CombatSpellAction.Backstab:
                // The BS weapon was pre-equipped at room-clear (OnRoomCleared);
                // when none is configured we backstab with whatever is equipped.
                SendAttack("bs", picked.RawName, picked.Priority);
                _currentTarget = picked.RawName;
                // Arm the surprise-round watch: the first of our combat-result
                // lines naming this species decides landed-vs-failed. Species
                // (unflavored) is the substring the combat line reliably carries.
                _awaitingBackstabResolution = true;
                _pendingBackstabSpecies = string.IsNullOrEmpty(picked.ResolvedName)
                    ? picked.RawName
                    : picked.ResolvedName;
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
                    // A cast is an attack for engage-verify purposes — if the
                    // server never confirms *Combat Engaged*, the spell hit a
                    // stale room view and the CR-reverify net must recover.
                    NoteAttackSent();
                }
                _currentTarget = picked.RawName;
                break;
        }

        // Any action taken here spends the room's surprise round — a re-engage
        // (interrupt resume, target re-pick) must not re-issue `bs` into a fight
        // that has already begun. PrepBackstabForMove re-opens it on the next
        // sneak-approach.
        _backstabOpenerConsumed = true;
    }

    // Per-round heartbeat — wired to TickEngine.CombatTickElapsed in AppServices
    // AFTER the coordinator's tick-reset and the CastingDirector's survival
    // casts. Only acts while in spell mode (_castingSpellTarget set); re-runs the
    // chooser against the live room and either re-casts the chosen spell or, when
    // the spell's conditions have lapsed (mana drained / cast cap hit / room
    // thinned below MinEnemies), drops to the weapon command once (the server
    // then auto-repeats and the heartbeat goes quiet).
    public void OnCombatTick()
    {
        if (_disposed) return;

        // Engage-verification runs on every tick regardless of spell
        // wiring — a pure-weapon build still needs the stale-room CR
        // recovery. Must precede the CombatSpellsWired gate below.
        VerifyEngagement();

        // Follow-deferral fallback: we held our own room-entry pick waiting for the
        // followed player's attack announce (ShouldWaitForFollow), but a full round
        // elapsed with no announce (the tick is the round heartbeat, driven by the
        // combat-damage lines). Per the spec, no announce → fall back to our own
        // game-data pick. Re-run the observation with the defer branch bypassed so
        // we make an independent choice this round. Guard on _currentTarget still
        // null — if a target got set meanwhile (announce landed, manual attack) the
        // hold already resolved.
        if (_awaitingFollowAnnounce
            && _isEnabled()
            && _currentTarget is null
            && _classifier.Current is { } followFallback)
        {
            _awaitingFollowAnnounce = false;
            _log?.Combat(LogCategory,
                "target-priority follow — no announce this round; falling back to own pick");
            _followDeferBypass = true;
            try { OnEntitiesObserved(followFallback); }
            finally { _followDeferBypass = false; }
            return;
        }

        // Deterministic interrupt-resume: the combat tick is the round
        // heartbeat, so this re-issues a weapon attack at most once per
        // round after an in-between cast (CastingDirector self-heal / buff)
        // turned it off. Unlike the OnCombatLine resume it doesn't depend on
        // the mob's attack line matching — a mob whose swing message we
        // don't parse would otherwise leave us idle for several rounds (the
        // "long pause before re-attack" symptom). Runs before the spell
        // gates because a pure-weapon build never wires the combat-spell
        // caster. Weapon mode only (_castingSpellTarget null): in spell mode
        // the heartbeat below owns the re-cast. Skipped after a kill
        // (_currentTarget cleared by the death watcher) so we never swing at
        // a corpse. TryResumeEngage's pacing prevents a double-fire with the
        // OnCombatLine resume in the same round.
        if (_isEnabled()
            && _combatOff
            && _castingSpellTarget is null
            && _currentTarget is not null
            && _classifier.Current is { } resume
            && HasEngageable(resume))
        {
            TryResumeEngage(resume);
            return;
        }

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
        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), ResolveMonsterNumber(obs, target));

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action is CombatSpellAction.WeaponAttack or CombatSpellAction.Backstab)
        {
            // Spell conditions lapsed mid-room — fall to the weapon command
            // once. SendWeaponAttack clears the bridge (via SendAttack), so
            // the heartbeat goes quiet and the server's auto-repeat takes over.
            // (Backstab can't occur mid-combat — sneak is already broken — but
            // we treat it as the weapon fallback defensively.)
            bool useAlt = ShouldUseAlternateWeapon(
                settings, ResolveSpeciesFromCurrentTarget(), ResolveMonsterNumber(obs, target));
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

    // Answer the CastingDirector's "is there a debuff to fire this in-between
    // window?" query. The combat engine owns the decision — the chooser's
    // ChooseDebuff applies the Combat-tab config and the once-per-room /
    // once-per-target gating — but the cast itself rides the shared in-between
    // window so it competes with (and loses to) survival heals by the director's
    // priority order. Returns the cast code + target (null target ⇒ area/multi
    // debuff) when a debuff is due, else null. Stashes the decision so
    // CommitInBetweenDebuff can mark it cast only after the coordinator confirms
    // it went out. No-ops (returns null) until the caster is wired, the engine is
    // enabled, and a live target is present.
    public (string Spell, string? Target)? PickInBetweenDebuff()
    {
        if (_disposed) return null;
        if (!CombatSpellsWired || !_isEnabled()) return null;
        if (_currentTarget is not { } target) return null;
        if (_classifier.Current is not { } obs) return null;
        if (!TargetPresent(obs, target)) return null;

        CombatSettings settings = _readSettings();
        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), ResolveMonsterNumber(obs, target));
        if (_spellChooser.ChooseDebuff(settings, ctx) is not { } decision) return null;

        // The target name is appended to every combat-spell cast — matching
        // the weapon engine's historical convention (area/multi room spells
        // included). The combat action verb is re-sent after the in-between
        // cast by the existing combat-off resume path.
        _pendingDebuff = decision;
        _pendingDebuffTarget = target;
        return (decision.Spell!, target);
    }

    // Confirm the in-between debuff the director just sent. Marks the stashed
    // decision cast (advancing the once-per-room / once-per-target bookkeeping so
    // it won't re-fire) and clears the stash. Called only on a successful
    // coordinator cast — a blocked cast leaves the stash so the next window
    // retries. No-ops when nothing is pending.
    public void CommitInBetweenDebuff()
    {
        if (_pendingDebuff is not { } decision) return;
        _spellChooser.MarkCast(decision, _pendingDebuffTarget ?? string.Empty);
        _pendingDebuff = null;
        _pendingDebuffTarget = null;
    }

    // Count engageable monsters in the observation using the SAME filter as the
    // candidate build in OnEntitiesObserved (Monster + known MonsterNumber +
    // Enemy relationship) so the chooser's MinEnemies math matches the initial
    // cast decision. Distinct from HasEngageable, which treats unknown-number
    // monsters as engageable for its stale-room safety net.
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

    // Build the per-round chooser context for target, reading live mana and
    // folding in any room-scoped attack-spell immunity for that target's species.
    // Shared by the initial cast decision (DispatchRoundAction, passed enemyCount
    // from the candidate build) and the per-round heartbeat (OnCombatTick,
    // counting the live observation).
    private CombatSpellContext BuildContext(
        CombatSettings settings, RoomEntitiesObservation obs, string target,
        int enemyCount, int monsterNumber)
    {
        (int ma, int maxMa) = _readMana!();
        (string? attackOverride, int? attackCap) = AttackOverrideFor(monsterNumber);
        (string? preAttackOverride, int? preAttackCap) = PreAttackOverrideFor(monsterNumber);
        return new CombatSpellContext(
            EnemyCount:          enemyCount,
            TargetRawName:       target,
            Mana:                ma,
            MaxMana:             maxMa,
            BackstabPending:     BackstabPending(settings, obs),
            ImmuneAttackSpells:  ImmuneActionsFor(target),
            SpellsAvailable:     true,
            LevelBlockedActions: LevelBlockedFor(settings, monsterNumber),
            AllowNukes:          _autoNukeGate?.Invoke() ?? true,
            ResistBlockedActions: ResistBlockedFor(settings, monsterNumber),
            TargetDontBackstab:  IsDontBackstab(monsterNumber),
            OverrideAttackSpell:       attackOverride,
            OverrideAttackMaxCasts:    attackCap,
            OverridePreAttackSpell:    preAttackOverride,
            OverridePreAttackMaxCasts: preAttackCap);
    }

    // The current target's per-monster DontBackstab overlay flag — the backstab
    // opener must skip a flagged target and open with a normal attack instead.
    // Fail-safe false for an unknown monster number (nothing to resolve).
    private bool IsDontBackstab(int monsterNumber) =>
        monsterNumber >= 0 && (ResolveOverlay(monsterNumber).DontBackstab ?? false);

    // Resolve this monster's override attack spell to a (cast-code, cap) pair, or
    // (null, null) when there's no active override. Delegates to the shared
    // resolver — see ResolveSpellOverride for the "active" conditions.
    private (string? Spell, int? Cap) AttackOverrideFor(int monsterNumber)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return ResolveSpellOverride(overlay.OverrideAttackSpellId, overlay.OverrideAttackCount);
    }

    // Resolve this monster's override pre-attack spell to a (cast-code, cap) pair,
    // or (null, null) when there's no active override.
    private (string? Spell, int? Cap) PreAttackOverrideFor(int monsterNumber)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return ResolveSpellOverride(overlay.OverridePreAttackSpellId, overlay.OverridePreAttackCount);
    }

    // Turn a per-monster override slot (Spell.Number + configured cast count) into
    // the Short cast-code and per-room cap the chooser needs, or (null, null) when
    // the override is inactive. An override is active only when it's fully
    // configured: the resolver is wired, a positive Spell.Number is set, the count
    // is a positive cap (a null/zero count means "not really configured" — the
    // overlay documents null = 0 — so we fall back to the global slot), and the
    // number maps to a real Short cast-code (unknown number → fall back).
    private (string? Spell, int? Cap) ResolveSpellOverride(int? spellId, int? count)
    {
        if (_spellShortByNumber is null) return (null, null);
        if (spellId is not { } number || number <= 0) return (null, null);
        int cap = count ?? 0;
        if (cap <= 0) return (null, null);
        string? code = _spellShortByNumber(number);
        return string.IsNullOrWhiteSpace(code) ? (null, null) : (code, cap);
    }

    // Choose the alternate weapon when (a) this species already produced a "no
    // effect" line vs the normal weapon this room, OR (b) game data says the
    // normal weapon can't hit this monster but the alternate can. The magic check
    // is deterministic: a weapon hits iff its HitMagic ≥ the monster's Magical
    // level. Fails open — no swap — when the eligibility indexes aren't wired, the
    // monster has no Magical level, the normal weapon is unknown, or the
    // alternate can't hit either.
    private bool ShouldUseAlternateWeapon(
        CombatSettings settings, string resolvedSpecies, int monsterNumber)
    {
        if (_normalWeaponFailedMonsters.Contains(resolvedSpecies)) return true;
        if (_monsterMagic is null || _itemMagic is null) return false;

        int magical = _monsterMagic.MagicalLevel(monsterNumber);
        if (magical <= 0) return false;                 // any weapon hits

        int normalHit = _itemMagic.HitMagic(settings.NormalWeapon);
        if (normalHit < 0) return false;                // unknown normal → don't second-guess
        if (normalHit >= magical) return false;         // normal already hits → keep it

        // Normal can't hit. Swap only if the alternate actually clears the bar.
        return _itemMagic.HitMagic(settings.AlternateWeapon) >= magical;
    }

    // The single-target spell actions the monster's SpellImmu level
    // deterministically blocks: any configured single-target debuff / attack
    // spell whose ReqLevel is below the immunity is unusable on this target. Area
    // / multi room spells are never level-blocked here — they hit the whole room,
    // so one immune occupant doesn't disqualify them (mirrors the
    // observed-immunity carve-out for multi-attack). Returns null (nothing
    // blocked) when the indexes aren't wired or the monster has no immunity.
    private IReadOnlySet<CombatSpellAction>? LevelBlockedFor(
        CombatSettings settings, int monsterNumber)
    {
        if (_monsterMagic is null || _spellReqLevel is null) return null;
        int immu = _monsterMagic.SpellImmunity(monsterNumber);
        if (immu <= 0) return null;                     // any spell allowed

        HashSet<CombatSpellAction>? blocked = null;
        void Check(CombatSpellSlot slot, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(slot.SpellName)) return;
            int req = _spellReqLevel.ReqLevel(slot.SpellName);
            if (req < 0) return;                        // unknown spell → fail open
            if (req >= immu) return;                    // eligible
            (blocked ??= new HashSet<CombatSpellAction>()).Add(action);
        }
        Check(settings.SingleTargetDebuffSpell, CombatSpellAction.SingleDebuff);
        Check(settings.NormalAttackSpell, CombatSpellAction.NormalAttackSpell);
        Check(settings.AlternateAttackSpell, CombatSpellAction.AlternateAttackSpell);
        return blocked;
    }

    // The single-target attack-spell actions the monster's elemental resistance
    // deterministically neutralizes: a configured Normal / Alternate attack spell
    // whose damage element the target resists ≥ 100% deals 0 damage (100%) or
    // *heals* it (> 100%), so it's skipped down the cascade (primary → alternate →
    // weapon). Only *elemental* spells qualify — Magic Resist (AttType 4) and
    // poison (AttType 6) are not deterministic, so their spells are never
    // pre-empted here. Debuffs and multi/area room spells are never resist-blocked.
    // A negative or 1–99% resist does not block — the spell still lands (bonus or
    // reduced) damage. Returns null (nothing blocked) when the indexes aren't wired
    // or no configured attack spell hits a ≥ 100% wall.
    private IReadOnlySet<CombatSpellAction>? ResistBlockedFor(
        CombatSettings settings, int monsterNumber)
    {
        if (_monsterResist is null || _spellAttackType is null) return null;

        HashSet<CombatSpellAction>? blocked = null;
        void Check(CombatSpellSlot slot, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(slot.SpellName)) return;
            int attType = _spellAttackType.AttackType(slot.SpellName);
            if (attType < 0) return;                                       // unknown spell → fail open
            int code = MonsterResistIndex.ElementalResistCode(attType);
            if (code < 0) return;                                          // non-elemental (M.R./poison)
            if (_monsterResist.ResistPercent(monsterNumber, code) < 100) return;  // still takes damage
            (blocked ??= new HashSet<CombatSpellAction>()).Add(action);
        }
        Check(settings.NormalAttackSpell, CombatSpellAction.NormalAttackSpell);
        Check(settings.AlternateAttackSpell, CombatSpellAction.AlternateAttackSpell);
        return blocked;
    }

    // Deterministic actionability gate: can we kill this monster at all? True
    // (engageable) unless game data proves we can neither hit it physically nor
    // land an attack spell on it — i.e. both weapons' HitMagic are below the
    // monster's Magical level AND every configured attack spell is level-blocked
    // by its SpellImmu. Fail-open at every unknown (indexes unwired, monster has
    // no Magical level, a weapon or spell is unknown) so a thin data set never
    // makes us skip a monster we could actually fight. Consumed by
    // OnEntitiesObserved (retarget / move-past) and, via the delegate AppServices
    // injects, by CombatStateTracker (walker-gate release).
    public bool CanEngageMonster(int monsterNumber) =>
        UnengageableReason(_readSettings(), monsterNumber) is null;

    // The reason we can't kill monsterNumber, or null when it's actionable.
    // Physical eligibility is checked first (deterministic, fails open); only when
    // both weapons are proven unable to hit do we consult the attack-spell slots.
    // An attack spell counts as a kill means when it's configured and either the
    // monster has no spell immunity or the spell's ReqLevel clears it. Area /
    // single-target debuffs are NOT kill means and never count here.
    private string? UnengageableReason(CombatSettings settings, int monsterNumber)
    {
        if (_monsterMagic is null || _itemMagic is null) return null;   // unwired → fail open
        if (monsterNumber < 0) return null;                             // unknown monster → fail open

        int magical = _monsterMagic.MagicalLevel(monsterNumber);
        if (magical <= 0) return null;                                  // any weapon hits

        int normalHit = _itemMagic.HitMagic(settings.NormalWeapon);
        if (normalHit < 0) return null;                                 // unknown normal weapon → fail open
        if (normalHit >= magical) return null;                          // normal hits

        int altHit = _itemMagic.HitMagic(settings.AlternateWeapon);
        if (altHit < 0) return null;                                    // unknown alt weapon → fail open
        if (altHit >= magical) return null;                             // alt hits

        // Neither weapon can hit. The monster is actionable only if some
        // configured attack spell can still land on it.
        if (_spellReqLevel is null) return null;                        // can't prove spell-blocked → fail open
        int immu = _monsterMagic.SpellImmunity(monsterNumber);
        if (AttackSpellCanLand(settings.NormalAttackSpell, immu)) return null;
        if (AttackSpellCanLand(settings.AlternateAttackSpell, immu)) return null;
        if (AttackSpellCanLand(settings.MultiAttackSpell, immu)) return null;

        return $"weapons HitMagic<{magical} (normal={normalHit} alt={altHit}) " +
               $"and no eligible attack spell (SpellImmu={immu})";
    }

    // True when slot holds a configured attack spell that can land on a monster
    // with spell-immunity immu: unconfigured slots are not a kill means; an
    // unknown spell fails open (assume it works); otherwise the spell lands iff
    // its ReqLevel is ≥ the immunity. Assumes _spellReqLevel is wired (the only
    // caller checks first).
    private bool AttackSpellCanLand(CombatSpellSlot slot, int immu)
    {
        if (string.IsNullOrWhiteSpace(slot.SpellName)) return false;    // unconfigured → not a kill means
        int req = _spellReqLevel!.ReqLevel(slot.SpellName);
        if (req < 0) return true;                                       // unknown spell → fail open
        if (immu <= 0) return true;                                     // no immunity → any spell lands
        return req >= immu;                                             // eligible iff ReqLevel ≥ SpellImmu
    }

    // Resolve the MonsterNumber of the entity matching rawName in obs, or -1 when
    // no match carries a number — which the eligibility helpers treat as "no
    // data, fail open".
    private static int ResolveMonsterNumber(RoomEntitiesObservation obs, string rawName)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (!string.Equals(e.RawName, rawName, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.MonsterNumber is int n) return n;
        }
        return -1;
    }

    // Build a spell-free chooser context for the weapon engine when no
    // combat-spell caster is wired. Reports SpellsAvailable false so the chooser
    // skips the Debuffing / Spells categories and the order collapses to Backstab
    // vs Physical — and reads no mana (none is available without the wired
    // reader).
    private CombatSpellContext BuildWeaponContext(
        CombatSettings settings, RoomEntitiesObservation obs, string target,
        int enemyCount, int monsterNumber) =>
        new(EnemyCount:      enemyCount,
            TargetRawName:   target,
            Mana:            0,
            MaxMana:         0,
            BackstabPending: BackstabPending(settings, obs),
            ImmuneAttackSpells: null,
            SpellsAvailable: false,
            TargetDontBackstab: IsDontBackstab(monsterNumber));

    // The single-target attack-spell actions the species of target has proven
    // immune to this room, or null when nothing is immune (the common case).
    private IReadOnlySet<CombatSpellAction>? ImmuneActionsFor(string target)
    {
        string species = ResolveSpeciesByName(target);
        if (string.IsNullOrEmpty(species)) return null;
        return _attackSpellImmuneSpecies.TryGetValue(species, out HashSet<CombatSpellAction>? set)
            ? set
            : null;
    }

    // "Your spell has no effect on X." — the attack spell we just cast can't hurt
    // that species (priest `harm` vs an acid slime, etc.). Mark the last-cast
    // action immune for the species so the chooser skips it down the attack
    // cascade (primary → alternate → weapon) for the rest of the room, then
    // re-decide this round. Only single-target attack spells fall back:
    // multi-attack room spells are never gated (one immune mob doesn't mean the
    // spell isn't damaging the rest of the room) and debuffs aren't attack
    // spells, so a no-effect line that follows one of those is ignored here.
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

    // After recording an immunity, re-run the chooser against the live room for
    // the current spell target. If the cascade has reached the weapon command,
    // swing immediately so the round isn't burned idle. If it still resolves to a
    // spell (primary immune → alternate), stay in spell mode and let the
    // heartbeat issue it next tick — the cast cooldown blocks an immediate
    // re-cast this round. Clears _lastCastAction so a second no-effect line this
    // round can't double-mark.
    private void ReDecideAfterImmunity(CombatSettings settings)
    {
        if (_castingSpellTarget is not { } target) return;
        if (_classifier.Current is not { } obs) return;
        if (!TargetPresent(obs, target)) return;

        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), ResolveMonsterNumber(obs, target));
        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);
        if (decision.Action is CombatSpellAction.WeaponAttack or CombatSpellAction.Backstab)
        {
            bool useAlt = ShouldUseAlternateWeapon(
                settings, ResolveSpeciesByName(target), ResolveMonsterNumber(obs, target));
            SendWeaponAttack(settings, target, useAlt);
            return;
        }
        _lastCastAction = null;
    }
}
