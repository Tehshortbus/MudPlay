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
// Casts route through the shared CastCoordinator.TryCast, which paces our attack
// casts on their own clock, apart from CastingDirector's between-round casts (the
// two are independent slots server-side); only its failure latch is shared. The
// INITIAL engage / a decision switch bypasses the round cooldown (TryCast
// bypassRoundCooldown) so a fresh mob is announced-at instantly, mirroring the
// weapon's cooldown-free send.
public sealed partial class CombatManager
{
    private readonly CombatSpellChooser _spellChooser = new();
    private CastCoordinator? _cast;
    private Func<(int Ma, int MaxMa)>? _readMana;
    // Live HP / max-HP for the drain spell's %-trigger. Optional — until wired the
    // drain gate reads HP as "not below trigger" so a drain never fires.
    private Func<(int Hp, int MaxHp)>? _readHp;
    // Drain-eligibility (living + not undead) by monster number. Optional — until
    // wired the drain treats every target as eligible (fail-open; the reactive "no
    // effect" line still catches a genuine NonLiving / Undead miss).
    private MonsterLifeIndex? _monsterLife;
    // A spell's target-class restriction (living-only / undead-only / animals-only) by
    // cast-code. Paired with _monsterLife to skip an attack spell the target's type makes
    // provably ineffective (turn-undead vs a non-undead mob) BEFORE the reactive probe.
    private SpellTargetTypeIndex? _spellTargetType;
    private Func<bool>? _autoNukeGate;
    private Func<string?>? _roomAttackHold;

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
    // The room roster captured with the pending debuff, so an AoE debuff tags the mobs
    // that were present when it was PICKED (not whenever the coordinator confirms it).
    private IReadOnlyList<string>? _pendingDebuffRoomKeys;

    // A debuff is sent OPTIMISTICALLY — TryCast returns on wire-write, before the
    // server accepts or rejects it — so MarkCast tags the mob(s) debuffed before we
    // know it landed. Remember the marked debuff so a "You have already cast a spell
    // this round!" rejection arriving moments later (it collided with a buff or the
    // user's manual cast) can roll the mark back and re-fire it next round, instead of
    // the mob staying falsely marked debuffed (reports paradigm-20260901-123720 /
    // -140747). Matched by cast-code within a freshness window so a stale marker can't
    // undo an unrelated later cast.
    private (CombatSpellDecision Decision, string Target, IReadOnlyList<string>? RoomKeys, DateTimeOffset At)? _debuffAwaitingConfirm;

    // A debuff rejection lands within the round it was sent — a couple of seconds at
    // most. Past this the awaiting-confirm marker is stale (the debuff is assumed to
    // have landed) and no longer rolls back.
    private static readonly TimeSpan DebuffRejectionWindow = TimeSpan.FromSeconds(2);

    // Optional bridge to the CastingDirector's in-between window (Evaluate). Used
    // by TryPreAttackInBetween to let a due survival cast — or, failing that, the
    // configured debuff — fire BEFORE the attack on engage, honouring the
    // Spells+Ailments priority. Returns the spell cast this pass, or null. Until
    // wired, the pre-attack pass no-ops and engage dispatches the attack as before.
    private Func<string?>? _inBetweenEvaluator;
    // Spends the CastingDirector's one-between-round-cast slot when the pre-attack
    // debuff fires directly (bypassing Evaluate), so the director can't queue a
    // second between-round cast the same round. Told which debuff it was, for a
    // listener that waits on that send. Optional — no-ops until set.
    private Action<string>? _markBetweenRoundSlotUsed;
    // Reads whether the director's one-between-round-cast slot is ALREADY spent this
    // round, so the pre-attack debuff can stand down instead of firing a doomed cast
    // into a spent slot (which draws "already cast this round" and latches the block
    // that then delays the coupled attack). Optional — assumed free until wired.
    private Func<bool>? _isBetweenRoundSlotUsed;

    // ----- Deterministic magic eligibility (game-data gated) ----------
    // Optional, like the spell caster. Until SetMagicEligibility runs, the
    // weapon/spell gating fails open: any weapon hits and no spell is
    // level-blocked, so the chooser/weapon path behave exactly as before.

    private MonsterMagicIndex? _monsterMagic;
    private ItemMagicIndex? _itemMagic;
    private SpellReqLevelIndex? _spellReqLevel;
    private MonsterResistIndex? _monsterResist;
    private SpellAttackTypeIndex? _spellAttackType;
    // Resolves an attack spell's actual mana cost by cast-code (Spellbook.ManaCostOf),
    // threaded into the chooser context as an affordability floor. Until wired, the
    // chooser gates on the slot's MinManaPerCast reserve alone (old behavior).
    private Func<string, int?>? _spellManaCost;

    // Wire the spell mana-cost lookup used to gate the combat chooser on real
    // affordability (a MinManaPerCast=0 slot must still not cast a spell we can't
    // pay for — a below-cost cast silently no-ops). Same source the CastingDirector
    // uses (Spellbook.ManaCostOf).
    public void SetSpellManaCost(Func<string, int?> manaCostOf)
        => _spellManaCost = manaCostOf ?? throw new ArgumentNullException(nameof(manaCostOf));

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

    // The room/multi-attack spell (hsto) currently channeling this engagement, or
    // null when no room spell is active. CONFIRMED game mechanic: a room-attack
    // spell, once cast while engaged, keeps hitting EVERY monster in the room each
    // round — survivors and mobs that roam in afterward — until a cast condition
    // fails (count < MinEnemies, MaxCastsPerRoom hit, or mana < the AoE's floor). It
    // is NOT re-cast to cover new arrivals, and the game has NO engine-side dedup
    // against recasting the same room spell: a redundant send BREAKS and restarts the
    // channel (*Combat Off* + *Combat Engaged*), wasting the round (report
    // paradigm-20260923-103938). Unlike _announcedSpellCode / _castingSpellTarget —
    // which a single kill clears so the round re-picks a fresh target — this marker
    // SURVIVES a kill (the channel outlives any one mob) and clears only on a genuine
    // end-of-fight (ClearAttackSpellCascadeState: room-clear / combat-off / death), a
    // physical move (NotePreMove), or when a non-room action is dispatched (the
    // channel is switched off). The post-kill re-pick reads it to re-anchor the round
    // to a survivor instead of re-issuing the room spell.
    private string? _roomChannelSpell;

