using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// The deterministic per-monster inputs CombatSpellChooser reads: a per-monster
// override slot resolved to its cast-code and cap, and the single-target spell
// actions game data proves can't land on a monster. Shared by CombatManager (live
// rounds) and the loop simulator so both make the same pick against the same
// monster. Every gate fails open when an index is missing or a value is unknown,
// leaving the reactive "no effect" line as the backstop.
//
// ownSpell maps a cast-code to the character's own spell for it (its Spells.Number),
// so a code several classes share is judged by the record this character casts:
// `blaz` is the druid's blaze (level 20, fire) and the priest's blazing light (level
// 56, undead only), and judging a druid's blaze by the priest's record refused it
// against every living monster (report paradigm-20261024-195420).
public static class CombatSpellGates
{
    // A per-monster override slot (Spell.Number + optional cast count + optional mana
    // floor) as the (cast-code, per-room cap) pair the chooser takes, or (null, null)
    // when inactive. The override activates on a positive Spell.Number alone (report
    // paradigm-20260813-132647: an override set with no Max was silently ignored) — a
    // blank / zero count means "no cap". Below its mana floor (read as % or absolute
    // per the Combat-tab mode, like CombatSpellSlot.MinManaPerCast) the override holds
    // and the configured slot takes the rung.
    public static (string? Spell, int? Cap) ResolveOverride(
        int? spellId, int? count, int? minMana, int mana, int maxMana, ThresholdMode manaMode,
        Func<int, string?>? shortByNumber)
    {
        int floor = minMana ?? 0;
        if (floor > 0 && !CombatSpellChooser.ManaMeetsReserve(floor, mana, maxMana, manaMode))
            return (null, null);
        if (shortByNumber is null) return (null, null);
        if (spellId is not { } number || number <= 0) return (null, null);
        string? code = shortByNumber(number);
        if (string.IsNullOrWhiteSpace(code)) return (null, null);
        return (code, count is > 0 ? count : null);
    }

    // The single-target actions the monster's SpellImmu level blocks: a spell whose
    // ReqLevel is below the immunity can't hurt it. Area / multi spells are never
    // level-blocked — one immune occupant doesn't disqualify a room spell. The caller
    // passes the EFFECTIVE cast-code per rung (the override when active, else the
    // configured slot), so an override is gated on its own ReqLevel.
    public static IReadOnlySet<CombatSpellAction>? LevelBlocked(
        MonsterMagicIndex? monsterMagic, SpellReqLevelIndex? spellReqLevel,
        int monsterNumber, string? singleCode, string? normalCode, string? altCode,
        Func<string, int?>? ownSpell, string? undeadCode = null)
    {
        if (monsterMagic is null || spellReqLevel is null) return null;
        int immu = monsterMagic.SpellImmunity(monsterNumber);
        if (immu <= 0) return null;

        HashSet<CombatSpellAction>? blocked = null;
        void Check(string? code, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            int req = spellReqLevel.ReqLevel(code, ownSpell?.Invoke(code));
            if (req < 0 || req >= immu) return;
            (blocked ??= new HashSet<CombatSpellAction>()).Add(action);
        }
        Check(singleCode, CombatSpellAction.SingleDebuff);
        Check(normalCode, CombatSpellAction.NormalAttackSpell);
        Check(altCode, CombatSpellAction.AlternateAttackSpell);
        Check(undeadCode, CombatSpellAction.UndeadAttackSpell);
        return blocked;
    }

    // The single-target attack actions whose damage ELEMENT the monster resists
    // ≥ 100% (0 damage, or a heal above 100). Magic Resist and poison aren't
    // deterministic, so their spells are never pre-empted here; a 1–99% or negative
    // resist still lands damage and doesn't block.
    public static IReadOnlySet<CombatSpellAction>? ResistBlocked(
        MonsterResistIndex? monsterResist, SpellAttackTypeIndex? spellAttackType,
        int monsterNumber, string? normalCode, string? altCode, Func<string, int?>? ownSpell,
        string? undeadCode = null)
    {
        if (monsterResist is null || spellAttackType is null) return null;

        HashSet<CombatSpellAction>? blocked = null;
        void Check(string? code, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            int attType = spellAttackType.AttackType(code, ownSpell?.Invoke(code));
            if (attType < 0) return;
            int elemCode = MonsterResistIndex.ElementalResistCode(attType);
            if (elemCode < 0) return;
            if (monsterResist.ResistPercent(monsterNumber, elemCode) < 100) return;
            (blocked ??= new HashSet<CombatSpellAction>()).Add(action);
        }
        Check(normalCode, CombatSpellAction.NormalAttackSpell);
        Check(altCode, CombatSpellAction.AlternateAttackSpell);
        Check(undeadCode, CombatSpellAction.UndeadAttackSpell);
        return blocked;
    }

    // The single-target attack actions whose target class (living / undead /
    // animals-only) the monster's type excludes — turn-undead vs a living mob, harm vs
    // a nonliving construct (report paradigm-20260922-082559).
    public static IReadOnlySet<CombatSpellAction>? TargetTypeBlocked(
        SpellTargetTypeIndex? spellTargetType, MonsterLifeIndex? monsterLife,
        int monsterNumber, string? normalCode, string? altCode, Func<string, int?>? ownSpell,
        string? undeadCode = null)
    {
        if (spellTargetType is null || monsterLife is null) return null;

        HashSet<CombatSpellAction>? blocked = null;
        void Check(string? code, CombatSpellAction action)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            SpellTargetType targetType = spellTargetType.TargetType(code, ownSpell?.Invoke(code));
            if (targetType == SpellTargetType.Any) return;
            if (monsterLife.CanAffect(monsterNumber, targetType)) return;
            (blocked ??= new HashSet<CombatSpellAction>()).Add(action);
        }
        Check(normalCode, CombatSpellAction.NormalAttackSpell);
        Check(altCode, CombatSpellAction.AlternateAttackSpell);
        Check(undeadCode, CombatSpellAction.UndeadAttackSpell);
        return blocked;
    }
}
