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
    private bool _areaDebuffCast;
    private int _singleDebuffCasts;
    private int _multiAttackCasts;
    private int _normalAttackCasts;
    private int _alternateAttackCasts;
    private readonly HashSet<string> _singleDebuffedTargets =
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

    // Which single-target attack slot CombatSettings.AlternateAttackSpells
    // cycling prefers FIRST on the NEXT call — false = Normal, true = Alternate.
    // Set directly in TryAttackSpell to match whichever slot it just picked.
    // Safe despite Choose()'s "pure" contract for the cast COUNTERS (those use
    // ++, so a redundant call would double-count) — this field only ever gets
    // assigned, never incremented, so a repeated call with the same inputs just
    // re-derives the same value; it doesn't accumulate. Reset to Normal (false)
    // on a fresh target/room, matching the "fresh mob reopens on Normal"
    // invariant the plain cascade already has.
    private bool _activeIsAlternate;

    // Reset all per-room cast bookkeeping. Call when the room clears / the engine
    // starts a fresh engagement.
    public void ResetForNewRoom()
    {
        _areaDebuffCast = false;
        _singleDebuffCasts = 0;
        _multiAttackCasts = 0;
        _normalAttackCasts = 0;
        _alternateAttackCasts = 0;
        _singleDebuffedTargets.Clear();
        _attackSpellLatchedOff = false;
        _castSingleTargetAttackThisTarget = false;
        _activeIsAlternate = false;
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
        _attackSpellLatchedOff = false;
        _castSingleTargetAttackThisTarget = false;
        _activeIsAlternate = false;
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

        // The per-target weapon latch suppresses the single-target attack cascade —
        // but never when the weapon can't hit this target (there we still need the
        // spell, so we ignore the latch and wait for mana).
        bool singleTargetSpent = _attackSpellLatchedOff && !ctx.WeaponIneffective;

        if (preferSpell
            && ctx.SpellsAvailable
            && TryAttackSpell(settings, ctx, settings.SpellManaThresholdMode, singleTargetSpent) is { } spell)
            return spell;

        // The single-target attack cascade could not fire this round. If we'd
        // already been casting a single-target attack spell at this target, its
        // mana reserve is now unmet or its MaxCasts rounds are spent — latch to the
        // weapon for the rest of this target so a mana regen can't flip us back
        // mid-fight. Skipped when the weapon can't hit (we stay on the spell and
        // wait for mana instead of committing to a useless swing), and skipped
        // entirely when AlternateAttackSpells cycling is actually engaged (both
        // slots configured) — there the whole point is to keep re-checking both
        // slots every round so a later mana regen (or a cleared immunity) can
        // bring back whichever slot just lapsed.
        if (preferSpell && ctx.SpellsAvailable && !CyclingEngaged(settings)
            && _castSingleTargetAttackThisTarget && !ctx.WeaponIneffective)
            _attackSpellLatchedOff = true;

        return CombatSpellDecision.Weapon;
    }

    // Pick the in-between debuff for the current round, or null when no debuff
    // applies. Debuffing is an in-between action (≤1/round) in the realm's round
    // model — NOT a combat action — so it's resolved here, separately from
    // Choose, and cast through the shared in-between window (CastingDirector)
    // rather than the combat-action path. Area debuff takes precedence and
    // excludes single (once per room when MinEnemies is met); otherwise the
    // single-target debuff fires once on every new target. Pure — the caller
    // commits via MarkCast only when the cast reaches the wire.
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
        // Auto-Nuke gate: debuffs are nukes — when the auto-engine is off
        // we never offer them.
        if (!ctx.AllowNukes) return null;

        CombatSpellSlot area = settings.AreaDebuffSpell;
        if (IsConfigured(area))
        {
            if (!_areaDebuffCast
                && ctx.EnemyCount >= area.MinEnemies
                && ManaOk(area, ctx, mode))
                return new CombatSpellDecision(CombatSpellAction.AreaDebuff, area.SpellName!);
            return null;
        }

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

        CombatSpellSlot normal = settings.NormalAttackSpell;
        CombatSpellSlot alt = settings.AlternateAttackSpell;

        if (ctx.OverrideAttackSpell is { } attackOverride)
        {
            // Per-monster attack-spell override occupies the normal-attack rung:
            // bypass the effectiveness gates (observed immunity / level / element
            // resist) — the user vouched this spell works on the species — but
            // keep the physical constraints: the slot's mana floor and the
            // override's own per-room cast cap. Shares the normal-attack counter
            // (override and configured slot are mutually exclusive per monster).
            // Takes priority over AlternateAttackSpells cycling — an override is a
            // deliberate per-monster choice, more specific than the general toggle.
            if (CastsOk(ctx.OverrideAttackMaxCasts, _normalAttackCasts)
                && ManaOk(normal, ctx, mode))
                return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, attackOverride);
            // Override present but not castable this round — fall through to the
            // plain Alternate check below (matching the pre-cycling cascade); the
            // override occupies the Normal rung outright, so cycling never applies.
        }
        else if (CyclingEngaged(settings))
        {
            // Cycling: try whichever slot is currently active first, then the
            // other — driven purely by the same eligibility gates the plain
            // cascade below uses, so a slot that lapses (mana / MaxCasts /
            // immunity) can come back once it's eligible again. Stay on the
            // active slot as long as IT holds; only flip once it stops being
            // castable (not the instant the other one becomes castable again) —
            // so a fully-eligible Normal doesn't yank the engine off a
            // still-eligible Alternate mid-fight.
            (CombatSpellSlot firstSlot, CombatSpellAction firstAction, int firstCasts) = _activeIsAlternate
                ? (alt, CombatSpellAction.AlternateAttackSpell, _alternateAttackCasts)
                : (normal, CombatSpellAction.NormalAttackSpell, _normalAttackCasts);
            (CombatSpellSlot secondSlot, CombatSpellAction secondAction, int secondCasts) = _activeIsAlternate
                ? (normal, CombatSpellAction.NormalAttackSpell, _normalAttackCasts)
                : (alt, CombatSpellAction.AlternateAttackSpell, _alternateAttackCasts);

            if (AttackSpellEligible(firstSlot, ctx, firstAction, mode, firstCasts))
                return new CombatSpellDecision(firstAction, firstSlot.SpellName!);
            if (AttackSpellEligible(secondSlot, ctx, secondAction, mode, secondCasts))
            {
                _activeIsAlternate = !_activeIsAlternate;
                return new CombatSpellDecision(secondAction, secondSlot.SpellName!);
            }
            return null;
        }

        else if (IsConfigured(normal)
            && !IsImmune(ctx, CombatSpellAction.NormalAttackSpell)
            && !IsLevelBlocked(ctx, CombatSpellAction.NormalAttackSpell)
            && !IsResistBlocked(ctx, CombatSpellAction.NormalAttackSpell)
            && CastsOk(normal, _normalAttackCasts)
            && ManaOk(normal, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, normal.SpellName!);

        if (IsConfigured(alt)
            && !IsImmune(ctx, CombatSpellAction.AlternateAttackSpell)
            && !IsLevelBlocked(ctx, CombatSpellAction.AlternateAttackSpell)
            && !IsResistBlocked(ctx, CombatSpellAction.AlternateAttackSpell)
            && CastsOk(alt, _alternateAttackCasts)
            && ManaOk(alt, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.AlternateAttackSpell, alt.SpellName!);

        return null;
    }

    // Shared eligibility check for the AlternateAttackSpells cycling branch —
    // the same gates the plain cascade above applies inline (configured, not
    // observed-immune, not level-blocked, not elementally resisted, under the
    // per-room cast cap, mana reserve met), extracted here because cycling needs
    // to run it for whichever slot is "first" or "second" this round.
    private static bool AttackSpellEligible(
        CombatSpellSlot slot, in CombatSpellContext ctx, CombatSpellAction action,
        ThresholdMode mode, int castsSoFar) =>
        IsConfigured(slot)
        && !IsImmune(ctx, action)
        && !IsLevelBlocked(ctx, action)
        && !IsResistBlocked(ctx, action)
        && CastsOk(slot, castsSoFar)
        && ManaOk(slot, ctx, mode);

    // Record that the engine successfully sent the cast for decision against
    // targetRawName. No-op for WeaponAttack. Keeps the per-room counters in step
    // with what actually went to the server, so a cast blocked by
    // CastCoordinator's cooldown isn't counted.
    public void MarkCast(in CombatSpellDecision decision, string targetRawName)
    {
        switch (decision.Action)
        {
            case CombatSpellAction.AreaDebuff:
                _areaDebuffCast = true;
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
            case CombatSpellAction.WeaponAttack:
            default:
                break;
        }
    }

    private static bool IsConfigured(CombatSpellSlot slot) =>
        !string.IsNullOrWhiteSpace(slot.SpellName);

    // AlternateAttackSpells only actually changes behavior when BOTH slots are
    // configured — with just one, there's nothing to cycle between, so the
    // plain cascade (and its permanent weapon latch) applies unchanged. Shared
    // by Choose (to skip the latch) and TryAttackSpell (to enter the cycling
    // branch) so the two conditions can't drift apart.
    private static bool CyclingEngaged(CombatSettings settings) =>
        settings.AlternateAttackSpells
        && IsConfigured(settings.NormalAttackSpell)
        && IsConfigured(settings.AlternateAttackSpell);

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

    private static bool ManaOk(CombatSpellSlot slot, in CombatSpellContext ctx, ThresholdMode mode) =>
        ManaMeetsReserve(slot.MinManaPerCast, ctx.Mana, ctx.MaxMana, mode);

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
    bool? AlternationPreferSpell = null);
