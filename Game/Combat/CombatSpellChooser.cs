using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.A (spell extension) — per-round combat-spell decision unit.
/// Pure decision + room-scoped cast bookkeeping; holds NO wire state. The
/// owning <see cref="CombatManager"/> calls <see cref="Choose"/> once per
/// attack decision, sends the resulting cast (or its weapon swing for
/// <see cref="CombatSpellAction.WeaponAttack"/>), then calls
/// <see cref="MarkCast"/> on a successful send so the per-room counters
/// stay accurate vs what actually reached the server.
/// <see cref="ResetForNewRoom"/> wipes the bookkeeping when the room
/// clears.
/// </summary>
/// <remarks>
/// <para>
/// Round ordering (per the realm's one-action-per-round model — a spell
/// IS the round's action, it does not stack with a swing):
/// </para>
/// <list type="number">
/// <item><b>Backstab gate</b> — when a backstab is still pending
/// (sneaking + <see cref="CombatSettings.DoBackstab"/>), the BS round
/// must fire first or it's a guaranteed fail; the chooser returns
/// <see cref="CombatSpellAction.WeaponAttack"/> so the engine's BS path
/// owns the swing and no spell goes out.</item>
/// <item><b>Pre-attack debuff</b> — if
/// <see cref="CombatSettings.AreaDebuffSpell"/> is configured, cast it
/// once per room (honours its <see cref="CombatSpellSlot.MinEnemies"/>)
/// and never use the single-target debuff. Otherwise, if
/// <see cref="CombatSettings.SingleTargetDebuffSpell"/> is configured,
/// cast it once on every target before attacking that target.</item>
/// <item><b>Attack</b> — <see cref="CombatSettings.MultiAttackSpell"/>
/// while its conditions hold (room count ≥ MinEnemies, mana, cast cap);
/// it stays selected round after round until a condition fails (mana
/// runs out, cap reached, or the room thins below MinEnemies as mobs
/// die). When multi-attack no longer qualifies, fall to
/// <see cref="CombatSettings.NormalAttackSpell"/>, then
/// <see cref="CombatSettings.AlternateAttackSpell"/>, then the engine's
/// weapon attack command.</item>
/// </list>
/// <para>
/// Mana gating reads <see cref="CombatSettings.SpellManaThresholdMode"/>:
/// <see cref="ThresholdMode.Percentage"/> treats
/// <see cref="CombatSpellSlot.MinManaPerCast"/> as a 0–100 share of max
/// MA; <see cref="ThresholdMode.Absolute"/> treats it as raw points. A
/// debuff that can't meet its mana gate this round is skipped (we fall
/// through to the attack phase) rather than stalling the round.
/// </para>
/// <para>
/// Damage-immunity fallback (priest <c>harm</c> vs an acid slime, etc.)
/// is intentionally NOT handled here yet — it needs the server's
/// spell-no-effect message wording, which isn't modelled. When that
/// lands, the engine marks the target's primary attack spell as
/// ineffective and the chooser skips it to the alternate.
/// </para>
/// </remarks>
public sealed class CombatSpellChooser
{
    private bool _areaDebuffCast;
    private int _singleDebuffCasts;
    private int _multiAttackCasts;
    private int _normalAttackCasts;
    private int _alternateAttackCasts;
    private readonly HashSet<string> _singleDebuffedTargets =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reset all per-room cast bookkeeping. Call when the room
    /// clears / the engine starts a fresh engagement.</summary>
    public void ResetForNewRoom()
    {
        _areaDebuffCast = false;
        _singleDebuffCasts = 0;
        _multiAttackCasts = 0;
        _normalAttackCasts = 0;
        _alternateAttackCasts = 0;
        _singleDebuffedTargets.Clear();
    }

    /// <summary>The four combat categories in canonical order. Used as the
    /// tie-break when two categories share a priority value, so duplicate
    /// numbers resolve to the historical Backstab → Preattack → Spells →
    /// Physical fallback.</summary>
    private static readonly CombatCategory[] Canonical =
    {
        CombatCategory.Backstab,
        CombatCategory.Preattack,
        CombatCategory.Spells,
        CombatCategory.Physical,
    };

