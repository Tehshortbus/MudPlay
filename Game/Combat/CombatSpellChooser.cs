using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// Per-round combat-spell decision unit. Pure decision + room-scoped cast
// bookkeeping; holds NO wire state. The owning CombatManager calls Choose once
// per attack decision, sends the resulting cast (or its weapon swing for
// WeaponAttack), then calls MarkCast on a successful send so the per-room
// counters stay accurate vs what actually reached the server. ResetForNewRoom
// wipes the bookkeeping when the room clears.
//
// Choose resolves the round's combat action (the 1/round physical swing XOR
// attack spell — a spell IS the round's action, it does not stack with a
// swing). Debuffing is an in-between action (≤1/round), resolved separately by
// ChooseDebuff and cast through the shared in-between window, so it never
// appears in the combat-action decision below.
//
// 1. Backstab opener — fires when a backstab is still pending (DoBackstab on +
//    sneaking + surprise round unspent, folded into ctx.BackstabPending) and the
//    target isn't flagged DontBackstab. The opener is the round's action or
//    nothing — a mid-round BS attempt is a guaranteed fail — so it always fires
//    FIRST when eligible, ahead of the spell-vs-physical choice. When it fires
//    the chooser returns Backstab so the engine's BS path owns the swing and no
//    spell goes out.
// 2. Round action — CombatSettings.ActionOrder picks between spells and the
//    weapon. SpellsFirst runs the attack-spell cascade: MultiAttackSpell while
//    its conditions hold (room count ≥ MinEnemies, mana, cast cap), then
//    NormalAttackSpell, then AlternateAttackSpell, then the weapon; a spell that
//    can't fire this round falls through to the swing. PhysicalFirst swings the
//    weapon and reaches for that same cascade only when the weapon path is proven
//    ineffective against this target (ctx.WeaponIneffective — the normal weapon
//    can't damage it and there's no working alternate), so a spell-less build
//    still just swings.
//
// Mana gating reads SpellManaThresholdMode: Percentage treats MinManaPerCast as
// a 0–100 share of max MA; Absolute treats it as raw points. A debuff that can't
// meet its mana gate this round is skipped (we fall through to the attack phase)
// rather than stalling the round.
//
// Three deterministic "skip this single-target attack spell" inputs flow in via
// CombatSpellContext, each gating NormalAttackSpell / AlternateAttackSpell down
// the cascade to the next slot (then the weapon):
//   - ImmuneAttackSpells — the target's species produced a "Your spell has no
//     effect on X." line this room (a hard targeting/immunity mismatch).
//   - LevelBlockedActions — the spell's ReqLevel is below the monster's SpellImmu
//     (a level gate from game data).
//   - ResistBlockedActions — the target resists the spell's damage *element*
//     ≥ 100%, so it would deal 0 damage or heal the monster (elemental only —
//     Magic Resist and poison are not deterministic; see MonsterResistIndex).
public sealed class CombatSpellChooser
{
    private int _areaDebuffCasts;
    private int _singleDebuffCasts;
    private int _multiAttackCasts;
    private int _normalAttackCasts;
    private int _alternateAttackCasts;
    private int _drainCasts;
    private readonly HashSet<string> _singleDebuffedTargets =
        new(StringComparer.OrdinalIgnoreCase);
    // RawName keys of the mobs present when each AoE debuff cast went out this room.
    // The AoE debuff casts ONCE and "tags" the room; it re-fires (up to its per-room
    // MaxCasts cap) only for a mob that wasn't tagged — a new arrival / summon — rather
    // than repeatedly like an attack spell. Per-room, keyed like _singleDebuffedTargets.
    //
    // The tags survive a wave-clear roster reset (an AoE multi-kill emptying the listed
    // pack while hidden same-species SURVIVORS still attack — report
    // paradigm-20260902-160110, "ISTO fired twice in the same fight"), so a survivor
    // that reveals a round later isn't re-debuffed. They're cleared on a PHYSICAL room
    // change (ResetForNewRoom) AND when the room is confirmed genuinely CLEARED of all
    // hostiles — signalled by the player entering a rest (NoteRoomCleared, wired to a
    // rest posture: you only rest once nothing is left to fight). That lets a same-room
    // RESPAWN be re-debuffed (report paradigm-20260903-070438) without touching the
    // survivor case (a survivor reveals within a round, long before any rest).
    private readonly HashSet<string> _areaDebuffedMobs =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-target weapon latch. Once we've actually been casting a single-target
    // attack spell at the current target and the cascade then lapses (its mana
    // reserve is now unmet OR its MaxCasts rounds are spent), commit to the weapon
    // for the rest of this target — a mana-regen tick that lifts mana back above
    // the reserve must NOT flip us back to the spell mid-fight (CONFIRMED: MaxCasts
    // / mana-reserve is a per-target latch, like observed immunity).
    // _castSingleTargetAttackThisTarget gates the latch on a real spell round
    // having happened, so a fresh target we simply couldn't afford yet is left free
    // to start casting once mana regenerates rather than being latched to the
    // weapon from the outset. Both are per-target and reset on target change /
    // room clear. The latch never suppresses the spell when the weapon can't hit
    // the target (ctx.WeaponIneffective) — there we still need the spell, so we
    // wait for mana rather than swing uselessly.
    private bool _attackSpellLatchedOff;
    private bool _castSingleTargetAttackThisTarget;

