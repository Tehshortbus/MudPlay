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

    /// <summary>
    /// Seconds per spell-duration round — MMUD Explorer's <c>SPELL_ROUND_SECS</c>
    /// (<c>modMMudFunc.bas</c>). <see cref="Duration"/> is returned in spell
    /// rounds; multiply by this for wall-clock seconds. Deliberately distinct
    /// from the <b>5-second combat round</b> — a spell round is 3 s. Lives here,
    /// next to the getter that produces rounds, so every duration consumer
    /// converts the same way (the display formatters and the recast clock).
    /// </summary>
    public const int SpellRoundSeconds = 3;

    /// <summary>Effect duration at <paramref name="level"/>, in <b>spell rounds</b>
    /// (one round = <see cref="SpellRoundSeconds"/> s — multiply for wall-clock
    /// seconds). No override, no energy multiplier — straight base + per-level
    /// slope.</summary>
    public static long Duration(in SpellFormulaInput spell, int level)
    {
        int clamped = ClampLevel(level, spell.Cap, spell.ReqLevel);
        return ScaleBase(spell.Dur, spell.DurInc, spell.DurIncLVLs, clamped);
    }

    /// <summary>
    /// Level-scaled affect magnitude range — the spell's Min/Max base value
    /// plus per-level slope, clamped to the spell's obtain level
    /// (<see cref="SpellFormulaInput.ReqLevel"/>) and cap. No energy
    /// multiplier: stat affects (AC, M.R., Stealth, MaxDamage, backstab
    /// bonuses…) don't multiply per round the way damage does. MMUD
    /// Explorer's <c>PullSpellEQ</c> appends exactly this range to a generic
    /// affect whose stored <c>AbilVal</c> is 0. Clamping to <c>ReqLevel</c>
    /// is what keeps a spell evaluated below its unlock level from showing a
    /// magnitude outside its real range — it always renders the value the
    /// formula yields at the level the spell is obtained.
    /// </summary>
    public static (long Min, long Max) AffectMagnitude(in SpellFormulaInput spell, int level)
    {
        int clamped = ClampLevel(level, spell.Cap, spell.ReqLevel);
        return (
            ScaleBase(spell.MinBase, spell.MinInc, spell.MinIncLVLs, clamped),
            ScaleBase(spell.MaxBase, spell.MaxInc, spell.MaxIncLVLs, clamped));
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
            result = useMax
                ? ScaleBase(spell.MaxBase, spell.MaxInc, spell.MaxIncLVLs, level)
                : ScaleBase(spell.MinBase, spell.MinInc, spell.MinIncLVLs, level);
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

    /// <summary>Base value plus per-level slope, truncated VB6-style. The
    /// slope is skipped when no per-level denominator is set or the clamped
    /// level is below 1.</summary>
    private static long ScaleBase(int baseVal, int inc, int incLvls, int level)
        => (incLvls == 0 || level < 1) ? baseVal : baseVal + Fix((double)inc / incLvls * level);

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
