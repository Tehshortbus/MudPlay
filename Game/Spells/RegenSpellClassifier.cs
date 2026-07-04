namespace FujinTerm.Game.Spells;

// The sustained-recovery roles a spell can fill, derived from its Abil-N /
// AbilVal-N slots. A spell can carry several at once (chaos surge is a mana HoT
// that also drains HP-regen), so this is a bitset, not a single label.
[System.Flags]
public enum RegenSpellTraits
{
    None = 0,

    // A code-145 mana-regen-rate modifier whose stored value is 0 — the
    // magnitude is rolled from the level-scaled Min/Max range on each cast
    // (nature tap / mana flux). The only reroll-eligible trait.
    ManaRegenRoll = 1 << 0,

    // A code-145 mana-regen-rate modifier with a fixed non-zero value — a flat
    // +N regen buff that lands the same every cast, so there is nothing to
    // reroll.
    ManaRegenFixed = 1 << 1,

    // A code-123 (HPRegen) modifier with a positive value — a passive
    // HP-regen-rate buff (rapid healing). Negative code-123 (the drain chaos
    // surge trades for its mana return) is not a buff and is excluded.
    HpRegenRateBuff = 1 << 2,

    // A code-18 (Heal) ability on a spell that has a duration — HP is restored
    // each round while it ticks (regeneration, rejuvinating field, way of the
    // troll). A code-18 heal with no duration is an instant heal, not a HoT, and
    // is excluded.
    HpHealOverTime = 1 << 3,

    // A code-150 (HealMana) ability on a spell that has a duration — mana is
    // restored each round while it ticks (chaos surge). A code-150 restore with
    // no duration is an instant mana refill and is excluded.
    ManaHealOverTime = 1 << 4,
}

// Single source of truth for recognising the four sustained-recovery spell
// roles by MajorMUD ability code, so the regen / HoT / reroll logic never
// hardcodes spell names — a new data set's "rapid healing" or "chaos surge"
// equivalent classifies the same way as long as it uses the same codes.
//
// The discriminators, grounded in the stock Spells table:
// - 145 (ManaRgn): mana-regen-rate modifier. Value 0 => ManaRegenRoll (rolled,
//   reroll-eligible); non-zero => ManaRegenFixed (flat buff).
// - 123 (HPRegen), value > 0: HP-regen-rate buff (HpRegenRateBuff). Rapid
//   healing carries 123/+100; chaos surge's 123/-150 is a drain, not a buff.
// - 18 (Heal) on a timed spell: HP heal-over-time (HpHealOverTime). The
//   duration gate is what separates regeneration (Dur=7) from an instant minor
//   heal (Dur=0).
// - 150 (HealMana) on a timed spell: mana heal-over-time (ManaHealOverTime) —
//   chaos surge.
//
// A spell is scanned once and every matching trait is OR-ed in; chaos surge
// therefore reports ManaHealOverTime even though it also carries a negative
// HP-regen slot.
public static class RegenSpellClassifier
{
    // Code 18 — restores HP; a HoT when the spell has a duration.
    public const int HealCode = 18;

    // Code 150 — restores mana; a HoT when the spell has a duration.
    public const int HealManaCode = 150;

    // Code 123 — passive HP-regen-rate modifier (+N buff / -N drain).
    public const int HpRegenCode = 123;

    // Code 145 — passive mana-regen-rate modifier (rolled at value 0, fixed at N).
    public const int ManaRegenCode = 145;

    // Every sustained-recovery trait formula's ability slots imply. Returns None
    // for an ordinary spell (instant heal, damage, stat buff) with none of the
    // recovery codes.
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

    // Does formula carry any of wanted? Cheaper to read at call sites than a
    // Classify(...).HasFlag(...) chain.
    public static bool Has(in SpellFormulaInput formula, RegenSpellTraits wanted)
        => (Classify(formula) & wanted) != RegenSpellTraits.None;

    // A spell counts as "timed" when its base duration is positive or it scales
    // up with level — the signal that a Heal / HealMana slot ticks over rounds
    // rather than firing once. Instant heals leave all three duration columns 0.
    private static bool HasDuration(in SpellFormulaInput f)
        => f.Dur > 0 || (f.DurInc != 0 && f.DurIncLVLs > 0);
}
