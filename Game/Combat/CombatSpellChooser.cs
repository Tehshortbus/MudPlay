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
// appears in the combat-action walk below.
//
// 1. Backstab gate — only when ranked at PriorityBackstab 1 AND a backstab is
//    still pending (sneaking + DoBackstab). The opener is the round's action or
//    nothing — a mid-round BS attempt is a guaranteed fail, so at any other rank
//    the category is ignored entirely. When it fires the chooser returns
//    Backstab so the engine's BS path owns the swing and no spell goes out.
// 2. Attack — MultiAttackSpell while its conditions hold (room count ≥
//    MinEnemies, mana, cast cap); it stays selected round after round until a
//    condition fails (mana runs out, cap reached, or the room thins below
//    MinEnemies as mobs die). When multi-attack no longer qualifies, fall to
//    NormalAttackSpell, then AlternateAttackSpell, then the engine's weapon
//    attack command.
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

    // The four combat categories in canonical order. Used as the tie-break when
    // two categories share a priority value, so duplicate numbers resolve to the
    // historical Backstab → Debuffing → Spells → Physical fallback.
    private static readonly CombatCategory[] Canonical =
    {
        CombatCategory.Backstab,
        CombatCategory.Debuffing,
        CombatCategory.Spells,
        CombatCategory.Physical,
    };

    // Pick the next combat action for the current round. Pure — does not mutate
    // counters; the caller commits the choice via MarkCast only when the cast
    // actually reaches the wire. Walks the four categories in the user-configured
    // priority order (PriorityBackstab etc.); the first applicable category owns
    // the round. Physical (the weapon swing) is the terminal category — always
    // applicable — so the loop always resolves.
    public CombatSpellDecision Choose(CombatSettings settings, in CombatSpellContext ctx)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ThresholdMode mode = settings.SpellManaThresholdMode;

        // Stable-sort the four categories by configured priority; the
        // canonical-index tie-break keeps equal priorities deterministic.
        Span<CombatCategory> order = stackalloc CombatCategory[Canonical.Length];
        Canonical.CopyTo(order);
        SortByPriority(order, settings);

        foreach (CombatCategory cat in order)
        {
            CombatSpellDecision? decision = cat switch
            {
                // Backstab is the round's opener or nothing: it only fires when
                // the user ranks it at priority 1. At any other rank we ignore
                // it entirely (a mid-round BS attempt is a guaranteed fail).
                CombatCategory.Backstab =>
                    (settings.PriorityBackstab == 1 && ctx.BackstabPending)
                        ? CombatSpellDecision.Backstab : null,
                // Debuffing is an in-between action, NOT a combat action —
                // it's resolved by ChooseDebuff and cast through the shared
                // in-between window (CastingDirector), so the combat-action
                // walk skips it. The category stays in the sort only to keep
                // the other three priorities ordering deterministically.
                CombatCategory.Debuffing => null,
                CombatCategory.Spells =>
                    ctx.SpellsAvailable ? TryAttackSpell(settings, ctx, mode) : null,
                CombatCategory.Physical => CombatSpellDecision.Weapon,
                _ => null,
            };
            if (decision is { } d) return d;
        }

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
        if (IsConfigured(normal)
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

    // In-place insertion sort of the four categories by configured priority
    // (ascending), tie-breaking on canonical index. Four elements, so insertion
    // sort is both the simplest and the fastest stable option.
    private static void SortByPriority(Span<CombatCategory> order, CombatSettings settings)
    {
        for (int i = 1; i < order.Length; i++)
        {
            CombatCategory key = order[i];
            int keyPri = PriorityOf(settings, key);
            int j = i - 1;
            while (j >= 0 && IsHigher(order[j], PriorityOf(settings, order[j]), key, keyPri))
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }
    }

    // True when category a should sort AFTER b (i.e. b fires first): higher
    // priority value, or equal value with a later canonical index.
    private static bool IsHigher(CombatCategory a, int aPri, CombatCategory b, int bPri) =>
        aPri > bPri || (aPri == bPri && CanonicalIndex(a) > CanonicalIndex(b));

    private static int PriorityOf(CombatSettings s, CombatCategory cat) => cat switch
    {
        CombatCategory.Backstab  => s.PriorityBackstab,
        CombatCategory.Debuffing => s.PriorityDebuffing,
        CombatCategory.Spells    => s.PrioritySpells,
        CombatCategory.Physical  => s.PriorityPhysical,
        _ => int.MaxValue,
    };

    private static int CanonicalIndex(CombatCategory cat) => cat switch
    {
        CombatCategory.Backstab  => 0,
        CombatCategory.Debuffing => 1,
        CombatCategory.Spells    => 2,
        CombatCategory.Physical  => 3,
        _ => int.MaxValue,
    };

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
        slot.MaxCastsPerRoom is not { } cap || castsSoFar < cap;

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

// The four orderable combat categories. Each maps to a CombatSettings priority
// field; the chooser walks them in ascending priority order.
internal enum CombatCategory
{
    Backstab,
    Debuffing,
    Spells,
    Physical,
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
// appear here.
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
    IReadOnlySet<CombatSpellAction>? ResistBlockedActions = null);