    // Reset the cast bookkeeping a fresh ROSTER owns — the single-target economy,
    // the multi-attack tally, and the weapon latches — WITHOUT touching the AoE
    // area-debuff per-room cap (_areaDebuffCasts / _areaDebuffedMobs). Called on an
    // end-of-fight / roster clear that is NOT a physical room change: an AoE
    // multi-kill that empties the listed pack fires a synthetic room-clear even
    // though the character never moved, and re-arming the area debuff there re-fires
    // it at the same room's survivors / late reveals (report paradigm-20260902-160110
    // — "ISTO fired twice in the same fight"). The area debuff blankets the PHYSICAL
    // room and its tags must ride through the wave-clear; the multi-attack (the kill)
    // still resets so it keeps swinging at what's left.
    public void ResetForRosterClear()
    {
        _singleDebuffCasts = 0;
        _multiAttackCasts = 0;
        _normalAttackCasts = 0;
        _alternateAttackCasts = 0;
        _drainCasts = 0;
        _singleDebuffedTargets.Clear();
        _attackSpellLatchedOff = false;
        _castSingleTargetAttackThisTarget = false;
    }

    // Full per-room reset — the roster-clear economy PLUS the AoE area-debuff cap.
    // Call on a genuine PHYSICAL room change (the pre-move hook): a new room's mobs
    // must be debuffable again, and same-species area-debuff tags must not bleed
    // across rooms (that bleed is why the tags reset on move — see NotePreMove).
    public void ResetForNewRoom()
    {
        ResetForRosterClear();
        ResetAreaDebuffTags();
    }

    // Clear the AoE area-debuff room tags + per-room cap so the next wave is debuffed
    // afresh — on a PHYSICAL room change (ResetForNewRoom) or when the room is confirmed
    // genuinely CLEARED of all hostiles (NoteRoomCleared, wired to a rest posture: you
    // only rest once nothing is left to fight, so a same-room RESPAWN after that is a new
    // wave — report paradigm-20260903-070438). NOT called on a mid-fight wave-clear
    // roster reset, where hidden same-species survivors must stay tagged (report
    // paradigm-20260902-160110).
    public void ResetAreaDebuffTags()
    {
        _areaDebuffCasts = 0;
        _areaDebuffedMobs.Clear();
    }

    // Reset the per-TARGET single-target cast counters. The single-target slots
    // (normal / alternate attack spell, single-target debuff) count MaxCasts PER
    // TARGET, so their tallies reset when the combat target changes within a room.
    // The AoE slots (multi-attack, area debuff) count PER ROOM and are untouched
    // here — they survive a target change and only reset at room-clear.
    public void ResetForNewTarget()
    {
        _singleDebuffCasts = 0;
        _normalAttackCasts = 0;
        _alternateAttackCasts = 0;
        _drainCasts = 0;
        _attackSpellLatchedOff = false;
        _castSingleTargetAttackThisTarget = false;
    }

    // Pick the round's combat action. Pure — does not mutate counters; the caller
    // commits the choice via MarkCast only when the cast actually reaches the
    // wire. The backstab opener fires first when eligible; otherwise
    // CombatSettings.ActionOrder decides between the attack-spell cascade and the
    // weapon swing (the swing is always available, so the method always resolves).
    public CombatSpellDecision Choose(CombatSettings settings, in CombatSpellContext ctx)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Backstab is the round's opener or nothing — a mid-round attempt is a
        // guaranteed fail — so it fires only as the engagement's very first
        // action, gated upstream by DoBackstab + stealth + eligibility
        // (ctx.BackstabPending) and suppressed on a DontBackstab target. It sits
        // outside the spells-vs-physical choice below; when it fires the engine's
        // BS path owns the swing and no spell goes out.
        if (ctx.BackstabPending && !ctx.TargetDontBackstab)
            return CombatSpellDecision.Backstab;

