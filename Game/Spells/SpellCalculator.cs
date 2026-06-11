namespace FujinTerm.Game.Spells;

/// <summary>
/// Level-scaled spell math, ported verbatim from MMUD Explorer's
/// <c>modMMudDatabase.bas</c> getters (<c>GetSpellMinDamage</c> /
/// <c>GetSpellMaxDamage</c> / <c>GetSpellDuration</c> /
/// <c>GetSpellManaCost</c>). All results are per-ROUND totals: the energy
/// multiplier folds in how many times the spell fires in one round
/// (1000 energy per round ÷ the spell's <c>EnergyCost</c>).
/// </summary>
/// <remarks>
/// We only ever compute for the player, so the VB <c>bForMonster</c> path
/// (which skips the energy multiplier and the override clamp) is dropped —
/// players always apply the multiplier.
/// </remarks>
public static class SpellCalculator
{
    // MajorMUD ability codes that carry a damage/heal magnitude.
    private const int AbilDamage = 1;     // direct damage
    private const int AbilDrain = 8;      // life drain (damage, or heal when bHealsInstead)
    private const int AbilDamageMr = 17;  // damage ignoring magic resistance
    private const int AbilHeal = 18;      // healing
    private const int AbilEndCast = 151;  // chained follow-up spell (AbilVal = spell Number)

    /// <summary>Minimum per-round damage at <paramref name="level"/>.</summary>
    public static long MinDamage(in SpellFormulaInput spell, int level,
        Func<int, SpellFormulaInput?>? resolveChain = null)
        => Scaled(spell, level, healsInstead: false, useMax: false, resolveChain, energyRem: 0);

    /// <summary>Maximum per-round damage at <paramref name="level"/>.</summary>
    public static long MaxDamage(in SpellFormulaInput spell, int level,
        Func<int, SpellFormulaInput?>? resolveChain = null)
        => Scaled(spell, level, healsInstead: false, useMax: true, resolveChain, energyRem: 0);

    /// <summary>Minimum per-round healing at <paramref name="level"/>.</summary>
    public static long MinHeal(in SpellFormulaInput spell, int level,
        Func<int, SpellFormulaInput?>? resolveChain = null)
        => Scaled(spell, level, healsInstead: true, useMax: false, resolveChain, energyRem: 0);

    /// <summary>Maximum per-round healing at <paramref name="level"/>.</summary>
    public static long MaxHeal(in SpellFormulaInput spell, int level,
        Func<int, SpellFormulaInput?>? resolveChain = null)
        => Scaled(spell, level, healsInstead: true, useMax: true, resolveChain, energyRem: 0);

    /// <summary>Effect duration at <paramref name="level"/>. No override,
    /// no energy multiplier — straight base + per-level slope.</summary>
    public static long Duration(in SpellFormulaInput spell, int level)
    {
        int clamped = ClampLevel(level, spell.Cap, spell.ReqLevel);
        if (spell.DurIncLVLs == 0 || clamped < 1)
            return spell.Dur;
        return spell.Dur + Fix((double)spell.DurInc / spell.DurIncLVLs * clamped);
    }

    /// <summary>Per-round mana cost. The energy multiplier here uses a
    /// <c>1000 / EnergyCost</c> divisor with NO 143-energy gate — the
    /// asymmetry against the damage getter is faithful to the source.</summary>
    public static long ManaCost(in SpellFormulaInput spell)
    {
        long result = spell.ManaCost;
        if (spell.EnergyCost > 0 && spell.EnergyCost <= 500)
            result *= Fix(1000.0 / spell.EnergyCost);
        return result;
    }

    /// <summary>
    /// The shared damage/heal core for both Min and Max. Loops the ability
    /// slots to find a flat-value override (last qualifying slot wins) or a
    /// damage/heal slot; falls back to level-scaled base + slope; then folds
    /// in the per-round energy multiplier (or recurses into a chained
    /// end-cast spell).
    /// </summary>
    private static long Scaled(
        in SpellFormulaInput spell,
        int castLevel,
        bool healsInstead,
        bool useMax,
        Func<int, SpellFormulaInput?>? resolveChain,
        int energyRem)
    {
        long result = 0;
        bool doesDamage = false;
        int endCast = 0;

        foreach (SpellAbility ability in spell.Abilities)
        {
            switch (ability.Code)
            {
                case AbilDamage:
                case AbilDrain:
                case AbilDamageMr:
                case AbilHeal:
                    // A heal slot is code 18, or code 8 (drain) when we want healing.
                    bool isHealSlot = ability.Code == AbilHeal
                        || (ability.Code == AbilDrain && healsInstead);
                    if (isHealSlot)
                    {
                        if (!healsInstead) continue; // want damage → skip heal slot
                    }
                    else
                    {
                        if (healsInstead) continue; // want heal → skip damage slot
                    }
                    doesDamage = true;
                    if (ability.Value != 0) result = ability.Value; // flat override, last wins
                    break;
                case AbilEndCast:
                    endCast = ability.Value;
                    break;
            }
        }

        if (result == 0)
        {
            if (!doesDamage) return 0;

            // Override path skips this clamp; base/slope path clamps and then
            // passes the clamped level into any chain recursion.
            int level = ClampLevel(castLevel, spell.Cap, spell.ReqLevel);
            int baseVal = useMax ? spell.MaxBase : spell.MinBase;
            int inc = useMax ? spell.MaxInc : spell.MinInc;
            int incLvls = useMax ? spell.MaxIncLVLs : spell.MinIncLVLs;
            result = (incLvls == 0 || level < 1)
                ? baseVal
                : baseVal + Fix((double)inc / incLvls * level);
            castLevel = level;
        }

        // multi_calc — per-round energy multiplier.
        if (energyRem == 0) energyRem = 1000;
        energyRem -= spell.EnergyCost;
        if (energyRem < 1) energyRem = 1;

        if (energyRem >= 143 && spell.EnergyCost >= 143)
        {
            if (endCast == 0)
            {
                if (spell.EnergyCost <= 500)
                    result += result * Fix((double)energyRem / spell.EnergyCost);
            }
            else if (resolveChain?.Invoke(endCast) is { } chained)
            {
                // Chained end-cast is always computed in damage mode (the VB
                // recursion omits bHealsInstead).
                result += Scaled(chained, castLevel, healsInstead: false, useMax, resolveChain, energyRem);
            }
        }

        return result;
    }

    private static int ClampLevel(int level, int cap, int reqLevel)
    {
        if (level > cap && cap > 0) level = cap;
        if (level < reqLevel) level = reqLevel;
        return level;
    }

    /// <summary>VB6 <c>Fix()</c> — truncate toward zero (differs from
    /// <see cref="Math.Floor(double)"/> for negative values).</summary>
    private static long Fix(double value) => (long)Math.Truncate(value);
}
