namespace MudPlay.Game.Spells;

// Recognises the regen ability codes a spell carries, from its Abil-N / AbilVal-N
// slots, so the regen and reroll logic never hardcodes spell names — a new data
// set's "nature tap" equivalent classifies the same way as long as it uses the
// same codes.
//
// - 145 (ManaRgn): mana-regen-rate modifier. Value 0 means the magnitude is
//   rolled from the level-scaled Min/Max range on each cast (nature tap / mana
//   flux), the only reroll-eligible shape; a non-zero value is a flat buff that
//   lands the same every cast.
// - 123 (HPRegen): HP-regen-rate modifier (+N buff / -N drain).
public static class RegenSpellClassifier
{
    // Code 123 — passive HP-regen-rate modifier (+N buff / -N drain).
    public const int HpRegenCode = 123;

    // Code 145 — passive mana-regen-rate modifier (rolled at value 0, fixed at N).
    public const int ManaRegenCode = 145;

    // True when formula carries a code-145 ability whose stored value is 0: a
    // mana-regen roll spell.
    public static bool IsManaRegenRoll(in SpellFormulaInput formula)
    {
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == ManaRegenCode && a.Value == 0) return true;
        return false;
    }
}
