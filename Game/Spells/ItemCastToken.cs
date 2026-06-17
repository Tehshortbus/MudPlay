using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Spells;

/// <summary>
/// The buff-slot token scheme for casting a spell <i>from an item</i>. A normal
/// spell slot holds a 4-letter cast-code (e.g. <c>mihe</c>) that's typed
/// straight to the wire; an item-cast slot instead holds
/// <c>#&lt;item name&gt;</c> (e.g. <c>#emerald tipped crozier</c>). The leading
/// <see cref="Prefix"/> marks the slot as "equip → <c>use</c> → re-equip this
/// item" rather than a direct cast; the remainder is the item name the game
/// accepts for <c>wield</c> / <c>use</c>.
/// </summary>
/// <remarks>
/// Item-casts are surfaced from the Spell Book's cast-on-use item list (only
/// unlimited-use items, which need no charge tracking) and resolved back to a
/// <see cref="ClassCastItem"/> at cast time. A token must never reach the raw
/// cast path — <see cref="CastCoordinator.TryCast"/> rejects it — because it
/// requires the equip/use/re-equip sequence instead.
/// </remarks>
public static class ItemCastToken
{
    /// <summary>The marker prefix that flags a slot value as an item-cast.</summary>
    public const string Prefix = "#";

    /// <summary>The buff-slot token for a cast item: <see cref="Prefix"/> + the item name.</summary>
    public static string Format(string itemName) => Prefix + (itemName ?? string.Empty).Trim();

    /// <summary>True when <paramref name="slotValue"/> is an item-cast token
    /// (starts with the prefix and names an item after it).</summary>
    public static bool IsToken(string? slotValue)
    {
        if (string.IsNullOrWhiteSpace(slotValue)) return false;
        string s = slotValue.Trim();
        return s.Length > Prefix.Length && s.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>The item name carried by a token, or <c>null</c> when the value
    /// isn't a token.</summary>
    public static string? ItemName(string? slotValue)
        => IsToken(slotValue) ? slotValue!.Trim()[Prefix.Length..].Trim() : null;

    /// <summary>
    /// Resolve a token to the matching cast item in <paramref name="items"/>
    /// (case-insensitive on name). Returns <c>false</c> when the value isn't a
    /// token or no item matches.
    /// </summary>
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