    /// <summary>
    /// Pick the next combat action for the current round. Pure — does not
    /// mutate counters; the caller commits the choice via
    /// <see cref="MarkCast"/> only when the cast actually reaches the wire.
    /// Walks the four categories in the user-configured priority order
    /// (<see cref="CombatSettings.PriorityBackstab"/> etc.); the first
    /// applicable category owns the round. Physical (the weapon swing) is
    /// the terminal category — always applicable — so the loop always
    /// resolves.
    /// </summary>
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
                CombatCategory.Backstab =>
                    ctx.BackstabPending ? CombatSpellDecision.Backstab : null,
                CombatCategory.Preattack =>
                    ctx.SpellsAvailable ? TryPreattack(settings, ctx, mode) : null,
                CombatCategory.Spells =>
                    ctx.SpellsAvailable ? TryAttackSpell(settings, ctx, mode) : null,
                CombatCategory.Physical => CombatSpellDecision.Weapon,
                _ => null,
            };
            if (decision is { } d) return d;
        }

        return CombatSpellDecision.Weapon;
    }

    /// <summary>Pre-attack debuff phase. Area takes precedence and excludes
    /// single (matches the historical behaviour). Returns <c>null</c> when
    /// nothing in the category can fire this round.</summary>
    private CombatSpellDecision? TryPreattack(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode)
    {
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
            && !_singleDebuffedTargets.Contains(ctx.TargetRawName)
            && CastsOk(single, _singleDebuffCasts)
            && ManaOk(single, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.SingleDebuff, single.SpellName!);

        return null;
    }

    /// <summary>Attack-spell phase: multi-attack room spell while it
    /// qualifies, then normal, then alternate single-target damage spells.
    /// Returns <c>null</c> when none can fire this round.</summary>
    private CombatSpellDecision? TryAttackSpell(
        CombatSettings settings, in CombatSpellContext ctx, ThresholdMode mode)
    {
        CombatSpellSlot multi = settings.MultiAttackSpell;
        if (IsConfigured(multi)
            && ctx.EnemyCount >= multi.MinEnemies
            && CastsOk(multi, _multiAttackCasts)
            && ManaOk(multi, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.MultiAttack, multi.SpellName!);

        CombatSpellSlot normal = settings.NormalAttackSpell;
        if (IsConfigured(normal)
            && !IsImmune(ctx, CombatSpellAction.NormalAttackSpell)
            && CastsOk(normal, _normalAttackCasts)
            && ManaOk(normal, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, normal.SpellName!);

        CombatSpellSlot alt = settings.AlternateAttackSpell;
        if (IsConfigured(alt)
            && !IsImmune(ctx, CombatSpellAction.AlternateAttackSpell)
            && CastsOk(alt, _alternateAttackCasts)
            && ManaOk(alt, ctx, mode))
            return new CombatSpellDecision(CombatSpellAction.AlternateAttackSpell, alt.SpellName!);

        return null;
    }

    /// <summary>In-place insertion sort of the four categories by configured
    /// priority (ascending), tie-breaking on canonical index. Four elements,
    /// so insertion sort is both the simplest and the fastest stable option.</summary>
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

    /// <summary>True when category <paramref name="a"/> should sort AFTER
    /// <paramref name="b"/> (i.e. <paramref name="b"/> fires first): higher
    /// priority value, or equal value with a later canonical index.</summary>
    private static bool IsHigher(CombatCategory a, int aPri, CombatCategory b, int bPri) =>
        aPri > bPri || (aPri == bPri && CanonicalIndex(a) > CanonicalIndex(b));

    private static int PriorityOf(CombatSettings s, CombatCategory cat) => cat switch
    {
        CombatCategory.Backstab  => s.PriorityBackstab,
        CombatCategory.Preattack => s.PriorityPreattack,
        CombatCategory.Spells    => s.PrioritySpells,
        CombatCategory.Physical  => s.PriorityPhysical,
        _ => int.MaxValue,
    };

    private static int CanonicalIndex(CombatCategory cat) => cat switch
    {
        CombatCategory.Backstab  => 0,
        CombatCategory.Preattack => 1,
        CombatCategory.Spells    => 2,
        CombatCategory.Physical  => 3,
        _ => int.MaxValue,
    };

    /// <summary>
    /// Record that the engine successfully sent the cast for
    /// <paramref name="decision"/> against <paramref name="targetRawName"/>.
    /// No-op for <see cref="CombatSpellAction.WeaponAttack"/>. Keeps the
    /// per-room counters in step with what actually went to the server, so
    /// a cast blocked by <see cref="Spells.CastCoordinator"/>'s cooldown
    /// isn't counted.
    /// </summary>
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

    /// <summary>The current target's species is immune to this single-target
    /// attack spell (a prior "Your spell has no effect on X." landed). Only
    /// the single-target attack slots are gated — multi-attack room spells
    /// are never marked immune (one immune mob doesn't mean the spell isn't
    /// damaging the rest of the room).</summary>
    private static bool IsImmune(in CombatSpellContext ctx, CombatSpellAction action) =>
        ctx.ImmuneAttackSpells is { } set && set.Contains(action);

    /// <summary>Under the per-room cast cap. 0 = unlimited.</summary>
    private static bool CastsOk(CombatSpellSlot slot, int castsSoFar) =>
        slot.MaxCastsPerRoom <= 0 || castsSoFar < slot.MaxCastsPerRoom;

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

/// <summary>The combat action a <see cref="CombatSpellChooser"/> picks for
/// a round. <see cref="WeaponAttack"/> means "no spell — fall through to
/// the engine's weapon attack command"; <see cref="Backstab"/> means "send
/// the backstab verb" (also no spell). Both carry a <c>null</c> spell.</summary>
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

/// <summary>The four orderable combat categories. Each maps to a
/// <see cref="CombatSettings"/> priority field; the chooser walks them in
/// ascending priority order.</summary>
internal enum CombatCategory
{
    Backstab,
    Preattack,
    Spells,
    Physical,
}

/// <summary>One round's chosen combat action plus the cast-code to send
/// (<c>null</c> for <see cref="CombatSpellAction.WeaponAttack"/> and
/// <see cref="CombatSpellAction.Backstab"/>).</summary>
public readonly record struct CombatSpellDecision(CombatSpellAction Action, string? Spell)
{
    /// <summary>Shared "no spell, swing the weapon" result.</summary>
    public static readonly CombatSpellDecision Weapon =
        new(CombatSpellAction.WeaponAttack, null);

    /// <summary>Shared "no spell, send the backstab verb" result.</summary>
    public static readonly CombatSpellDecision Backstab =
        new(CombatSpellAction.Backstab, null);
}

/// <summary>Per-round inputs the <see cref="CombatSpellChooser"/> reads.
/// <paramref name="EnemyCount"/> is the engageable-monster count in the
/// room; <paramref name="TargetRawName"/> is the current pick's
/// per-instance name (keys the once-per-target single-debuff set);
/// <paramref name="Mana"/>/<paramref name="MaxMana"/> drive the
/// per-cast mana gate; <paramref name="BackstabPending"/> is true while a
/// sneak backstab still owes its opening round;
/// <paramref name="ImmuneAttackSpells"/> is the set of single-target attack
/// actions the current target's species has proven immune to this room
/// (<c>null</c> when nothing is immune);
/// <paramref name="SpellsAvailable"/> is false when the engine has no combat
/// spell caster wired, so the Preattack / Spells categories are skipped and
/// the order collapses to Backstab vs Physical.</summary>
public readonly record struct CombatSpellContext(
    int EnemyCount,
    string TargetRawName,
    int Mana,
    int MaxMana,
    bool BackstabPending,
    IReadOnlySet<CombatSpellAction>? ImmuneAttackSpells = null,
    bool SpellsAvailable = true);