    // Opt into combat-spell casting. cast is the shared CastCoordinator (so the
    // per-round cooldown is shared with every other caster); readMana reports live
    // MA / max-MA for the chooser's per-cast mana gate. Until called the engine is
    // weapon-only and the chooser never runs.
    public void SetCombatSpellCaster(
        CastCoordinator cast, Func<(int Ma, int MaxMa)> readMana,
        Func<(int Hp, int MaxHp)>? readHp = null)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(readMana);
        if (_cast is not null) _cast.CastFailed -= OnCombatCastFailed;
        _cast = cast;
        _readMana = readMana;
        _readHp = readHp;
        // Watch for a debuff the server rejects after our optimistic send so we can
        // roll the mark back and re-fire it next round (see _debuffAwaitingConfirm).
        _cast.CastFailed += OnCombatCastFailed;
    }

    // A cast the engine sent failed on the wire. Two rejections matter here, both for
    // a DEBUFF whose optimistic MarkCast tagged the mob(s) debuffed though the cast
    // never landed: "already cast this round", and the room-wide "no effect in this
    // room" (nothing was there to debuff). Undo the mark so a later round re-fires it.
    // The room-wide refusal also ends a room-attack channel. Other reasons /
    // non-matching codes are left to the buff path (CastingDirector) and the attack
    // path (which owes-and-retries its own blocked send).
    private void OnCombatCastFailed(CastFailureReason reason, string detail, string? spell)
    {
        if (reason is not (CastFailureReason.AlreadyCastThisRound or CastFailureReason.NoTargets)) return;
        if (reason == CastFailureReason.AlreadyCastThisRound) DisarmCastResumeOnRefusal();
        if (TryRollBackRejectedDebuff(reason, spell)) return;
        if (reason == CastFailureReason.NoTargets) EndRoomChannelOnNoTargets();
    }

    private bool TryRollBackRejectedDebuff(CastFailureReason reason, string? spell)
    {
        if (_debuffAwaitingConfirm is not { } pending) return false;
        if (_now() - pending.At > DebuffRejectionWindow) { _debuffAwaitingConfirm = null; return false; }
        // The rejection line never names the spell; CastCoordinator attributes it to
        // the last code it sent. Only roll back when that matches the debuff we're
        // awaiting — otherwise a later attack's rejection could wrongly unmark it.
        if (!string.IsNullOrEmpty(spell)
            && !string.Equals(spell, pending.Decision.Spell, StringComparison.OrdinalIgnoreCase))
            return false;
        _spellChooser.UnmarkCast(pending.Decision, pending.Target, pending.RoomKeys);
        _debuffAwaitingConfirm = null;
        string why = reason == CastFailureReason.NoTargets
            ? "nothing in the room to hit" : "already cast this round";
        _log?.Combat(LogCategory,
            $"pre-attack debuff {pending.Decision.Spell} rejected ({why}) — un-marked so it re-fires");
        return true;
    }

    // A running room attack keeps its round queued after it kills everything, and
    // that round prints "Your spell has no effect in this room!" and queues no other
    // (GAME_MECHANICS "Room-attack spells: cast bare, persistent channel"): the channel is over with no
    // *Combat Off* to say so. Drop the marker and the announce latch with it, or a
    // monster that roams in afterwards is re-anchored "without recast" onto a channel
    // that no longer exists and is never attacked.
    private void EndRoomChannelOnNoTargets()
    {
        if (_roomChannelSpell is not { } ended) return;
        _roomChannelSpell = null;
        if (string.Equals(_announcedSpellCode, ended, StringComparison.OrdinalIgnoreCase))
            _announcedSpellCode = null;
        _log?.Info(LogCategory,
            $"room-attack channel '{ended}' ended — the game found nothing in the room to hit; "
            + "it is cast again when a monster is here");
    }

    // Opt into monster-type spell eligibility. monsterLife reports each monster's
    // life-class (nonliving / undead / animal); spellTargetType reports each spell's
    // target-class restriction. Together they drive the drain-eligibility gate AND the
    // attack-spell target-type gate (skip turn-undead vs a non-undead mob, etc.). Until
    // called, both fail open and lean on the reactive "no effect" line.
    public void SetDrainEligibility(MonsterLifeIndex monsterLife, SpellTargetTypeIndex spellTargetType)
    {
        ArgumentNullException.ThrowIfNull(monsterLife);
        ArgumentNullException.ThrowIfNull(spellTargetType);
        _monsterLife = monsterLife;
        _spellTargetType = spellTargetType;
    }

    // Wire the Auto-Nuke auto-engine gate. When the predicate returns false, the
    // chooser never offers the multi-target attack spell or the area debuff (the
    // single-target debuff and Normal / Alternate attack spells stay available —
    // they aren't nukes). Until called, nukes fail open (always allowed).
    public void SetAutoNukeGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _autoNukeGate = gate;
    }

    // Wire the room-attack hold: why our room spells mustn't go out (a player
    // outside our party is here, on a realm with PvP on), or null. While there is a
    // reason the chooser is offered no room attack, no room debuff and no
    // room-scoped spell in a single-target rung, and a room attack already
    // repeating is broken off. Until called, nothing is held.
    public void SetRoomAttackHold(Func<string?> heldBy)
    {
        ArgumentNullException.ThrowIfNull(heldBy);
        _roomAttackHold = heldBy;
    }

    // A room attack repeats on its own every round, so keeping it from the chooser
    // isn't enough once it is running: only `break` stops it. Stamped as a
    // between-round cast so the *Combat Off* that follows re-dispatches the round,
    // which now picks a single target.
    private void BreakRoomAttackForBystander()
    {
        if (_roomChannelSpell is not { } spell) return;
        if (_roomAttackHold?.Invoke() is not { } why) return;
        _log?.Info(LogCategory, $"room attack '{spell}' broken off: {why}. Single target meanwhile");
        _roomChannelSpell = null;
        NoteBetweenRoundCast(manual: false);
        Send("break");
    }

    // The single-target rungs whose spell is aimed at the room, added to the level
    // blocks while a room attack is held: a room spell slotted as the normal or
    // alternate attack hits a bystander just the same.
    private IReadOnlySet<CombatSpellAction>? WithRoomScopedHeld(
        IReadOnlySet<CombatSpellAction>? blocked, string? singleCode, string? normalCode, string? altCode)
    {
        HashSet<CombatSpellAction>? held = null;
        void Check(string? code, CombatSpellAction action)
        {
            if (IsRoomScoped(code))
                (held ??= blocked is null ? new() : new(blocked)).Add(action);
        }
        Check(singleCode, CombatSpellAction.SingleDebuff);
        Check(normalCode, CombatSpellAction.NormalAttackSpell);
        Check(altCode, CombatSpellAction.AlternateAttackSpell);
        return held ?? blocked;
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

    // Wire the in-between evaluator (CastingDirector.Evaluate) that
    // TryPreAttackInBetween runs to fire a due survival/debuff cast before the
    // attack on engage. Optional — the pre-attack pass no-ops until set.
    public void SetInBetweenEvaluator(Func<string?> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _inBetweenEvaluator = evaluate;
    }

    // Wire the callback that spends the CastingDirector's one-between-round-cast
    // slot when the pre-attack debuff fires directly (bypassing Evaluate).
    // Optional — the pre-attack debuff still fires without it, but the director
    // won't know the slot is used. It is given the debuff's cast code.
    public void SetBetweenRoundSlotMarker(Action<string> marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _markBetweenRoundSlotUsed = marker;
    }

    // Told when a fresh engage has no debuff to cast: none set, none allowed on this
    // target, its mana or cast cap not met. The attack goes out with nothing ahead of
    // it, and a listener keeping the round's cast for that debuff has nothing to keep
    // it for. Not told when the debuff is only put off a round (behind a backstab, or
    // a cast already spent): it is still coming. Optional.
    public void SetNoDebuffOnEngageSink(Action sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _noDebuffOnEngage = sink;
    }
    private Action? _noDebuffOnEngage;

    private bool NoDebuffOnThisEngage()
    {
        _noDebuffOnEngage?.Invoke();
        return false;
    }

    // Wire the read-side of the one-between-round-cast slot (CastingDirector's
    // BetweenRoundSlotUsed). TryPreAttackInBetween consults it so it won't send a
    // pre-attack debuff into a slot a survival/buff cast already spent this round.
    public void SetBetweenRoundSlotQuery(Func<bool> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _isBetweenRoundSlotUsed = query;
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
            // Anchor the tally clock at the engage moment ONLY in a MULTI-mob room.
            // MaxCasts counts real rounds and the engage announce is round 0 — the
            // attack spell fires on a LATER server round. In a multi-mob room the
            // OTHER mobs' swing lines trip the damage-driven combat tick within ~100ms
            // of the engage; a MinValue reset let that premature tick tally the spell
            // before it ever fired, so a MaxCasts-1 nuke cap-switched to the alternate
            // the same round and the normal spell never went out ("LBOL → MMIS without
            // firing LBOL", report paradigm-20260815-202241 — engageable=2,
            // sinceAttack≈79ms). Anchoring makes AttackTallyMinGap reject that
            // premature tick; the first genuine round tick tallies.
            //
            // A SINGLE-mob room has no other mob to produce a premature tick — its
            // first damage tick IS round 1. But that tick can land UNDER
            // AttackTallyMinGap after the engage (the server delivered round 1 ~3.3s
            // out), and anchoring at engage wrongly rejected it too, slipping the first
            // tally to round 2. A MaxCasts-1 spell then auto-repeated one extra round
            // before the cap-switch fired (report paradigm-20260818-055820: engine cast
            // lbol, cap-switch lbol→mmis at sinceAttack≈9333ms, engageable=1). So only
            // anchor when there's another mob to guard against; leave solo fights at
            // MinValue so the first genuine tick always tallies.
            _lastAttackTallyAt = enemyCount > 1 ? _now() : DateTimeOffset.MinValue;
            // Round-count anchor: the first MaxCasts tally waits for the NEXT round to
            // CLOSE, so a premature tick — a solo mob's counter-swing or a stale
            // interval projection right after the engage — can't cap the spell before
            // it fires (report paradigm-20260819-120938). This makes the solo/multi
            // distinction above moot when a round-count source is wired. Unwired leaves
            // it -1 and the legacy wall-clock gate runs.
            _lastTalliedRound = ReadRoundCount?.Invoke() ?? -1;
        }

        CombatSpellContext ctx = CombatSpellsWired
            ? BuildContext(settings, obs, picked.RawName, enemyCount, picked.MonsterNumber)
            : BuildWeaponContext(settings, obs, picked.RawName, enemyCount, picked.MonsterNumber);

        // Announce an active per-monster spell override once at target-pick time
        // (not on the per-round heartbeat) so a log read shows why the cast-code
        // differs from the Combat-tab slot for this species. Each override
        // substitutes into its rung and runs the same gates — no bypass.
        if (ctx.OverrideAttackSpell is { } atkOverride)
            _log?.Combat(LogCategory,
                $"per-monster attack override {atkOverride} (#{picked.MonsterNumber}) — substituting into the normal-attack rung");
        if (ctx.OverrideAltAttackSpell is { } altOverride)
            _log?.Combat(LogCategory,
                $"per-monster alt-attack override {altOverride} (#{picked.MonsterNumber})");
        if (ctx.OverridePreAttackSpell is { } preOverride)
            _log?.Combat(LogCategory,
                $"per-monster debuff override {preOverride} (#{picked.MonsterNumber})");

        CombatSpellDecision decision = _spellChooser.Choose(settings, ctx);

        // Drain-life gate trace (Combat diagnostics): every dispatch where a drain is
        // configured logs the gate inputs + the chosen action, so a future report can
        // see WHY the drain engaged / held / released this round — HP vs the trigger
        // and the higher release threshold (hysteresis), target eligibility, and mana.
        // Fires on decision changes (engage / disengage / switch), not every tick, so
        // it's a readable trail rather than per-round spam.
        if (!string.IsNullOrWhiteSpace(settings.DrainSpell.SpellName))
            _log?.Combat(LogCategory,
                $"drain gate → action={decision.Action}: hp≤trigger={ctx.HpBelowDrainTrigger} "
                + $"eligible={ctx.DrainTargetEligible} "
                + $"mana={ctx.Mana}/{ctx.MaxMana} overrideAoe={settings.DrainsOverrideAoe}");

        // A fresh dispatch supersedes any previously announced spell — clear it so a
        // weapon/backstab decision, or a spell whose announce is blocked, is never
        // mistaken by the heartbeat for "the server is still repeating it". A
        // successful spell announce sets it again in the default case below.
        _announcedSpellCode = null;

        switch (decision.Action)
        {
            case CombatSpellAction.WeaponAttack:
                // A per-monster Physical override replaces the weapon command on the
                // rounds the decision engine already landed on physical — it does NOT
                // force physical (a spell-phase round cast a spell above) and carries
                // no gating; the server auto-repeats it like any attack command.
                // Announced once per target so a log read shows the divergence.
                if (PhysicalCommandOverrideFor(picked.MonsterNumber) is { } physCmd)
                {
                    if (!string.Equals(_currentTarget, picked.RawName, StringComparison.OrdinalIgnoreCase))
                        _log?.Combat(LogCategory,
                            $"per-monster physical override '{physCmd}' (#{picked.MonsterNumber}) — replacing the weapon command");
                    SendAttack(physCmd, picked.RawName, picked.Priority);
                    _currentTarget = picked.RawName;
                    break;
                }
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
                // A combat spell owns the round. Announce it ONCE and instantly: the engage
                // bypasses the shared 5.5s round cooldown (mirroring a weapon's
                // cooldown-free SendAttack), so a fresh mob is cast at immediately rather
                // than after it swings. bypassRecastInterval additionally skips the 500ms
                // burst guard: the debuff half of debuff-then-attack is cast on the same
                // attack-slot clock moments before this dispatch, so a fresh engage /
                // arrival and an interrupt-resume pass it true to still land THIS round's
                // attack behind it. The plain per-round re-decide paths (@kill retarget,
                // target-priority follow) leave it false so the guard still absorbs a burst
                // of dispatches in the same frame. The server then auto-repeats the spell
                // each round; the heartbeat re-announces only if the chooser's decision
                // later changes. Set the bridge even when the cast is blocked so we stay in
                // spell mode and retry next tick rather than swinging.
                _castingSpellTarget = picked.RawName;
                // Clear _combatOff here, not only inside the TryCast-succeeded branch
                // below. A fresh engage can lose the round's one-cast slot to a
                // between-round survival cast that landed the same instant (self-heal/
                // buff firing on the same HP-changed tick) — TryCast then returns false
                // and the attack never reaches the server, so no *Combat Engaged*/*Combat
                // Off* cycle ever starts for this target to interrupt. The existing
                // OnCombatStatus resume (elsewhere in this class) only fires off a real
                // *Combat Off* line, so it never sees this case — _combatOff is left
                // however it was before this engage (stuck true from the PRIOR fight's
                // kill), and OnCombatTick's spell-mode heartbeat gates on !_combatOff, so
                // nothing ever retries the blocked cast. Report paradigm-20260824-215802:
                // engaged a fresh shade, lost the round to a self-buff, and never attacked
                // it again for the rest of the capture. Clearing it unconditionally here
                // means the next tick always gets a chance to retry, cast blocked or not.
                _combatOff = false;
                // Room-wide spells (e.g. dancing blades) hit every enemy and MUST be
                // cast bare — `blad`, never `blad <mob>` (the server treats the targeted
                // form as an unknown command). That's the multi-attack slots, and a room
                // spell a monster's attack override names. Single-target attack/debuff
                // spells keep their mob; _castingSpellTarget still tracks the round's mob.
                bool roomSpell = decision.Action is CombatSpellAction.MultiAttack
                                                 or CombatSpellAction.MultiAttack2
                                 || IsRoomScoped(decision.Spell);
                string? castTarget = roomSpell ? null : picked.RawName;
                // AttacksBlocked short-circuits the cast: an AttackPrevented condition
                // makes the server reject an attack spell exactly like a weapon swing, so
                // fall into the owed/retry branch and re-attempt next tick when it clears.
                if (!AttacksBlocked()
                 && _cast!.TryCast(decision.Spell!, castTarget, bypassRoundCooldown: true, bypassRecastInterval: bypassRecastInterval))
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
                    // Record / end the room-attack channel. A multi-attack cast opens
                    // (or continues) the channel; any other attack action switches off
                    // it, so drop the marker — the post-kill re-pick guard keys on it.
                    _roomChannelSpell = roomSpell ? decision.Spell : null;
                    if (decision.Action == CombatSpellAction.DrainSpell)
                        _log?.Info(LogCategory,
                            $"drain-life {decision.Spell} vs {picked.RawName} — HP under trigger, overriding the round's attack");
                    // A cast is an attack for engage-verify purposes — if the
                    // server never confirms *Combat Engaged*, the spell hit a
                    // stale room view and the CR-reverify net must recover.
                    NoteAttackSent();
                }
                else
                {
                    // The attack never reached the server. Mark it owed (diagnostic)
                    // and make that round exist even in a freshly started session whose
                    // TickEngine has not seen a generic combat line yet. The old retry
                    // assumed CombatTickElapsed would arrive, but its timer fallback is
                    // intentionally inert while its anchor is null; a shade's "reaches
                    // out for you" armour-block line is outside the generic tick
                    // patterns, so the pending attack could otherwise remain here
                    // forever with CombatOff=false.
                    _spellAttackOwed = true;
                    _ensureCombatTickAnchor?.Invoke();
                    _log?.Combat(LogCategory,
                        $"attack-spell {decision.Spell} vs {picked.RawName} blocked before send — "
                        + "attack owed; ensuring combat-tick retry anchor");
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

        // New round (the round boundary) — release any user-attack override so the
        // engine resumes its auto attack this round. A hand-typed attack suppresses the
        // engine's re-send only for the round it landed in.
        ClearUserAttackOverrideForNewRound();
        bool resumeHeldForUserAttack = _spellResumeHeldForUserAttack;
        _spellResumeHeldForUserAttack = false;

        // New round — reset the per-round exp-line tallies the AoE-wipe path reads.
        _expGainsThisRound = 0;
        _expGainsSinceRosterRead = 0;

        // New round — re-arm the once-per-round attack-immunity handler so the next
        // round's "no effect" burst can drive the next cascade step.
        _immunityHandledThisRound = false;

        // Engage-verification runs on every tick regardless of spell
        // wiring — a pure-weapon build still needs the stale-room CR
        // recovery. Must precede the CombatSpellsWired gate below.
        VerifyEngagement();

        NoteRoundBoundaryUnderUserBreakHold();

        // The user's attack took a break hold off without naming a monster we could
        // place; their round is over, so pick from the room as a fresh fight would.
        if (_repickAfterUserAttack)
        {
            _repickAfterUserAttack = false;
            if (Fighting() && _currentTarget is null && _classifier.Current is { } afterUserAttack)
            {
                _log?.Combat(LogCategory, "the user's attack ended a break hold with no target of ours — picking one now");
                OnEntitiesObserved(afterUserAttack);
                return;
            }
        }

        // Follow-deferral fallback: we held our own room-entry pick waiting for the
        // followed player's attack announce (ShouldWaitForFollow), but a full round
        // elapsed with no announce (the tick is the round heartbeat, driven by the
        // combat-damage lines). Per the spec, no announce → fall back to our own
        // game-data pick. Re-run the observation with the defer branch bypassed so
        // we make an independent choice this round. Guard on _currentTarget still
        // null — if a target got set meanwhile (announce landed, manual attack) the
        // hold already resolved.
        if (_awaitingFollowAnnounce
            && Fighting()
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

        // Attack-not-last fallback: we held our own pick waiting for the first party
        // announce, but a full round elapsed without one — engage our own pick so we
        // never freeze (same shape as the follow fallback above).
        if (_awaitingNotLast
            && Fighting()
            && _currentTarget is null
            && _classifier.Current is { } notLastFallback)
        {
            _awaitingNotLast = false;
            _log?.Combat(LogCategory,
                "attack-not-last — no party announce this round; falling back to own pick");
            _notLastDeferBypass = true;
            try { OnEntitiesObserved(notLastFallback); }
            finally { _notLastDeferBypass = false; }
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
        // caster. Weapon mode here (_castingSpellTarget null); spell mode has
        // its own a few lines down, past the spell gates. Skipped after a kill
        // (_currentTarget cleared by the death watcher) so we never swing at
        // a corpse. TryResumeEngage's pacing prevents a double-fire with the
        // OnCombatLine resume in the same round.
        if (Fighting()
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
        if (!Fighting()) return;
        if (_combatOff)
        {
            // Round interrupted. A between-round cast's Off is answered on the Off line
            // itself, and the one a user-typed attack held last round falls due here.
            if (resumeHeldForUserAttack) ResumeSpellHeldForUserAttack();
            // Any other Off left standing in a spell fight (a stun, a typed command,
            // a resume that was rate-limited) used to wait for a combat line the
            // client reads, and with a monster whose swings it doesn't read that
            // never came. The round tick brings the spell back as it does the weapon
            // (user, 2026-10-09), through the same door: TryResumeEngage keeps its
            // guards (a fresh attack not yet answered, one resume a round, shared
            // with OnCombatLine, so a line and the tick of one round are one
            // re-announce), and the re-dispatch goes through the chooser, which is
            // what holds the spell to its cast cap and mana floor. One announce a
            // round is all it can send, and the game fires an announced spell as
            // often as its energy allows from there, no more.
            else if (_castingSpellTarget is { } spellTarget
                && _currentTarget is not null
                && _classifier.Current is { } spellRoom
                && TargetPresent(spellRoom, spellTarget))
                TryResumeEngage(spellRoom);
            return;
        }

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
            DeferRoundContinuation(settings, altTarget, "alternate-order");
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
                DeferRoundContinuation(settings, cycleTarget, "round-cycle switch");
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

        // Weapon→spell re-climb for the FIXED orders (SpellsFirst / PhysicalFirst).
        // Once the attack-spell cascade lapses to the weapon (mana fell under the
        // per-cast floor, or the cast cap was spent), _castingSpellTarget is null and
        // the heartbeat would return just below and never look again — so mana
        // regenerating back over the threshold went unnoticed until an unrelated room
        // re-observation forced a fresh decision (report paradigm-20260921-074300:
        // kept swinging ~6 rounds after mana had recovered, only re-climbing on the
        // next room display). The Alternate* / CustomRoundCycle orders already have
        // this edge (above); the fixed orders lacked it. Peek the chooser at a round
        // boundary while in weapon mode; if it now picks an attack spell, switch to it.
        // Choose already consults the per-target weapon latch (_attackSpellLatchedOff
        // via singleTargetSpent), so a target we deliberately committed to the weapon
        // on — the CONFIRMED "a mana regen must not flip us back mid-fight on the SAME
        // target" mechanic — still yields the weapon here; only a fresh / non-latched
        // target re-climbs. Peeking Choose is side-effect-safe: the cast counters and
        // the cast-at flag are set in MarkCast (on send), not Choose.
        // Not while a backstab round is unresolved: its resolution re-announces the
        // round's attack itself, so re-climbing here too sent the spell twice (report
        // paradigm-20260928-032724).
        if (settings.ActionOrder is CombatActionOrder.SpellsFirst or CombatActionOrder.PhysicalFirst
            && _castingSpellTarget is null
            && !_awaitingBackstabResolution
            && _currentTarget is { } weaponTarget
            && _now() - _lastAlternationAdvanceAt >= AlternationAdvanceMinGap
            && _classifier.Current is { } climbObs
            && TargetPresent(climbObs, weaponTarget))
        {
            CombatSpellContext climbCtx = BuildContext(
                settings, climbObs, weaponTarget, CountEngageable(climbObs),
                ResolveMonsterNumber(climbObs, weaponTarget));
            CombatSpellDecision climb = _spellChooser.Choose(settings, climbCtx);
            if (IsAttackSpellRoundOwner(climb.Action))
            {
                _lastAlternationAdvanceAt = _now();
                _log?.Combat(LogCategory,
                    $"weapon→spell re-climb at '{weaponTarget}': chooser now picks {climb.Spell} "
                    + $"({climb.Action}) — attack-spell resource recovered, switching off the weapon");
                DeferRoundContinuation(settings, weaponTarget, "weapon→spell re-climb");
                return;
            }
        }

        if (_castingSpellTarget is not { } target) return;   // weapon / idle mode — nothing to drive

        if (_classifier.Current is not { } obs)
        {
            _castingSpellTarget = null;
            return;
        }

        // Self-heal: the auto-attack target diverged from the spell target
        // (mob died → the kill path cleared _currentTarget, or a re-pick
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
            // The tick is damage-line driven, so — exactly as AlternationAdvanceMinGap
            // documents for the phase advance — a multi-hit attack spell (each cast lands
            // several damage lines) plus the mob's counter-swing trips this heartbeat 2-3×
            // within one real ~5s round. Tallying every trip counted a MaxCasts=2 spell to
            // its cap in a single round, so the engage spell swapped a round early (report
            // paradigm-20260815-130957: "hamm set to 2, swapped after the first cast").
            // Gate the tally to once per real round so MaxCasts counts rounds cast, not
            // tick fires — the cap-preempt still fires on the genuine capping-round tick.
            // Robust count: tally at most ONCE per real cast. ReadRoundCount is wired to
            // ConfirmedAttackCastCount (see its declaration), which moves once per cast
            // actually seen landing, so multiple damage-line ticks for one cast — our
            // multi-hit, the mob's counter-swing, a stale interval — collapse to a
            // single tally, and a genuine cast that lands fast still counts (fixes the
            // -055820 vs -120938 wall-clock tension). Falls back to the wall-clock gate
            // only when no count source is wired.
            if (ReadRoundCount is { } readRound)
            {
                int round = readRound();
                if (round == _lastTalliedRound) return;
                _lastTalliedRound = round;
            }
            else
            {
                if (_now() - _lastAttackTallyAt < AttackTallyMinGap) return;
                _lastAttackTallyAt = _now();
            }

            _spellChooser.MarkCast(decision, target);   // tally this round's fired cast

            // Pre-empt the extra auto-repeat. The server will fire this same spell
            // AGAIN next round unless we announce the switch now — and announcing it
            // on the NEXT tick (the old behaviour) is one round too late, so a capped
            // spell fires one time past its MaxCasts (MaxCasts=1 → two casts; report
            // paradigm-20260814-061340). Re-decide with the freshly-tallied count: if
            // that round just exhausted the cap (or otherwise changed the decision),
            // announce the switch THIS tick so the server swaps next round instead of
            // repeating the capped spell once more. The target is still present
            // (checked above) and the switch routes through the same DispatchRoundAction
            // the old tick-later switch used, so a kill on the capping cast is caught by
            // the identical death-clears-target guard — no corpse-cast of the next rung.
            CombatSpellDecision afterTally = _spellChooser.Choose(settings, ctx);
            bool decisionChanged = afterTally.Action != decision.Action
                || !string.Equals(afterTally.Spell, decision.Spell, StringComparison.OrdinalIgnoreCase);
            if (decisionChanged && TryBuildCandidate(obs, target) is not null)
                DeferSwitchDispatch(settings, target, "cap-switch", _announcedSpellCode, afterTally.Spell);
            return;
        }

        // Decision changed (MaxCasts elapsed, mana crossed the slot floor, immunity, or
        // room thinned below MinEnemies) — re-announce the new action. Route through
        // DispatchRoundAction so the switch gets the full weapon-selection / cast /
        // bookkeeping the initial engage does (a weapon/backstab decision leaves spell
        // mode; a different spell is announced, bypassing the round cooldown).
        if (TryBuildCandidate(obs, target) is not null)
            DeferSwitchDispatch(settings, target, "switch", _announcedSpellCode, decision.Spell);
        else
            _castingSpellTarget = null;   // can't rebuild the target — drop; next observe re-picks
    }

    // Consume a newly-observed single-target attack-spell cast immediately, from
    // OnAttackCastConfirmed, instead of relying on a later CombatTickElapsed. One
    // cast may emit several projectile result lines (disrupt emits two); the caller
    // groups those lines and invokes this only for the first line in the cast.
    //
    // The ReadRoundCount comparison is intentional. Production wires it to
    // ConfirmedAttackCastCount, so the just-incremented value differs from the
    // per-target baseline and is consumed here. A caller that wires a
    // different round source keeps the heartbeat-owned tally path.
    private void ApplyConfirmedAttackCastToCap()
    {
        if (!CombatSpellsWired || !Fighting()) return;
        if (_castingSpellTarget is not { } target
            || !string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase))
            return;
        if (_announcedSpellCode is not { } announced
            || _lastCastAction is not (CombatSpellAction.NormalAttackSpell
                or CombatSpellAction.AlternateAttackSpell
                or CombatSpellAction.DrainSpell
                or CombatSpellAction.MultiAttack
                or CombatSpellAction.MultiAttack2))
            return;
        if (ReadRoundCount is not { } readRound) return;

        int confirmedCast = readRound();
        if (confirmedCast == _lastTalliedRound) return;
        _lastTalliedRound = confirmedCast;

        // Attribute the observed cast to what the server was actually repeating,
        // rather than re-choosing first. Mana may already have dropped below the
        // slot floor by the time its projectile lines arrive, but that does not make
        // the cast which just consumed mana disappear from MaxCasts bookkeeping.
        CombatSpellDecision fired = new(_lastCastAction.Value, announced);
        _spellChooser.MarkCast(fired, target);

        // The cast is tallied even if the room view vanished or a between-round cast
        // has already interrupted combat. In those cases the established kill/resume
        // paths own the next dispatch. With a live target, re-decide now and route a
        // capped/otherwise-changed choice through the same corpse-safe delay as the
        // heartbeat path.
        if (_combatOff
            || _classifier.Current is not { } obs
            || !TargetPresent(obs, target))
            return;

        CombatSettings settings = _readSettings();
        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), ResolveMonsterNumber(obs, target));
        CombatSpellDecision afterTally = _spellChooser.Choose(settings, ctx);
        bool decisionChanged = afterTally.Action != fired.Action
            || !string.Equals(afterTally.Spell, fired.Spell, StringComparison.OrdinalIgnoreCase);
        if (decisionChanged && TryBuildCandidate(obs, target) is not null)
            DeferSwitchDispatch(settings, target, "cap-switch", announced, afterTally.Spell);
    }

    // The combat tick is DAMAGE-LINE driven, so a spell switch decided in the
    // heartbeat above is decided on the round's (possibly killing) damage line —
    // BEFORE the death / exp / *Combat Off* lines that drop the target. When those
    // trailing lines ride the SAME server burst as the damage line (one synchronous
    // MessageRouter.Dispatch fed from a single UI-thread post), a queued _post already
    // runs after the burst unwinds, by which point the exp-inferred kill has nulled
    // _currentTarget / _castingSpellTarget. But the kill's exp / *Combat Off* very often
    // arrives in a LATER network packet than the damage line — a distinct UI-thread post
    // — and a MaxCasts-1 nuke fires its cap-switch on its OWN killing blow, so the queued
    // switch runs in the gap BEFORE that next packet is processed and the alternate
    // corpse-casts at the just-killed mob (Mage lbol→mmis at MA 99-108, reports
    // paradigm-20260815-201731 / -202241; also the Paladin harm→aa weapon fallback,
    // -120544 / -120934). A bare _post can't bridge a packet gap of unknown dispatcher
    // cycles — only wall-clock can. So delay the dispatch a short real-time window
    // (SwitchDispatchDelay) via the injected one-shot scheduler, letting the adjacent
    // kill packet land and process first, THEN re-validate against the now-current state
    // and skip a target that's gone (the next observation re-picks the survivor cleanly).
    // A legit mid-fight switch (mob still alive) re-validates fine and dispatches a hair
    // later, so the cap-preempt (report paradigm-20260814-061340) is preserved — the
    // window is short enough (SwitchDispatchDelay) to still land ahead of the server's
    // next round even for a fast multi-projectile caster (report paradigm-20260822-003106).
    // _pendingSwitch keeps a re-tick inside the delay from arming the same switch twice.
    // Falls back to a bare _post when no scheduler is wired.
    // Pending deferred switch (target→spell). While one is armed, a re-tick during
    // the dispatch delay can't schedule the same switch again — the double-cast in
    // reports paradigm-20260819-121003 / -142147 (mmis fired twice after lbol
    // capped). Cleared when the dispatch runs, so the next round may re-arm.
    private (string Target, string Spell)? _pendingSwitch;

    private void DeferSwitchDispatch(
        CombatSettings settings, string target, string reason, string? from, string? to)
    {
        // Idempotency guard: while a switch to the SAME target→spell is already
        // scheduled, don't arm a second. During SwitchDispatchDelay the announce is
        // still the pre-switch spell, so a re-tick recomputes sameSpell=false and
        // would re-arm the identical switch — the double-cast this fixes.
        string toKey = to ?? string.Empty;
        if (_pendingSwitch is { } pend
            && string.Equals(pend.Target, target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pend.Spell, toKey, StringComparison.OrdinalIgnoreCase))
            return;
        _pendingSwitch = (target, toKey);

        void Dispatch()
        {
            _pendingSwitch = null;   // consumed — the next round may re-arm if needed
            if (_disposed || !Fighting() || _combatOff) return;
            // The announce already IS the switch target — e.g. a between-round-cast
            // resume re-announced it while this deferral sat pending. Firing again
            // double-casts the alternate (report paradigm-20260819-121003). No-op.
            if (to is { } toSpell
                && string.Equals(_announcedSpellCode, toSpell, StringComparison.OrdinalIgnoreCase))
                return;
            // A kill landed since this switch was armed and the death→re-observe path
            // already re-picked a SAME-SPECIES survivor and fired a fresh attack at it
            // (_attackSentSinceDeath). This switch was armed for the DEAD instance;
            // firing it now is a second attack on the survivor — the reported
            // double-attack (report paradigm-20260903-113054). The name / TargetPresent
            // guard below can't catch it because the survivor shares the killed mob's
            // RawName. Skip (mirrors the resume path's death-interrupt guard).
            if (DateTimeOffset.Now - _lastDeathAt < DeathInterruptWindow && _attackSentSinceDeath)
            {
                _log?.Combat(LogCategory,
                    $"spell {reason} {from ?? "?"}→{to ?? "?"} at '{target}' skipped — a kill re-picked a "
                    + "same-species survivor and already re-attacked; not double-firing at it");
                return;
            }
            // A same-burst / adjacent-packet kill / departure nulls both target latches;
            // either mismatch means the switch would land on a corpse (or a re-picked mob).
            if (!string.Equals(_castingSpellTarget, target, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase)
                || _classifier.Current is not { } obs
                || !TargetPresent(obs, target))
            {
                _log?.Combat(LogCategory,
                    $"spell {reason} {from ?? "?"}→{to ?? "?"} at '{target}' skipped — target "
                    + "gone before the deferred dispatch (kill/leave landed first); no corpse-cast");
                return;
            }
            if (TryBuildCandidate(obs, target) is { } cand)
            {
                LogSpellReannounce(reason, from, to, target, obs);
                DispatchRoundAction(settings, cand, CountEngageable(obs), obs, bypassRecastInterval: true);
            }
        }

        if (_scheduleSwitchDispatch is { } schedule)
            schedule(SwitchDispatchDelay, Dispatch);
        else
            _post(Dispatch);
    }

    // A spell whose targeting scope is the room (DebuffTargeting.IsAreaEnemy). False
    // when the code can't be resolved.
    private bool IsRoomScoped(string? code)
        => code is not null && _resolveSpellByCode?.Invoke(code) is { } spell
           && DebuffTargeting.IsAreaEnemy(spell.Targets);

    // The spell the server is repeating for us hits the room: a multi-attack slot,
    // or a room spell sitting in another slot.
    private bool AnnouncedSpellIsRoomWide()
        => _lastCastAction is CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2
           || IsRoomScoped(_announcedSpellCode);

    // The chooser decisions that own the round as a SPELL (enter spell mode via the
    // DispatchRoundAction default branch) — i.e. a switch off the weapon is warranted.
    // Excludes WeaponAttack (the thing we're leaving), Backstab (a stealth opener, not
    // a cascade rung), and the between-round debuffs (resolved separately, not round
    // owners).
    private static bool IsAttackSpellRoundOwner(CombatSpellAction action) => action is
        CombatSpellAction.NormalAttackSpell or CombatSpellAction.AlternateAttackSpell
        or CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2
        or CombatSpellAction.DrainSpell;

    // The AlternateSpellPhysical/AlternatePhysicalSpell/CustomRoundCycle branches
    // above force a fresh dispatch EVERY round — unlike DeferSwitchDispatch's
    // spell-only cascade, this can re-announce a WEAPON action too, so it can't
    // reuse that guard's _castingSpellTarget check (null in a physical-phase
    // round would wrongly skip a live target). Same underlying race though: the
    // heartbeat is damage-line driven, so it decides on a round's own killing
    // blow ahead of the "dies." / "You gain N experience." that would drop the
    // target. Defer by SwitchDispatchDelay and re-validate against _currentTarget
    // + the freshest room view before dispatching (report paradigm-20260905-205200:
    // every kill wasted a round re-announcing at the corpse — "You don't see X
    // here!").
    private void DeferRoundContinuation(CombatSettings settings, string target, string reason)
    {
        void Dispatch()
        {
            if (_disposed || !Fighting() || _combatOff) return;
            if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase)
                || _classifier.Current is not { } obs
                || !TargetPresent(obs, target))
            {
                _log?.Combat(LogCategory,
                    $"round continuation ({reason}) at '{target}' skipped — target gone before "
                    + "the deferred dispatch (kill/leave landed first); no corpse-cast");
                return;
            }
            if (TryBuildCandidate(obs, target) is { } cand)
                DispatchRoundAction(settings, cand, CountEngageable(obs), obs);
            else
                _castingSpellTarget = null;   // can't rebuild the target — drop; next observe re-picks
        }

        if (_scheduleSwitchDispatch is { } schedule)
            schedule(SwitchDispatchDelay, Dispatch);
        else
            _post(Dispatch);
    }

    // Diagnostic for the caster-side corpse-cast / no-re-engage class (reports
    // paradigm-20260815-135756 / -135853): the combat tick is DAMAGE-LINE driven, so a
    // killing blow's damage line fires this heartbeat, which re-announces the next spell
    // at the target BEFORE the death / exp / *Combat Off* lines that follow it in the same
    // server burst have dropped it — the caster re-announce racing the kill (unlike the
    // passive physical path, which never re-announces per round). Logs the exact timing
    // relative to the last attack / exp / death so a capture shows whether the re-announce
    // fired ahead of the kill signal. Combat-level — only with combat diagnostics on.
    private void LogSpellReannounce(string reason, string? from, string? to, string target, RoomEntitiesObservation obs)
    {
        if (_log is null) return;
        DateTimeOffset now = DateTimeOffset.Now;
        _log.Combat(LogCategory,
            $"spell {reason} {from ?? "?"}→{to ?? "?"} at '{target}' — "
            + $"sinceAttack={(now - _lastAttackSentAt).TotalMilliseconds:F0}ms, "
            + $"sinceExp={(now - _lastExpGainAt).TotalMilliseconds:F0}ms, "
            + $"sinceDeath={(now - _lastDeathAt).TotalMilliseconds:F0}ms, "
            + $"attackSentSinceDeath={_attackSentSinceDeath}, "
            + $"targetPresent={TargetPresent(obs, target)}, engageable={CountEngageable(obs)}");
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
        // Auto-Combat gate — mirrors PickInBetweenDebuff's own !Fighting() check.
        // The single-target debuff is gated by Auto-Combat (see CombatSpellChooser
        // .TryDebuffing); without this, the see-hidden force-clear override — which
        // engages hostiles with Auto-Combat OFF and reaches here — would leak the
        // debuff now that the Auto-Nuke gate no longer blocks the single rung.
        if (!CombatSpellsWired || _cast is null || !Fighting()) return NoDebuffOnThisEngage();

        CombatSpellContext ctx = BuildContext(
            settings, obs, picked.RawName, CountEngageable(obs), picked.MonsterNumber);
        if (_spellChooser.ChooseDebuff(settings, ctx) is not { } decision) return NoDebuffOnThisEngage();
        if (!DebuffDecisionAllowed(decision, ctx, picked.MonsterNumber)) return NoDebuffOnThisEngage();
        if (AreaDebuffPastFirstRound(settings, decision)) return NoDebuffOnThisEngage();
        // A cast at the monster before the backstab spends the surprise (reports
        // paradigm-20260928-030642 / -031828), so the debuff waits for the round after
        // the backstab; the chooser then offers it again.
        if (_spellChooser.WouldBackstab(settings, ctx))
        {
            _log?.Combat(LogCategory,
                $"pre-attack debuff {decision.Spell} held — the backstab opener goes first");
            return false;
        }

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

        // The evaluator can also return null because the round's ONE between-round
        // slot is already spent (a survival / self-buff cast took it) — not because
        // nothing was due. Firing the debuff now would only draw the server's
        // "already cast a spell this round" rejection, whose failure latch then
        // blocks the very attack we defer behind it (report paradigm-20260902-134633:
        // a pre-attack isto right after a blur delayed the msto ~3s to the next tick).
        // Stand the debuff down and let the caller dispatch the attack directly this
        // round; the debuff re-offers next round once the slot frees.
        if (_isBetweenRoundSlotUsed?.Invoke() == true)
        {
            _log?.Combat(LogCategory,
                $"pre-attack debuff {decision.Spell} held — between-round slot already spent this " +
                "round; dispatching the attack directly (debuff re-offers next round)");
            return false;
        }

        // Nothing higher-priority was queued — fire the debuff before the attack.
        // Open the round's per-target caps exactly as DispatchRoundAction would on
        // a new target (the AoE / debuff per-room counters are untouched).
        if (!string.Equals(_currentTarget, picked.RawName, StringComparison.OrdinalIgnoreCase))
        {
            _spellChooser.ResetForNewTarget();
            _alternationRound = 0;
            _lastAlternationAdvanceAt = DateTimeOffset.MinValue;
            // Anchor the tally clock at the engage moment (see DispatchRoundAction's
            // new-target reset): a MinValue reset let a multi-mob swing tick tally the
            // attack spell before it fired, cap-switching a MaxCasts-1 nuke off its
            // own first round (report paradigm-20260815-202241).
            _lastAttackTallyAt = _now();
        }

        // Area debuffs blanket the room and MUST be cast bare — `stnk`, never
        // `stnk <mob>` — whether from the AoE slot or a monster's debuff override; a
        // single-target debuff keeps its mob.
        string? castTarget =
            decision.Action == CombatSpellAction.AreaDebuff || IsRoomScoped(decision.Spell)
                ? null : picked.RawName;
        // A pre-attack debuff is offensive output too — hold it while AttackPrevented is
        // active (fear doesn't stop debuffs). Returning false lets the caller try the
        // attack directly, which its own AttacksBlocked gate then also holds, so nothing
        // goes out until the block clears.
        if (AttacksBlocked(debuff: true) || !_cast.TryCast(decision.Spell!, castTarget, bypassRoundCooldown: true))
            return false;

        _spellChooser.MarkCast(decision, picked.RawName, ctx.RoomMobKeys);
        // Optimistic send — remember the mark so a server rejection can roll it back.
        _debuffAwaitingConfirm = (decision, picked.RawName, ctx.RoomMobKeys, _now());
        _currentTarget = picked.RawName;
        // Stamp the between-round cast so the debuff's *Combat Off* is attributed to
        // OUR cast (not misread as a kill by OnCombatStatus's no-between-round-cast
        // branch). On a fresh engage _castingSpellTarget is null, so this does NOT arm
        // _spellAttackOwed — we are firing the attack now, not owing it to a resume.
        NoteBetweenRoundCast();
        // Spend the round's between-round slot in the director too — this cast
        // fired directly (not via Evaluate), so without this the director could
        // queue a SECOND between-round cast this round and draw the "already cast
        // this round" rejection.
        _markBetweenRoundSlotUsed?.Invoke(decision.Spell!);
        // Fire the combat attack in the SAME round, right behind the debuff, instead
        // of waiting for the debuff's *Combat Off* (a whole wasted combat round —
        // report paradigm-20260825-103417). The between-round debuff and the combat
        // attack are independent slots, so both go out this round. DeferPostDebuffAttack
        // spaces the attack a short window behind the debuff (so the two casts don't
        // overrun the server's flood guard) and re-validates the target still lives (an
        // AoE debuff may have killed it) so the attack never corpse-casts. Arming the
        // suppress with this cast's between-round stamp stops the debuff's own *Combat
        // Off* (and any quick Off from the attack itself) from ALSO firing the attack.
        _suppressResumeForBetweenRoundStamp = _betweenRoundCastAt;
        DeferPostDebuffAttack(settings, picked.RawName);
        _log?.Combat(LogCategory,
            $"pre-attack debuff {decision.Spell} at {picked.RawName} — " +
            "firing the combat attack immediately after (same round)");
        return true;
    }

    // Fire this round's combat attack a short window after a pre-attack debuff so both
    // land in the SAME round. Re-uses the deferred-dispatch guard the cap-switch path
    // trusts (DeferSwitchDispatch): re-validate the target still lives before firing so
    // a debuff that killed it doesn't corpse-cast; the debuff is already MarkCast, so
    // DispatchRoundAction now picks the attack. Unlike the old resume-after-*Combat
    // Off* path this fires the attack proactively and pairs with
    // _suppressResumeForBetweenRoundStamp so the debuff's Off can't fire it a second time.
    private void DeferPostDebuffAttack(CombatSettings settings, string target)
    {
        DateTimeOffset debuffAt = DateTimeOffset.Now;
        void Dispatch()
        {
            if (_disposed || !Fighting()) return;
            // Something else already sent this round's attack in the gap: a monster
            // walking in re-ran the room dispatch (the interrupt resume) before this
            // fired. Sending it again re-announced the spell, breaking and restarting
            // combat for nothing (report paradigm-20261007-122734: isto, hsto, hsto).
            if (_lastAttackSentAt >= debuffAt)
            {
                _log?.Combat(LogCategory,
                    $"post-debuff attack at '{target}' skipped — the attack already went out behind the debuff");
                return;
            }
            if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase)
                || _classifier.Current is not { } obs
                || !TargetPresent(obs, target))
            {
                _log?.Combat(LogCategory,
                    $"post-debuff attack at '{target}' skipped — target gone before the "
                    + "deferred dispatch (the debuff's kill landed first); no corpse-cast");
                return;
            }
            // bypassRecastInterval: the attack lands right behind the debuff's own
            // cast (within the ~200ms spacing), so without the bypass CastCoordinator's
            // recast interval defers an attack SPELL to the next tick and the round is
            // lost — the exact "missed a combat round" this fix removes. The debuff and
            // the attack are independent slots server-side, so the back-to-back cast is
            // legitimate (mirrors the between-round-cast spell-resume path).
            if (TryBuildCandidate(obs, target) is { } cand)
                DispatchRoundAction(settings, cand, CountEngageable(obs), obs,
                    bypassRecastInterval: true);
        }

        if (_scheduleSwitchDispatch is { } schedule)
            schedule(SwitchDispatchDelay, Dispatch);
        else
            _post(Dispatch);
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
        if (!CombatSpellsWired || !Fighting()) return null;
        if (_currentTarget is not { } target) return null;
        if (_classifier.Current is not { } obs) return null;
        if (!TargetPresent(obs, target)) return null;

        // Corpse-cast guard, mirroring the attack heartbeat's death window
        // (CombatManager.cs, _lastDeathAt / _attackSentSinceDeath): the recurring
        // debuff is evaluated on the killing damage-line tick — before the exp
        // line nulls _currentTarget and before the death line drops the roster —
        // so within the death-interrupt window after an inferred kill, and before
        // an attack has confirmed a live survivor, _currentTarget may be a corpse
        // the roster hasn't dropped yet. Hold rather than dispatch a debuff at it.
        if (DateTimeOffset.Now - _lastDeathAt < DeathInterruptWindow && !_attackSentSinceDeath)
            return null;

        CombatSettings settings = _readSettings();
        int monsterNumber = ResolveMonsterNumber(obs, target);
        CombatSpellContext ctx = BuildContext(
            settings, obs, target, CountEngageable(obs), monsterNumber);
        if (_spellChooser.ChooseDebuff(settings, ctx) is not { } decision) return null;
        if (!DebuffDecisionAllowed(decision, ctx, monsterNumber)) return null;
        if (AreaDebuffPastFirstRound(settings, decision)) return null;
        if (_spellChooser.WouldBackstab(settings, ctx)) return null;   // the backstab goes first

        // An area debuff (e.g. stinking cloud) blankets the room and MUST be cast
        // bare — `stnk`, never `stnk <mob>`. A single-target debuff keeps its mob.
        // The combat action verb is re-sent after the in-between cast by the
        // existing combat-off resume path.
        _pendingDebuff = decision;
        _pendingDebuffTarget = target;
        _pendingDebuffRoomKeys = ctx.RoomMobKeys;
        return (decision.Spell!,
            decision.Action == CombatSpellAction.AreaDebuff ? null : target);
    }

    // Guard the Settings → Combat debuff slots: the chosen spell must be a
    // between-round (0-energy) spell — that's what separates a debuff from an
    // attack spell (lbol/mmis cost 500-1000 energy) — AND its targeting scope must
    // fit the slot (single-enemy for the single-target slot, an area/room scope
    // for the AoE slot). Blocks a mis-slotted spell before it reaches the wire
    // (an attack spell as a debuff, or a targeted spell as an AoE / vice-versa)
    // rather than letting the server reject a malformed cast and the engine churn.
    // Fails OPEN when the cast-code can't be resolved (unknown / no catalog yet) —
    // the guard is about a resolvable mismatch, not a resolution gap. The
    // per-monster debuff OVERRIDE is held to the same rules: an attack spell there
    // (e.g. mmis) was cast as a between-round debuff and then overwritten by the
    // attack sent right behind it, so it never landed (reports
    // paradigm-20260928-030642 / -031828). An opener attack spell belongs in the
    // per-monster attack override, which has its own cast count.
    private bool DebuffDecisionAllowed(CombatSpellDecision decision, in CombatSpellContext ctx, int monsterNumber)
    {
        if (decision.Spell is not { } code) return false;
        bool isOverride = ctx.OverridePreAttackSpell is { } ov
            && decision.Action == CombatSpellAction.SingleDebuff
            && string.Equals(code, ov, StringComparison.OrdinalIgnoreCase);
        if (_resolveSpellByCode?.Invoke(code) is not { } spell) return true;

        int energy = spell.Formula.EnergyCost;
        int targets = spell.Targets;
        bool ok = decision.Action switch
        {
            // A monster's debuff override may name a room debuff too (cast bare).
            CombatSpellAction.SingleDebuff =>
                DebuffTargeting.IsBetweenRound(energy)
                && (DebuffTargeting.IsSingleTargetEnemy(targets) || isOverride && DebuffTargeting.IsAreaEnemy(targets)),
            CombatSpellAction.AreaDebuff =>
                DebuffTargeting.IsBetweenRound(energy) && DebuffTargeting.IsAreaEnemy(targets),
            _ => true,
        };
        if (!ok) WarnInvalidDebuffSlot(code, decision.Action, energy, targets, isOverride ? monsterNumber : null);
        return ok;
    }

    // The AoE-debuff "first round only" gate (CombatSettings.AreaDebuffFirstRoundOnly):
    // once combat has been seen in this room, abandon the area debuff — a debuff that
    // couldn't win the between-round slot on entry (a higher-priority buff/heal took it)
    // is wasted mana on a round-2+ half-dead room. Only the AREA debuff is gated; the
    // single-target debuff is unaffected. Logged once per skip so a capture explains the
    // hold. Off by default — the debuff otherwise keeps retrying until it lands once.
    private bool AreaDebuffPastFirstRound(CombatSettings settings, CombatSpellDecision decision)
    {
        if (decision.Action != CombatSpellAction.AreaDebuff) return false;
        if (!settings.AreaDebuffFirstRoundOnly || !_combatSeenThisRoom) return false;
        _log?.Combat(LogCategory,
            $"AoE debuff {decision.Spell} skipped — 'first round only' is set and combat has already been seen this room");
        return true;
    }

    private void WarnInvalidDebuffSlot(string code, CombatSpellAction action, int energy, int targets,
        int? overrideMonster = null)
    {
        // One line per bad slot / override, not per round.
        if (!_warnedInvalidDebuffSlots.Add(overrideMonster is { } n ? $"{code}#{n}" : code)) return;
        string slot = action == CombatSpellAction.AreaDebuff ? "AoE" : "single-target";
        string why = !DebuffTargeting.IsBetweenRound(energy)
            ? $"it costs {energy} energy — a debuff needs a 0-energy between-round spell (an attack spell can't be a debuff)"
            : $"its targeting scope ({targets}) doesn't fit the {slot} slot";
        _log?.Warn(LogCategory, overrideMonster is { } mob
            ? $"debuff override misconfigured on monster #{mob}: '{code}' won't cast as its debuff — {why}. " +
              "For an opener attack spell use the monster's attack-spell override (it has a cast count). Fix it in Game Data → Monsters."
            : $"debuff slot misconfigured: '{code}' won't cast as the {slot} debuff — {why}. Fix it in Settings → Combat.");
    }

    // Confirm the in-between debuff the director just sent. Marks the stashed
    // decision cast (advancing the once-per-room / once-per-target bookkeeping so
    // it won't re-fire) and clears the stash. Called only on a successful
    // coordinator cast — a blocked cast leaves the stash so the next window
    // retries. No-ops when nothing is pending.
    public void CommitInBetweenDebuff()
    {
        if (_pendingDebuff is not { } decision) return;
        _spellChooser.MarkCast(decision, _pendingDebuffTarget ?? string.Empty, _pendingDebuffRoomKeys);
        // Optimistic send — remember the mark so a server rejection can roll it back.
        _debuffAwaitingConfirm = (decision, _pendingDebuffTarget ?? string.Empty, _pendingDebuffRoomKeys, _now());
        _pendingDebuff = null;
        _pendingDebuffTarget = null;
        _pendingDebuffRoomKeys = null;
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
            if (MonsterEngagement.IsEngageable(overlay, _userEngagedInstances.Contains(e.RawName)))
                count++;
        }
        return count;
    }

    // The RawNames of the engageable monsters in the room — the roster the AoE debuff
    // tags on cast and checks for new arrivals against. Mirrors CountEngageable's filter
    // exactly (unknown number → engageable, else the overlay), so EnemyCount and
    // RoomMobKeys never disagree about which mobs count.
    private List<string> CollectEngageableMobKeys(RoomEntitiesObservation obs)
    {
        var keys = new List<string>();
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) { keys.Add(e.RawName); continue; }
            if (MonsterEngagement.IsEngageable(ResolveOverlay(n), _userEngagedInstances.Contains(e.RawName)))
                keys.Add(e.RawName);
        }
        return keys;
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
        (string? attackOverride, int? attackCap) = AttackOverrideFor(monsterNumber, ma, maxMa, settings.SpellManaThresholdMode);
        (string? altAttackOverride, int? altAttackCap) = AltAttackOverrideFor(monsterNumber, ma, maxMa, settings.SpellManaThresholdMode);
        (string? preAttackOverride, int? preAttackCap) = PreAttackOverrideFor(monsterNumber, ma, maxMa, settings.SpellManaThresholdMode);
        (bool hpBelowDrain, bool drainEligible) = DrainGates(settings, monsterNumber);

        // Level / element-resist blocks are computed against the EFFECTIVE spell in
        // each single-target rung — the per-monster override when it's active this
        // round (its own mana floor was already applied above), else the configured
        // slot — so an override spell is gated on ITS OWN ReqLevel / element, not the
        // global slot's.
        string? singleEff = preAttackOverride ?? NullIfBlank(settings.SingleTargetDebuffSpell.SpellName);
        string? normalEff = attackOverride    ?? NullIfBlank(settings.NormalAttackSpell.SpellName);
        string? altEff    = altAttackOverride ?? NullIfBlank(settings.AlternateAttackSpell.SpellName);

        bool roomAttackHeld = _roomAttackHold?.Invoke() is not null;
        IReadOnlySet<CombatSpellAction>? levelBlocked = LevelBlockedFor(monsterNumber, singleEff, normalEff, altEff);
        if (roomAttackHeld) levelBlocked = WithRoomScopedHeld(levelBlocked, singleEff, normalEff, altEff);

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
            LevelBlockedActions: levelBlocked,
            AllowNukes:          !roomAttackHeld && (_autoNukeGate?.Invoke() ?? true),
            ResistBlockedActions: ResistBlockedFor(monsterNumber, normalEff, altEff),
            TargetTypeBlockedActions: TargetTypeBlockedFor(monsterNumber, normalEff, altEff),
            TargetDontBackstab:  IsDontBackstab(monsterNumber),
            OverrideAttackSpell:       attackOverride,
            OverrideAttackMaxCasts:    attackCap,
            OverridePreAttackSpell:    preAttackOverride,
            OverridePreAttackMaxCasts: preAttackCap,
            OverrideAltAttackSpell:    altAttackOverride,
            OverrideAltAttackMaxCasts: altAttackCap,
            WeaponIneffective:   WeaponPathExhausted(
                settings, ResolveSpeciesByName(target), monsterNumber),
            AlternationPreferSpell: AlternationPreferSpell(settings),
            HpBelowDrainTrigger: hpBelowDrain,
            DrainTargetEligible: drainEligible,
            RoomMobKeys:         CollectEngageableMobKeys(obs),
            ManaCostOf:          _spellManaCost);
    }

    // Resolve the drain spell's two live gates for a target: whether HP has fallen
    // to the configured %-trigger, and whether the target is drain-eligible (living
    // AND not undead). Both are false when no drain spell is configured, so the
    // chooser's drain rung stays inert. Eligibility fails OPEN when the index is
    // unwired or the number is unknown (the reactive "no effect" line is the
    // backstop); the HP gate fails CLOSED when the HP reader is unwired. There is no
    // release band: the drain is a pure HP-driven survival cast that fires only while
    // HP is at/under the trigger and hands the round back to the normal attack the
    // moment HP recovers above it (report paradigm-20260827-153630).
    private (bool HpBelowTrigger, bool Eligible) DrainGates(
        CombatSettings settings, int monsterNumber)
    {
        if (string.IsNullOrWhiteSpace(settings.DrainSpell.SpellName)) return (false, false);

        bool hpBelowTrigger = false;
        if (_readHp is not null && settings.DrainHpTrigger > 0)
        {
            (int hp, int maxHp) = _readHp();
            if (maxHp > 0)
                hpBelowTrigger = hp <= (int)Math.Round(maxHp * settings.DrainHpTrigger / 100.0);
        }

        bool eligible = _monsterLife?.CanDrain(monsterNumber) ?? true;
        return (hpBelowTrigger, eligible);
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
    // (null, null) when there's no active override (CombatSpellGates.ResolveOverride
    // has the "active" conditions, mana floor included).
    private (string? Spell, int? Cap) AttackOverrideFor(int monsterNumber, int mana, int maxMana, ThresholdMode manaMode)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return CombatSpellGates.ResolveOverride(overlay.OverrideAttackSpellId, overlay.OverrideAttackCount,
            overlay.OverrideAttackMinMana, mana, maxMana, manaMode, _spellShortByNumber);
    }

    // The ALTERNATE-attack counterpart of AttackOverrideFor.
    private (string? Spell, int? Cap) AltAttackOverrideFor(int monsterNumber, int mana, int maxMana, ThresholdMode manaMode)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return CombatSpellGates.ResolveOverride(overlay.OverrideAltAttackSpellId, overlay.OverrideAltAttackCount,
            overlay.OverrideAltAttackMinMana, mana, maxMana, manaMode, _spellShortByNumber);
    }

    // The per-monster PHYSICAL override command for this species, or null when none
    // is set. A raw verb ("attack", "bash") that replaces the weapon command on a
    // round the decision engine already chose physical (it does NOT force physical
    // or suppress the spell rungs); no cast-rung, no mana gate; trimmed to null when
    // blank. The server repeats it like any attack command.
    private string? PhysicalCommandOverrideFor(int monsterNumber)
    {
        if (monsterNumber < 0) return null;
        string? cmd = ResolveOverlay(monsterNumber).OverridePhysicalCommand;
        return string.IsNullOrWhiteSpace(cmd) ? null : cmd.Trim();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Resolve this monster's override pre-attack spell to a (cast-code, cap) pair,
    // or (null, null) when there's no active override.
    private (string? Spell, int? Cap) PreAttackOverrideFor(int monsterNumber, int mana, int maxMana, ThresholdMode manaMode)
    {
        if (monsterNumber < 0) return (null, null);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        return CombatSpellGates.ResolveOverride(overlay.OverridePreAttackSpellId, overlay.OverridePreAttackCount,
            overlay.OverridePreAttackMinMana, mana, maxMana, manaMode, _spellShortByNumber);
    }

    // An override slot's cast-code and cap with no mana floor applied — for the
    // callers that only ask WHICH spell occupies a rung.
    private (string? Spell, int? Cap) ResolveSpellOverride(int? spellId, int? count) =>
        CombatSpellGates.ResolveOverride(spellId, count, minMana: null, 0, 0, ThresholdMode.Absolute, _spellShortByNumber);

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

        int normalHit = NormalHitMagic(settings);
        if (normalHit < 0) return false;                // unknown normal → don't second-guess
        if (normalHit >= magical) return false;         // normal already hits → keep it

        // Normal can't hit. Swap only if the alternate actually clears the bar.
        return AlternateHitMagic(settings) >= magical;
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
        if (!WeaponCannotHit(NormalHitMagic(settings), resolvedSpecies, monsterNumber,
                _normalWeaponFailedMonsters))
            return false;

        if (!string.IsNullOrWhiteSpace(settings.AlternateWeapon)
            && !WeaponCannotHit(AlternateHitMagic(settings),
                resolvedSpecies, monsterNumber, _alternateWeaponFailedMonsters))
            return false;

        return true;
    }

    // One weapon's effectiveness verdict against the current target: out when the
    // room fail-set already names this species OR game data's HitMagic can't clear
    // the monster's Magical level. Fails open (effective) when the eligibility
    // indexes aren't wired or the weapon / monster levels are unknown — we don't
    // second-guess a swing on missing data.
    private bool WeaponCannotHit(
        int hit, string resolvedSpecies, int monsterNumber, HashSet<string> failSet)
    {
        if (!string.IsNullOrEmpty(resolvedSpecies) && failSet.Contains(resolvedSpecies))
            return true;
        if (_monsterMagic is null || _itemMagic is null) return false;
        int magical = _monsterMagic.MagicalLevel(monsterNumber);
        if (magical <= 0) return false;                 // any weapon hits
        if (hit < 0) return false;                       // unknown weapon → don't assume
        return hit < magical;
    }

    // Why the weapons are out against a monster, for the skip line in the log: the
    // magic it needs against what each weapon hits with, and any no-effect evidence
    // from this room. Empty when the monster needs no magic and nothing was observed.
    private string WeaponGateNote(CombatSettings settings, int monsterNumber, string? species)
    {
        int magical = _monsterMagic?.MagicalLevel(monsterNumber) ?? 0;
        bool normalFailed = !string.IsNullOrEmpty(species) && _normalWeaponFailedMonsters.Contains(species);
        bool altFailed = !string.IsNullOrEmpty(species) && _alternateWeaponFailedMonsters.Contains(species);
        if (magical <= 0 && !normalFailed && !altFailed) return string.Empty;
        return $" (needs hit magic {magical}; normal attack `{settings.NormalAttackCommand}` hits {NormalHitMagic(settings)}" +
               (MartialArtsCommand.IsStrike(settings.NormalAttackCommand) ? " as a martial-arts strike, weapon not used" : string.Empty) +
               (normalFailed ? ", no effect seen" : string.Empty) +
               $"; alternate `{settings.AlternateAttackCommand}` hits {AlternateHitMagic(settings)}" +
               (altFailed ? ", no effect seen" : string.Empty) +
               $"; the character's own hit magic is {Math.Max(0, _innateHitMagic?.Invoke() ?? 0)}; -1 = unknown)";
    }

    // The magic-hit level of the weapon that swings on the normal side, or -1 when
    // unknown (callers fail open). That is the configured normal weapon while it
    // can come to hand. When the character doesn't have it, the swap to it can't
    // land and whatever is on the hand does the swinging, so that weapon is judged
    // instead: a profile still naming the plain weapon a magical one replaced wrote
    // off every monster that needs magic to hit (report paradigm-20261007-182916).
    private int NormalHitMagic(CombatSettings settings) =>
        AttackHitMagic(settings.NormalAttackCommand, NormalWeaponHitMagic(settings));

    private int NormalWeaponHitMagic(CombatSettings settings)
    {
        if (_itemMagic is null) return -1;
        int configured = _itemMagic.HitMagic(settings.NormalWeapon);
        if (string.IsNullOrWhiteSpace(settings.NormalWeapon)) return configured;
        if (_hasItem?.Invoke(settings.NormalWeapon) != false) return configured;

        string? worn = _readWornWeapon?.Invoke();
        if (string.IsNullOrWhiteSpace(worn)) return configured;
        // The alternate on the hand says nothing about the normal side.
        if (!string.IsNullOrWhiteSpace(settings.AlternateWeapon)
            && string.Equals(worn.Trim(), settings.AlternateWeapon.Trim(), StringComparison.OrdinalIgnoreCase))
            return configured;
        return _itemMagic.HitMagic(worn);
    }

    // For the bug report: what the gate weighs besides the weapons.
    internal (int Innate, string? NormalCommand, string? AlternateCommand) SnapshotHitMagicInputs()
    {
        CombatSettings settings = _readSettings();
        return (Math.Max(0, _innateHitMagic?.Invoke() ?? 0), settings.NormalAttackCommand, settings.AlternateAttackCommand);
    }

    // The same for the alternate side: its weapon under its own attack command.
    private int AlternateHitMagic(CombatSettings settings) =>
        AttackHitMagic(settings.AlternateAttackCommand, _itemMagic?.HitMagic(settings.AlternateWeapon) ?? -1);

    // The magic-hit level an attack lands with, or -1 when unknown (callers fail
    // open). A class or race can carry hit magic of its own (a Mystic, a
    // Witchunter), and the weapon is only half the answer:
    //   - a martial-arts strike is thrown without the weapon, so it lands with the
    //     character's own hit magic and nothing else. Judging it by the weapon in
    //     hand wrote off every monster the weapon couldn't touch, and a Mystic
    //     walked past (and was bitten by) monsters its punches hurt.
    //   - a weapon swing has both: added together on Stock, the higher of the two
    //     on Paradigm. An unknown weapon stays unknown unless the character's own
    //     is the answer either way, which it can't tell from here.
    // GAME_MECHANICS "Weapons: "no effect" lines and the magical-weapon requirement".
    private int AttackHitMagic(string? attackCommand, int weaponHit)
    {
        int innate = Math.Max(0, _innateHitMagic?.Invoke() ?? 0);
        if (MartialArtsCommand.IsStrike(attackCommand)) return innate;
        if (weaponHit < 0) return -1;
        return _hitMagicAdds?.Invoke() == true ? weaponHit + innate : Math.Max(weaponHit, innate);
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

        DispatchRoundAction(settings, cand, CountEngageable(obs), obs, bypassRecastInterval: true);
        return _castingSpellTarget is not null;
    }

    // Game-data spell blocks for this target (CombatSpellGates has the rules). The
    // caller passes the EFFECTIVE cast-code per rung — the per-monster override when
    // active, else the configured slot.
    private IReadOnlySet<CombatSpellAction>? LevelBlockedFor(
        int monsterNumber, string? singleCode, string? normalCode, string? altCode) =>
        CombatSpellGates.LevelBlocked(_monsterMagic, _spellReqLevel, monsterNumber, singleCode, normalCode, altCode);

    private IReadOnlySet<CombatSpellAction>? ResistBlockedFor(
        int monsterNumber, string? normalCode, string? altCode) =>
        CombatSpellGates.ResistBlocked(_monsterResist, _spellAttackType, monsterNumber, normalCode, altCode);

    private IReadOnlySet<CombatSpellAction>? TargetTypeBlockedFor(
        int monsterNumber, string? normalCode, string? altCode) =>
        CombatSpellGates.TargetTypeBlocked(_spellTargetType, _monsterLife, monsterNumber, normalCode, altCode);

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

    // One monster the engine has seen in the current room, with the exact
    // engageability verdict + weapon/spell magic data behind it. Frozen for the
    // bug report so a "skip un-actionable … Unkillable" decision is diagnosable
    // from the capture without combat logging being on or the scrollback still
    // holding the line.
    internal readonly record struct RoomEngageSnapshot(
        int MonsterNumber,
        string Species,
        int Magical,
        int SpellImmu,
        int NormalWeaponHit,
        int AltWeaponHit,
        EngageAssessment Assessment,
        string? UnengageableReason);

    // Snapshot every monster known in the current room (the room-scoped
    // _speciesByNumber map, cleared on room change) with its live EngageAssessment
    // and the raw magic numbers the gate weighs. Reuses the same _readSettings() /
    // magic indexes the live path uses, so the report shows precisely what the
    // engine decided.
    internal IReadOnlyList<RoomEngageSnapshot> SnapshotRoomEngage()
    {
        List<RoomEngageSnapshot> list = new(_speciesByNumber.Count);
        if (_speciesByNumber.Count == 0) return list;

        CombatSettings settings = _readSettings();
        int normalHit = NormalHitMagic(settings);
        int altHit = AlternateHitMagic(settings);
        foreach ((int number, string species) in _speciesByNumber)
        {
            int magical = _monsterMagic?.MagicalLevel(number) ?? -1;
            int immu = _monsterMagic?.SpellImmunity(number) ?? -1;
            list.Add(new RoomEngageSnapshot(
                number, species, magical, immu, normalHit, altHit,
                AssessEngage(settings, number, species),
                UnengageableReason(settings, number)));
        }
        list.Sort((a, b) => a.MonsterNumber.CompareTo(b.MonsterNumber));
        return list;
    }

    // The runtime weapon fail-sets standing behind an Unkillable verdict. These
    // hold observed "no effect" evidence, which UnengageableReason (pure game data)
    // can't show — so a report where the engine walks past a monster game data says
    // it should be able to hit has nothing to explain the decision without them.
    // Room-scoped, cleared with the rest of the per-room state.
    internal (IReadOnlyList<string> Normal, IReadOnlyList<string> Alternate) SnapshotWeaponFailSets()
        => (_normalWeaponFailedMonsters.ToArray(), _alternateWeaponFailedMonsters.ToArray());

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

        // Effective single-target kill spell per rung: the per-monster override when
        // set, else the configured slot — so a monster only an override could kill is
        // still assessed as actionable, not skipped by the walker. The override code
        // is resolved mana-INDEPENDENTLY; its own floor feeds the reserve check below,
        // so a mana-blocked override reads StuckOnMana (transient), never Unkillable.
        // (When both an override and a lower-floor configured slot are set for one
        // rung, engageability follows the override — the user's authoritative pick.)
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        (string? normalCode, int normalFloor) = EffectiveKillSpell(
            overlay.OverrideAttackSpellId, overlay.OverrideAttackMinMana, settings.NormalAttackSpell);
        (string? altCode, int altFloor) = EffectiveKillSpell(
            overlay.OverrideAltAttackSpellId, overlay.OverrideAltAttackMinMana, settings.AlternateAttackSpell);

        // Level / element-resist blocks weigh the EFFECTIVE spell too — an override is
        // gated on its own ReqLevel / element. The single-debuff code is not a kill
        // means, so it never decides this verdict.
        IReadOnlySet<CombatSpellAction>? levelBlocked = LevelBlockedFor(monsterNumber,
            NullIfBlank(settings.SingleTargetDebuffSpell.SpellName), normalCode, altCode);
        IReadOnlySet<CombatSpellAction>? resistBlocked = ResistBlockedFor(monsterNumber, normalCode, altCode);

        bool anyUnaffordable = false;
        ThresholdMode manaMode = settings.SpellManaThresholdMode;

        // A rung is castable this round unless its spell is unconfigured, observed-
        // immune, level- or resist-blocked, or unaffordable (which marks the transient
        // mana stall rather than a permanent block). Shared by the normal + alternate
        // rungs so the effective-spell gating stays in one place.
        EngageAssessment? RungVerdict(string? code, int floor, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            if (immune?.Contains(action) == true) return null;
            if (levelBlocked?.Contains(action) == true) return null;
            if (resistBlocked?.Contains(action) == true) return null;
            if (!CombatSpellChooser.ManaMeetsReserve(floor, ma, maxMa, manaMode))
            {
                anyUnaffordable = true;
                return null;
            }
            return EngageAssessment.CanAct;
        }

        if (RungVerdict(normalCode, normalFloor, CombatSpellAction.NormalAttackSpell) is { } nv) return nv;
        if (RungVerdict(altCode, altFloor, CombatSpellAction.AlternateAttackSpell) is { } av) return av;

        // Nothing castable now: unaffordable-only means a mana tick fixes it
        // (transient); otherwise every spell is permanently blocked / unconfigured.
        return anyUnaffordable ? EngageAssessment.StuckOnMana : EngageAssessment.Unkillable;
    }

    // The effective single-target kill spell for a rung — the per-monster override
    // (resolved mana-independently) when set, else the configured slot — paired with
    // the mana floor that applies (the override's own, or the slot's).
    private (string? Code, int Floor) EffectiveKillSpell(
        int? overrideSpellId, int? overrideMinMana, CombatSpellSlot configured)
    {
        if (ResolveSpellOverride(overrideSpellId, null).Spell is { } code)
            return (code, overrideMinMana ?? 0);
        return (NullIfBlank(configured.SpellName), configured.MinManaPerCast);
    }

    // The reason we can't kill monsterNumber, or null when it's actionable.
    // Physical eligibility is checked first (deterministic, fails open); only when
    // both weapons are proven unable to hit do we consult the attack-spell rungs.
    // An attack spell counts as a kill means when it's present (a per-monster
    // override, else the configured slot) and either the monster has no spell
    // immunity or the spell's ReqLevel clears it. Area / single-target debuffs are
    // NOT kill means and never count here.
    private string? UnengageableReason(CombatSettings settings, int monsterNumber)
    {
        if (_monsterMagic is null || _itemMagic is null) return null;   // unwired → fail open
        if (monsterNumber < 0) return null;                             // unknown monster → fail open

        int magical = _monsterMagic.MagicalLevel(monsterNumber);
        if (magical <= 0) return null;                                  // any weapon hits

        int normalHit = NormalHitMagic(settings);
        if (normalHit < 0) return null;                                 // unknown normal weapon → fail open
        if (normalHit >= magical) return null;                          // normal hits

        int altHit = AlternateHitMagic(settings);
        if (altHit < 0) return null;                                    // unknown alt weapon → fail open
        if (altHit >= magical) return null;                             // alt hits

        // Neither weapon can hit. The monster is actionable only if some attack
        // spell can still land on it — weighing the per-monster override (resolved
        // mana-independently) ahead of the configured slot for the normal/alternate
        // rungs, so an override that clears the immunity keeps the monster killable.
        if (_spellReqLevel is null) return null;                        // can't prove spell-blocked → fail open
        int immu = _monsterMagic.SpellImmunity(monsterNumber);
        MonsterOverlay overlay = ResolveOverlay(monsterNumber);
        string? normalEff = ResolveSpellOverride(overlay.OverrideAttackSpellId, null).Spell
                            ?? NullIfBlank(settings.NormalAttackSpell.SpellName);
        string? altEff = ResolveSpellOverride(overlay.OverrideAltAttackSpellId, null).Spell
                         ?? NullIfBlank(settings.AlternateAttackSpell.SpellName);
        if (AttackSpellCanLand(normalEff, immu)) return null;
        if (AttackSpellCanLand(altEff, immu)) return null;
        if (AttackSpellCanLand(NullIfBlank(settings.MultiAttackSpell.SpellName), immu)) return null;
        if (settings.MultiAttack2Enabled
            && AttackSpellCanLand(NullIfBlank(settings.MultiAttack2Spell.SpellName), immu)) return null;

        return $"weapons HitMagic<{magical} (normal={normalHit} alt={altHit}) " +
               $"and no eligible attack spell (SpellImmu={immu})";
    }

    // True when code is an attack spell that can land on a monster with spell-
    // immunity immu: a null/blank code is not a kill means; an unknown spell fails
    // open (assume it works); otherwise the spell lands iff its ReqLevel is ≥ the
    // immunity. Assumes _spellReqLevel is wired (the only caller checks first).
    private bool AttackSpellCanLand(string? code, int immu)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;              // no spell → not a kill means
        int req = _spellReqLevel!.ReqLevel(code);
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
        if (!Fighting()) return;

        // Act on the first "no effect" of this round's burst only (see the field
        // comment on _immunityHandledThisRound); the rest of the burst is a leftover
        // from the primary and must not tear down the fallback we just switched to.
        if (_immunityHandledThisRound) return;

        // A manual user cast (the user hand-typed an attack / drain spell to probe a
        // target) draws this same "no effect" line — but _lastCastAction /
        // _castingSpellTarget still point at the engine's PRIOR auto cast, so
        // attributing the immunity here would blame the wrong spell (report
        // paradigm-20260818-055955: a hand-typed `dtch` at an elemental marked the
        // engine's `mmis` immune, dropping the cascade straight to melee). The engine
        // holds its own attack during an override round, so the only cast in flight
        // is the user's — a manual probe must not mutate the auto-cascade immune maps.
        if (_userAttackOverride) return;

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
        // re-decide the cascade instantly (below). Only the single-target attack
        // spells gate down — a debuff / multi-attack no-effect isn't ours to act on
        // (one immune mob doesn't mean a room spell isn't hitting the rest). The
        // DRAIN spell is included: a "no effect" here means the target was NonLiving /
        // Undead (a drain can't affect it) that the game-data eligibility index
        // missed — marking it drain-immune makes the chooser fall back to the normal
        // attack for this species, the reactive backstop to the proactive gate.
        if (!CombatSpellsWired) return;
        if (_lastCastAction is not (CombatSpellAction.NormalAttackSpell
                                  or CombatSpellAction.AlternateAttackSpell
                                  or CombatSpellAction.DrainSpell))
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

        // A per-monster physical override owns this species' physical rounds — don't
        // second-guess it with a normal→alternate command fallback.
        int monsterNumber = _classifier.Current is { } obs ? ResolveMonsterNumber(obs, target) : -1;
        if (PhysicalCommandOverrideFor(monsterNumber) is not null) return;

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
    //
    // bypassRecastInterval is REQUIRED here, not just bypassRoundCooldown: the
    // primary was sent < 500 ms ago (same server burst as the no-effect reply), so
    // without it CastCoordinator's MinRecastInterval defers the alternate SPELL to the
    // next combat tick and the round is lost anyway — the alt still lagged ~one round
    // (report paradigm-20260827-081223). The primary is already server-confirmed (the
    // no-effect line proves it) and spent no round, so the alternate is entitled to
    // this round's cast slot — the same reasoning DeferPostDebuffAttack already uses.
    private void ReDecideAfterImmunity(CombatSettings settings)
    {
        if (_castingSpellTarget is not { } target) return;
        if (_classifier.Current is not { } obs) return;
        if (!TargetPresent(obs, target)) return;
        if (TryBuildCandidate(obs, target) is { } cand)
            DispatchRoundAction(settings, cand, CountEngageable(obs), obs, bypassRecastInterval: true);
    }
}
