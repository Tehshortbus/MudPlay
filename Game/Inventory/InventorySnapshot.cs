namespace FujinTerm.Game.Inventory;

/// <summary>
/// Immutable point-in-time view of the player's currency and carry weight,
/// published by <see cref="InventoryManager"/>. Consumers (the cash engine)
/// read this instead of tracking coin lines themselves, so there is a single
/// source of truth for "how much do I hold and how heavy am I".
/// </summary>
/// <param name="Currency">Per-denomination coin counts + consolidated wealth.</param>
/// <param name="Encumbrance">Numeric carry-weight reading.</param>
/// <param name="EquippedItems">
/// Worn items harvested from the last full <c>i</c> dump (those with a trailing
/// <c>(&lt;Slot&gt;)</c> suffix). Empty until the first dump is parsed.
/// </param>
/// <param name="CarriedItems">
/// Carried-but-unworn item names harvested from the last full <c>i</c> dump
/// (those <i>without</i> a slot suffix), currency tokens excluded. Empty until
/// the first dump is parsed. Used by death-recovery to record the "inventory
/// lost" half of a deathpile (the worn half comes from <see cref="EquippedItems"/>).
/// </param>
/// <param name="LastUpdated">
/// When this snapshot was last refreshed.
/// <see cref="System.DateTimeOffset.MinValue"/> means never observed —
/// pair with <see cref="InventoryManager.IsLoaded"/> to tell "empty purse"
/// from "haven't parsed an <c>i</c> yet".
/// </param>
public readonly record struct InventorySnapshot(
    CurrencyHoldings Currency,
    EncumbranceReading Encumbrance,
    System.Collections.Generic.IReadOnlyList<EquippedItem> EquippedItems,
    System.Collections.Generic.IReadOnlyList<string> CarriedItems,
    System.DateTimeOffset LastUpdated)
{
    /// <summary>Never-observed snapshot.</summary>
    public static InventorySnapshot Empty => new(
        CurrencyHoldings.Empty,
        EncumbranceReading.Empty,
        System.Array.Empty<EquippedItem>(),
        System.Array.Empty<string>(),
        System.DateTimeOffset.MinValue);
}
