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
}
