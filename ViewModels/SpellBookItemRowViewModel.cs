using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

/// <summary>
/// One cast-on-use item row in the Spell Book's item section: the carrier item,
/// the spell it casts, and a charges indicator (∞ for unlimited). An immutable
/// projection of a <see cref="ClassCastItem"/> — the Spell Book rebuilds the row
/// list whenever the book's class changes.
/// </summary>
public sealed class SpellBookItemRowViewModel
{
    private readonly ClassCastItem _item;

    public SpellBookItemRowViewModel(ClassCastItem item) => _item = item;

    /// <summary>The carrier item's display name.</summary>
    public string ItemName => _item.ItemName;

    /// <summary>The cast spell's name, or a <c>#number</c> fallback when it didn't resolve.</summary>
    public string SpellName => _item.SpellName.Length > 0 ? _item.SpellName : $"spell #{_item.SpellNumber}";

    /// <summary>"casts &lt;spell&gt;" sub-label shown next to the item name.</summary>
    public string CastsText => $"casts {SpellName}";

    /// <summary>Compact charges indicator: "∞" for unlimited, else "N use(s)".</summary>
    public string ChargesText => _item.Unlimited ? "∞" : $"{_item.UseCount} use{(_item.UseCount == 1 ? "" : "s")}";

    /// <summary>Hover text spelling out the charges indicator.</summary>
    public string ChargesTip => _item.Unlimited
        ? "Unlimited uses"
        : $"{_item.UseCount} charge{(_item.UseCount == 1 ? "" : "s")} before the item is consumed";
}
