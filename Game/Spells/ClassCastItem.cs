namespace FujinTerm.Game.Spells;

/// <summary>
/// A class-usable item that casts a spell when used — an Items row carrying a
/// <c>CastsSp</c> ability (MajorMUD ability code 43, <c>AbilVal</c> = the cast
/// <c>Spells.Number</c>). Wands, scrolls, potions, and proc weapons all surface
/// this way. The Spell Book lists them alongside the class's learnable spells so
/// a caster sees every spell source they have access to, not just the ones they
/// memorise.
/// </summary>
/// <param name="ItemNumber"><c>Items.Number</c> of the carrier item.</param>
/// <param name="ItemName">Display <c>Items.Name</c> of the carrier item.</param>
/// <param name="SpellNumber"><c>Spells.Number</c> the item casts on use (the code-43 slot's <c>AbilVal</c>).</param>
/// <param name="SpellName">Resolved <c>Spells.Name</c> of the cast spell (empty when the spell number doesn't resolve).</param>
/// <param name="ManaCost">The cast spell's <c>Spells.ManaCost</c> — mana deducted when the item is used; <c>0</c> = free (most charge wands / proc gear).</param>
/// <param name="UseCount">Charges before the item is consumed; <c>0</c> = unlimited.</param>
public readonly record struct ClassCastItem(
    int ItemNumber, string ItemName, int SpellNumber, string SpellName, int ManaCost, int UseCount)
{
    /// <summary>True when the item has unlimited uses (<c>UseCount</c> 0).</summary>
    public bool Unlimited => UseCount == 0;

    /// <summary>True when using the item draws mana (<c>ManaCost</c> &gt; 0).</summary>
    public bool CostsMana => ManaCost > 0;
}
