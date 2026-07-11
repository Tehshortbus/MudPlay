using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Combat;

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
        bool preferSpell = settings.ActionOrder == CombatActionOrder.SpellsFirst
                           || ctx.WeaponIneffective;
        if (preferSpell
            && ctx.SpellsAvailable
            && TryAttackSpell(settings, ctx, settings.SpellManaThresholdMode) is { } spell)
            return spell;

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
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode)
    {
        // Auto-Nuke gate: the multi-target attack spell is a nuke — when the
        // auto-engine is off we skip it and fall to the single-target spells.
        CombatSpellSlot multi = settings.MultiAttackSpell;
        if (ctx.AllowNukes
            && IsConfigured(multi)
            && ctx.EnemyCount >= multi.MinEnemies
            && CastsOk(multi, _multiAttackCasts)
            && ManaOk(multi, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.MultiAttack, multi.SpellName!);

        CombatSpellSlot normal = settings.NormalAttackSpell;
        if (ctx.OverrideAttackSpell is { } attackOverride)
        {
            // Per-monster attack-spell override occupies the normal-attack rung:
            // bypass the effectiveness gates (observed immunity / level / element
            // resist) — the user vouched this spell works on the species — but
            // keep the physical constraints: the slot's mana floor and the
            // override's own per-room cast cap. Shares the normal-attack counter
            // (override and configured slot are mutually exclusive per monster).
            if (CastsOk(ctx.OverrideAttackMaxCasts, _normalAttackCasts)
                && ManaOk(normal, ctx, mode))
                return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, attackOverride);
        }
        else if (IsConfigured(normal)
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
                break;
            case CombatSpellAction.AlternateAttackSpell:
                _alternateAttackCasts++;
                break;
            case CombatSpellAction.WeaponAttack:
            default:
                break;
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
        if (slot.MinManaPerCast <= 0) return true;
        if (mode == ThresholdMode.Absolute)
            return ctx.Mana >= slot.MinManaPerCast;
        // Percentage of live max MA.
        if (ctx.MaxMana <= 0) return false;
        double pct = ctx.Mana * 100.0 / ctx.MaxMana;
        return pct >= slot.MinManaPerCast;
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
// unwired callers / tests behave as before.
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
    bool WeaponIneffective = false);
