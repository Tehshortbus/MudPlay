namespace MudPlay.Game.Inventory;

// Immutable point-in-time view of the player's currency and carry weight,
// published by InventoryManager. Consumers (the cash engine) read this instead
// of tracking coin lines themselves, so there is a single source of truth for
// "how much do I hold and how heavy am I".
//
// Currency is per-denomination coin counts + consolidated wealth. Encumbrance
// is the numeric carry-weight reading. EquippedItems are worn items harvested
// from the last full 'i' dump (those with a trailing (<Slot>) suffix), empty
// until the first dump is parsed. CarriedItems are carried-but-unworn item names
// from the same dump (those without a slot suffix), currency tokens excluded;
// death-recovery uses them to record the "inventory lost" half of a deathpile
// (the worn half comes from EquippedItems). LastUpdated is when the snapshot was
// last refreshed — MinValue means never observed, so pair it with
// InventoryManager.IsLoaded to tell "empty purse" from "haven't parsed an 'i'
// yet". ReadiedLight is the currently-lit light source if the dump listed one as
// "… (Readied/N)", null when nothing is readied; it is reported here, not in
// CarriedItems. Keys are the ring's contents from the dump's "You have the
// following keys: …" trailer — a carry list the game tracks apart from the pack;
// null (never observed) reads the same as empty. Satchel is what Anomaly's gang
// satchel holds besides worn gear (GangSatchel.Unworn): items `i` doesn't list but
// the character holds and uses like any other; null when none was read.
public readonly record struct InventorySnapshot(
    CurrencyHoldings Currency,
    EncumbranceReading Encumbrance,
    System.Collections.Generic.IReadOnlyList<EquippedItem> EquippedItems,
    System.Collections.Generic.IReadOnlyList<string> CarriedItems,
    System.DateTimeOffset LastUpdated,
    ReadiedLight? ReadiedLight = null,
    System.Collections.Generic.IReadOnlyList<string>? Keys = null,
    System.Collections.Generic.IReadOnlyList<string>? Satchel = null)
{
    // Never-observed snapshot.
    public static InventorySnapshot Empty => new(
        CurrencyHoldings.Empty,
        EncumbranceReading.Empty,
        System.Array.Empty<EquippedItem>(),
        System.Array.Empty<string>(),
        System.DateTimeOffset.MinValue);

    // True when the pack currently holds — carried, worn, on the key-ring or in the gang
    // satchel — an item
    // whose name contains itemName (case-insensitive), the same loose match @have uses
    // (Remote.InventoryQueryHandler). Existence only, stack counts ignored. Meaningful
    // only once a full 'i' dump has loaded the pack (InventoryManager.IsLoaded); an
    // unloaded snapshot's lists are empty, so this reads false for everything — callers
    // that must tell "don't have it" from "haven't looked yet" gate on IsLoaded first.
    public bool Has(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return false;
        string needle = itemName.Trim();
        foreach (string c in CarriedItems)
            if (c.Contains(needle, System.StringComparison.OrdinalIgnoreCase)) return true;
        foreach (EquippedItem e in EquippedItems)
            if (e.Name.Contains(needle, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (Keys is { } keys)
            foreach (string k in keys)
                if (k.Contains(needle, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (Satchel is { } satchel)
            foreach (string s in satchel)
                if (s.Contains(needle, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The pack keeps identical items as ONE entry under a leading count ("3 dagger"),
    // from a full 'i' read and from every get, buy and hand-over between reads. So
    // an entry is not one item, and its text is not the item's name: whatever counts
    // copies or looks an item up by name reads the pack through the members below.
    // Worn gear, the lit light and the key ring are lists of their own, not the pack.

    // Each pack entry as the item's bare name and the copies it stands for.
    public static System.Collections.Generic.IEnumerable<(string Name, int Count)> Stacks(
        System.Collections.Generic.IEnumerable<string> entries)
    {
        foreach (string entry in entries)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(entry.Trim());
            if (name.Length > 0) yield return (name, count);
        }
    }

    // Copies of the item of exactly this name in the pack. A worn copy isn't one.
    public int PackCount(string itemName)
    {
        string wanted = itemName.Trim();
        int copies = 0;
        foreach ((string name, int count) in Stacks(CarriedItems))
            if (string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase)) copies += count;
        return copies;
    }

    // The names of what is in the pack, one per pile.
    public System.Collections.Generic.HashSet<string> PackNames()
    {
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int _) in Stacks(CarriedItems)) names.Add(name);
        return names;
    }

    // True when an item of exactly this name is in the pack or worn. Has matches
    // loosely, which suits a question a person asks; this is for gear the client is
    // about to swap to, where a near miss on the name would send the wrong piece.
    public bool IsCarriedOrWorn(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return false;
        string wanted = itemName.Trim();
        foreach (EquippedItem e in EquippedItems)
            if (string.Equals(e.Name, wanted, System.StringComparison.OrdinalIgnoreCase)) return true;
        return PackCount(wanted) > 0;
    }

    // Split a key-ring entry into its stack quantity and item name. The dump
    // stacks duplicate keys behind a leading count ("3 black star key") and
    // lists a lone key bare ("black star key"); a bare entry is quantity 1.
    // Callers counting held keys against an item Number need the count broken
    // out of the display string.
    public static (int Quantity, string Name) ParseKeyEntry(string entry)
    {
        string name = (entry ?? string.Empty).Trim();
        int space = name.IndexOf(' ');
        if (space > 0
            && int.TryParse(name.AsSpan(0, space), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n)
            && n > 0)
            return (n, name[(space + 1)..]);
        return (1, name);
    }
}
