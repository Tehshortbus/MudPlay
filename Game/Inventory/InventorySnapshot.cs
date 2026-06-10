namespace FujinTerm.Game.Inventory;

/// <summary>
/// Immutable point-in-time view of the player's currency and carry weight,
/// published by <see cref="InventoryManager"/>. Consumers (the cash engine)
/// read this instead of tracking coin lines themselves, so there is a single
/// source of truth for "how much do I hold and how heavy am I".
/// </summary>
/// <param name="Currency">Per-denomination coin counts + consolidated wealth.</param>
/// <param name="Encumbrance">Numeric carry-weight reading.</param>
/// <param name="LastUpdated">
/// When this snapshot was last refreshed.
/// <see cref="System.DateTimeOffset.MinValue"/> means never observed —
/// pair with <see cref="InventoryManager.IsLoaded"/> to tell "empty purse"
/// from "haven't parsed an <c>i</c> yet".
/// </param>
public readonly record struct InventorySnapshot(
    CurrencyHoldings Currency,
    EncumbranceReading Encumbrance,
    System.DateTimeOffset LastUpdated)
{
    /// <summary>Never-observed snapshot.</summary>
    public static InventorySnapshot Empty => new(
        CurrencyHoldings.Empty,
        EncumbranceReading.Empty,
        System.DateTimeOffset.MinValue);
}