        // SpellsFirst always reaches for the attack-spell cascade (multi → normal
        // → alternate); PhysicalFirst swings and reaches for it only when the
        // weapon path is proven ineffective against this target
        // (ctx.WeaponIneffective — normal can't hit and there's no working
        // alternate). Either way a cascade that can't fire this round falls
        // through to the swing. Debuffs are NOT resolved here; they're in-between
        // casts handled by ChooseDebuff / CastingDirector (ranked against heals &
        // buffs by the Spells tab), so they land alongside the round's action
        // regardless of this choice.
        //
        // The alternating orders resolve their spell-vs-physical preference per
        // round from the parity of an engine-owned round counter and pass it in
        // via ctx.AlternationPreferSpell (null for the fixed orders). A spell-phase
        // round then behaves exactly like SpellsFirst and a physical-phase round
        // like PhysicalFirst — the fallback-to-the-other-type is the same cascade
        // path both fixed orders already take.
        bool preferSpell =
            (ctx.AlternationPreferSpell ?? (settings.ActionOrder == CombatActionOrder.SpellsFirst))
            || ctx.WeaponIneffective;

        // Drain-life override — an emergency heal that also attacks. When HP has
        // fallen to the drain trigger and the target can be drained (living + not
        // undead), the drain spell takes the round in place of whatever the normal
        // pick would be — the single-target attack cascade AND the physical swing.
        // It does NOT respect ActionOrder (survival first): a PhysicalFirst build
        // still drains when hurt. The one thing it yields to is the room AoE — with
        // enough enemies to be rooming, dropping the AoE to single-target a drain is
        // usually the more dangerous play — unless DrainsOverrideAoe says otherwise.
        // The heartbeat re-runs Choose each round, so it reverts to the normal action
        // the moment HP recovers past the trigger or mana falls below the floor.
        if (DrainApplies(settings, ctx, settings.SpellManaThresholdMode, preferSpell))
            return new CombatSpellDecision(CombatSpellAction.DrainSpell, settings.DrainSpell.SpellName!);

        // The per-target weapon latch suppresses the single-target attack cascade —
        // but never when the weapon can't hit this target (there we still need the
        // spell, so we ignore the latch and wait for mana).
        bool singleTargetSpent = _attackSpellLatchedOff && !ctx.WeaponIneffective;

