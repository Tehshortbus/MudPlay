using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Combat-spell round economy — the opt-in half of CombatManager that turns the
// pure CombatSpellChooser decisions into casts on the wire. Split out of
// CombatManager.cs to keep the weapon engine and the spell sequencing each in a
// file scoped to one responsibility.
//
// Wiring is optional: until SetCombatSpellCaster runs, the engine is pure
// weapon-attack and every existing path is unchanged. Once wired,
// OnEntitiesObserved consults the chooser before the backstab / weapon path.
//
// CONFIRMED game mechanic: an announced spell attack AUTO-REPEATS server-side every
// round — it fires next tick while the target is present and mana suffices, and does
// NOT need re-announcing, exactly like a weapon swing. So this engine mirrors the
// weapon engine: it ANNOUNCES the chosen attack once (DispatchRoundAction) and the
// per-round heartbeat (OnCombatTick) re-announces ONLY when the chooser's decision
// must change (target died, MaxCasts elapsed, mana below the slot floor, immunity,
// room thinned below MinEnemies). Re-announcing a spell the server is already
// repeating was the double-cast / corpse-cast bug — hence _announcedSpellCode, the
// spell analogue of "we already sent `attack X`; the server owns the repeat."
//
// Casts route through the shared CastCoordinator.TryCast so the one-cast-per-round
// cooldown is honoured across every casting engine (a survival heal from
// CastingDirector blocks our offensive cast — survival beats offense). The INITIAL
// engage / a decision switch bypasses that round cooldown (TryCast bypassRoundCooldown)
// so a fresh mob is announced-at instantly, mirroring the weapon's cooldown-free send.
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
    // Reverse of the above: a cast-code → its Spells.Number, or null when the text
    // isn't a known spell. Lets the forced-command path tell a mana-costing spell
    // cast-code (a legacy override saved as a command) from a free verb ("bash").
    private Func<string, int?>? _spellNumberByShort;

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

    // Optional bridge to the CastingDirector's in-between window (Evaluate). Used
    // by TryPreAttackInBetween to let a due survival cast — or, failing that, the
    // configured debuff — fire BEFORE the attack on engage, honouring the
    // Spells+Ailments priority. Returns the spell cast this pass, or null. Until
    // wired, the pre-attack pass no-ops and engage dispatches the attack as before.
    private Func<string?>? _inBetweenEvaluator;

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

    // True once we've acted on an attack-immunity ("no effect") this round. The
    // attack fires several times per round, so an immune target draws a BURST of
    // "no effect" lines; we mark + re-decide on the first and ignore the rest of
    // the burst, else the fallback we just switched to is itself mis-marked immune
    // (or the command path concedes) by a leftover line meant for the primary.
    // Reset each round at the top of OnCombatTick.
    private bool _immunityHandledThisRound;

    // The attack-spell cast-code the server is currently auto-repeating for us (with
    // _castingSpellTarget as its target). CONFIRMED game mechanic: an announced spell
    // attack auto-repeats server-side every round — it fires next tick while the
    // target is present and mana suffices, and does NOT need re-announcing, exactly
    // like a weapon swing. So we announce ONCE and re-announce ONLY when the chooser's
    // decision changes (target died / MaxCasts elapsed / mana below the slot floor /
    // immunity / room thinned below MinEnemies). Null ⇒ no spell announced (weapon or
    // idle mode). This is the spell analogue of "we already sent `attack X`; the
    // server owns the repeat." Re-announcing a spell the server is already repeating
    // is the double-cast / corpse-cast bug this rework removes.
    private string? _announcedSpellCode;

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
    // The optional reverse (cast-code → Number) lets the forced-command path
    // recognise a mana-costing spell code so it can stand down at 0 mana.
    public void SetSpellShortResolver(Func<int, string?> resolver, Func<string, int?>? reverse = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _spellShortByNumber = resolver;
        _spellNumberByShort = reverse;
    }

    // Wire the in-between evaluator (CastingDirector.Evaluate) that
    // TryPreAttackInBetween runs to fire a due survival/debuff cast before the
    // attack on engage. Optional — the pre-attack pass no-ops until set.
    public void SetInBetweenEvaluator(Func<string?> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _inBetweenEvaluator = evaluate;
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
        RoomEntitiesObservation obs, bool bypassRecastInterval = false)
    {
        // A change of combat target resets the per-target single-target cast caps —
        // MaxCasts is per-target for those slots (the AoE slots stay per-room) — and
        // restarts the alternating-order phase so each new fight opens on the mode's
        // first phase. A same-target re-announce (a mid-fight decision switch, incl.
        // the per-round alternation heartbeat) doesn't reset.
        if (!string.Equals(_currentTarget, picked.RawName, StringComparison.OrdinalIgnoreCase))
        {
            _spellChooser.ResetForNewTarget();
            _alternationRound = 0;
            _lastAlternationAdvanceAt = DateTimeOffset.MinValue;
        }

        // A per-monster forced attack COMMAND wins over the entire normal flow
        // (spell chooser + weapon pick): send it verbatim and let the server
        // auto-repeat it like any attack command. Announced once per target so a
        // log read shows why this species diverges from the Combat-tab commands.
        // It's trusted — no effectiveness-gate fallback second-guesses it (the
        // user hand-picked it), mirroring the spell-id override's bypass contract.
        if (AttackCommandOverrideFor(picked.MonsterNumber) is { } forcedCommand)
        {
            // A forced command that is actually a spell cast-code (a legacy override
            // saved as a raw command before cast-codes auto-routed to the spell
            // rung — see MonsterEditDialogViewModel.ParseAttackOverride) costs mana.
            // At 0 mana the server silently no-ops it with no error line, so re-
            // sending it every round just leaves the player standing there getting
            // hit until a regen tick (report paradigm-20260813-064159). Fall back to
            // the physical weapon; the next round re-evaluates so it resumes when
            // mana ticks back. A free verb ("bash", "kick") isn't a spell code, so it
            // still fires at 0 mana — it costs no MA.
            if (_readMana is not null && _readMana().Ma <= 0
                && _spellNumberByShort?.Invoke(forcedCommand) is not null)
            {
                bool useAlt = ShouldUseAlternateWeapon(settings, picked.ResolvedName, picked.MonsterNumber);
                SendWeaponAttack(settings, picked.RawName, useAlt, picked.Priority);
                _currentTarget = picked.RawName;
                _backstabOpenerConsumed = true;
                return;
            }
            bool newTarget = !string.Equals(_currentTarget, picked.RawName, StringComparison.OrdinalIgnoreCase);
            if (newTarget)
                _log?.Combat(LogCategory,
                    $"per-monster attack-command override '{forcedCommand}' (#{picked.MonsterNumber}) — forcing over normal flow");
            SendAttack(forcedCommand, picked.RawName, picked.Priority);
            _currentTarget = picked.RawName;
            _backstabOpenerConsumed = true;
            return;
        }

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

        // A fresh dispatch supersedes any previously announced spell — clear it so a
        // weapon/backstab decision, or a spell whose announce is blocked, is never
        // mistaken by the heartbeat for "the server is still repeating it". A
        // successful spell announce sets it again in the default case below.
        _announcedSpellCode = null;

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
                // A combat spell owns the round. Announce it ONCE and instantly: the
                // engage bypasses the shared 5.5s round cooldown (mirroring a weapon's
                // cooldown-free SendAttack), so a fresh mob is cast at immediately
                // rather than after it swings. bypassRecastInterval (set ONLY by the
                // interrupt-resume caller) additionally skips the 500ms burst guard so a
                // re-attack fired the instant a mid-fight self-buff's *Combat Off* lands
                // isn't deferred a whole round — the mob's free swing. Ordinary dispatch
                // (engage / arrival / tick) leaves it false so the guard still absorbs a
                // burst of dispatches in the same frame. The server then auto-repeats the
                // spell each round; the heartbeat re-announces only if the chooser's
                // decision later changes. Set the bridge even when the cast is blocked so
                // we stay in spell mode and retry next tick rather than swinging.
                _castingSpellTarget = picked.RawName;
                // Room-wide multi-attack spells (e.g. dancing blades) hit every enemy
                // and MUST be cast bare — `blad`, never `blad <mob>` (the server treats
                // the targeted form as an unknown command). Single-target attack/debuff
                // spells keep their mob; _castingSpellTarget still tracks the round's mob.
                string? castTarget = decision.Action == CombatSpellAction.MultiAttack
                    ? null : picked.RawName;
                if (_cast!.TryCast(decision.Spell!, castTarget, bypassRoundCooldown: true, bypassRecastInterval: bypassRecastInterval))
                {
                    // Do NOT tally MaxCasts here. Announcing is round 0 — the spell
                    // fires on the NEXT combat tick, not now — so the round is
                    // counted by the per-round heartbeat (OnCombatTick, once the
                    // same decision has actually held a round). Tallying at announce
                    // capped MaxCasts=1 the instant the spell went out, so the very
                    // next re-choose skipped the normal spell and cascaded to the
                    // alternate before the normal ever fired a round — the reported
                    // "fires normal then immediately the alternate", and the
                    // corpse-cast of the alternate after the normal's kill (the
                    // cascade had already advanced by the time the kill landed).
                    _lastCastAction = decision.Action;
                    _announcedSpellCode = decision.Spell;
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

        // New round — re-arm the once-per-round attack-immunity handler so the next
        // round's "no effect" burst can drive the next cascade step.
        _immunityHandledThisRound = false;

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

        CombatSettings settings = _readSettings();

        // Alternating action orders (CombatActionOrder.Alternate*) drive a fresh
        // command EVERY round — the desired action flips round to round, so the
        // server auto-repeat the fixed orders lean on is never what we want here.
        // This must run ahead of the weapon-mode early-return below: a physical-phase
        // round leaves weapon mode (_castingSpellTarget cleared by the swing), and
        // without this branch the heartbeat would then return early and never flip
        // back to a spell next round. Gated on _currentTarget (not _castingSpellTarget)
        // so it drives both spell-phase and weapon-phase rounds, advancing the phase
        // once per round. An interrupted round (_combatOff, handled above) pauses the
        // phase rather than advancing it. Explicit enum match (not "AlternationPreferSpell
        // is not null") so CustomRoundCycle — which also returns non-null — takes its OWN
        // branch below instead of this always-redispatch one: a round-cycle phase can span
        // many rounds, and redispatching every one of them would re-announce a spell the
        // server is already auto-repeating (the double-cast bug this file's spell heartbeat
        // otherwise prevents).
        if (settings.ActionOrder is CombatActionOrder.AlternateSpellPhysical
                or CombatActionOrder.AlternatePhysicalSpell
            && _currentTarget is { } altTarget)
        {
            if (_classifier.Current is not { } altObs || !TargetPresent(altObs, altTarget))
            {
                _castingSpellTarget = null;
                return;
            }
            // Not yet a real round boundary — see AlternationAdvanceMinGap.
            if (_now() - _lastAlternationAdvanceAt < AlternationAdvanceMinGap) return;
            _lastAlternationAdvanceAt = _now();
            _alternationRound++;
            if (TryBuildCandidate(altObs, altTarget) is { } altCand)
                DispatchRoundAction(settings, altCand, CountEngageable(altObs), altObs);
            else
                _castingSpellTarget = null;   // can't rebuild the target — drop; next observe re-picks
            return;
        }

        // CustomRoundCycle: unlike the fixed Alternate* orders above, a phase can
        // span many rounds, so we must NOT redispatch every tick — only on a
        // genuine phase boundary. Advancing _alternationRound every tick (even
        // when nothing is forced) is what lets the phase math in
        // CustomCyclePreferSpell track elapsed rounds correctly. Only the
        // physical→spell edge needs forcing here: a physical round is otherwise
        // fully passive (the server auto-repeats the swing with no client-side
        // heartbeat), so nothing else would ever notice the schedule says it's
        // time to cast. The reverse edge (spell→physical) needs no special case —
        // it falls through to the ordinary spell-mode heartbeat below, which
        // already re-decides every tick and naturally redispatches the moment
        // ctx.AlternationPreferSpell (fed by the same CustomCyclePreferSpell call)
        // turns false, exactly like a PhysicalFirst weapon-fallback would.
        if (settings.ActionOrder == CombatActionOrder.CustomRoundCycle
            && _currentTarget is { } cycleTarget)
        {
            if (_classifier.Current is not { } cycleObs || !TargetPresent(cycleObs, cycleTarget))
            {
                _castingSpellTarget = null;
                return;
            }
            // Not yet a real round boundary — see AlternationAdvanceMinGap.
            if (_now() - _lastAlternationAdvanceAt < AlternationAdvanceMinGap) return;
            _lastAlternationAdvanceAt = _now();
            _alternationRound++;
            if (CustomCyclePreferSpell(settings) && _castingSpellTarget is null)
            {
                _log?.Combat(LogCategory,
                    $"round-cycle switch → spell phase (round={_alternationRound})");
                if (TryBuildCandidate(cycleObs, cycleTarget) is { } cycleCand)
                    DispatchRoundAction(settings, cycleCand, CountEngageable(cycleObs), cycleObs);
                return;
            }
            // Not a forced physical→spell edge — fall through. A continuing
            // physical round (_castingSpellTarget null) hits the weapon-mode
            // early-return just below and does nothing, correctly leaving the
            // server's auto-repeat alone. A continuing or ending spell round
            // (_castingSpellTarget set) reaches the ordinary heartbeat, which
            // already handles both "same decision, just tally" and "decision
            // changed, redispatch".
        }

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

        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), ResolveMonsterNumber(obs, target));
        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);

        // The server auto-repeats the announced spell every round (CONFIRMED mechanic),
        // so re-announce ONLY when the chooser's decision CHANGES. While it stays the
        // same attack spell at the same target, count this round toward the slot's
        // MaxCasts and send NOTHING — re-sending a spell the server is already
        // repeating is exactly the double-cast / corpse-cast bug this rework removes.
        bool sameSpell = _announcedSpellCode is { } announced
            && decision.Action == _lastCastAction
            && string.Equals(announced, decision.Spell, StringComparison.OrdinalIgnoreCase);
        if (sameSpell)
        {
            _spellChooser.MarkCast(decision, target);   // tally the round against MaxCasts
            return;
        }

        // Decision changed (MaxCasts elapsed, mana crossed the slot floor, immunity, or
        // room thinned below MinEnemies) — re-announce the new action. Route through
        // DispatchRoundAction so the switch gets the full weapon-selection / cast /
        // bookkeeping the initial engage does (a weapon/backstab decision leaves spell
        // mode; a different spell is announced, bypassing the round cooldown).
        if (TryBuildCandidate(obs, target) is { } cand)
            DispatchRoundAction(settings, cand, CountEngageable(obs), obs);
        else
            _castingSpellTarget = null;   // can't rebuild the target — drop; next observe re-picks
    }

    // Give the in-between window its shot BEFORE the attack on engage, so a
    // configured debuff lands ahead of the combat action rather than a round
    // later (report paradigm-20260812-052003: "should fire the debuff before the
    // attack spell"). Only runs when a debuff is actually configured and due for
    // this room/target — otherwise it returns false leaving every bit of state
    // untouched, so the common no-debuff engage (and every weapon engage) is
    // exactly as before.
    //
    // When a debuff IS due it still honours the user's Spells+Ailments spell-type
    // priority: the director's Evaluate runs first, so a higher-priority survival
    // cast (heal / cure / buff) that's due fires ahead of the debuff and the
    // debuff waits for the next in-between pass. Only when nothing higher-priority
    // is queued does the debuff fire here, ahead of the attack. Either way the
    // caller defers the attack — it re-announces on the fired cast's *Combat Off*
    // through the combat-off resume path.
    //
    // The debuff is cast directly (explicit target, not via the director's
    // PickInBetweenDebuff, which can't see the target until _currentTarget is set
    // — the very chicken-and-egg that made the attack win the race). Returns true
    // when it fired something.
    private bool TryPreAttackInBetween(
        CombatSettings settings, EngageableCandidate picked, RoomEntitiesObservation obs)
    {
        if (!CombatSpellsWired || _cast is null) return false;

        CombatSpellContext ctx = BuildContext(
            settings, obs, picked.RawName, CountEngageable(obs), picked.MonsterNumber);
        if (_spellChooser.ChooseDebuff(settings, ctx) is not { } decision) return false;

        // A debuff is due. Let the director's in-between window fire a
        // higher-priority survival cast first (it can't fire the debuff itself
        // yet — its PickInBetweenDebuff keys on _currentTarget, still unset here).
        if (_inBetweenEvaluator?.Invoke() is { } survivalCast)
        {
            _log?.Combat(LogCategory,
                $"pre-attack: in-between {survivalCast} preferred over debuff by priority — " +
                "attack resumes after its *Combat Off*");
            return true;
        }

        // Nothing higher-priority was queued — fire the debuff before the attack.
        // Open the round's per-target caps exactly as DispatchRoundAction would on
        // a new target (the AoE / debuff per-room counters are untouched).
        if (!string.Equals(_currentTarget, picked.RawName, StringComparison.OrdinalIgnoreCase))
        {
            _spellChooser.ResetForNewTarget();
            _alternationRound = 0;
            _lastAlternationAdvanceAt = DateTimeOffset.MinValue;
        }

        // Area debuffs blanket the room and MUST be cast bare — `stnk`, never
        // `stnk <mob>`; a single-target debuff keeps its mob.
        string? castTarget =
            decision.Action == CombatSpellAction.AreaDebuff ? null : picked.RawName;
        if (!_cast.TryCast(decision.Spell!, castTarget, bypassRoundCooldown: true))
            return false;

        _spellChooser.MarkCast(decision, picked.RawName);
        _currentTarget = picked.RawName;
        // Arm the attack resume: the debuff's *Combat Off* re-announces the combat
        // action through the combat-off resume path — mirroring the director's own
        // debuff, whose CastFired arms this same signal.
        NoteBetweenRoundCast();
        _log?.Combat(LogCategory,
            $"pre-attack debuff {decision.Spell} at {picked.RawName} — " +
            "attack resumes after its *Combat Off*");
        return true;
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

        // An area debuff (e.g. stinking cloud) blankets the room and MUST be cast
        // bare — `stnk`, never `stnk <mob>`. A single-target debuff keeps its mob.
        // The combat action verb is re-sent after the in-between cast by the
        // existing combat-off resume path.
        _pendingDebuff = decision;
        _pendingDebuffTarget = target;
        return (decision.Spell!,
            decision.Action == CombatSpellAction.AreaDebuff ? null : target);
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

    // Count engageable monsters in the observation, matching the candidate build in
    // OnEntitiesObserved AND HasEngageable: every Monster counts except one whose
    // KNOWN number resolves to a non-Enemy relationship. An unknown-number monster
    // is counted (fail-open) — the candidate build already adds it (sentinel -1), and
    // a room-wide AoE hits it regardless of whether we resolved its number. Excluding
    // it here undercounted the MinEnemies gate and skipped rooming a full room (report
    // paradigm-20260811-063728: a 5-mob wrapped "Also here:" where a "dark goblin
    // archer" variant's number didn't resolve counted as 4, below MinEnemies=5).
    private int CountEngageable(RoomEntitiesObservation obs)
    {
        int count = 0;
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n)
            {
                count++;   // unknown number → assume engageable (room-nuke hits it)
                continue;
            }
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
            // At literal 0 mana nothing that costs MA can land, no matter what a
            // slot's MinManaPerCast says (0 there means "no floor set", not "free").
            // Marking spells unavailable collapses the chooser to Backstab vs
            // Physical, so combat keeps swinging (and can still backstab) instead of
            // re-casting a spell the server no-ops (report paradigm-20260813-064159).
            SpellsAvailable:     ma > 0,
            LevelBlockedActions: LevelBlockedFor(settings, monsterNumber),
            AllowNukes:          _autoNukeGate?.Invoke() ?? true,
            ResistBlockedActions: ResistBlockedFor(settings, monsterNumber),
            TargetDontBackstab:  IsDontBackstab(monsterNumber),
            OverrideAttackSpell:       attackOverride,
            OverrideAttackMaxCasts:    attackCap,
            OverridePreAttackSpell:    preAttackOverride,
            OverridePreAttackMaxCasts: preAttackCap,
            WeaponIneffective:   WeaponPathExhausted(
                settings, ResolveSpeciesByName(target), monsterNumber),
            AlternationPreferSpell: AlternationPreferSpell(settings));
    }

    // This round's forced spell-vs-physical preference for the alternating /
    // round-cycle action orders, from _alternationRound; null for the fixed
    // orders (the chooser then follows settings.ActionOrder). AlternateSpellPhysical
    // opens on the spell (even rounds → spell); AlternatePhysicalSpell opens on the
    // swing; CustomRoundCycle delegates to CustomCyclePreferSpell for its
    // configurable-length phases.
    private bool? AlternationPreferSpell(CombatSettings settings) => settings.ActionOrder switch
    {
        CombatActionOrder.AlternateSpellPhysical => (_alternationRound % 2) == 0,
        CombatActionOrder.AlternatePhysicalSpell => (_alternationRound % 2) == 1,
        CombatActionOrder.CustomRoundCycle => CustomCyclePreferSpell(settings),
        _ => null,
    };

    // CustomRoundCycle's phase math: phase 1 runs for p1 rounds (0 = forever),
    // then phase 2 runs for p2 rounds (0 = forever once reached), then — only
    // when BOTH are positive — the pair repeats every (p1 + p2) rounds.
    // CycleStartOnSpell picks which phase (1 or 2) is the spell phase.
    // _alternationRound is 0 on the fight's first round (the initial engage),
    // matching the fixed alternating orders' convention.
    private bool CustomCyclePreferSpell(CombatSettings settings)
    {
        int p1 = Math.Max(0, settings.CycleStartOnSpell ? settings.CycleRoundsSpell : settings.CycleRoundsPhysical);
        int p2 = Math.Max(0, settings.CycleStartOnSpell ? settings.CycleRoundsPhysical : settings.CycleRoundsSpell);
        bool phase1IsSpell = settings.CycleStartOnSpell;
        int r = _alternationRound;

        bool inPhase1;
        if (p1 == 0) inPhase1 = true;                  // phase 1 never ends
        else if (p2 == 0) inPhase1 = r < p1;            // phase 2 never ends once reached
        else inPhase1 = (r % (p1 + p2)) < p1;           // both finite — repeat every p1+p2

        return inPhase1 == phase1IsSpell;
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

    // The per-monster forced attack COMMAND for this species, or null when none
    // is set. A raw verb sent verbatim (bypassing the spell/weapon flow and the
    // "no effect" fallback); trimmed to null when blank. Distinct from the
    // spell-id override — no cast-rung, no mana gate; the server repeats it like
    // any attack command.
    private string? AttackCommandOverrideFor(int monsterNumber)
    {
        if (monsterNumber < 0) return null;
        string? cmd = ResolveOverlay(monsterNumber).OverrideAttackCommand;
        return string.IsNullOrWhiteSpace(cmd) ? null : cmd.Trim();
    }

    // Resolve this monster's override pre-attack spell to a (cast-code, cap) pair,
    // or (null, null) when there's no active override.
    private (string? Spell, int? Cap) PreAttackOverrideFor(int monsterNumber)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return ResolveSpellOverride(overlay.OverridePreAttackSpellId, overlay.OverridePreAttackCount);
    }

    // Turn a per-monster override slot (Spell.Number + optional cast count) into
    // the Short cast-code and per-room cap the chooser needs, or (null, null) when
    // the override is inactive. The override activates on a positive Spell.Number
    // alone (report paradigm-20260813-132647: an override spell set with no Max
    // was silently ignored, casting the global attack spell instead) — the count
    // is only a per-room cast cap, and CastsOk already treats a null cap as
    // unlimited, so a blank/zero count here means "no cap", not "not configured".
    // Falls back when the number doesn't map to a real Short cast-code.
    private (string? Spell, int? Cap) ResolveSpellOverride(int? spellId, int? count)
    {
        if (_spellShortByNumber is null) return (null, null);
        if (spellId is not { } number || number <= 0) return (null, null);
        string? code = _spellShortByNumber(number);
        if (string.IsNullOrWhiteSpace(code)) return (null, null);
        int? cap = count is > 0 ? count : null;
        return (code, cap);
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

    // True when neither configured weapon can damage the current target, so a
    // Physical-first round should fall back to the attack-spell cascade rather
    // than swing uselessly. The path is exhausted when the normal weapon is out
    // AND the alternate is out (or unconfigured); if either weapon can still hit,
    // it isn't (ShouldUseAlternateWeapon owns the swap to a working alternate).
    // Fails closed — keep swinging — when nothing proves ineffectiveness, so we
    // never divert to spells on missing data.
    private bool WeaponPathExhausted(
        CombatSettings settings, string resolvedSpecies, int monsterNumber)
    {
        if (!WeaponCannotHit(settings.NormalWeapon, resolvedSpecies, monsterNumber,
                _normalWeaponFailedMonsters))
            return false;

        if (!string.IsNullOrWhiteSpace(settings.AlternateWeapon)
            && !WeaponCannotHit(settings.AlternateWeapon, resolvedSpecies, monsterNumber,
                _alternateWeaponFailedMonsters))
            return false;

        return true;
    }

    // One weapon's effectiveness verdict against the current target: out when the
    // room fail-set already names this species OR game data's HitMagic can't clear
    // the monster's Magical level. Fails open (effective) when the eligibility
    // indexes aren't wired or the weapon / monster levels are unknown — we don't
    // second-guess a swing on missing data.
    private bool WeaponCannotHit(
        string? weapon, string resolvedSpecies, int monsterNumber, HashSet<string> failSet)
    {
        if (!string.IsNullOrEmpty(resolvedSpecies) && failSet.Contains(resolvedSpecies))
            return true;
        if (_monsterMagic is null || _itemMagic is null) return false;
        int magical = _monsterMagic.MagicalLevel(monsterNumber);
        if (magical <= 0) return false;                 // any weapon hits
        int hit = _itemMagic.HitMagic(weapon);
        if (hit < 0) return false;                       // unknown weapon → don't assume
        return hit < magical;
    }

    // Both configured weapons proved ineffective against the current target (a "no
    // effect" line landed on the alternate too). When a combat-spell caster is
    // wired, re-run the round decision — the context now reports WeaponIneffective
    // via both fail-sets, so Choose offers a spell under Physical-first (SpellsFirst
    // would already have tried spells). Returns true when the re-decision entered
    // spell mode. No-op (false) with no caster / target / live room.
    private bool TryFallBackToSpellAfterWeaponFail(CombatSettings settings)
    {
        if (!CombatSpellsWired) return false;
        if (_currentTarget is not { } target) return false;
        if (_classifier.Current is not { } obs) return false;
        if (TryBuildCandidate(obs, target) is not { } cand) return false;

        DispatchRoundAction(settings, cand, CountEngageable(obs), obs);
        return _castingSpellTarget is not null;
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
        AssessEngage(_readSettings(), monsterNumber, species: null) == EngageAssessment.CanAct;

    // How the engine can act against a monster THIS round.
    internal enum EngageAssessment
    {
        // A weapon can still hit it, or an attack spell is castable right now.
        CanAct,
        // Nothing is castable this round only because MA is short — transient.
        // Re-assessed on every observation, so once MA regenerates the spell
        // becomes castable and it reads CanAct again ("retry the cast chain if we
        // regen"). The walker still moves on rather than stand getting beaten
        // waiting for a mana tick, but we do NOT permanently write the mob off.
        StuckOnMana,
        // Permanently un-actionable this room: weapons can't hit (game-data or
        // observed) AND every attack spell is blocked (immune / level / element-
        // resist) or unconfigured. No amount of waiting helps.
        Unkillable,
    }

    // Assess whether we can do anything to monsterNumber this round. Consumed by
    // the retarget loop (which skips anything but CanAct, and only announces
    // "cannot attack" on Unkillable) and, via CanEngageMonster, by the walker gate
    // (which releases on StuckOnMana OR Unkillable — move on, don't sit and take
    // hits waiting for mana). The optional species override lets the retarget loop
    // pass EngageableCandidate.ResolvedName directly (so an unresolved -1 Number
    // still gets its runtime check); the gate path passes only a Number and
    // resolves the species from _speciesByNumber.
    internal EngageAssessment AssessEngage(CombatSettings settings, int monsterNumber, string? species)
    {
        // Game data proves it un-killable (weapons can't hit + spells level/immu-blocked).
        if (UnengageableReason(settings, monsterNumber) is not null)
            return EngageAssessment.Unkillable;

        species ??= _speciesByNumber.GetValueOrDefault(monsterNumber);
        if (string.IsNullOrEmpty(species)) return EngageAssessment.CanAct;   // can't assess → assume actionable

        // A weapon that can still hit (not observed-failed, not game-data-blocked)
        // keeps it actionable — swing.
        if (!WeaponPathExhausted(settings, species, monsterNumber)) return EngageAssessment.CanAct;

        // Both weapons are out. With no caster there's nothing left to try.
        if (!CombatSpellsWired) return EngageAssessment.Unkillable;

        (int ma, int maxMa) = _readMana!();
        IReadOnlySet<CombatSpellAction>? immune = _attackSpellImmuneSpecies.GetValueOrDefault(species);
        IReadOnlySet<CombatSpellAction>? levelBlocked = LevelBlockedFor(settings, monsterNumber);
        IReadOnlySet<CombatSpellAction>? resistBlocked = ResistBlockedFor(settings, monsterNumber);

        bool anyUnaffordable = false;
        foreach ((CombatSpellSlot slot, CombatSpellAction action) in AttackSpellSlots(settings))
        {
            if (string.IsNullOrWhiteSpace(slot.SpellName)) continue;          // unconfigured
            if (immune?.Contains(action) == true) continue;                  // observed immune
            if (levelBlocked?.Contains(action) == true) continue;            // level-blocked
            if (resistBlocked?.Contains(action) == true) continue;           // element-resisted
            if (!ManaMeets(slot, ma, maxMa, settings.SpellManaThresholdMode))
            {
                anyUnaffordable = true;                                        // would work with more MA
                continue;
            }
            return EngageAssessment.CanAct;                                   // castable this round
        }

        // Nothing castable now: unaffordable-only means a mana tick fixes it
        // (transient); otherwise every spell is permanently blocked / unconfigured.
        return anyUnaffordable ? EngageAssessment.StuckOnMana : EngageAssessment.Unkillable;
    }

    // The single-target attack-spell slots, in cascade order — the kill means the
    // affordability check weighs. Multi-attack (a room nuke gated on enemy count)
    // isn't a reliable single-target kill means, so it's excluded here, matching
    // the observed-immunity map which only records the single-target slots.
    private static IEnumerable<(CombatSpellSlot Slot, CombatSpellAction Action)> AttackSpellSlots(
        CombatSettings settings)
    {
        yield return (settings.NormalAttackSpell, CombatSpellAction.NormalAttackSpell);
        yield return (settings.AlternateAttackSpell, CombatSpellAction.AlternateAttackSpell);
    }

    // Whether the live MA meets a slot's per-cast mana floor — the SAME gate the
    // chooser applies (shared CombatSpellChooser.ManaMeetsReserve, which compares
    // the percentage reserve against its rounded absolute equivalent so it matches
    // the Settings conversion label), lifted here so the actionability assessment
    // can tell a transient mana stall from a permanent block.
    private static bool ManaMeets(CombatSpellSlot slot, int ma, int maxMa, ThresholdMode mode) =>
        CombatSpellChooser.ManaMeetsReserve(slot.MinManaPerCast, ma, maxMa, mode);

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
    private int ResolveMonsterNumber(RoomEntitiesObservation obs, string rawName)
    {
        // Prefer the record actually placed / summoned in the current room so a
        // display name shared across zones resolves this room's variant's overrides
        // (a graveyard zombie's spell, not a tunnels zombie's). Falls through to the
        // first-match entity scan below when the room has no monster with this name.
        if (_roomAwareResolve?.Invoke(rawName) is { } roomId) return roomId;

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
        if (!_isEnabled()) return;

        // Act on the first "no effect" of this round's burst only (see the field
        // comment on _immunityHandledThisRound); the rest of the burst is a leftover
        // from the primary and must not tear down the fallback we just switched to.
        if (_immunityHandledThisRound) return;

        // Command mode: a spell placed in the ATTACK-COMMAND slot (NormalAttackCommand
        // = "harm") reaches the wire via SendAttack, not a chooser cast — no spell is
        // in flight (_castingSpellTarget null) — so the spell-slot cascade below can't
        // act on it. Fall the COMMAND path back to the alternate command instead
        // (report paradigm-20260809-131642: priest `harm` as the attack command vs an
        // acid slime never dropped to `attack`). Spell mode leaves _castingSpellTarget
        // set, so this branch never eats a spell-cascade line.
        if (_castingSpellTarget is null)
        {
            if (_lastCastAction is null && _currentTarget is not null)
            {
                _immunityHandledThisRound = true;
                FallBackToAlternateCommandOnImmunity();
            }
            return;
        }

        // Spell mode: attribute the immunity to the attack spell we last cast, then
        // re-decide the cascade instantly (below). Only the two single-target attack
        // spells gate down — a debuff / multi-attack no-effect isn't ours to act on
        // (one immune mob doesn't mean a room spell isn't hitting the rest).
        if (!CombatSpellsWired) return;
        if (_lastCastAction is not (CombatSpellAction.NormalAttackSpell
                                  or CombatSpellAction.AlternateAttackSpell))
            return;

        string species = match.Groups.Count > 0
            ? ResolveSpeciesByName(match.Groups[0].Trim())
            : ResolveSpeciesFromCurrentTarget();
        if (string.IsNullOrEmpty(species)) return;

        _immunityHandledThisRound = true;
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

    // A spell used as the attack COMMAND draws the same "no effect" immunity line
    // as a chooser-cast attack spell, but it went out through SendAttack so the
    // spell-slot cascade never sees it. Mirror OnWeaponNoEffect's normal-fail
    // branch for the command path: mark the species failed vs the normal command
    // (so the next pick's ShouldUseAlternateWeapon prefers the alternate) and
    // re-send THIS round with the alternate command so the round isn't burned. A
    // per-monster forced command owns its species and is trusted, so it never
    // falls back here. _normalWeaponFailedMonsters is shared with the weapon path —
    // it reads as "the normal ATTACK (command or weapon) failed vs this species".
    private void FallBackToAlternateCommandOnImmunity()
    {
        if (_currentTarget is not { } target) return;
        string species = ResolveSpeciesByName(target);
        if (string.IsNullOrEmpty(species)) return;

        // A forced per-monster command owns this species — don't second-guess it.
        int monsterNumber = _classifier.Current is { } obs ? ResolveMonsterNumber(obs, target) : -1;
        if (AttackCommandOverrideFor(monsterNumber) is not null) return;

        CombatSettings settings = _readSettings();
        string normal    = settings.NormalAttackCommand?.Trim()    ?? string.Empty;
        string alternate = settings.AlternateAttackCommand?.Trim() ?? string.Empty;
        bool distinctAlternate = alternate.Length > 0
            && !string.Equals(alternate, normal, StringComparison.OrdinalIgnoreCase);

        if (distinctAlternate && _normalWeaponFailedMonsters.Add(species))
        {
            _log?.Info(LogCategory,
                $"command-no-effect species={species} cmd='{normal}' — falling back to '{alternate}'");
            SendAttack(alternate, target, priority: null);
            return;
        }

        // No distinct alternate, or the alternate already failed too → the whole
        // command chain is out for this species. Concede it and re-pick / move on.
        AnnounceCannotAttack(species);
        _currentTarget = null;
        TrySendRoomRefresh($"attack command ineffective vs {species} — re-pick / move on");
    }

    // After recording an immunity, re-dispatch this same target's round immediately.
    // A "no effect" cast spent no round (CONFIRMED game mechanic, user 2026-08-09),
    // so the cascade's next action fires THIS round via DispatchRoundAction's instant
    // bypass-cooldown cast: the alternate attack SPELL now lands at once instead of
    // idling until the next ~5s heartbeat tick (report paradigm-20260809-162350 —
    // harm→hamm always lost a round; the weapon branch already swung immediately, only
    // the spell branch waited). DispatchRoundAction re-runs the chooser against the
    // now-immune-marked context, so it uniformly picks the alternate spell, the
    // weapon, or (both out) concedes. The per-round guard in OnSpellNoEffect keeps
    // this to once per round, so the alternate isn't re-marked by the same burst.
    private void ReDecideAfterImmunity(CombatSettings settings)
    {
        if (_castingSpellTarget is not { } target) return;
        if (_classifier.Current is not { } obs) return;
        if (!TargetPresent(obs, target)) return;
        if (TryBuildCandidate(obs, target) is { } cand)
            DispatchRoundAction(settings, cand, CountEngageable(obs), obs);
    }
}
