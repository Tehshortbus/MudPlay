namespace FujinTerm.Game.Inventory;

/// <summary>
/// Per-denomination coin counts the player is carrying, plus the
/// consolidated wealth value. Counts are individual coins, not values
/// (e.g. <see cref="Gold"/> = 30 means thirty gold crowns, worth 3000
/// copper). The MajorMUD ratios used to derive
/// <see cref="TotalCopperValue"/> are 1 silver = 10 copper, 1 gold = 100,
/// 1 platinum = 10000, 1 runic = 1000000 — the same ladder MudProxy's
/// InventoryManager uses so encumbrance/wealth math stays faithful.
/// </summary>
/// <param name="Copper">Copper farthings held.</param>
/// <param name="Silver">Silver nobles held.</param>
/// <param name="Gold">Gold crowns held.</param>
/// <param name="Platinum">Platinum pieces held.</param>
/// <param name="Runic">Runic coins held.</param>
/// <param name="TotalCopperValue">
/// Consolidated wealth in copper farthings — authoritative on a full
/// <c>i</c> parse (read from the game's <c>Wealth:</c> line), recomputed
/// from the per-coin counts on incremental pickup/drop.
/// </param>
public readonly record struct CurrencyHoldings(
    int Copper,
    int Silver,
    int Gold,
    int Platinum,
    int Runic,
    long TotalCopperValue)
{
    /// <summary>All-zero holdings (never observed).</summary>
    public static CurrencyHoldings Empty => new(0, 0, 0, 0, 0, 0);

    /// <summary>Total physical coin count across every denomination —
    /// the input to the 3-coins-per-encumbrance-unit weight rule.</summary>
    public long TotalCoinCount => (long)Copper + Silver + Gold + Platinum + Runic;
}