        // A per-monster attack-SPELL override wins over the entire normal flow the
        // same way a per-monster attack-COMMAND override already does (see
        // CombatManager.Spells.cs's AttackCommandOverrideFor path, whose own
        // comment already claims to mirror this contract) — it isn't subject to
        // ActionOrder/alternation's preferSpell gate, only its own cap and mana
        // floor. Previously it only ever got tried when preferSpell happened to
        // already be true that round, so an Alternate*/CustomRoundCycle order
        // silently skipped a still-available override on every physical-phase
        // round, falling back to a plain weapon swing instead (report
        // paradigm-20260904-220509).
        if (!singleTargetSpent
            && ctx.SpellsAvailable
            && ctx.OverrideAttackSpell is { } overrideSpell
            && CastsOk(ctx.OverrideAttackMaxCasts, _normalAttackCasts)
            && ManaOk(settings.NormalAttackSpell, ctx, settings.SpellManaThresholdMode))
            return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, overrideSpell);

        if (preferSpell
            && ctx.SpellsAvailable
            && TryAttackSpell(settings, ctx, settings.SpellManaThresholdMode, singleTargetSpent) is { } spell)
            return spell;

        // The single-target attack cascade could not fire this round. If we'd
        // already been casting a single-target attack spell at this target, its
        // mana reserve is now unmet or its MaxCasts rounds are spent — latch to the
        // weapon for the rest of this target so a mana regen can't flip us back
        // mid-fight. Skipped when the weapon can't hit (we stay on the spell and
        // wait for mana instead of committing to a useless swing).
        if (preferSpell && ctx.SpellsAvailable
            && _castSingleTargetAttackThisTarget && !ctx.WeaponIneffective)
            _attackSpellLatchedOff = true;

        return CombatSpellDecision.Weapon;
    }

    // Pick the in-between debuff for the current round, or null when no debuff
    // applies. Debuffing is an in-between action (≤1/round) in the realm's round
    // model — NOT a combat action — so it's resolved here, separately from
    // Choose, and cast through the shared in-between window (CastingDirector)
    // rather than the combat-action path. While the AoE debuff is COVERING the room
    // (configured + Auto-Nuke on + at/over MinEnemies) it owns the round: cast once,
    // re-fire only for a new (untagged) mob up to its cap, else skip. When it isn't
    // covering (unconfigured / Auto-Nuke off / below MinEnemies) the single-target
    // debuff fires once per target. Pure — the caller commits via MarkCast only when
    // the cast reaches the wire.
    public CombatSpellDecision? ChooseDebuff(CombatSettings settings, in CombatSpellContext ctx)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ctx.SpellsAvailable) return null;
        return TryDebuffing(settings, ctx, settings.SpellManaThresholdMode);
    }

    // Debuffing phase. Area takes precedence and excludes single (matches the
    // historical behaviour). Returns null when nothing in the category can fire
    // this round.
    private CombatSpellDecision? TryDebuffing(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode)
    {
        // The AoE debuff "covers" the room this round only when it's configured, Auto-
        // Nuke is on, and the room meets its minimum-enemy count. Those three are the
        // exact complement of the cases the single-target rung takes over in: AoE
        // unconfigured, Auto-Nuke off, or the room below MinEnemies. (report
        // paradigm-20260817-205819: an AoE-configured user with Auto-Nuke off never got
        // their single-target debuff because a configured-but-inactive AoE short-circuited
        // it — the single block below used to be reachable only when the AoE was
        // UNCONFIGURED.)
        CombatSpellSlot area = settings.AreaDebuffSpell;
        bool areaCovering = IsConfigured(area)
            && ctx.AllowNukes
            && ctx.EnemyCount >= area.MinEnemies;

        if (areaCovering)
        {
            // The AoE owns the room. Cast it as FEW times as possible: fire only when a
            // mob that wasn't present at the last AoE cast is here (the first cast, or a
            // new arrival / summon), under the per-room cap and mana floor — NOT every
            // round like an attack spell. Blanketed / at cap / mana-short → skip; the
            // single-target rung must not fire while the AoE is covering the room.
            if (ManaOk(area, ctx, mode)
                && CastsOk(area, _areaDebuffCasts)
                && AoeHasNewTarget(ctx))
                return new CombatSpellDecision(CombatSpellAction.AreaDebuff, area.SpellName!);
            return null;
        }

        // AoE not covering → the single-target debuff rung takes over, once per mob up to
        // its per-room cap (this is the report-#1 fall-through).
        CombatSpellSlot single = settings.SingleTargetDebuffSpell;
        if (ctx.OverridePreAttackSpell is { } preAttackOverride)
        {
            // Per-monster pre-attack override occupies the single-target debuff
            // rung: bypass the level gate (user vouched for it) but keep the
            // once-per-target guard, the slot's mana floor, and the override's
            // own per-room cast cap. Shares the single-debuff counter / target
            // set (override and configured slot are mutually exclusive here).
            if (!_singleDebuffedTargets.Contains(ctx.TargetRawName)
                && CastsOk(ctx.OverridePreAttackMaxCasts, _singleDebuffCasts)
                && ManaOk(single, ctx, mode))
                return new CombatSpellDecision(CombatSpellAction.SingleDebuff, preAttackOverride);
            return null;
        }

        if (IsConfigured(single)
            && !IsLevelBlocked(ctx, CombatSpellAction.SingleDebuff)
            && !_singleDebuffedTargets.Contains(ctx.TargetRawName)
            && CastsOk(single, _singleDebuffCasts)
            && ManaOk(single, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.SingleDebuff, single.SpellName!);

        return null;
    }

    // Is there a mob in the room this round the AoE debuff hasn't already tagged — the
    // first cast (nothing tagged yet) or a new arrival / summon? When the roster isn't
    // wired (unwired callers / tests supply no RoomMobKeys), degrade to once-per-room:
    // fire only while no AoE debuff has gone out yet.
    private bool AoeHasNewTarget(in CombatSpellContext ctx)
    {
        if (ctx.RoomMobKeys is not { } keys) return _areaDebuffCasts == 0;
        foreach (string key in keys)
            if (!_areaDebuffedMobs.Contains(key)) return true;   // untagged — a new arrival / summon
        return false;
    }

    // Whether the drain spell should take this round. Fires only when configured,
    // The drain is a pure HP-driven survival cast (user-confirmed 2026-08-27): it
    // fires each round while HP is at/under the trigger, mana meets the slot floor,
    // and the per-TARGET cast cap isn't spent — and stops the instant HP recovers
    // ABOVE the trigger (no hysteresis band; report paradigm-20260827-153630, where a
    // trigger+20 release pinned to 100% HP made it drain to full). The per-target cap
    // resets on a target change so a fresh target gets the full cap again (report
    // paradigm-20260827-101845). Also needs the target drain-eligible (living + not
    // undead, and not reactively marked drain-immune off a "no effect" line). Yields
    // to the room AoE (multi-attack) unless DrainsOverrideAoe — see the note in Choose.
    private bool DrainApplies(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode, bool preferSpell)
    {
        CombatSpellSlot drain = settings.DrainSpell;
        if (!IsConfigured(drain)) return false;
        if (!ctx.SpellsAvailable) return false;
        // Fire only while HP is at/under the trigger; the moment HP climbs above it,
        // hand the round back to the normal attack cycle. A big drain-heal jumps HP
        // well past the trigger, so this naturally fires only while genuinely hurt.
        if (!ctx.HpBelowDrainTrigger) return false;
        if (!ctx.DrainTargetEligible) return false;          // living + not undead (game data)
        if (IsImmune(ctx, CombatSpellAction.DrainSpell)) return false;   // reactive no-effect backstop
        if (!CastsOk(drain, _drainCasts)) return false;      // per-target cap (reset on target change)
        if (!ManaOk(drain, ctx, mode)) return false;
        // AoE precedence: by default don't steal the round from a multi-attack that
        // would fire this round; the checkbox flips it so the drain wins.
        if (!settings.DrainsOverrideAoe && MultiAttackWouldFire(settings, ctx, mode, preferSpell))
            return false;
        return true;
    }

    // Would the room multi-attack spell fire this round? Mirrors the multi-attack
    // rung in TryAttackSpell (config + MinEnemies + cast cap + mana + Auto-Nuke),
    // plus the preferSpell gate — under PhysicalFirst with an effective weapon the
    // cascade isn't reached, so no AoE fires and the drain has nothing to yield to.
    private bool MultiAttackWouldFire(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode, bool preferSpell)
    {
        if (!preferSpell) return false;
        CombatSpellSlot multi = settings.MultiAttackSpell;
        return ctx.AllowNukes
            && IsConfigured(multi)
            && ctx.EnemyCount >= multi.MinEnemies
            && CastsOk(multi, _multiAttackCasts)
            && ManaOk(multi, ctx, mode);
    }

    // Attack-spell phase: multi-attack room spell while it qualifies, then
    // normal, then alternate single-target damage spells. Returns null when none
    // can fire this round.
    private CombatSpellDecision? TryAttackSpell(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode, bool singleTargetSpent)
    {
        // Auto-Nuke gate: the multi-target attack spell is a nuke — when the
        // auto-engine is off we skip it and fall to the single-target spells.
        // Multi-attack is room-scoped (MinEnemies), not target-scoped, so the
        // per-target single-target latch never suppresses it.
        CombatSpellSlot multi = settings.MultiAttackSpell;
        if (ctx.AllowNukes
            && IsConfigured(multi)
            && ctx.EnemyCount >= multi.MinEnemies
            && CastsOk(multi, _multiAttackCasts)
            && ManaOk(multi, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.MultiAttack, multi.SpellName!);

        // Single-target attack cascade latched off for this target (its mana
        // reserve is spent or its MaxCasts rounds elapsed) — skip to the weapon.
        if (singleTargetSpent) return null;

        // A per-monster attack-spell override occupies the normal-attack rung and
        // is mutually exclusive with the configured NormalAttackSpell slot for
        // this monster — Choose already attempted it (unconditionally, ahead of
        // preferSpell) before ever calling here, so reaching this method with an
        // override still configured means it was already rejected this round
        // (cap spent / mana short). Skip straight to the alternate slot below
        // rather than firing the unrelated global NormalAttackSpell instead.
        CombatSpellSlot normal = settings.NormalAttackSpell;
        if (ctx.OverrideAttackSpell is null
            && IsConfigured(normal)
            && !IsImmune(ctx, CombatSpellAction.NormalAttackSpell)
            && !IsLevelBlocked(ctx, CombatSpellAction.NormalAttackSpell)
            && !IsResistBlocked(ctx, CombatSpellAction.NormalAttackSpell)
            && CastsOk(normal, _normalAttackCasts)
            && ManaOk(normal, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, normal.SpellName!);

        CombatSpellSlot alt = settings.AlternateAttackSpell;
        if (IsConfigured(alt)
            && !IsImmune(ctx, CombatSpellAction.AlternateAttackSpell)
            && !IsLevelBlocked(ctx, CombatSpellAction.AlternateAttackSpell)
            && !IsResistBlocked(ctx, CombatSpellAction.AlternateAttackSpell)
            && CastsOk(alt, _alternateAttackCasts)
            && ManaOk(alt, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.AlternateAttackSpell, alt.SpellName!);

        return null;
    }

    // Record that the engine successfully sent the cast for decision against
    // targetRawName. No-op for WeaponAttack. Keeps the per-room counters in step
    // with what actually went to the server, so a cast blocked by
    // CastCoordinator's cooldown isn't counted.
    public void MarkCast(in CombatSpellDecision decision, string targetRawName,
                         IReadOnlyList<string>? roomMobKeys = null)
    {
        switch (decision.Action)
        {
            case CombatSpellAction.AreaDebuff:
                // Count the AoE cast toward its per-room cap and tag every mob present, so
                // it re-fires only for a later NEW arrival rather than every round. A null
                // roster (unwired callers / tests) still bumps the counter → once-per-room.
                _areaDebuffCasts++;
                if (roomMobKeys is not null)
                    foreach (string key in roomMobKeys) _areaDebuffedMobs.Add(key);
                break;
            case CombatSpellAction.SingleDebuff:
                if (!string.IsNullOrEmpty(targetRawName))
                    _singleDebuffedTargets.Add(targetRawName);
                _singleDebuffCasts++;
                break;
            case CombatSpellAction.MultiAttack:
                _multiAttackCasts++;
                break;
            case CombatSpellAction.NormalAttackSpell:
                _normalAttackCasts++;
                _castSingleTargetAttackThisTarget = true;
                break;
            case CombatSpellAction.AlternateAttackSpell:
                _alternateAttackCasts++;
                _castSingleTargetAttackThisTarget = true;
                break;
            case CombatSpellAction.DrainSpell:
                // Per-target cap only; the drain does NOT arm the single-target
                // weapon latch (it's HP-gated, not part of the damage cascade). It
                // DOES arm the hysteresis latch so the HP gate holds on the release
                // threshold until HP recovers past it.
                _drainCasts++;
                break;
            case CombatSpellAction.WeaponAttack:
            default:
                break;
        }
    }

    // Roll back a debuff MarkCast whose cast the server REJECTED after the optimistic
    // send (the pre-attack / in-between debuff returns TryCast=true on wire-write, then
    // "You have already cast a spell this round!" arrives async). The optimistic mark
    // tagged the mob(s) debuffed and spent the per-room count; undo both so the debuff
    // re-fires next round instead of being treated as landed (reports
    // paradigm-20260901-123720 / -140747). Only the debuff actions have a mark to undo.
    public void UnmarkCast(in CombatSpellDecision decision, string targetRawName,
                           IReadOnlyList<string>? roomMobKeys = null)
    {
        switch (decision.Action)
        {
            case CombatSpellAction.AreaDebuff:
                if (_areaDebuffCasts > 0) _areaDebuffCasts--;
                if (roomMobKeys is not null)
                    foreach (string key in roomMobKeys) _areaDebuffedMobs.Remove(key);
                break;
            case CombatSpellAction.SingleDebuff:
                if (!string.IsNullOrEmpty(targetRawName))
                    _singleDebuffedTargets.Remove(targetRawName);
                if (_singleDebuffCasts > 0) _singleDebuffCasts--;
                break;
            default:
                break;   // attack / drain marks aren't rolled back this way
        }
    }

    private static bool IsConfigured(CombatSpellSlot slot) =>
        !string.IsNullOrWhiteSpace(slot.SpellName);

    // The current target's species is immune to this single-target attack spell
    // (a prior "Your spell has no effect on X." landed). Only the single-target
    // attack slots are gated — multi-attack room spells are never marked immune
    // (one immune mob doesn't mean the spell isn't damaging the rest of the
    // room).
    private static bool IsImmune(in CombatSpellContext ctx, CombatSpellAction action) =>
        ctx.ImmuneAttackSpells is { } set && set.Contains(action);

    // The current target's SpellImmu level deterministically blocks this
    // single-target spell (its ReqLevel < the monster's immunity), per game data
    // — distinct from the observed "no effect" immunity in IsImmune. Only the
    // single-target slots (SingleDebuff / NormalAttackSpell /
    // AlternateAttackSpell) are level-gated; area / multi room spells hit the
    // whole room, so one immune occupant doesn't disqualify them.
    private static bool IsLevelBlocked(in CombatSpellContext ctx, CombatSpellAction action) =>
        ctx.LevelBlockedActions is { } set && set.Contains(action);

    // The current target resists this attack spell's damage *element* ≥ 100%, so
    // the spell would deal 0 damage (exactly 100%) or heal the monster (> 100%) —
    // game data lets us skip it pre-emptively down the cascade. Only single-target
    // attack slots are guarded (multi/area room spells hit the whole room), and
    // only elemental spells are ever resist-blocked: Magic Resist (AttType 4) and
    // poison (AttType 6) aren't deterministic, so their spells never land here (see
    // MonsterResistIndex). A negative or 1–99% resist is NOT blocked — the spell
    // still deals (bonus or reduced) damage.
    private static bool IsResistBlocked(in CombatSpellContext ctx, CombatSpellAction action) =>
        ctx.ResistBlockedActions is { } set && set.Contains(action);

    // Under the per-room cast cap. null = no limit; 0 = never cast (explicit
    // off); N = fire until N reached.
    private static bool CastsOk(CombatSpellSlot slot, int castsSoFar) =>
        CastsOk(slot.MaxCastsPerRoom, castsSoFar);

    private static bool CastsOk(int? cap, int castsSoFar) =>
        cap is not { } c || castsSoFar < c;

    private static bool ManaOk(CombatSpellSlot slot, in CombatSpellContext ctx, ThresholdMode mode)
    {
        if (!ManaMeetsReserve(slot.MinManaPerCast, ctx.Mana, ctx.MaxMana, mode)) return false;
        // Hard affordability floor: never pick a spell we can't actually pay for (the
        // server silently no-ops a below-cost cast). Unknown cost never blocks — same
        // convention as CastingDirector.IsAffordable.
        if (slot.SpellName is { } code && ctx.ManaCostOf?.Invoke(code) is { } cost && ctx.Mana < cost)
            return false;
        return true;
    }

    // Whether live MA meets a slot's per-cast mana reserve. In Percentage mode the
    // reserve is compared against the ROUNDED absolute equivalent — the exact value
    // the Settings "= mana / %" conversion label shows the user — NOT the raw
    // fractional percentage. Otherwise a reserve like 82% of a 66 max (= 54.12) would
    // reject 54 mana even though the label promises "54", so the spell wrongly
    // swapped to physical at the value the user set as castable. A 0 reserve always
    // passes; a 0 max in Percentage mode never does.
    public static bool ManaMeetsReserve(int minManaPerCast, int ma, int maxMa, ThresholdMode mode)
    {
        if (minManaPerCast <= 0) return true;
        if (mode == ThresholdMode.Absolute) return ma >= minManaPerCast;
        if (maxMa <= 0) return false;
        int threshold = (int)System.Math.Round(maxMa * minManaPerCast / 100.0);
        return ma >= threshold;
    }
}

// The combat action a CombatSpellChooser picks for a round. WeaponAttack means
// "no spell — fall through to the engine's weapon attack command"; Backstab
// means "send the backstab verb" (also no spell). Both carry a null spell.
public enum CombatSpellAction
{
    WeaponAttack = 0,
    AreaDebuff,
    SingleDebuff,
    MultiAttack,
    NormalAttackSpell,
    AlternateAttackSpell,
    Backstab,
    DrainSpell,
}

// One round's chosen combat action plus the cast-code to send (null for
// WeaponAttack and Backstab).
public readonly record struct CombatSpellDecision(CombatSpellAction Action, string? Spell)
{
    // Shared "no spell, swing the weapon" result.
    public static readonly CombatSpellDecision Weapon =
        new(CombatSpellAction.WeaponAttack, null);

    // Shared "no spell, send the backstab verb" result.
    public static readonly CombatSpellDecision Backstab =
        new(CombatSpellAction.Backstab, null);
}

// Per-round inputs the CombatSpellChooser reads. EnemyCount is the
// engageable-monster count in the room; TargetRawName is the current pick's
// per-instance name (keys the once-per-target single-debuff set); Mana/MaxMana
// drive the per-cast mana gate; BackstabPending is true while a sneak backstab
// still owes its opening round; ImmuneAttackSpells is the set of single-target
// attack actions the current target's species has proven immune to this room
// (null when nothing is immune); SpellsAvailable is false when the engine has no
// combat spell caster wired, so the Debuffing / Spells categories are skipped
// and the order collapses to Backstab vs Physical; LevelBlockedActions is the
// set of single-target spell actions the current target's SpellImmu level
// deterministically blocks (their ReqLevel < the monster's immunity), or null
// when nothing is level-blocked; AllowNukes is the Auto-Nuke auto-engine gate —
// when false the chooser never offers the multi-target attack spell or either
// debuff (the single-target Normal / Alternate attack spells are NOT nukes and
// stay available). Defaults true so unwired callers / tests behave as before.
// ResistBlockedActions is the set of single-target attack actions whose damage
// *element* the current target resists ≥ 100% (0 damage or heal), or null when
// nothing is resist-blocked — elemental only; M.R. and poison spells never
// appear here. TargetDontBackstab is the current target's per-monster
// DontBackstab overlay flag — when set the backstab gate is suppressed and the
// opener falls through to a normal attack. WeaponIneffective is true when neither
// configured weapon can damage the current target (normal is out and there's no
// working alternate — proven by game-data HitMagic or a "no effect" line this
// room); it only matters for PhysicalFirst, where it flips the round from a
// useless swing to the attack-spell cascade. Defaults false so SpellsFirst and
// unwired callers / tests behave as before. AlternationPreferSpell forces this
// round's spell-vs-physical preference for the alternating action orders (true =
// treat the round as SpellsFirst, false = as PhysicalFirst); null — the default —
// leaves the choice to settings.ActionOrder, so the fixed orders and unwired
// callers / tests behave as before.
//
// The four Override* fields carry the current target's per-monster spell
// overrides (game-data Monster overlay), already resolved from Spell.Number to
// the Short cast-code. OverrideAttackSpell substitutes at the NormalAttackSpell
// rung and OverridePreAttackSpell at the SingleTargetDebuffSpell rung; when set,
// the effectiveness gates (observed immunity / level / element resist) are
// bypassed — the user hand-picked the spell for this species — while the
// physical constraints (the slot's mana floor, once-per-target for the debuff)
// still apply. The paired *MaxCasts values are the per-room cast caps the user
// configured (always positive when the spell is set; a null/zero configured
// count leaves the override spell null so the global slot is used unchanged).
public readonly record struct CombatSpellContext(
    int EnemyCount,
    string TargetRawName,
    int Mana,
    int MaxMana,
    bool BackstabPending,
    IReadOnlySet<CombatSpellAction>? ImmuneAttackSpells = null,
    bool SpellsAvailable = true,
    IReadOnlySet<CombatSpellAction>? LevelBlockedActions = null,
    bool AllowNukes = true,
    IReadOnlySet<CombatSpellAction>? ResistBlockedActions = null,
    bool TargetDontBackstab = false,
    string? OverrideAttackSpell = null,
    int? OverrideAttackMaxCasts = null,
    string? OverridePreAttackSpell = null,
    int? OverridePreAttackMaxCasts = null,
    bool WeaponIneffective = false,
    bool? AlternationPreferSpell = null,
    // Drain-life gating (see DrainApplies). HpBelowDrainTrigger is true when live HP
    // is at/under the DrainSpell trigger % — the drain fires only while it holds and
    // releases at the trigger (no hysteresis band). DrainTargetEligible is true when
    // the current target is living AND not undead (a drain can't affect NonLiving /
    // Undead). Both default false so a drain never fires unless BuildContext populates
    // them — unwired callers / tests behave exactly as before.
    bool HpBelowDrainTrigger = false,
    bool DrainTargetEligible = false,
    // RawNames of every engageable monster in the room this round — the roster the AoE
    // debuff tags on cast and checks for new (untagged) arrivals to decide a re-fire.
    // null when unwired (weapon-only context, tests), where the AoE degrades to
    // once-per-room.
    IReadOnlyList<string>? RoomMobKeys = null,
    // Resolves a spell's actual mana cost by cast-code (Spellbook.ManaCostOf). ManaOk
    // uses it as a hard affordability floor beneath the slot's MinManaPerCast reserve:
    // a MinManaPerCast of 0 means "no reserve", NOT "cast even when you can't afford
    // it" — a below-cost cast is a silent no-op (report paradigm-20260820-082741, DTCH
    // spammed at 5/13/20 mana vs its 25 cost). null (unwired / tests) → no cost floor,
    // old behavior.
    System.Func<string, int?>? ManaCostOf = null);
