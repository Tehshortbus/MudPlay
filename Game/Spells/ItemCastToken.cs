using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Spells;

// The buff-slot token scheme for casting a spell from an item. A normal spell slot
// holds a 4-letter cast-code (e.g. mihe) that's typed straight to the wire; an
// item-cast slot instead holds #<item name> (e.g. #emerald tipped crozier). The
// leading Prefix marks the slot as "equip → use → re-equip this item" rather than a
// direct cast; the remainder is the item name the game accepts for wield / use.
//
// Item-casts are surfaced from the Spell Book's cast-on-use item list (only
// unlimited-use items, which need no charge tracking) and resolved back to a
// ClassCastItem at cast time. A token must never reach the raw cast path —
// CastCoordinator.TryCast rejects it — because it requires the equip/use/re-equip
// sequence instead.
public static class ItemCastToken
{
    // The marker prefix that flags a slot value as an item-cast.
    public const string Prefix = "#";

    // The buff-slot token for a cast item: Prefix + the item name.
    public static string Format(string itemName) => Prefix + (itemName ?? string.Empty).Trim();

    // True when the slot value is an item-cast token (starts with the prefix and
    // names an item after it).
    public static bool IsToken(string? slotValue)
    {
        if (string.IsNullOrWhiteSpace(slotValue)) return false;
        string s = slotValue.Trim();
        return s.Length > Prefix.Length && s.StartsWith(Prefix, StringComparison.Ordinal);
    }

    // The item name carried by a token, or null when the value isn't a token.
    public static string? ItemName(string? slotValue)
        => IsToken(slotValue) ? slotValue!.Trim()[Prefix.Length..].Trim() : null;

    // Resolve a token to the matching cast item in the list (case-insensitive on
    // name). Returns false when the value isn't a token or no item matches.
    public static bool TryResolve(
        string? slotValue, IReadOnlyList<ClassCastItem> items, out ClassCastItem match)
    {
        match = default;
        if (ItemName(slotValue) is not { } name) return false;
        if (items is null) return false;
        foreach (ClassCastItem item in items)
            if (string.Equals(item.ItemName.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                match = item;
                return true;
            }
        return false;
    }
}
