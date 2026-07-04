namespace FujinTerm.Game.Spells;

/// <summary>
/// The sustained-recovery roles a spell can fill, derived from its
/// <c>Abil-N</c> / <c>AbilVal-N</c> slots. A spell can carry several at once
/// (chaos surge is a mana HoT that also drains HP-regen), so this is a bitset,
/// not a single label.
/// </summary>
[System.Flags]
public enum RegenSpellTraits
{
    None = 0,

    /// <summary>A code-145 mana-regen-rate modifier whose stored value is 0 —
    /// the magnitude is <i>rolled</i> from the level-scaled Min/Max range on each
    /// cast (nature tap / mana flux). The only reroll-eligible trait.</summary>
    ManaRegenRoll = 1 << 0,

    /// <summary>A code-145 mana-regen-rate modifier with a fixed non-zero value —
    /// a flat <c>+N</c> regen buff that lands the same every cast, so there is
    /// nothing to reroll.</summary>
    ManaRegenFixed = 1 << 1,

    /// <summary>A code-123 (HPRegen) modifier with a positive value — a passive
    /// HP-regen-<i>rate</i> buff (rapid healing). Negative code-123 (the drain
    /// chaos surge trades for its mana return) is not a buff and is excluded.</summary>
    HpRegenRateBuff = 1 << 2,

    /// <summary>A code-18 (Heal) ability on a spell that has a duration — HP is
    /// restored each round while it ticks (regeneration, rejuvinating field, way
    /// of the troll). A code-18 heal with no duration is an instant heal, not a
    /// HoT, and is excluded.</summary>
    HpHealOverTime = 1 << 3,

    /// <summary>A code-150 (HealMana) ability on a spell that has a duration —
    /// mana is restored each round while it ticks (chaos surge). A code-150
    /// restore with no duration is an instant mana refill and is excluded.</summary>
    ManaHealOverTime = 1 << 4,
}

/// <summary>
/// Single source of truth for recognising the four sustained-recovery spell
/// roles by MajorMUD ability code, so the regen / HoT / reroll logic never
/// hardcodes spell names — a new data set's "rapid healing" or "chaos surge"
/// equivalent classifies the same way as long as it uses the same codes.
/// </summary>
/// <remarks>
/// <para>
/// The discriminators, grounded in the stock <c>Spells</c> table:
/// </para>
/// <list type="bullet">
/// <item><c>145</c> (ManaRgn): mana-regen-rate modifier. Value 0 ⇒
/// <see cref="RegenSpellTraits.ManaRegenRoll"/> (rolled, reroll-eligible);
/// non-zero ⇒ <see cref="RegenSpellTraits.ManaRegenFixed"/> (flat buff).</item>
/// <item><c>123</c> (HPRegen), value &gt; 0: HP-regen-rate buff
/// (<see cref="RegenSpellTraits.HpRegenRateBuff"/>). Rapid healing carries
/// <c>123/+100</c>; chaos surge's <c>123/-150</c> is a drain, not a buff.</item>
/// <item><c>18</c> (Heal) on a timed spell: HP heal-over-time
/// (<see cref="RegenSpellTraits.HpHealOverTime"/>). The duration gate is what
/// separates regeneration (<c>Dur=7</c>) from an instant minor heal
/// (<c>Dur=0</c>).</item>
/// <item><c>150</c> (HealMana) on a timed spell: mana heal-over-time
/// (<see cref="RegenSpellTraits.ManaHealOverTime"/>) — chaos surge.</item>
/// </list>
/// <para>
/// A spell is scanned once and every matching trait is OR-ed in; chaos surge
/// therefore reports <see cref="RegenSpellTraits.ManaHealOverTime"/> even though
/// it also carries a negative HP-regen slot.
/// </para>
/// </remarks>
public static class RegenSpellClassifier
{
    /// <summary>Code 18 — restores HP; a HoT when the spell has a duration.</summary>
    public const int HealCode = 18;

    /// <summary>Code 150 — restores mana; a HoT when the spell has a duration.</summary>
    public const int HealManaCode = 150;

    /// <summary>Code 123 — passive HP-regen-rate modifier (<c>+N</c> buff / <c>-N</c> drain).</summary>
    public const int HpRegenCode = 123;

    /// <summary>Code 145 — passive mana-regen-rate modifier (rolled at value 0, fixed at <c>N</c>).</summary>
    public const int ManaRegenCode = 145;

    /// <summary>
    /// Every sustained-recovery trait <paramref name="formula"/>'s ability slots
    /// imply. Returns <see cref="RegenSpellTraits.None"/> for an ordinary spell
    /// (instant heal, damage, stat buff) with none of the recovery codes.
    /// </summary>
    public static RegenSpellTraits Classify(in SpellFormulaInput formula)
    {
        RegenSpellTraits traits = RegenSpellTraits.None;
        bool timed = HasDuration(formula);

        foreach (SpellAbility a in formula.Abilities)
        {
            switch (a.Code)
            {
                case ManaRegenCode:
                    traits |= a.Value == 0
                        ? RegenSpellTraits.ManaRegenRoll
                        : RegenSpellTraits.ManaRegenFixed;
                    break;
                case HpRegenCode when a.Value > 0:
                    traits |= RegenSpellTraits.HpRegenRateBuff;
                    break;
                case HealCode when timed:
                    traits |= RegenSpellTraits.HpHealOverTime;
                    break;
                case HealManaCode when timed:
                    traits |= RegenSpellTraits.ManaHealOverTime;
                    break;
            }
        }

        return traits;
    }

    /// <summary>Convenience: does <paramref name="formula"/> carry <i>any</i> of
    /// <paramref name="wanted"/>? Cheaper to read at call sites than a
    /// <c>Classify(...).HasFlag(...)</c> chain.</summary>
    public static bool Has(in SpellFormulaInput formula, RegenSpellTraits wanted)
        => (Classify(formula) & wanted) != RegenSpellTraits.None;

    // A spell counts as "timed" when its base duration is positive or it scales
    // up with level — the signal that a Heal / HealMana slot ticks over rounds
    // rather than firing once. Instant heals leave all three duration columns 0.
    private static bool HasDuration(in SpellFormulaInput f)
        => f.Dur > 0 || (f.DurInc != 0 && f.DurIncLVLs > 0);
}
