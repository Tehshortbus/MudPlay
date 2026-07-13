namespace FujinTerm.Game.Inventory;

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
// null (never observed) reads the same as empty.
public readonly record struct InventorySnapshot(
    CurrencyHoldings Currency,
    EncumbranceReading Encumbrance,
    System.Collections.Generic.IReadOnlyList<EquippedItem> EquippedItems,
    System.Collections.Generic.IReadOnlyList<string> CarriedItems,
    System.DateTimeOffset LastUpdated,
    ReadiedLight? ReadiedLight = null,
    System.Collections.Generic.IReadOnlyList<string>? Keys = null)
{
    // Never-observed snapshot.
    public static InventorySnapshot Empty => new(
        CurrencyHoldings.Empty,
        EncumbranceReading.Empty,
        System.Array.Empty<EquippedItem>(),
        System.Array.Empty<string>(),
        System.DateTimeOffset.MinValue);

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
