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

    // The cast spell's name, or a #number fallback when it didn't resolve.
    public string SpellName => _item.SpellName.Length > 0 ? _item.SpellName : $"spell #{_item.SpellNumber}";

    // "casts <spell>" sub-label shown next to the item name.
    public string CastsText => $"casts {SpellName}";

    // Compact charges indicator: "∞" for unlimited, else "N use(s)".
    public string ChargesText => _item.Unlimited ? "∞" : $"{_item.UseCount} use{(_item.UseCount == 1 ? "" : "s")}";

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

    // The buff-slot token to paste into a Bless slot for auto-use, or empty
    // for a limited-charge item (those aren't safe to recast on a buff loop,
    // so only unlimited-use items get a token).
    public string BuffToken => _item.Unlimited ? ItemCastToken.Format(_item.ItemName) : string.Empty;

    // True when this item exposes a buff-slot token (unlimited-use).
    public bool HasBuffToken => BuffToken.Length > 0;

    // Hover text explaining what the buff token does.
    public string BuffTokenTip =>
        "Paste this into a Bless slot on the Settings → Spells tab to auto-use this " +
        "item as a buff (equip → use → re-equip), recast on its buff timer.";
}
