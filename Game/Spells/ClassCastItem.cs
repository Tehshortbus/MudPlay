namespace FujinTerm.Game.Spells;

// A class-usable item that casts a spell when used — an Items row carrying a
// CastsSp ability (MajorMUD ability code 43, AbilVal = the cast Spells.Number).
// Wands, scrolls, potions, and proc weapons all surface this way. The Spell Book
// lists them alongside the class's learnable spells so a caster sees every spell
// source they have access to, not just the ones they memorise.
//
// ItemNumber / ItemName identify the carrier item. SpellNumber is the Spells.Number
// the item casts on use (the code-43 slot's AbilVal); SpellName is the resolved
// spell name (empty when the number doesn't resolve). ManaCost is the cast spell's
// Spells.ManaCost — mana deducted when the item is used; 0 = free (most charge wands
// / proc gear). UseCount is charges before the item is consumed; 0 = unlimited.
// IsTwoHanded marks a two-handed weapon carrier — the cast sequencer must free the
// off-hand before wielding it to use it. ClassRestricted is true when the item's
// ClassRest list names this class specifically (as opposed to a universal item
// anyone can use) — the Spell Book display lists only the class-specific sources,
// while the casting automation still consumes the full usable set. MinLevel is the
// item's wear/use level gate (Items ability code 135's AbilVal); 0 = no requirement.
public readonly record struct ClassCastItem(
    int ItemNumber, string ItemName, int SpellNumber, string SpellName, int ManaCost, int UseCount,
    bool IsTwoHanded = false, bool ClassRestricted = false, int MinLevel = 0)
{
    // True when the item has unlimited uses (UseCount 0).
    public bool Unlimited => UseCount == 0;

    // True when using the item draws mana (ManaCost > 0).
    public bool CostsMana => ManaCost > 0;
}
