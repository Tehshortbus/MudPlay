using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

// One cast-on-use item row in the Spell Book's item section: the carrier
// item, the spell it casts, and a charges indicator (∞ for unlimited). An
// immutable projection of a ClassCastItem — the Spell Book rebuilds the row
// list whenever the book's class changes.
public sealed class SpellBookItemRowViewModel
{
    private readonly ClassCastItem _item;

    public SpellBookItemRowViewModel(ClassCastItem item) => _item = item;

    // The carrier item's display name.
    public string ItemName => _item.ItemName;

    // The item's minimum-level gate, for the display's ascending sort.
    public int MinLevel => _item.MinLevel;

    // Level-requirement badge: "Lv N" for a gated item, "Lv —" when the item
    // has no level requirement (MinLevel 0).
    public string LevelText => _item.MinLevel > 0 ? $"Lv {_item.MinLevel}" : "Lv —";

    // The cast spell's name, or a #number fallback when it didn't resolve.
    public string SpellName => _item.SpellName.Length > 0 ? _item.SpellName : $"spell #{_item.SpellNumber}";

    // "casts <spell>" sub-label shown next to the item name.
    public string CastsText => $"casts {SpellName}";

    // The cast spell's decoded effect wrapped in parentheses ("(AC +10)",
    // "(Dmg 14–22)"), shown between the spell name and the mana cost so the
    // reader sees what the item actually does. Empty when the spell decodes to no
    // figure — the view collapses the label then.
    public string AffectsText => _item.SpellEffect.Length > 0 ? $"({_item.SpellEffect})" : string.Empty;

    // True when the cast spell has a renderable effect (drives the label's visibility).
    public bool HasAffects => _item.SpellEffect.Length > 0;

    // Compact charges indicator: "Unlimited" for an unlimited item, else "N use(s)".
    public string ChargesText => _item.Unlimited ? "Unlimited" : $"{_item.UseCount} use{(_item.UseCount == 1 ? "" : "s")}";

    // Mana the cast draws when the item is used: "N mana" for a paid
    // use-spell (e.g. a shimmering greatsword), or "free" when the cast costs
    // nothing (most charge wands / proc gear, e.g. an emerald tipped
    // crozier).
    public string ManaText => _item.CostsMana ? $"{_item.ManaCost} mana" : "free";

    // True when using the item draws mana (drives the mana-label emphasis).
    public bool CostsMana => _item.CostsMana;

    // Hover text spelling out the charges indicator.
    public string ChargesTip => _item.Unlimited
        ? "Unlimited uses"
        : $"{_item.UseCount} charge{(_item.UseCount == 1 ? "" : "s")} before the item is consumed";
}
