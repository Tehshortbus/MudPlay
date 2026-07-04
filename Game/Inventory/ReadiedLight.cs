namespace FujinTerm.Game.Inventory;

// The light source the player currently has lit, parsed from an 'i' dump where a
// readied light prints inline as "lantern (Readied/239)" — the same (<suffix>)
// shape as worn gear, but the suffix is Readied/<N> where N is the remaining
// charge counter. Only one light is readied at a time, so InventorySnapshot
// carries at most one of these.
//
// Name is the bare item name, suffix stripped (e.g. lantern). Readied is the
// live Readied/N counter: it equals the item's game-data UseCount / 10 at full
// charge (torch 800 → 80, lantern 2400 → 240) and drains one point on a global
// 30-second tick.
public readonly record struct ReadiedLight(string Name, int Readied)
{
    // Estimated burn time left at 30 s per readied point. This is an upper
    // bound: the first drop after lighting can land under 30 s (alignment to the
    // global tick), so the true remaining time is within one tick of this.
    public System.TimeSpan RemainingTime => System.TimeSpan.FromSeconds(Readied * 30);
}
