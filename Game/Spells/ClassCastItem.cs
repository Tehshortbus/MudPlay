namespace FujinTerm.Game.Spells;

// A class-usable item that casts a spell on command when used — an Items row
// carrying a bare CastsSp ability (MajorMUD ability code 43, AbilVal = the cast
// Spells.Number). Readied gear you activate deliberately — wands, staves, charged
// worn items — surfaces this way. The Spell Book lists them alongside the class's
// learnable spells so a caster sees every spell source they have access to, not
// just the ones they memorise. Automatic combat procs (a %Spell per-swing weapon,
// a CastOnKill% on-kill item) and one-time consumables (potions, food) are NOT
// cast sources in this sense — you swing or quaff those — and are excluded.
//
// ItemNumber / ItemName identify the carrier item. SpellNumber is the Spells.Number
// the item casts on use (the code-43 slot's AbilVal); SpellName is the resolved
// spell name (empty when the number doesn't resolve). ManaCost is the cast spell's
// Spells.ManaCost — mana deducted when the item is used; 0 = free (most charge wands
// / proc gear). UseCount is charges before the item is consumed; a positive count is
// real charges, while anything <= 0 (MajorMUD stores -1, occasionally 0) is an
// unlimited-use item — MMUD Explorer itself normalises the field with
// "If uses <= 0 Then uses = -1". IsTwoHanded marks a
// two-handed weapon carrier — the cast sequencer must free the off-hand before
// wielding it to use it. ClassRestricted is true when the item's ClassRest list
// names this class specifically (as opposed to a universal item anyone can use) —
// the Spell Book display lists only the class-specific sources, while the casting
// automation still consumes the full usable set. MinLevel is the item's wear/use
// level gate (Items ability code 135's AbilVal); 0 = no requirement. SpellEffect is
// the cast spell's rendered affect line ("AC +10", "Dmg 14–22", …) scaled to the
// item's use-level, empty when the spell decodes to no figure — the Spell Book shows
// it inline so a caster reads what the item actually does, not just its name.
public readonly record struct ClassCastItem(
    int ItemNumber, string ItemName, int SpellNumber, string SpellName, int ManaCost, int UseCount,
    bool IsTwoHanded = false, bool ClassRestricted = false, int MinLevel = 0, string SpellEffect = "")
{
    // True when the item has unlimited uses. A genuine charge count is always
    // positive, so any value <= 0 (MajorMUD's -1 sentinel, occasionally 0) means
    // unlimited — matching MMUD Explorer's own "If uses <= 0 Then uses = -1"
    // normalisation. An unlimited weapon like a shimmering greatsword can safely
    // feed a buff loop, a limited-charge item can't.
    public bool Unlimited => UseCount <= 0;

    // True when using the item draws mana (ManaCost > 0).
    public bool CostsMana => ManaCost > 0;
}
